using System.Globalization;
using System.Net;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Layout;
using ServerBrowser.Core;

namespace ServerBrowser.Desktop;

public partial class MainWindow
{
    private sealed record GameChoice(int Id, string Name) { public override string ToString() => Name; }
    private List<GameChoice> gameChoices = [];

    private void InitializeClassicControls()
    {
        gameChoices = new List<GameChoice>
        {
            new(282440, "Quake Live"), new(328070, "Reflex"), new(324810, "Toxikk"),
            new(730, "Counter-Strike 2 / CS:GO"), new(440, "Team Fortress 2"), new(550, "Left 4 Dead 2")
        }.Concat(Enum.GetValues<QueryMaster.Game>().Select(g => new GameChoice((int)g, g.ToString().Replace('_', ' '))))
            .DistinctBy(g => g.Id).OrderBy(g => g.Name, StringComparer.OrdinalIgnoreCase).ThenBy(g => g.Id).ToList();
        GameBox.ItemsSource = gameChoices.Select(g => g.Name).ToArray();
        MasterBox.ItemsSource = new[] { "hl2master.steampowered.com:27011", "<Steam Web API>" };
        AutoMinutesBox.Value = settings.AutoRefreshSeconds == 0 ? 2 : (decimal)settings.AutoRefreshSeconds / 60;
        AutoOffBox.IsChecked = settings.AutoRefreshSeconds == 0;
        AutoDiscoverBox.IsChecked = settings.AutoRefreshSeconds > 0 && settings.AutoDiscover;
        AutoStatusBox.IsChecked = settings.AutoRefreshSeconds > 0 && !settings.AutoDiscover;
        PauseInGameBox.IsChecked = settings.PauseWhileInGame;
        RefreshSelectedBox.IsChecked = settings.RefreshSelected;
        FavoritesOnTopBox.IsChecked = settings.FavoritesOnTop;
        AddressHiddenBox.IsChecked = settings.AddressMode == 0;
        AddressQueryBox.IsChecked = settings.AddressMode == 1;
        AddressGameBox.IsChecked = settings.AddressMode == 2;
        ShowFilterInfoBox.IsChecked = settings.ShowFilterInfo;
        ShowCountsBox.IsChecked = settings.ShowCounts;
        DoubleClickBox.IsChecked = settings.DoubleClickConnect;
        HideGhostsBox.IsChecked = settings.HideGhosts;
        MasterFilterInfo.IsVisible = ClientFilterInfo.IsVisible = settings.ShowFilterInfo;
        PreferencesPanel.IsVisible = settings.ShowGeneralPreferences;
        QuickFilterPanel.IsVisible = settings.ShowQuickFilter;
        QueryPanel.IsVisible = settings.ShowQueryPanel;
    }

