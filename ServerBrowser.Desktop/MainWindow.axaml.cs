using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Notifications;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ServerBrowser.Core;

namespace ServerBrowser.Desktop;

public partial class MainWindow : Window
{
    private readonly SettingsStore store;
    private readonly ServerBrowserService service = new();
    private BrowserSettings settings;
    private readonly ObservableCollection<BrowserTab> tabItems = [];
    private readonly Dictionary<BrowserTab, List<ServerEntry>> cache = [];
    private BrowserTab? active;
    private CancellationTokenSource? queryCancellation;
    private CancellationTokenSource? detailCancellation;
    private readonly CancellationTokenSource lifetime = new();
    private readonly DispatcherTimer autoTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private WindowNotificationManager? notifications;
    private DateTime lastRefresh = DateTime.UtcNow;
    private bool loading;
    private bool busy;
    private string apiKey = "";
    private static readonly int[] Regions = [255, 4, 3, 0, 1, 2, 5, 6, 7];

    public MainWindow() : this(new SettingsStore()) { }
    public MainWindow(SettingsStore store)
    {
        this.store = store;
        settings = store.Load();
        settings.GeoIpPath = GeoIp.RelativePath(settings.GeoIpPath);
        apiKey = settings.SteamWebApiKey.Length > 0 ? settings.SteamWebApiKey : Environment.GetEnvironmentVariable("STEAM_WEB_API_KEY") ?? "";
        loading = true;
        InitializeComponent();
        Tabs.ItemsSource = tabItems;
        InitializeClassicControls();
        InitializeServerColumns();
        InitializePlayerGrid();
        ApplyTheme();
        ResetTabs();
        if (store.LoadWarning is { } warning) StatusText.Text = warning;
        Opened += (_, _) => notifications = new WindowNotificationManager(this) { MaxItems = 3 };
        Closing += (_, _) =>
        {
            CaptureInputs(false); SaveSettings();
            queryCancellation?.Cancel(); detailCancellation?.Cancel(); lifetime.Cancel(); autoTimer.Stop();
        };
        autoTimer.Tick += async (_, _) =>
        {
            if (settings.AutoRefreshSeconds > 0 && !busy && DateTime.UtcNow - lastRefresh >= TimeSpan.FromSeconds(settings.AutoRefreshSeconds))
            {
                if (settings.PauseWhileInGame && SteamProcessDetector.IsGameRunning()) { lastRefresh = DateTime.UtcNow; return; }
                await QueryAsync(settings.AutoDiscover);
            }
        };
        autoTimer.Start();
    }

