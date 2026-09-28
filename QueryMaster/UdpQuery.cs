using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using ICSharpCode.SharpZipLib.BZip2;
using ICSharpCode.SharpZipLib.Checksum;

namespace QueryMaster
{
  // Portable replacement: validate IDs, de-duplicate/reorder fragments, bound
  // decompression, and read the Source compressed size as a full uint32.
  internal class UdpQuery : ServerSocket
  {
    private bool firstPacket = true;
    private const int MaxResponseSize = 1024 * 1024;
    public bool SendFirstPacketTwice { get; set; }

    internal UdpQuery(IPEndPoint address, int sendTimeOut, int receiveTimeOut) : base(SocketType.Udp)
    {
      Connect(address);
      socket.SendTimeout = sendTimeOut;
      socket.ReceiveTimeout = receiveTimeOut;
    }

    internal byte[] GetResponse(byte[] msg, EngineType type, Stopwatch sw = null)
    {
      sw?.Start();
      try
      {
        SendData(msg);
        if (firstPacket && SendFirstPacketTwice) SendData(msg);
        firstPacket = false;
        var data = ReceiveData();
        if (data.Length < 5) throw new InvalidPacketException("Truncated UDP response");
        int header = BitConverter.ToInt32(data, 0);
        if (header == -1) return data.Skip(4).ToArray();
        if (header != -2) throw new InvalidHeaderException("Invalid UDP header");
        return ReadFragments(data, type);
      }
      finally { sw?.Stop(); }
    }

    private byte[] ReadFragments(byte[] first, EngineType type)
    {
      bool source = type == EngineType.Source;
      int headerSize = source ? 12 : 9;
      if (first.Length < headerSize) throw new InvalidPacketException("Truncated split header");
      int id = BitConverter.ToInt32(first, 4);
      int count = source ? first[8] : first[8] & 15;
      if (count == 0) throw new InvalidPacketException("Empty split response");
      bool compressed = source && id < 0;
      var fragments = new Dictionary<int, byte[]>();
      var data = first;
      for (int received = 0; received < count * 3 + 8; received++)
      {
        if (data.Length >= headerSize && BitConverter.ToInt32(data, 0) == -2 && BitConverter.ToInt32(data, 4) == id)
        {
          int currentCount = source ? data[8] : data[8] & 15;
          int number = source ? data[9] : data[8] >> 4;
          if (currentCount != count || number >= count) throw new InvalidPacketException("Invalid split numbering");
          fragments.TryAdd(number, data);
          if (fragments.Count == count) break;
        }
        data = ReceiveData();
      }
      if (fragments.Count != count) throw new InvalidPacketException("Incomplete split response");
      int expectedLength = 0;
      uint checksum = 0;
      using (var buffer = new MemoryStream())
      {
        for (int i = 0; i < count; i++)
        {
          var fragment = fragments[i];
          int skip = headerSize;
          if (compressed && i == 0)
          {
            if (fragment.Length < skip + 8) throw new InvalidPacketException("Truncated compression header");
            expectedLength = BitConverter.ToInt32(fragment, skip);
            checksum = BitConverter.ToUInt32(fragment, skip + 4);
            skip += 8;
            if (expectedLength <= 0 || expectedLength > MaxResponseSize) throw new InvalidPacketException("Response size exceeds limit");
          }
          buffer.Write(fragment, skip, fragment.Length - skip);
          if (buffer.Length > MaxResponseSize) throw new InvalidPacketException("Response size exceeds limit");
        }
        data = buffer.ToArray();
      }
      if (compressed)
      {
        using (var input = new MemoryStream(data))
        using (var unzip = new BZip2InputStream(input))
        using (var output = new MemoryStream())
        {
          var chunk = new byte[4096];
          int read;
          while ((read = unzip.Read(chunk, 0, chunk.Length)) > 0)
          {
            if (output.Length + read > expectedLength) throw new InvalidPacketException("Invalid decompressed size");
            output.Write(chunk, 0, read);
          }
          data = output.ToArray();
        }
        var crc = new Crc32();
        crc.Update(data);
        if (data.Length != expectedLength || (uint)crc.Value != checksum) throw new InvalidPacketException("Invalid decompressed size or checksum");
      }
      // Some Source servers omit the nested single-packet header.
      return data.Length >= 4 && BitConverter.ToInt32(data, 0) == -1 ? data.Skip(4).ToArray() : data;
    }
  }
}