    private void Preferences_Changed(object? sender, RoutedEventArgs e)
    {
        if (loading || AutoMinutesBox is null) return;
        settings.AutoDiscover = AutoDiscoverBox.IsChecked == true;
        settings.AutoRefreshSeconds = AutoOffBox.IsChecked == true ? 0 : (int)((AutoMinutesBox.Value ?? 2) * 60);
        settings.PauseWhileInGame = PauseInGameBox.IsChecked == true;
        settings.RefreshSelected = RefreshSelectedBox.IsChecked == true;
        settings.FavoritesOnTop = FavoritesOnTopBox.IsChecked == true;
        settings.AddressMode = AddressHiddenBox.IsChecked == true ? 0 : AddressGameBox.IsChecked == true ? 2 : 1;
        settings.ShowFilterInfo = ShowFilterInfoBox.IsChecked == true;
        settings.ShowCounts = ShowCountsBox.IsChecked == true;
        settings.DoubleClickConnect = DoubleClickBox.IsChecked == true;
        settings.HideGhosts = HideGhostsBox.IsChecked == true;
        MasterFilterInfo.IsVisible = ClientFilterInfo.IsVisible = settings.ShowFilterInfo;
        ApplyFilter(); SaveSettings();
    }
    private int ReadGameId()
    {
        var text = GameBox.Text?.Trim() ?? "";
        if (int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out int id)) return id;
        var game = gameChoices.FirstOrDefault(g => string.Equals(g.Name, text, StringComparison.OrdinalIgnoreCase));
        if (game is not null) return game.Id;
        throw new FormatException("Game: select a game from the list or enter a numeric Steam App ID.");
    }
    private void GameDropDown_Click(object? sender, RoutedEventArgs e)
    {
        GameBox.Focus();
        GameBox.IsDropDownOpen = !GameBox.IsDropDownOpen;
    }
    private void CloseTab_Click(object? sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is Button { DataContext: BrowserTab tab }) CloseTab(tab);
    }
    private void CloseTab(BrowserTab tab)
    {
        int index = settings.Tabs.IndexOf(tab);
        if (index < 0) return;
        CaptureInputs(false);
        var previous = active;
        settings.Tabs.RemoveAt(index);
        cache.Remove(tab);
        if (settings.Tabs.Count == 0)
            settings.Tabs.Add(new BrowserTab { Name = "Master Server", AppId = 0 });
        var next = previous is not null && !ReferenceEquals(previous, tab) ? previous : settings.Tabs[Math.Min(index, settings.Tabs.Count - 1)];
        settings.ActiveTab = settings.Tabs.IndexOf(next);
        ResetTabs(!ReferenceEquals(previous, next));
        SaveSettings();
    }
    private void Master_Changed(object? sender, SelectionChangedEventArgs e)
    {
        if (loading) return;
        SourceBox.SelectedIndex = MasterBox.SelectedItem as string == "<Steam Web API>" ? 1 : 0;
    }
    private void TogglePreferences_Click(object? sender, RoutedEventArgs e)
    {
        settings.ShowGeneralPreferences = PreferencesPanel.IsVisible = !PreferencesPanel.IsVisible; SaveSettings();
    }
    private void ToggleQuery_Click(object? sender, RoutedEventArgs e)
    {
        settings.ShowQueryPanel = QueryPanel.IsVisible = !QueryPanel.IsVisible; SaveSettings();
    }
    private void ToggleFilter_Click(object? sender, RoutedEventArgs e)
    {
        settings.ShowQuickFilter = QuickFilterPanel.IsVisible = !QuickFilterPanel.IsVisible; SaveSettings();
    }
    private void ToggleRcon_Click(object? sender, RoutedEventArgs e) => RconPanel.IsVisible = !RconPanel.IsVisible;
    private void RestoreLayout_Click(object? sender, RoutedEventArgs e)
    {
        PreferencesPanel.IsVisible = QueryPanel.IsVisible = QuickFilterPanel.IsVisible = true;
        settings.ShowGeneralPreferences = settings.ShowQuickFilter = settings.ShowQueryPanel = true;
        RconPanel.IsVisible = false; ContentGrid.ColumnDefinitions[2].Width = new GridLength(350);
        DetailsTabs.SelectedIndex = 0;
        SaveSettings();
    }
    private void Skin_Click(object? sender, RoutedEventArgs e)
    {
        settings.DarkTheme = !settings.DarkTheme; ApplyTheme(); SaveSettings();
    }
    private void LightTheme_Click(object? sender, RoutedEventArgs e)
    {
        settings.DarkTheme = false; ApplyTheme(); SaveSettings();
    }
    private void DarkTheme_Click(object? sender, RoutedEventArgs e)
    {
        settings.DarkTheme = true; ApplyTheme(); SaveSettings();
    }
    private void DetailsTab_Changed(object? sender, SelectionChangedEventArgs e)
    {
        if (loading || !ReferenceEquals(e.Source, DetailsTabs)) return;
        DetailTitle.Text = (DetailsTabs.SelectedItem as TabItem)?.Header?.ToString() ?? "Players";
    }
    private void FillDetailRows(ServerEntry row, string warning = "")
    {
        var values = new Dictionary<string, string>
        {
            ["Address"] = row.Address, ["Game Address"] = row.GameEndpoint.ToString(),
            ["Country"] = row.Country, ["Status"] = row.Error
        };
        if (row.Info is { } info)
        {
            foreach (var property in typeof(QueryMaster.ServerInfo).GetProperties().Where(p => p.Name is not ("Extra" or "EndPoint")))
                values[property.Name] = property.GetValue(info)?.ToString() ?? "";
            values["SteamID"] = info.Extra.SteamID.ToString();
            values["GameID"] = info.Extra.GameId.ToString();
            values["Keywords"] = info.Extra.Keywords ?? "";
            values["Spectator Port"] = info.Extra.SpecInfo.Port.ToString();
        }
        if (warning.Length > 0) values["Query status"] = warning;
        DetailItemsGrid.ItemsSource = values.OrderBy(p => p.Key).ToList();
    }
    private void NewMasterTab_Click(object? sender, RoutedEventArgs e) => AddClassicTab(ServerSource.Master, "Master Server Query");
    private void NewCustomTab_Click(object? sender, RoutedEventArgs e) => AddClassicTab(ServerSource.Custom, "Custom Server List");
    private void NewFavoritesTab_Click(object? sender, RoutedEventArgs e) => AddClassicTab(ServerSource.Favorites, "Favorites");
    private void AddClassicTab(ServerSource source, string name)
    {
        CaptureInputs(false);
        settings.Tabs.Add(new BrowserTab { Name = name, Source = source, AppId = source == ServerSource.Master ? 440 : 0 });
        settings.ActiveTab = settings.Tabs.Count - 1; ResetTabs(); SaveSettings();
    }

    private void AddFavorite_Click(object? sender, RoutedEventArgs e) => SetSelectedFavorites(true);
    private void Unfavorite_Click(object? sender, RoutedEventArgs e) => SetSelectedFavorites(false);
    private void SetSelectedFavorites(bool favorite)
    {
        foreach (var row in ServersGrid.SelectedItems.Cast<ServerEntry>().ToList())
        {
            if (favorite) settings.Favorites[row.Address] = row.Name; else settings.Favorites.Remove(row.Address);
            foreach (var cached in cache.Values.SelectMany(x => x).Where(x => x.Address == row.Address)) cached.Favorite = favorite;
        }
        ApplyFilter(); SaveSettings();
    }
    private async void CopySteam_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            string text = string.Join(Environment.NewLine, ServersGrid.SelectedItems.Cast<ServerEntry>().Select(r => SteamLauncher.ConnectionUri(r.GameEndpoint)));
            if (Clipboard is { } clipboard && text.Length > 0) await clipboard.SetTextAsync(text);
        }
        catch (Exception ex) { StatusText.Text = ex.Message; }
    }
    private void Spectate_Click(object? sender, RoutedEventArgs e)
    {
        if (ServersGrid.SelectedItem is not ServerEntry row) return;
        if (row.Info?.Extra.SpecInfo.Port is not > 0) { StatusText.Text = "This server does not advertise a spectator port."; return; }
        try { SteamLauncher.Connect(new IPEndPoint(row.Endpoint.Address, row.Info.Extra.SpecInfo.Port)); }
        catch (Exception ex) { StatusText.Text = ex.Message; }
    }
    private async void PasteAddresses_Click(object? sender, RoutedEventArgs e)
    {
        try { if (Clipboard is { } clipboard) await AddAddressText(await clipboard.TryGetTextAsync() ?? ""); }
        catch (Exception ex) { StatusText.Text = "Paste failed: " + ex.Message; }
    }
    private async void AddAddress_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        try { await AddAddressText(AddAddressBox.Text ?? ""); AddAddressBox.Text = ""; }
        catch (Exception ex) { StatusText.Text = ex.Message; }
    }
    private async Task AddAddressText(string text)
    {
        if (busy) { StatusText.Text = "Stop the current query before editing this list."; return; }
        var addresses = new List<string>(); int invalid = 0;
        foreach (var value in ServerFilter.Tokens(text).Take(20000))
        {
            try { addresses.Add((await Endpoints.ResolveAsync(value, cancellation: lifetime.Token)).ToString()); }
            catch (Exception) when (!lifetime.IsCancellationRequested) { invalid++; }
        }
        if (addresses.Count == 0) { StatusText.Text = "No valid server addresses found."; return; }
        if (active?.Source == ServerSource.Favorites)
            foreach (var address in addresses) settings.Favorites.TryAdd(address, address);
        else
        {
            if (active?.Source != ServerSource.Custom) AddClassicTab(ServerSource.Custom, "Custom Server List");
            active!.Addresses = active.Addresses.Concat(addresses).Distinct().ToList();
        }
        SaveSettings(); await QueryAsync(false);
        if (invalid > 0) StatusText.Text += $" Skipped {invalid} invalid addresses.";
    }
    private async void RefreshSelected_Click(object? sender, RoutedEventArgs e)
    {
        if (busy || active is null) return;
        var selected = ServersGrid.SelectedItems.Cast<ServerEntry>().ToList();
        if (selected.Count == 0) return;
        var tab = active;
        using var cancellation = new CancellationTokenSource();
        suppressDetailAutoload = false;
        queryCancellation = cancellation; busy = true; UpdateActionStates();
        try
        {
            foreach (var previous in selected)
            {
                bool queryRules = tab.Columns.Any(c => (c.Visible || c.Id == tab.SortColumn) && c.Field.StartsWith("Rule."));
                var row = await service.QueryAsync(previous.Endpoint, tab.AppId, settings.TimeoutMs, false, cancellation.Token, queryRules);
                var replacement = new ServerEntry { Endpoint = row.Endpoint, Info = row.Info, Error = row.Error,
                    Favorite = settings.Favorites.ContainsKey(row.Address), Country = previous.Country, CachedName = previous.Name,
                    Rules = queryRules ? row.Rules : previous.Rules };
                var rows = Rows(tab); int index = rows.FindIndex(r => r.Address == row.Address);
                if (index >= 0) rows[index] = replacement;
            }
            if (ReferenceEquals(active, tab)) { ApplyFilter(); StatusText.Text = $"Updated {selected.Count} selected server(s)."; }
        }
        catch (OperationCanceledException) { StatusText.Text = "Update stopped."; }
        catch (Exception ex) { StatusText.Text = ex.Message; }
        finally
        {
            queryCancellation = null; busy = false;
            if (!cancellation.IsCancellationRequested && ReferenceEquals(active, tab)) SynchronizeDetailsSelection(force: true);
            UpdateActionStates();
            if (ReferenceEquals(active, tab) && tab.AutoFitColumns) BestFitColumns();
        }
    }
    private void Rcon_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && RconButton.IsEnabled) { e.Handled = true; Rcon_Click(sender, e); }
    }
    private async void AdvancedFilter_Click(object? sender, RoutedEventArgs e)
    {
        if (active is null || !CaptureInputs()) return;
        var name = Dialogs.Text(active.Search); var map = Dialogs.Text(active.Map);
        var country = Dialogs.Text(active.Countries); var version = Dialogs.Text(active.Version);
        var secure = new CheckBox { Content = "Only VAC-secured servers", IsChecked = active.SecureOnly };
        var regions = new ComboBox { ItemsSource = new[] { "World", "Asia", "Europe", "US East", "US West", "South America", "Australia", "Middle East", "Africa" }, SelectedIndex = RegionBox.SelectedIndex };
        if (!await Dialogs.Form(this, "Filter editor",
            ("Name / address / game (supports * and ?)", name), ("Map", map), ("Country codes (comma-separated)", country),
            ("Version", version), ("Master query region", regions), ("Security", secure))) return;
        SearchBox.Text = name.Text; MapBox.Text = map.Text; CountriesBox.Text = country.Text; VersionBox.Text = version.Text;
        SecureOnlyBox.IsChecked = secure.IsChecked; RegionBox.SelectedIndex = regions.SelectedIndex;
        ApplyFilter_Click(sender, e);
    }
    private void Countries_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button button) return;
        var selected = ServerFilter.Tokens(CountriesBox.Text ?? "").ToHashSet(StringComparer.OrdinalIgnoreCase);
        var countries = CultureInfo.GetCultures(CultureTypes.SpecificCultures)
            .Select(c => { try { var region = new RegionInfo(c.Name); return (Code: region.TwoLetterISORegionName, Name: region.EnglishName); } catch { return (Code: "", Name: ""); } })
            .Where(c => c.Code.Length == 2).DistinctBy(c => c.Code).OrderBy(c => c.Code).ToList();
        var choices = new StackPanel { Spacing = 3, Margin = new Thickness(6) };
        var checks = new List<CheckBox>();
        var flagConverter = new CountryFlagConverter();
        foreach (var country in countries)
        {
            var label = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5 };
            label.Children.Add(new Image
            {
                Width = 16, Height = 16,
                Source = flagConverter.Convert(country.Code, typeof(Avalonia.Media.IImage), null, CultureInfo.CurrentCulture) as Avalonia.Media.IImage
            });
            label.Children.Add(new TextBlock { Text = country.Code + "  " + country.Name, VerticalAlignment = VerticalAlignment.Center });
            var check = new CheckBox { Content = label, Tag = country.Code, IsChecked = selected.Contains(country.Code) };
            checks.Add(check); choices.Children.Add(check);
        }
        var clear = new Button { Content = "Clear" }; var ok = new Button { Content = "OK", IsDefault = true };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(6) };
        buttons.Children.Add(clear); buttons.Children.Add(ok);
        var panel = new DockPanel { Width = 285, Height = 340 };
        DockPanel.SetDock(buttons, Dock.Bottom); panel.Children.Add(buttons);
        panel.Children.Add(new ScrollViewer { Content = choices });
        var flyout = new Flyout { Content = panel };
        clear.Click += (_, _) => { foreach (var check in checks) check.IsChecked = false; };
        ok.Click += (_, _) =>
        {
            CountriesBox.Text = string.Join(',', checks.Where(c => c.IsChecked == true).Select(c => c.Tag?.ToString()));
            flyout.Hide(); ApplyFilter_Click(button, e);
        };
        flyout.ShowAt(button);
    }
    private async void About_Click(object? sender, RoutedEventArgs e)
    {
        await Dialogs.Form(this, "Steam Server Browser 2.71 - Linux",
            ("Native Linux desktop port", new TextBlock
            {
                Text = "The original menu order, preferences, query panel, tabs, grid and right-hand details layout are retained.\n\n" +
                       "Uses Avalonia and .NET. DevExpress skins, floating docking, Windows key injection and Steamworks IPC are not available in this port.",
                TextWrapping = Avalonia.Media.TextWrapping.Wrap
            }));
    }
}
