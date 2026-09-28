using System.Net;
using MaxMind.Db;

namespace ServerBrowser.Core;

public interface ICountryLookup : IDisposable
{
    bool IsAvailable { get; }
    string Lookup(IPAddress ip);
}

public sealed class GeoIp : ICountryLookup
{
    private readonly Reader? reader;
    public bool IsAvailable => reader is not null;
    public GeoIp(string path) { if (!string.IsNullOrWhiteSpace(path)) reader = new Reader(ResolvePath(path)); }
    public static string ResolvePath(string path, string? appDirectory = null) =>
        Path.GetFullPath(path, appDirectory ?? AppContext.BaseDirectory);
    public static string RelativePath(string path, string? appDirectory = null)
    {
        if (string.IsNullOrWhiteSpace(path)) return "";
        string root = appDirectory ?? AppContext.BaseDirectory;
        return Path.GetRelativePath(root, ResolvePath(path, root)).Replace('\\', '/');
    }
    public string Lookup(IPAddress ip)
    {
        if (reader is null) return "";
        var record = reader.Find<Dictionary<string, object>>(ip);
        return ReadCountry(record);
    }
    public static string ReadCountry(IDictionary<string, object>? record)
    {
        if (record is null) return "";
        string Read(string key) => record.TryGetValue(key, out var value) ? value as string ?? "" : "";
        string code = Read("country_code");
        if (code.Length != 2) code = Read("country_short");
        if (code.Length != 2) code = Read("country"); // ipinfo_lite
        if (code.Length != 2 && record.TryGetValue("country", out var country) && country is IDictionary<string, object> nested)
            code = nested.TryGetValue("iso_code", out var iso) ? iso as string ?? "" : "";
        return code.Length == 2 ? code.ToUpperInvariant() : "";
    }
    public void Dispose() => reader?.Dispose();
}