    private void ApplyTheme()
    {
        var theme = settings.DarkTheme ? ThemeVariant.Dark : ThemeVariant.Light;
        if (Application.Current is { } app) app.RequestedThemeVariant = theme;
        RequestedThemeVariant = theme;
        LightThemeItem.IsChecked = !settings.DarkTheme;
        DarkThemeItem.IsChecked = settings.DarkTheme;
    }
    private List<ServerEntry> Rows(BrowserTab tab)
    {
        if (!cache.TryGetValue(tab, out var rows)) cache[tab] = rows = [];
        return rows;
    }
    private void ResetTabs(bool switchView = true)
    {
        loading = true;
        tabItems.Clear();
        foreach (var tab in settings.Tabs) tabItems.Add(tab);
        Tabs.SelectedIndex = Math.Clamp(settings.ActiveTab, 0, settings.Tabs.Count - 1);
        loading = false;
        if (switchView) SwitchTab();
    }
    private void Tab_Changed(object? sender, SelectionChangedEventArgs e)
    {
        if (loading || !ReferenceEquals(e.Source, Tabs)) return;
        CaptureInputs(false); CaptureColumnLayout(); SwitchTab();
    }
    private void SwitchTab()
    {
        suppressDetailAutoload = false;
        queryCancellation?.Cancel(); detailCancellation?.Cancel();
        active = Tabs.SelectedItem as BrowserTab;
        if (active is null) return;
        settings.ActiveTab = Tabs.SelectedIndex;
        loading = true;
        GameBox.SelectedItem = null;
        GameBox.Text = gameChoices.FirstOrDefault(g => g.Id == active.AppId)?.Name ?? active.AppId.ToString();
        GameBox.IsDropDownOpen = false;
        LimitBox.Text = active.Limit.ToString();
        SourceBox.SelectedIndex = active.UseWebApi ? 1 : 0;
        RegionBox.SelectedIndex = Math.Max(0, Array.IndexOf(Regions, active.Region));
        SearchBox.Text = active.Search; MapBox.Text = active.Map; CountriesBox.Text = active.Countries;
        MinPlayersBox.Text = active.MinPlayers.ToString(); MaxPingBox.Text = active.MaxPing.ToString();
        IncludeTagsBox.Text = active.IncludeTags; ExcludeTagsBox.Text = active.ExcludeTags;
        PlayerSearchBox.Text = active.PlayerSearch; VersionBox.Text = active.Version;
        HideEmptyBox.IsChecked = active.HideEmpty; HideFullBox.IsChecked = active.HideFull;
        IncludeBotsBox.IsChecked = active.IncludeBots; SecureOnlyBox.IsChecked = active.SecureOnly;
        HideOfflineBox.IsChecked = active.HideOffline;
        GetEmptyBox.IsChecked = !active.HideEmpty; GetFullBox.IsChecked = !active.HideFull;
        MasterBox.Text = active.UseWebApi ? "<Steam Web API>" : active.Master;
        ModBox.Text = active.GameDirectory;
        MasterIncludeTagsBox.Text = active.MasterIncludeTags; MasterExcludeTagsBox.Text = active.MasterExcludeTags;
        MasterQueryPanel.IsVisible = active.Source == ServerSource.Master;
        StaticListPanel.IsVisible = active.Source != ServerSource.Master;
        SourceBox.IsEnabled = RegionBox.IsEnabled = active.Source == ServerSource.Master;
        RestoreServerColumns();
        loading = false;
        ClearDetails();
        ApplyFilter();
    }
    private static int Number(TextBox box, string name, int min = 0, int max = int.MaxValue)
    {
        if (!int.TryParse(box.Text, out int value) || value < min || value > max)
            throw new FormatException($"{name} must be between {min} and {max}.");
        return value;
    }
    private bool CaptureInputs(bool showError = true)
    {
        if (loading || active is null) return true;
        try
        {
            int app = ReadGameId(), limit = Number(LimitBox, "Result limit", 1, 20000);
            int min = Number(MinPlayersBox, "Minimum players", 0, 65535), ping = Number(MaxPingBox, "Maximum ping", 0, 60000);
            active.AppId = app; active.Limit = limit; active.MinPlayers = min; active.MaxPing = ping;
            active.UseWebApi = string.Equals(MasterBox.Text?.Trim(), "<Steam Web API>", StringComparison.OrdinalIgnoreCase);
            if (!active.UseWebApi) active.Master = MasterBox.Text?.Trim() ?? "hl2master.steampowered.com:27011";
            active.GameDirectory = ModBox.Text ?? "";
            active.MasterIncludeTags = MasterIncludeTagsBox.Text ?? ""; active.MasterExcludeTags = MasterExcludeTagsBox.Text ?? "";
            active.Region = Regions[Math.Clamp(RegionBox.SelectedIndex, 0, Regions.Length - 1)];
            active.Search = SearchBox.Text ?? ""; active.Map = MapBox.Text ?? ""; active.Countries = CountriesBox.Text ?? "";
            active.IncludeTags = IncludeTagsBox.Text ?? ""; active.ExcludeTags = ExcludeTagsBox.Text ?? "";
            active.PlayerSearch = PlayerSearchBox.Text ?? ""; active.Version = VersionBox.Text ?? "";
            active.HideEmpty = GetEmptyBox.IsChecked != true; active.HideFull = GetFullBox.IsChecked != true;
            active.IncludeBots = IncludeBotsBox.IsChecked == true; active.SecureOnly = SecureOnlyBox.IsChecked == true;
            active.HideOffline = HideOfflineBox.IsChecked == true;
            return true;
        }
        catch (FormatException ex) { if (showError) StatusText.Text = ex.Message; return false; }
    }
    private bool SaveSettings()
    {
        try { CaptureColumnLayout(); CapturePlayerWidths(); settings.SteamWebApiKey = apiKey; store.Save(settings); return true; }
        catch (Exception ex) { StatusText.Text = "Could not save settings: " + ex.Message; return false; }
    }
    private void Filter_Changed(object? sender, RoutedEventArgs e)
    {
        if (loading || active is null) return;
        active.Search = SearchBox.Text ?? ""; active.HideOffline = HideOfflineBox.IsChecked == true;
        ApplyFilter();
    }
    private void ApplyFilter_Click(object? sender, RoutedEventArgs e)
    {
        if (CaptureInputs()) { ApplyFilter(); SaveSettings(); }
        if (active?.Countries.Length > 0 && string.IsNullOrEmpty(settings.GeoIpPath))
            StatusText.Text = "Country filtering requires an offline MMDB database. Select it in Options.";
    }
    private void ApplyFilter()
    {
        if (active is null) return;
        var selected = ServersGrid.SelectedItem as ServerEntry;
        var all = Rows(active);
        foreach (var row in all) row.DisplayGamePort = settings.AddressMode == 2;
        ApplyColumnVisibility();
        var visible = all.Where(r => (active.Source != ServerSource.Favorites || r.Favorite) && ServerFilter.Matches(r, active))
            .OrderByDescending(r => settings.FavoritesOnTop && r.Favorite).ThenByDescending(r => r.HumanPlayers).ThenByDescending(r => r.Bots).ThenBy(r => r.Ping ?? long.MaxValue).ToList();
        BindServerRows(visible, selected);
        CountText.Text = $"Servers: {visible.Count} / {all.Count}     Players: {visible.Sum(r => r.HumanPlayers)}     Bots: {visible.Sum(r => r.Bots)}";
        CountText.IsVisible = settings.ShowCounts;
    }

