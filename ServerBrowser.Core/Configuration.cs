using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ServerBrowser.Core;

public sealed class BrowserSettings
{
    public int LayoutVersion { get; set; }
    public List<BrowserTab> Tabs { get; set; } = DefaultTabs();
    public Dictionary<string, string> Favorites { get; set; } = [];
    public int ActiveTab { get; set; }
    public string GeoIpPath { get; set; } = "";
    public string SteamWebApiKey { get; set; } = "";
    public bool DarkTheme { get; set; } = true;
    public int TimeoutMs { get; set; } = 1500;
    public int Concurrency { get; set; } = 32;
    public int AutoRefreshSeconds { get; set; }
    public bool AutoDiscover { get; set; }
    public bool PauseWhileInGame { get; set; }
    public bool RefreshSelected { get; set; } = true;
    public bool FavoritesOnTop { get; set; } = true;
    public int AddressMode { get; set; } = 1;
    public bool ShowFilterInfo { get; set; } = true;
    public bool ShowCounts { get; set; } = true;
    public bool DoubleClickConnect { get; set; } = true;
    public bool HideGhosts { get; set; }
    public bool ShowGeneralPreferences { get; set; } = true;
    public bool ShowQuickFilter { get; set; } = true;
    public bool ShowQueryPanel { get; set; } = true;
    public string PlayerSortColumn { get; set; } = "Score";
    public bool PlayerSortDescending { get; set; } = true;
    public Dictionary<string, double> PlayerColumnWidths { get; set; } = [];

    public static List<BrowserTab> DefaultTabs() =>
    [
        new() { Name = "Master Server", AppId = 0 }
    ];
}

