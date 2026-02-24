using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Xml;

namespace QueryMaster
{
  /// <summary>
  ///   Provides methods to query master server.
  ///   An instance can only be used for a single request and is automatically disposed when the request completes or times out
  /// </summary>
  public class MasterServerWebApi : MasterServer
  {
    private const int MaxDownloadAttemptsPerEndpoint = 1;
    private static readonly string[] ApiUrls =
    {
      "https://api.steampowered.com/IGameServersService/GetServerList/v1/",
      "https://api.steampowered.com/IGameServersService/GetServerList/v0001/"
    };
    private static readonly Regex JsonAddrPattern = new Regex("\\\"addr\\\"\\s*:\\s*\\\"(?<addr>[^\\\"]+)\\\"", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex AnyIpPortPattern = new Regex("(?<addr>\\b(?:\\d{1,3}\\.){3}\\d{1,3}:\\d{1,5}\\b)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private readonly string steamWebApiKey = ""; // create an account and get a steam web api key at http://steamcommunity.com/dev/apikey

    public MasterServerWebApi(string steamWebApiKey)
    {
      this.steamWebApiKey = steamWebApiKey;
    }

    /// <summary>
    /// Gets a server list from the Steam master server.
    /// The callback is invoked from a background thread every time a batch of servers is received.
    /// The end of the list is marked with an IPEndPoint of 0.0.0.0:0
    /// In case of a timeout or an exception, the callback is invoked with a NULL parameter value.
    /// </summary>
    /// <exception cref="InvalidOperationException">Thrown if the object is still busy handling a previous call to GetAddresses</exception>
    public override void GetAddresses(Region region, MasterIpCallback callback, IpFilter filter)
    {
      ThreadPool.QueueUserWorkItem(x =>
      {
        try
        {
          using (var cli = new XWebClient())
          {
            var filters = (MasterUtil.ProcessFilter(filter) ?? string.Empty).TrimEnd('\0');
            var responseText = DownloadServerList(cli, filters, GetAddressesLimit);

            var parsedAddresses = ParseServerAddresses(responseText);
            var endpoints = new List<Tuple<IPEndPoint, ServerInfo>>();
            foreach (var ep in parsedAddresses)
              endpoints.Add(new Tuple<IPEndPoint, ServerInfo>(ep, null));

            callback(new ReadOnlyCollection<Tuple<IPEndPoint, ServerInfo>>(endpoints), null, false);
          }
        }
        catch(Exception ex)
        {
          callback(null, ex, false);
        }
      });
    }

    private string DownloadServerList(XWebClient cli, string filter, int limit)
    {
      var encodedKey = Uri.EscapeDataString(steamWebApiKey ?? string.Empty);
      var encodedFilter = Uri.EscapeDataString(filter ?? string.Empty);
      Exception lastError = null;

      foreach (var apiUrl in ApiUrls)
      {
        var url = string.Format("{0}?key={1}&format=xml&filter={2}&limit={3}", apiUrl, encodedKey, encodedFilter, limit);
        for (int attempt = 0; attempt < MaxDownloadAttemptsPerEndpoint; attempt++)
        {
          try
          {
            return cli.DownloadString(url);
          }
          catch (WebException ex)
          {
            lastError = ex;
            if (!IsTransient(ex) || attempt >= MaxDownloadAttemptsPerEndpoint - 1)
              break;
            Thread.Sleep(300);
          }
        }
      }

      if (lastError != null)
        throw lastError;
      throw new InvalidOperationException("Steam Web API request failed.");
    }

    private static bool IsTransient(WebException ex)
    {
      switch (ex.Status)
      {
        case WebExceptionStatus.Timeout:
        case WebExceptionStatus.ConnectFailure:
        case WebExceptionStatus.ConnectionClosed:
        case WebExceptionStatus.NameResolutionFailure:
        case WebExceptionStatus.ReceiveFailure:
        case WebExceptionStatus.SendFailure:
        case WebExceptionStatus.KeepAliveFailure:
        case WebExceptionStatus.PipelineFailure:
          return true;
        default:
          return false;
      }
    }

    private static List<IPEndPoint> ParseServerAddresses(string responseText)
    {
      var endpoints = ParseXmlAddresses(responseText);
      if (endpoints.Count == 0)
        endpoints = ParseJsonAddresses(responseText);
      if (endpoints.Count == 0)
        endpoints = ParseLooseAddressMatches(responseText);

      if (endpoints.Count > 0)
        return endpoints;

      string snippet = responseText ?? "";
      snippet = snippet.Replace('\r', ' ').Replace('\n', ' ');
      if (snippet.Length > 220)
        snippet = snippet.Substring(0, 220);
      throw new FormatException("Steam Web API response format is not recognized. " + snippet);
    }

    private static List<IPEndPoint> ParseXmlAddresses(string responseText)
    {
      var result = new List<IPEndPoint>();
      if (string.IsNullOrWhiteSpace(responseText))
        return result;

      try
      {
        var xml = SanitizeXml(responseText);
        var doc = new XmlDocument();
        doc.XmlResolver = null;

        var settings = new XmlReaderSettings();
        settings.DtdProcessing = DtdProcessing.Ignore;
        settings.XmlResolver = null;
        using (var sr = new StringReader(xml))
        using (var xr = XmlReader.Create(sr, settings))
          doc.Load(xr);

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Newer/older API variants may provide either <message> or <server> entries.
        var serverNodes = doc.SelectNodes("//message|//server");
        if (serverNodes != null)
        {
          foreach (XmlNode serverNode in serverNodes)
          {
            var addrText = serverNode.SelectSingleNode("addr|address")?.InnerText;
            var portText = serverNode.SelectSingleNode("gameport|port")?.InnerText;

            IPEndPoint ep;
            if (!TryParseAddressWithOptionalPort(addrText, portText, out ep))
              continue;

            if (seen.Add(ep.ToString()))
              result.Add(ep);
          }
        }

        if (result.Count == 0)
        {
          var nodes = doc.SelectNodes("//addr|//address");
          if (nodes != null)
          {
            foreach (XmlNode node in nodes)
            {
              IPEndPoint ep;
              if (!TryParseAddress(node.InnerText, out ep))
                continue;

              if (seen.Add(ep.ToString()))
                result.Add(ep);
            }
          }
        }
      }
      catch
      {
        // ignore and fall through to JSON / loose parsing
      }

      return result;
    }

    private static List<IPEndPoint> ParseJsonAddresses(string responseText)
    {
      var result = new List<IPEndPoint>();
      if (string.IsNullOrWhiteSpace(responseText))
        return result;

      var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
      foreach (Match match in JsonAddrPattern.Matches(responseText))
      {
        IPEndPoint ep;
        if (!TryParseAddress(match.Groups["addr"].Value, out ep))
          continue;

        if (seen.Add(ep.ToString()))
          result.Add(ep);
      }

      return result;
    }

    private static List<IPEndPoint> ParseLooseAddressMatches(string responseText)
    {
      var result = new List<IPEndPoint>();
      if (string.IsNullOrWhiteSpace(responseText))
        return result;

      var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
      foreach (Match match in AnyIpPortPattern.Matches(responseText))
      {
        IPEndPoint ep;
        if (!TryParseAddress(match.Groups["addr"].Value, out ep))
          continue;

        if (seen.Add(ep.ToString()))
          result.Add(ep);
      }

      return result;
    }

    private static bool TryParseAddressWithOptionalPort(string address, string portText, out IPEndPoint endpoint)
    {
      endpoint = null;
      if (TryParseAddress(address, out endpoint))
        return true;

      if (string.IsNullOrWhiteSpace(address))
        return false;

      int port;
      if (!int.TryParse((portText ?? "").Trim(), out port) || port <= 0 || port > 65535)
        return false;

      IPAddress ip;
      if (!IPAddress.TryParse(address.Trim(), out ip))
        return false;

      endpoint = new IPEndPoint(ip, port);
      return true;
    }

    private static bool TryParseAddress(string address, out IPEndPoint endpoint)
    {
      endpoint = null;
      if (string.IsNullOrWhiteSpace(address))
        return false;

      var value = address.Trim();
      int i = value.LastIndexOf(':');
      if (i <= 0 || i >= value.Length - 1)
        return false;

      int port;
      if (!int.TryParse(value.Substring(i + 1), out port) || port <= 0 || port > 65535)
        return false;

      IPAddress ip;
      if (!IPAddress.TryParse(value.Substring(0, i), out ip))
        return false;

      endpoint = new IPEndPoint(ip, port);
      return true;
    }

    private static string SanitizeXml(string xml)
    {
      var sb = new StringBuilder(xml);
      for (int i = 0, c = sb.Length; i < c; i++)
      {
        if (sb[i] < 32 && !char.IsWhiteSpace(sb[i]))
          sb[i] = ' ';
      }
      return sb.ToString();
    }
  }
}