    private async void Query_Click(object? sender, RoutedEventArgs e) => await QueryAsync(true);
    private async void Refresh_Click(object? sender, RoutedEventArgs e) => await QueryAsync(false);
    private void Stop_Click(object? sender, RoutedEventArgs e)
    {
        suppressDetailAutoload = true;
        queryCancellation?.Cancel(); detailCancellation?.Cancel(); UpdateActionStates();
    }
    private async Task QueryAsync(bool discover)
    {
        if (busy || active is null || !CaptureInputs()) return;
        var tab = active;
        if (!discover && tab.Source == ServerSource.Master && Rows(tab).Count == 0)
        {
            StatusText.Text = "No existing servers to update. Use Find Servers first.";
            return;
        }
        // Snapshot options so tab edits during an in-flight query cannot change its scope.
        suppressDetailAutoload = false;
        var options = JsonSerializer.Deserialize<BrowserTab>(JsonSerializer.Serialize(tab))!;
        var cancellation = new CancellationTokenSource();
        queryCancellation = cancellation;
        var token = cancellation.Token;
        int timeoutMs = settings.TimeoutMs, concurrency = settings.Concurrency;
        var favorites = new Dictionary<string, string>(settings.Favorites);
        var oldRows = Rows(tab).ToDictionary(r => r.Address);
        var pending = new ConcurrentQueue<ServerEntry>();
        var working = Rows(tab);
        int total = 0, completed = 0, failures = 0;
        string? warning = null;
        bool queryFinished = false;
        int flushPosted = 0;
        void Flush()
        {
            bool changed = false;
            while (pending.TryDequeue(out var entry))
            {
                entry.Favorite = settings.Favorites.ContainsKey(entry.Address);
                int index = working.FindIndex(r => r.Address == entry.Address);
                if (index >= 0) working[index] = entry; else working.Add(entry);
                changed = true;
            }
            if (ReferenceEquals(active, tab))
            {
                if (changed) ApplyFilter();
                Progress.Value = total == 0 ? 0 : (double)completed / total * 100;
                if (total > 0) StatusText.Text = $"Updating {completed} / {total}; {failures} timed out.";
            }
        }
        void PostFlush()
        {
            if (Interlocked.Exchange(ref flushPosted, 1) != 0) return;
            Dispatcher.UIThread.Post(() =>
            {
                Interlocked.Exchange(ref flushPosted, 0);
                if (!queryFinished && !lifetime.IsCancellationRequested) Flush();
            }, DispatcherPriority.Background);
        }
        busy = true; UpdateActionStates();
        Progress.IsIndeterminate = true;
        StatusText.Text = discover && tab.Source == ServerSource.Master ? "Finding servers..." : "Updating existing servers...";
        SaveSettings();
        try
        {
            List<IPEndPoint> endpoints;
            if (options.Source == ServerSource.Favorites || options.Source == ServerSource.Custom)
            {
                var addresses = options.Source == ServerSource.Favorites ? favorites.Keys.ToList() : options.Addresses;
                endpoints = [];
                int invalid = 0;
                foreach (var address in addresses)
                {
                    token.ThrowIfCancellationRequested();
                    try { endpoints.Add(await Endpoints.ResolveAsync(address, cancellation: token)); }
                    catch (Exception) when (!token.IsCancellationRequested) { invalid++; }
                }
                if (invalid > 0) warning = $"{invalid} addresses could not be resolved.";
            }
            else if (discover)
            {
                var result = await service.DiscoverAsync(options, apiKey, token);
                endpoints = result.Addresses.ToList(); warning = result.Warning;
            }
            else endpoints = oldRows.Values.Select(r => r.Endpoint).ToList();
            endpoints = endpoints.Distinct().ToList();
            if (discover && options.Source == ServerSource.Master) endpoints = endpoints.Take(options.Limit).ToList();
            total = endpoints.Count;
            token.ThrowIfCancellationRequested();
            using var geo = new GeoIp(settings.GeoIpPath);
            if (discover && options.Source == ServerSource.Master) working.Clear();
            else
            {
                foreach (var ep in endpoints)
                    if (!working.Any(r => r.Endpoint.Equals(ep))) working.Add(new ServerEntry { Endpoint = ep, CachedName = favorites.GetValueOrDefault(ep.ToString(), "") });
            }
            Progress.IsIndeterminate = false;
            await Parallel.ForEachAsync(endpoints, new ParallelOptions { MaxDegreeOfParallelism = concurrency, CancellationToken = token }, async (ep, ct) =>
            {
                bool queryRules = options.Columns.Any(c => (c.Visible || c.Id == options.SortColumn) && c.Field.StartsWith("Rule."));
                var row = await service.QueryAsync(ep, options.AppId, timeoutMs, !string.IsNullOrWhiteSpace(options.PlayerSearch), ct, queryRules);
                string country = "";
                try { country = geo.Lookup(ep.Address); }
                catch (Exception ex) { Interlocked.CompareExchange(ref warning, "Country lookup failed: " + ex.Message, null); }
                ct.ThrowIfCancellationRequested();
                oldRows.TryGetValue(ep.ToString(), out var old);
                pending.Enqueue(new ServerEntry
                {
                    Endpoint = ep, Info = row.Info, Error = row.Error, Players = row.Players, PlayersQueried = row.PlayersQueried,
                    Rules = queryRules ? row.Rules : old?.Rules ?? [],
                    Country = country, Favorite = favorites.ContainsKey(ep.ToString()),
                    CachedName = favorites.GetValueOrDefault(ep.ToString()) ?? old?.Name ?? ""
                });
                if (row.Info is null) Interlocked.Increment(ref failures);
                Interlocked.Increment(ref completed);
                PostFlush();
            });
            Flush();
            foreach (var row in working.Where(r => settings.Favorites.ContainsKey(r.Address) && r.Info is not null))
                settings.Favorites[row.Address] = row.Name;
            SaveSettings();
            if (ReferenceEquals(active, tab))
            {
                ApplyFilter();
                StatusText.Text = $"Complete: {total - failures} online, {failures} timed out." + (warning is null ? "" : " " + warning);
                if (AlarmBox.IsChecked == true && working.Any(r => r.Info is not null && ServerFilter.Matches(r, options)))
                    notifications?.Show(new Notification("Matching servers found", CountText.Text ?? "", NotificationType.Information));
            }
        }
        catch (Exception) when (token.IsCancellationRequested)
        {
            Flush();
            if (ReferenceEquals(active, tab)) StatusText.Text = "Update stopped. Existing servers and completed results were kept.";
        }
        catch (Exception ex) { if (ReferenceEquals(active, tab)) StatusText.Text = "Query failed: " + ex.Message; }
        finally
        {
            queryFinished = true; busy = false; Progress.IsIndeterminate = false;
            if (ReferenceEquals(queryCancellation, cancellation)) queryCancellation = null;
            if (!token.IsCancellationRequested && ReferenceEquals(active, tab)) SynchronizeDetailsSelection(force: true);
            cancellation.Dispose(); lastRefresh = DateTime.UtcNow;
            UpdateActionStates();
            if (ReferenceEquals(active, tab) && tab.AutoFitColumns) BestFitColumns();
        }
    }