public sealed class SettingsStore(string? directory = null)
{
    public string DirectoryPath { get; } = directory ?? DefaultDirectory();
    public string FilePath => Path.Combine(DirectoryPath, "settings.json");
    public string? LoadWarning { get; private set; }
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    public static string DefaultDirectory()
    {
        var xdg = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        var root = !string.IsNullOrEmpty(xdg) && Path.IsPathRooted(xdg) ? xdg :
            OperatingSystem.IsWindows() ? Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData) :
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");
        return Path.Combine(root, "steam-server-browser");
    }

    public BrowserSettings Load()
    {
        if (!File.Exists(FilePath)) return new();
        try
        {
            var settings = JsonSerializer.Deserialize<BrowserSettings>(File.ReadAllText(FilePath)) ?? throw new JsonException("Empty configuration");
            if (settings.LayoutVersion == 0) MigrateDefaultTabs(settings);
            Normalize(settings);
            return settings;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or InvalidDataException)
        {
            LoadWarning = "Could not read configuration: " + ex.Message;
            // Preserve the original before the next save, including malformed JSON.
            if (File.Exists(FilePath)) File.Copy(FilePath, FilePath + ".broken-" + DateTime.UtcNow.ToString("yyyyMMddHHmmssfff"));
            return new();
        }
    }

    public static void Normalize(BrowserSettings value)
    {
        value.LayoutVersion = 1;
        if (value.Tabs is null || value.Tabs.Any(t => t is null) || value.Favorites is null) throw new InvalidDataException("Invalid tab or favorites configuration");
        if (value.Tabs.Count == 0) value.Tabs = BrowserSettings.DefaultTabs();
        value.ActiveTab = Math.Clamp(value.ActiveTab, 0, value.Tabs.Count - 1);
        value.SteamWebApiKey ??= "";
        if (value.PlayerSortColumn is not ("Name" or "Score" or "Time")) value.PlayerSortColumn = "Score";
        value.PlayerColumnWidths ??= [];
        value.PlayerColumnWidths = value.PlayerColumnWidths.Where(p => double.IsFinite(p.Value) && p.Value >= 24).ToDictionary(p => p.Key, p => p.Value);
        value.TimeoutMs = Math.Clamp(value.TimeoutMs, 200, 10000);
        value.Concurrency = Math.Clamp(value.Concurrency, 1, 64);
        value.AutoRefreshSeconds = value.AutoRefreshSeconds == 0 ? 0 : Math.Clamp(value.AutoRefreshSeconds, 15, 3600);
        foreach (var tab in value.Tabs)
        {
            tab.Limit = Math.Clamp(tab.Limit, 1, 20000);
            tab.Addresses ??= [];
            tab.Name ??= "Server"; tab.Master ??= "hl2master.steampowered.com:27011";
            tab.Map ??= ""; tab.GameDirectory ??= ""; tab.Countries ??= ""; tab.IncludeTags ??= "";
            tab.ExcludeTags ??= ""; tab.Version ??= ""; tab.Search ??= ""; tab.PlayerSearch ??= "";
            tab.MasterIncludeTags ??= ""; tab.MasterExcludeTags ??= "";
            tab.Columns ??= [];
            tab.Columns = tab.Columns.Where(c => c is not null && !string.IsNullOrWhiteSpace(c.Id) && !string.IsNullOrWhiteSpace(c.Field))
                .DistinctBy(c => c.Id).ToList();
            foreach (var column in tab.Columns)
            {
                column.Title ??= column.Field;
                column.Width = double.IsFinite(column.Width) ? Math.Max(column.Width, 24) : 120;
            }
        }
    }

    private static void MigrateDefaultTabs(BrowserSettings settings)
    {
        if (settings.Tabs is null || settings.Tabs.Count == 0) return;
        string[] oldNames = ["Quake Live", "Reflex", "Toxikk", "Counter-Strike", "Team Fortress 2", "Favorites", "Master Server Query", "Master Server"];
        var old = settings.Tabs.Where(t => t is not null && oldNames.Contains(t.Name)).ToList();
        if (old.Count == 0) return;
        var selected = settings.Tabs.ElementAtOrDefault(settings.ActiveTab);
        var master = selected is { Source: ServerSource.Master } && old.Contains(selected) ? selected : old.FirstOrDefault(t => t.Source == ServerSource.Master);
        settings.Tabs.RemoveAll(old.Contains);
        master ??= new BrowserTab { AppId = 0 };
        master.Name = "Master Server";
        settings.Tabs.Insert(0, master);
        settings.ActiveTab = selected is not null && settings.Tabs.Contains(selected) ? settings.Tabs.IndexOf(selected) : 0;
    }

    public void Save(BrowserSettings settings)
    {
        Normalize(settings);
        Directory.CreateDirectory(DirectoryPath);
        string temp = Path.Combine(DirectoryPath, ".settings-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
            if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            using (var stream = new FileStream(temp, options))
            {
                JsonSerializer.Serialize(stream, settings, JsonOptions);
                stream.Flush(true);
            }
            File.Move(temp, FilePath, true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }

    public static BrowserSettings ImportWindowsIni(string text)
    {
        var sections = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, string>? current = null;
        foreach (var raw in text.Split('\n'))
        {
            string line = raw.Trim();
            if (line.Length == 0 || line.StartsWith(';')) continue;
            if (line.StartsWith('[') && line.EndsWith(']')) { current = new(StringComparer.OrdinalIgnoreCase); sections[line[1..^1]] = current; continue; }
            int equals = line.IndexOf('=');
            if (equals >= 0 && current is not null) current[line[..equals].Trim()] = line[(equals + 1)..].Trim();
        }
        var result = new BrowserSettings();
        if (sections.TryGetValue("FavoriteServers", out var favorites))
            foreach (var pair in favorites)
                if (IPEndPoint.TryParse(pair.Key, out var endpoint) && endpoint.Port > 0) result.Favorites[endpoint.ToString()] = pair.Value;
        var tabs = new List<BrowserTab>();
        foreach (var pair in sections.Where(s => Regex.IsMatch(s.Key, "^Tab[0-9]+$")))
        {
            var s = pair.Value;
            string Get(string key, string fallback = "") => s.GetValueOrDefault(key, fallback);
            int Number(string key, int fallback = 0) => int.TryParse(Get(key), out var n) ? n : fallback;
            bool Flag(string key, bool fallback = false) => Get(key, fallback ? "1" : "0") is "1" or "True" or "true";
            var tab = new BrowserTab
            {
                Name = Get("TabName", "Imported tab"), AppId = Number("InitialGameID"),
                Source = Number("Type") switch { 1 => ServerSource.Custom, 2 => ServerSource.Favorites, _ => ServerSource.Master },
                Master = Get("MasterServer", "hl2master.steampowered.com:27011"),
                Limit = Number("MasterServerQueryLimit", 1000), Map = Get("FilterMap"), Countries = Get("FilterCountry"),
                GameDirectory = Get("FilterMod"), IncludeTags = Get("TagsIncludeClient", Get("TagsInclude")),
                ExcludeTags = Get("TagsExcludeClient", Get("TagsExclude")), Version = Get("VersionMatch"),
                MasterIncludeTags = Get("TagsInclude"), MasterExcludeTags = Get("TagsExclude"),
                MinPlayers = Number("MinPlayers"), MaxPing = Number("MaxPing"), IncludeBots = Flag("MinPlayersInclBots"),
                HideEmpty = !Flag("GetEmptyServers", true), HideFull = !Flag("GetFullServers", true)
            };
            if (tab.Master == "<Steam Web API>") { tab.UseWebApi = true; tab.Master = "hl2master.steampowered.com:27011"; }
            if (sections.TryGetValue(pair.Key + "_Servers", out var servers)) tab.Addresses.AddRange(servers.Keys);
            else tab.Addresses.AddRange(ServerFilter.Tokens(Get("Servers")));
            tabs.Add(tab);
        }
        if (tabs.Count > 0) result.Tabs = tabs;
        Normalize(result);
        return result;
    }
}
