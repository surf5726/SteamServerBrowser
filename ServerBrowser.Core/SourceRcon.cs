using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace ServerBrowser.Core;

public static class SourceRcon
{
    public static async Task<string> SendAsync(IPEndPoint endpoint, string password, string command, CancellationToken cancellation)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        using var tcp = new TcpClient();
        await tcp.ConnectAsync(endpoint.Address, endpoint.Port, timeout.Token);
        using var stream = tcp.GetStream();
        await WriteAsync(stream, 1, 3, password, timeout.Token);
        bool authorized = false;
        for (int i = 0; i < 4; i++)
        {
            var reply = await ReadAsync(stream, timeout.Token);
            if (reply.Id == -1) throw new InvalidOperationException("Incorrect RCON password.");
            if (reply.Type == 2 && reply.Id == 1) { authorized = true; break; }
        }
        if (!authorized) throw new InvalidDataException("Invalid RCON authentication response.");
        await WriteAsync(stream, 2, 2, command, timeout.Token);
        // A response-value probe marks the end of a multi-packet command reply.
        await WriteAsync(stream, 3, 0, "", timeout.Token);
        var output = new StringBuilder();
        for (int i = 0; i < 1024; i++)
        {
            var reply = await ReadAsync(stream, timeout.Token);
            if (reply.Id == 3) return output.ToString();
            if (reply.Id != 2) throw new InvalidDataException("RCON response ID does not match.");
            output.Append(reply.Text);
            if (output.Length > 4 * 1024 * 1024) throw new InvalidDataException("RCON response is too large.");
        }
        throw new InvalidDataException("Too many RCON response packets.");
    }

    private static async Task WriteAsync(NetworkStream stream, int id, int type, string text, CancellationToken token)
    {
        if (text.Contains('\0')) throw new ArgumentException("RCON commands cannot contain null characters.");
        byte[] body = Encoding.UTF8.GetBytes(text);
        if (body.Length > 4086) throw new ArgumentException("RCON command is too long.");
        byte[] packet = new byte[body.Length + 14];
        BinaryPrimitives.WriteInt32LittleEndian(packet, body.Length + 10);
        BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(4), id);
        BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(8), type);
        body.CopyTo(packet, 12);
        await stream.WriteAsync(packet, token);
    }
    private static async Task<(int Id, int Type, string Text)> ReadAsync(NetworkStream stream, CancellationToken token)
    {
        byte[] length = new byte[4];
        await stream.ReadExactlyAsync(length, token);
        int size = BinaryPrimitives.ReadInt32LittleEndian(length);
        if (size is < 10 or > 1024 * 1024) throw new InvalidDataException("Invalid RCON packet length.");
        byte[] packet = new byte[size];
        await stream.ReadExactlyAsync(packet, token);
        if (packet[^1] != 0 || packet[^2] != 0) throw new InvalidDataException("Invalid RCON terminator.");
        return (BinaryPrimitives.ReadInt32LittleEndian(packet), BinaryPrimitives.ReadInt32LittleEndian(packet.AsSpan(4)), Encoding.UTF8.GetString(packet, 8, size - 10));
    }
}