    private async void Connect_Click(object? sender, RoutedEventArgs e) => await ConnectSelected();
    private async void Server_DoubleTapped(object? sender, TappedEventArgs e)
    {
        if (settings.DoubleClickConnect && e.Source is Control control && control.FindAncestorOfType<DataGridRow>() is not null)
            await ConnectSelected();
    }
    private async Task ConnectSelected()
    {
        if (ServersGrid.SelectedItem is not ServerEntry row) { StatusText.Text = "Select a server first."; return; }
        try
        {
            string password = "";
            if (row.Info?.IsPrivate == true)
            {
                var input = new TextBox { PasswordChar = '●' };
                if (!await Dialogs.Form(this, "Server password", ("Password (not saved)", input))) return;
                password = input.Text ?? "";
            }
            SteamLauncher.Connect(row.GameEndpoint, password);
            StatusText.Text = "Requested Steam connection to " + row.GameEndpoint;
        }
        catch (Exception ex) { StatusText.Text = "Could not start Steam: " + ex.Message; }
    }
    private void Favorite_Click(object? sender, RoutedEventArgs e)
    {
        var rows = ServersGrid.SelectedItems.Cast<ServerEntry>().ToList();
        foreach (var row in rows)
        {
            bool newValue = !row.Favorite;
            if (newValue) settings.Favorites[row.Address] = row.Name; else settings.Favorites.Remove(row.Address);
            foreach (var cached in cache.Values.SelectMany(x => x).Where(x => x.Address == row.Address)) cached.Favorite = newValue;
        }
        ApplyFilter(); SaveSettings();
    }
    private async void Copy_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            var text = string.Join(Environment.NewLine, ServersGrid.SelectedItems.Cast<ServerEntry>().Select(r => r.GameEndpoint.ToString()));
            if (Clipboard is { } clipboard && text.Length > 0) await clipboard.SetTextAsync(text);
        }
        catch (Exception ex) { StatusText.Text = "Copy failed: " + ex.Message; }
    }
    private void Remove_Click(object? sender, RoutedEventArgs e)
    {
        if (busy) { StatusText.Text = "Stop the current update before removing servers."; return; }
        if (active is null) return;
        var selected = ServersGrid.SelectedItems.Cast<ServerEntry>().Select(r => r.Address).ToHashSet();
        Rows(active).RemoveAll(r => selected.Contains(r.Address));
        if (active.Source == ServerSource.Custom) active.Addresses.RemoveAll(selected.Contains);
        if (active.Source == ServerSource.Favorites)
            foreach (var address in selected)
            {
                settings.Favorites.Remove(address);
                foreach (var row in cache.Values.SelectMany(x => x).Where(x => x.Address == address)) row.Favorite = false;
            }
        ApplyFilter(); SaveSettings();
    }
    private async void NewTab_Click(object? sender, RoutedEventArgs e)
    {
        var name = Dialogs.Text("New tab"); var app = Dialogs.Text("440");
        var source = new ComboBox { ItemsSource = new[] { "Master server", "Favorites", "Custom list" }, SelectedIndex = 0 };
        if (!await Dialogs.Form(this, "New tab", ("Name", name), ("App ID (0 for any game)", app), ("Type", source))) return;
        try
        {
            var tab = new BrowserTab { Name = name.Text ?? "New tab", AppId = Number(app, "App ID"), Source = (ServerSource)source.SelectedIndex };
            CaptureInputs(false); settings.Tabs.Add(tab); settings.ActiveTab = settings.Tabs.Count - 1; ResetTabs(); SaveSettings();
        }
        catch (Exception ex) { StatusText.Text = ex.Message; }
    }
    private async void TabSettings_Click(object? sender, RoutedEventArgs e)
    {
        if (active is null) return;
        CaptureInputs(false);
        var tab = active;
        var name = Dialogs.Text(tab.Name); var master = Dialogs.Text(tab.Master); var folder = Dialogs.Text(tab.GameDirectory);
        var action = new ComboBox { ItemsSource = new[] { "Save changes", "Duplicate tab as a server snapshot", "Delete tab" }, SelectedIndex = 0 };
        if (!await Dialogs.Form(this, "Tab settings", ("Name", name), ("UDP master server (hostname:port)", master), ("Game directory filter (optional)", folder), ("Action", action))) return;
        queryCancellation?.Cancel();
        if (action.SelectedIndex == 2)
        {
            CloseTab(tab);
            return;
        }
        else if (action.SelectedIndex == 1)
        {
            var snapshot = JsonSerializer.Deserialize<BrowserTab>(JsonSerializer.Serialize(tab))!;
            snapshot.Name = (name.Text ?? tab.Name) + " · Snapshot";
            snapshot.Source = ServerSource.Custom; snapshot.Addresses = Rows(tab).Select(r => r.Address).ToList();
            settings.Tabs.Add(snapshot); cache[snapshot] = [.. Rows(tab)]; settings.ActiveTab = settings.Tabs.Count - 1;
        }
        else { tab.Name = name.Text ?? tab.Name; tab.Master = master.Text ?? ""; tab.GameDirectory = folder.Text ?? ""; }
        ResetTabs(); SaveSettings();
    }
    private async void AddAddresses_Click(object? sender, RoutedEventArgs e)
    {
        var input = Dialogs.Text("", true);
        if (!await Dialogs.Form(this, "Add servers", ("Enter one IP:query-port or hostname:port per line, or an HTTP(S) plain-text list URL.", input))) return;
        try
        {
            string text = input.Text?.Trim() ?? "";
            if (text.StartsWith("http://") || text.StartsWith("https://")) text = await service.DownloadListAsync(text, lifetime.Token);
            var addresses = new List<string>(); int invalid = 0;
            foreach (var value in ServerFilter.Tokens(text).Take(20000))
            {
                try { addresses.Add((await Endpoints.ResolveAsync(value, cancellation: lifetime.Token)).ToString()); }
                catch (Exception) when (!lifetime.IsCancellationRequested) { invalid++; }
            }
            if (addresses.Count == 0) { StatusText.Text = "No valid addresses."; return; }
            if (active?.Source == ServerSource.Favorites)
                foreach (var address in addresses) settings.Favorites.TryAdd(address, address);
            else
            {
                if (active?.Source != ServerSource.Custom)
                {
                    CaptureInputs(false);
                    settings.Tabs.Add(new BrowserTab { Name = "Imported list", Source = ServerSource.Custom, AppId = 0 });
                    settings.ActiveTab = settings.Tabs.Count - 1; ResetTabs();
                }
                active!.Addresses = active.Addresses.Concat(addresses).Distinct().ToList();
            }
            SaveSettings(); await QueryAsync(false);
            if (invalid > 0) StatusText.Text += $" Skipped {invalid} invalid addresses.";
        }
        catch (Exception ex) { StatusText.Text = "Import failed: " + ex.Message; }
    }
    private async void ImportIni_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Select Windows ServerBrowser.ini",
                FileTypeFilter = [new FilePickerFileType("INI configuration") { Patterns = ["*.ini"] }]
            });
            if (files.Count == 0) return;
            using var stream = await files[0].OpenReadAsync();
            using var reader = new StreamReader(stream);
            var imported = SettingsStore.ImportWindowsIni(await reader.ReadToEndAsync());
            queryCancellation?.Cancel(); CaptureInputs(false);
            foreach (var pair in imported.Favorites) settings.Favorites[pair.Key] = pair.Value;
            settings.Tabs.AddRange(imported.Tabs);
            settings.ActiveTab = settings.Tabs.Count - imported.Tabs.Count;
            ResetTabs(); SaveSettings();
            StatusText.Text = $"Imported {imported.Tabs.Count} tabs and {imported.Favorites.Count} favorites. DevExpress layouts and Windows hotkeys are not imported.";
        }
        catch (Exception ex) { StatusText.Text = "Import failed: " + ex.Message; }
    }
    private async void Export_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions { Title = "Export current filtered list", SuggestedFileName = "servers.csv", DefaultExtension = "csv" });
            if (file is null) return;
            static string Csv(string text) => "\"" + (text.Length > 0 && "=+-@".Contains(text[0]) ? "'" : "") + text.Replace("\"", "\"\"") + "\"";
            using var stream = await file.OpenWriteAsync();
            using var writer = new StreamWriter(stream, new UTF8Encoding(true));
            stream.SetLength(0);
            await writer.WriteLineAsync("Name,QueryAddress,GameAddress,Map,Players,MaxPlayers,Bots,Ping,Country,Tags");
            foreach (var row in ServersGrid.ItemsSource.Cast<ServerEntry>())
                await writer.WriteLineAsync(string.Join(',', new[] { row.Name, row.Address, row.GameEndpoint.ToString(), row.Map, row.PlayerCount.ToString(),
                    row.Info?.MaxPlayers.ToString() ?? "", row.Bots.ToString(), row.Ping?.ToString() ?? "", row.Country, row.Tags }.Select(Csv)));
            StatusText.Text = "Exported the current filtered list.";
        }
        catch (Exception ex) { StatusText.Text = "Export failed: " + ex.Message; }
    }
    private async void Settings_Click(object? sender, RoutedEventArgs e)
    {
        var key = new TextBox { Name = "ApiKeyInput", Text = apiKey, PasswordChar = '●' };
        var geo = Dialogs.Text(settings.GeoIpPath); geo.Name = "GeoIpPathInput";
        var choose = new Button { Content = "Choose MMDB file" };
        choose.Click += async (_, _) =>
        {
            try
            {
                var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions { Title = "Offline country database", FileTypeFilter = [new FilePickerFileType("MMDB") { Patterns = ["*.mmdb"] }] });
                if (files.Count > 0 && files[0].TryGetLocalPath() is { } path) geo.Text = GeoIp.RelativePath(path);
            }
            catch (Exception ex) { StatusText.Text = ex.Message; }
        };
        var geobox = new StackPanel { Spacing = 6 }; geobox.Children.Add(geo); geobox.Children.Add(choose);
        var timeout = Dialogs.Text(settings.TimeoutMs.ToString()); var concurrency = Dialogs.Text(settings.Concurrency.ToString());
        var refresh = Dialogs.Text(settings.AutoRefreshSeconds.ToString()); var dark = new CheckBox { IsChecked = settings.DarkTheme, Content = "Dark theme" };
        if (!await Dialogs.Form(this, "Settings",
            ("Steam Web API Key (saved on this device)", key),
            ("MMDB path, relative to the application folder (e.g. GeoIp/ipinfo_lite.mmdb)", geobox),
            ("Query timeout (milliseconds, 200-10000)", timeout), ("Concurrent queries (1-64)", concurrency),
            ("Auto-update interval (seconds; 0 = off, minimum 15)", refresh), ("Appearance", dark),
            ("Configuration location", new TextBlock { Text = store.FilePath, TextWrapping = Avalonia.Media.TextWrapping.Wrap }))) return;
        try
        {
            int timeoutValue = Number(timeout, "Timeout", 200, 10000), concurrencyValue = Number(concurrency, "Concurrency", 1, 64);
            int refreshValue = Number(refresh, "Auto update", 0, 3600);
            if (refreshValue is > 0 and < 15) throw new FormatException("Auto-update interval must be at least 15 seconds.");
            using var validate = new GeoIp(geo.Text ?? "");
            settings.TimeoutMs = timeoutValue; settings.Concurrency = concurrencyValue; settings.AutoRefreshSeconds = refreshValue;
            settings.GeoIpPath = GeoIp.RelativePath(geo.Text ?? ""); settings.DarkTheme = dark.IsChecked == true;
            loading = true;
            AutoMinutesBox.Value = refreshValue == 0 ? 2 : (decimal)refreshValue / 60;
            AutoOffBox.IsChecked = refreshValue == 0;
            AutoDiscoverBox.IsChecked = refreshValue > 0 && settings.AutoDiscover;
            AutoStatusBox.IsChecked = refreshValue > 0 && !settings.AutoDiscover;
            loading = false;
            apiKey = key.Text?.Trim() ?? ""; ApplyTheme();
            if (SaveSettings()) StatusText.Text = "Settings saved.";
        }
        catch (Exception ex) { StatusText.Text = "Settings were not saved: " + ex.Message; }
    }
    private async void Rcon_Click(object? sender, RoutedEventArgs e)
    {
        if (ServersGrid.SelectedItem is not ServerEntry row) { StatusText.Text = "Select a server first."; return; }
        if (string.IsNullOrWhiteSpace(RconCommandBox.Text)) return;
        RconButton.IsEnabled = false;
        try
        {
            int port = Number(RconPortBox, "RCON port", 1, 65535);
            string result = await SourceRcon.SendAsync(new IPEndPoint(row.Endpoint.Address, port), RconPasswordBox.Text ?? "", RconCommandBox.Text, lifetime.Token);
            if (ReferenceEquals(ServersGrid.SelectedItem, row)) RconOutputBox.Text = result;
        }
        catch (Exception ex) { RconOutputBox.Text = "RCON failed: " + ex.Message; }
        finally { RconButton.IsEnabled = true; }
    }

    // Used by the headless UI smoke test; it never fabricates production results.
    public void LoadPreviewRows(IEnumerable<ServerEntry> rows)
    {
        if (active is null) return;
        cache[active] = rows.ToList(); ApplyFilter();
    }
}
