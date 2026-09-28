using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using QueryMaster;

namespace ServerBrowser.Core;

public static class Endpoints
{
    public static async Task<IPEndPoint> ResolveAsync(string address, int defaultPort = 27015, CancellationToken cancellation = default)
    {
        address = address.Trim();
        if (address.StartsWith("steam://connect/", StringComparison.OrdinalIgnoreCase)) address = address[16..].Split('/')[0];
        if (IPEndPoint.TryParse(address, out var direct) && direct.Port > 0 && direct.AddressFamily == AddressFamily.InterNetwork) return direct;
        int colon = address.LastIndexOf(':');
        string host = colon < 0 ? address : address[..colon];
        int port = defaultPort;
        if (colon >= 0 && !int.TryParse(address[(colon + 1)..], out port)) throw new FormatException("Invalid port: " + address);
        if (port is < 1 or > 65535 || host.Length == 0 || host.Contains('/') || host.Contains(':')) throw new FormatException("Enter an IPv4 address or hostname:port.");
        var ips = await Dns.GetHostAddressesAsync(host, cancellation);
        return new(ips.FirstOrDefault(ip => ip.AddressFamily == AddressFamily.InterNetwork) ?? throw new FormatException("The hostname has no IPv4 address."), port);
    }
}

public sealed record DiscoveryResult(IReadOnlyList<IPEndPoint> Addresses, string? Warning = null);
public sealed record ServerDetails(IReadOnlyList<Player> Players, IReadOnlyList<Rule> Rules, string Warning,
    bool PlayersSucceeded = true, bool RulesSucceeded = true, bool PlayersComplete = true, bool RulesComplete = true);

public sealed class ServerBrowserService(HttpClient? httpClient = null)
{
    private static readonly HttpClient SharedHttp = new() { Timeout = TimeSpan.FromSeconds(30) };
    private readonly HttpClient http = httpClient ?? SharedHttp;

