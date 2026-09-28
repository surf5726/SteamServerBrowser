using System.ComponentModel;
using System.Net;
using System.Text.RegularExpressions;
using QueryMaster;

namespace ServerBrowser.Core;

public enum ServerSource { Master, Favorites, Custom }

public sealed class BrowserTab
{
    public string Name { get; set; } = "Server";
    public ServerSource Source { get; set; }
    public int AppId { get; set; } = 440;
    public string Master { get; set; } = "hl2master.steampowered.com:27011";
    public bool UseWebApi { get; set; }
    public int Region { get; set; } = 255;
    public int Limit { get; set; } = 1000;
    public string Map { get; set; } = "";
    public string GameDirectory { get; set; } = "";
    public string Countries { get; set; } = "";
    public string IncludeTags { get; set; } = "";
    public string ExcludeTags { get; set; } = "";
    public string MasterIncludeTags { get; set; } = "";
    public string MasterExcludeTags { get; set; } = "";
    public string Version { get; set; } = "";
    public string Search { get; set; } = "";
    public string PlayerSearch { get; set; } = "";
    public int MinPlayers { get; set; }
    public int MaxPing { get; set; }
    public bool IncludeBots { get; set; }
    public bool HideEmpty { get; set; }
    public bool HideFull { get; set; }
    public bool HideOffline { get; set; } = true;
    public bool SecureOnly { get; set; }
    public List<string> Addresses { get; set; } = [];
    public List<ServerColumn> Columns { get; set; } = [];
    public string? SortColumn { get; set; }
    public bool SortDescending { get; set; }
    public bool AutoFitColumns { get; set; }
    public override string ToString() => Name;

    public IpFilter ToMasterFilter() => new()
    {
        App = (Game)AppId, Map = Map, GameDirectory = GameDirectory,
        IsNotEmpty = HideEmpty, IsNotFull = HideFull, IsSecure = SecureOnly,
        Sv_Tags = MasterIncludeTags, VersionMatch = Version,
        Nor = string.IsNullOrWhiteSpace(MasterExcludeTags) && AppId != 730 ? null : new IpFilter
        { Sv_Tags = string.Join(',', new[] { MasterExcludeTags, AppId == 730 ? "valve_ds" : "" }.Where(x => x.Length > 0)) }
    };
}

public sealed class ServerEntry : INotifyPropertyChanged
{
    public required IPEndPoint Endpoint { get; init; }
    public ServerInfo? Info { get; init; }
    public string CachedName { get; init; } = "";
    public string Error { get; init; } = "";
    public string Country { get; init; } = "";
    public IReadOnlyList<Player> Players { get; set; } = [];
    public bool PlayersQueried { get; set; }
    public IReadOnlyList<Rule> Rules { get; set; } = [];
    public Dictionary<string, object?> ColumnValues { get; private set; } = [];
    private bool favorite;
    public bool Favorite { get => favorite; set { favorite = value; PropertyChanged?.Invoke(this, new(nameof(Star))); } }
    public string Star => Favorite ? "★" : "";
    public string Address => Endpoint.ToString();
    public bool DisplayGamePort { get; set; }
    public string DisplayAddress => DisplayGamePort ? GameEndpoint.ToString() : Address;
    public string Dedicated => Info?.ServerType == "Dedicated" ? "◆" : "";
    public string JoinState => Info is null ? "" : Info.Players < Info.MaxPlayers ? "●" : "—";
    public int? BuddyCount => PlayersQueried ? Players.Count : null;
    public int? MaxPlayers => Info?.MaxPlayers;
    public string Name => Info?.Name ?? (CachedName.Length > 0 ? CachedName : Address);
    public string Map => Info?.Map ?? "";
    public int PlayerCount => Info?.Players ?? 0;
    // The original CS:GO extension treats its player counter as humans-only.
    public int HumanPlayers => Math.Max(0, PlayerCount - (Info?.Id == 730 ? 0 : (Info?.Bots ?? 0)));
    public int Bots => Info?.Bots ?? 0;
    public string Population => Info is null ? "" : $"{HumanPlayers}{(Bots > 0 ? "+" + Bots : "")} / {Info.MaxPlayers}";
    public long? Ping => Info?.Ping;
    public string Tags => Info?.Extra.Keywords ?? "";
    public string Game => Info?.Description ?? "";
    public string Secure => Info?.IsSecure == true ? "VAC" : "";
    public string Locked => Info?.IsPrivate == true ? "●" : "";
    public string State => Info is null ? "Timed out" : "Online";
    public IPEndPoint GameEndpoint => new(Endpoint.Address, Info?.Extra.Port > 0 ? Info.Extra.Port : Endpoint.Port);
    public event PropertyChangedEventHandler? PropertyChanged;

    public void UpdateColumnValues(IEnumerable<ServerColumn> columns)
    {
        ColumnValues = columns.Where(c => c.Custom).ToDictionary(c => c.Id, c => c.GetValue(this));
        PropertyChanged?.Invoke(this, new(nameof(ColumnValues)));
    }
}

public static class ServerFilter
{
    public static bool Matches(ServerEntry row, BrowserTab tab)
    {
        if (tab.HideOffline && row.Info is null) return false;
        int players = tab.IncludeBots ? row.PlayerCount : row.HumanPlayers;
        if (players < tab.MinPlayers || (tab.HideEmpty && players == 0)) return false;
        if (tab.HideFull && row.Info is { } info && info.Players >= info.MaxPlayers) return false;
        if (tab.MaxPing > 0 && (row.Ping is null || row.Ping > tab.MaxPing)) return false;
        if (tab.SecureOnly && row.Info?.IsSecure != true) return false;
        if (!MatchText(row.Name + " " + row.Address + " " + row.Game, tab.Search)) return false;
        if (!MatchText(row.Map, tab.Map) || !MatchText(row.Info?.GameVersion ?? "", tab.Version)) return false;
        if (!string.IsNullOrWhiteSpace(tab.PlayerSearch) && !row.Players.Any(p => MatchText(p.Name, tab.PlayerSearch))) return false;
        if (!CountryQueryPlan.Matches(row.Country, tab.Countries)) return false;
        var tags = Tokens(row.Tags);
        if (Tokens(tab.IncludeTags).Any(t => !tags.Contains(t, StringComparer.OrdinalIgnoreCase))) return false;
        if (Tokens(tab.ExcludeTags).Any(t => tags.Contains(t, StringComparer.OrdinalIgnoreCase))) return false;
        return true;
    }

    public static string[] Tokens(string text) => text.Split([',', ';', '|', ' ', '\r', '\n'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
    public static bool MatchText(string text, string pattern)
    {
        if (string.IsNullOrWhiteSpace(pattern)) return true;
        if (!pattern.Contains('*') && !pattern.Contains('?')) return text.Contains(pattern, StringComparison.OrdinalIgnoreCase);
        string regex = "^" + Regex.Escape(pattern).Replace("\\*", ".*").Replace("\\?", ".") + "$";
        return Regex.IsMatch(text, regex, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);
    }
}