    public async Task<DiscoveryResult> DiscoverAsync(BrowserTab tab, string apiKey, CancellationToken cancellation)
    {
        if (tab.UseWebApi)
        {
            if (string.IsNullOrWhiteSpace(apiKey)) throw new InvalidOperationException("Enter a Steam Web API Key in Options, or use a UDP master server.");
            string filter = MasterUtil.ProcessFilter(tab.ToMasterFilter()).TrimEnd('\0');
            if (tab.Region != 255) filter += "\\region\\" + tab.Region;
            string uri = "https://api.steampowered.com/IGameServersService/GetServerList/v1/?key=" + Uri.EscapeDataString(apiKey) +
                         "&filter=" + Uri.EscapeDataString(filter) + "&limit=" + Math.Clamp(tab.Limit, 1, 20000);
            // Never include the request URI (it contains the key) in error messages.
            using var response = await http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellation);
            if (!response.IsSuccessStatusCode) throw new InvalidOperationException($"Steam Web API returned HTTP {(int)response.StatusCode}. Check your API key and network connection.");
            using var json = JsonDocument.Parse(await ReadLimitedAsync(response.Content, 16 * 1024 * 1024, cancellation));
            if (!json.RootElement.TryGetProperty("response", out var body)) throw new InvalidDataException("Steam Web API returned an unknown format.");
            if (!body.TryGetProperty("servers", out var servers)) return new([]);
            var endpoints = new HashSet<IPEndPoint>();
            foreach (var item in servers.EnumerateArray())
                if (item.TryGetProperty("addr", out var addr) && IPEndPoint.TryParse(addr.GetString() ?? "", out var ep) && ep.Port > 0 && ep.AddressFamily == AddressFamily.InterNetwork)
                    endpoints.Add(ep);
            return new(endpoints.Take(tab.Limit).ToList());
        }
        var master = await Endpoints.ResolveAsync(tab.Master, 27011, cancellation);
        using var udp = new UdpClient(AddressFamily.InterNetwork);
        udp.Connect(master);
        var found = new HashSet<IPEndPoint>();
        string seed = "0.0.0.0:0";
        var seeds = new HashSet<string>();
        for (int page = 0; page < 30; page++)
        {
            cancellation.ThrowIfCancellationRequested();
            byte[] packet = MasterUtil.BuildPacket(seed, (Region)tab.Region, tab.ToMasterFilter());
            byte[]? data = null;
            for (int retry = 0; retry < 3; retry++)
            {
                await udp.SendAsync(packet, cancellation);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
                timeout.CancelAfter(2000);
                try { data = (await udp.ReceiveAsync(timeout.Token)).Buffer; break; }
                catch (OperationCanceledException) when (!cancellation.IsCancellationRequested) { }
            }
            if (data is null)
            {
                if (found.Count > 0) return new(found.ToList(), "Master query timed out. Partial results were kept. Try again later or use the Web API.");
                throw new TimeoutException("UDP master server did not respond. Check UDP port 27011 or configure a Steam Web API Key.");
            }
            var batch = ParseMasterPacket(data);
            if (batch.Count == 0) throw new InvalidDataException("Master server returned an empty page.");
            foreach (var endpoint in batch)
            {
                if (endpoint.Address.Equals(IPAddress.Any) && endpoint.Port == 0) return new(found.ToList());
                if (endpoint.Port > 0) found.Add(endpoint);
                if (found.Count >= tab.Limit) return new(found.ToList());
            }
            seed = batch[^1].ToString();
            if (!seeds.Add(seed)) return new(found.ToList(), "Master server repeated the same page. Further requests were stopped.");
        }
        return new(found.ToList(), "UDP page limit reached. Narrow your filters or use the Web API.");
    }

    public static List<IPEndPoint> ParseMasterPacket(byte[] data)
    {
        byte[] header = [255, 255, 255, 255, 0x66, 0x0a];
        if (data.Length < 6 || (data.Length - 6) % 6 != 0 || !data.AsSpan(0, 6).SequenceEqual(header)) throw new InvalidDataException("Invalid master server response.");
        var result = new List<IPEndPoint>();
        for (int i = 6; i < data.Length; i += 6) result.Add(new(new IPAddress(data.AsSpan(i, 4)), data[i + 4] * 256 + data[i + 5]));
        return result;
    }

    public Task<ServerEntry> QueryAsync(IPEndPoint endpoint, int appId, int timeoutMs, bool queryPlayers, CancellationToken cancellation, bool queryRules = false) => Task.Run(() =>
    {
        cancellation.ThrowIfCancellationRequested();
        try
        {
            using var server = ServerQuery.GetServerInstance(appId is > 0 and <= 130 ? EngineType.GoldSource : EngineType.Source, endpoint, false, timeoutMs, timeoutMs);
            server.Retries = 2;
            using var cancel = cancellation.Register(server.Dispose);
            var info = server.GetInfo();
            cancellation.ThrowIfCancellationRequested();
            if (info is null) return new ServerEntry { Endpoint = endpoint, Error = "Query timed out" };
            IReadOnlyList<Player> players = [];
            string error = "";
            if (queryPlayers)
            {
                try { players = server.GetPlayers()?.ToArray() ?? []; }
                catch (Exception ex) when (!cancellation.IsCancellationRequested) { error = "Player query failed: " + ex.Message; }
            }
            IReadOnlyList<Rule> rules = [];
            if (queryRules)
            {
                try { rules = server.GetRules()?.ToArray() ?? []; }
                catch (Exception ex) when (!cancellation.IsCancellationRequested) { error = "Rules query failed: " + ex.Message; }
            }
            cancellation.ThrowIfCancellationRequested();
            return new ServerEntry { Endpoint = endpoint, Info = info, Players = players, PlayersQueried = queryPlayers, Rules = rules, Error = error };
        }
        catch (Exception) when (cancellation.IsCancellationRequested) { throw new OperationCanceledException(cancellation); }
        catch (Exception ex) { return new ServerEntry { Endpoint = endpoint, Error = ex.Message }; }
    }, cancellation);

    public Task<ServerDetails> DetailsAsync(ServerEntry row, int timeoutMs, CancellationToken cancellation, Action<ServerDetails>? progress = null) => Task.Run(() =>
    {
        using var server = ServerQuery.GetServerInstance(row.Info?.IsObsolete == true ? EngineType.GoldSource : EngineType.Source,
            row.Endpoint, row.Info?.IsObsolete == true, timeoutMs, timeoutMs);
        server.Retries = 2;
        using var registration = cancellation.Register(server.Dispose);
        IReadOnlyList<Player> players = [];
        IReadOnlyList<Rule> rules = [];
        var warnings = new List<string>();
        bool playersSucceeded = false, rulesSucceeded = false;
        try { players = server.GetPlayers()?.ToArray() ?? throw new TimeoutException(); playersSucceeded = true; }
        catch (Exception) when (!cancellation.IsCancellationRequested) { warnings.Add("Player list did not respond"); }
        cancellation.ThrowIfCancellationRequested();
        progress?.Invoke(new(players, rules, string.Join("; ", warnings), playersSucceeded, false, true, false));
        try { rules = server.GetRules()?.OrderBy(x => x.Name).ToArray() ?? throw new TimeoutException(); rulesSucceeded = true; }
        catch (Exception) when (!cancellation.IsCancellationRequested) { warnings.Add("Rules query did not respond"); }
        cancellation.ThrowIfCancellationRequested();
        return new ServerDetails(players, rules, string.Join("; ", warnings), playersSucceeded, rulesSucceeded);
    }, cancellation);

    public async Task<string> DownloadListAsync(string url, CancellationToken cancellation)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("https" or "http")) throw new FormatException("List URL must use HTTP or HTTPS.");
        using var response = await http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellation);
        response.EnsureSuccessStatusCode();
        return Encoding.UTF8.GetString(await ReadLimitedAsync(response.Content, 2 * 1024 * 1024, cancellation));
    }

    private static async Task<byte[]> ReadLimitedAsync(HttpContent content, int max, CancellationToken token)
    {
        using var input = await content.ReadAsStreamAsync(token);
        using var output = new MemoryStream();
        byte[] buffer = new byte[8192];
        int length;
        while ((length = await input.ReadAsync(buffer, token)) > 0)
        {
            if (output.Length + length > max) throw new InvalidDataException("Response exceeds the size limit.");
            await output.WriteAsync(buffer.AsMemory(0, length), token);
        }
        return output.ToArray();
    }
}
