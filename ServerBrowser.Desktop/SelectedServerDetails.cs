using Avalonia.Controls;
using Avalonia.Threading;
using ServerBrowser.Core;

namespace ServerBrowser.Desktop;

public partial class MainWindow
{
    private BrowserTab? detailTab;
    private string? detailAddress;
    private ServerDetails? displayedDetails;
    private bool suppressDetailAutoload;

    private void Server_Changed(object? sender, SelectionChangedEventArgs e)
    {
        if (changingServerSelection || changingColumns || loading || !ReferenceEquals(e.Source, ServersGrid)) return;
        suppressDetailAutoload = false;
        SynchronizeDetailsSelection(refreshInfo: settings.RefreshSelected);
        UpdateActionStates();
    }

    private void ClearDetails()
    {
        detailCancellation?.Cancel();
        detailTab = null; detailAddress = null; displayedDetails = null;
        PlayersGrid.ItemsSource = null; RulesGrid.ItemsSource = null; DetailItemsGrid.ItemsSource = null;
        PlayersStatus.Text = "Select a server to view players.";
        RulesStatus.Text = "Select a server to view rules.";
        DetailsBox.Text = ""; RconPasswordBox.Text = ""; RconOutputBox.Text = "";
        UpdateActionStates();
    }

    private void SynchronizeDetailsSelection(bool force = false, bool refreshInfo = false)
    {
        if (lifetime.IsCancellationRequested || loading || changingColumns) return;
        if (active is not { } tab || ServersGrid.SelectedItem is not ServerEntry row) { ClearDetails(); return; }
        if (!CountryQueryPlan.Matches(row.Country, tab.Countries)) { ClearDetails(); return; }
        bool sameServer = ReferenceEquals(tab, detailTab) && row.Address == detailAddress;
        FillDetailRows(row, displayedDetails?.Warning ?? "");
        if (sameServer && !force)
        {
            if (displayedDetails is { } cached) RenderDetails(cached);
            return;
        }
        if (suppressDetailAutoload) return;
        detailCancellation?.Cancel();
        detailTab = tab; detailAddress = row.Address; displayedDetails = null;
        if (!sameServer) { RconPasswordBox.Text = ""; RconOutputBox.Text = ""; }
        RconPortBox.Text = row.GameEndpoint.Port.ToString();
        SetPlayerRows(row.Players);
        RulesGrid.ItemsSource = row.Rules;
        PlayersStatus.Text = "Loading players...";
        RulesStatus.Text = "Loading rules...";
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        detailCancellation = cancellation;
        UpdateActionStates();
        _ = LoadSelectedDetailsAsync(row, tab, cancellation, refreshInfo);
    }

    private void Server_Tapped(object? sender, Avalonia.Input.TappedEventArgs e)
    {
        if (!suppressDetailAutoload || e.Source is not Control control) return;
        if (!Avalonia.VisualTree.VisualExtensions.GetVisualAncestors(control).Any(a => a is DataGridRow)) return;
        suppressDetailAutoload = false;
        SynchronizeDetailsSelection(force: true, refreshInfo: settings.RefreshSelected);
    }

    private bool IsCurrentDetailRequest(BrowserTab tab, string address, CancellationTokenSource request) =>
        !request.IsCancellationRequested && ReferenceEquals(request, detailCancellation) &&
        ReferenceEquals(active, tab) && ServersGrid.SelectedItem is ServerEntry selected && selected.Address == address;

    private async Task LoadSelectedDetailsAsync(ServerEntry row, BrowserTab tab, CancellationTokenSource cancellation, bool refreshInfo)
    {
        var token = cancellation.Token;
        void Apply(ServerDetails result)
        {
            if (!IsCurrentDetailRequest(tab, row.Address, cancellation)) return;
            if (displayedDetails?.RulesComplete == true && !result.RulesComplete) return;
            displayedDetails = result;
            var current = (ServerEntry)ServersGrid.SelectedItem!;
            if (result.PlayersSucceeded) { current.Players = result.Players; current.PlayersQueried = true; }
            if (result.RulesSucceeded) current.Rules = result.Rules;
            current.UpdateColumnValues(tab.Columns);
            RenderDetails(result);
            FillDetailRows(current, result.Warning);
            if (tab.Columns.Any(c => c.Id == tab.SortColumn && c.Field.StartsWith("Rule.")))
            {
                changingServerSelection = true;
                try { serverView?.Refresh(); ServersGrid.SelectedItem = current; }
                finally { changingServerSelection = false; }
            }
        }
        async Task ReadDetails()
        {
            var result = await service.DetailsAsync(row, settings.TimeoutMs, token,
                partial => Dispatcher.UIThread.Post(() => { if (!token.IsCancellationRequested) Apply(partial); }));
            Apply(result);
        }
        async Task ReadInfo()
        {
            if (!refreshInfo) return;
            var updated = await service.QueryAsync(row.Endpoint, tab.AppId, settings.TimeoutMs, false, token);
            if (!IsCurrentDetailRequest(tab, row.Address, cancellation) || updated.Info is null) return;
            var rows = Rows(tab);
            int index = rows.FindIndex(r => r.Address == row.Address);
            if (index < 0) return;
            var previous = rows[index];
            rows[index] = new ServerEntry { Endpoint = row.Endpoint, Info = updated.Info, Error = updated.Error,
                Country = previous.Country, Favorite = previous.Favorite, CachedName = previous.Name,
                Players = previous.Players, PlayersQueried = previous.PlayersQueried, Rules = previous.Rules };
            ApplyFilter();
        }
        try { await Task.WhenAll(ReadDetails(), ReadInfo()); }
        catch (Exception) when (token.IsCancellationRequested)
        {
            if (ReferenceEquals(detailCancellation, cancellation) && detailAddress == row.Address)
            {
                if (displayedDetails?.PlayersComplete != true) PlayersStatus.Text = "Player query stopped.";
                if (displayedDetails?.RulesComplete != true) RulesStatus.Text = "Rules query stopped.";
            }
        }
        catch (Exception ex)
        {
            if (IsCurrentDetailRequest(tab, row.Address, cancellation))
            {
                PlayersStatus.Text = "Player query failed.";
                RulesStatus.Text = "Rules query failed.";
                FillDetailRows((ServerEntry)ServersGrid.SelectedItem!, ex.Message);
            }
        }
        finally
        {
            if (ReferenceEquals(detailCancellation, cancellation)) detailCancellation = null;
            cancellation.Dispose();
            UpdateActionStates();
        }
    }

    private void RenderDetails(ServerDetails details)
    {
        if (details.PlayersComplete)
        {
            var players = details.Players.Where(p => !settings.HideGhosts || !string.IsNullOrWhiteSpace(p.Name)).ToList();
            SetPlayerRows(players);
            PlayersStatus.Text = !details.PlayersSucceeded ? "Player query failed or timed out."
                : details.Players.Count == 0 ? "No players reported."
                : players.Count != details.Players.Count ? $"{players.Count} visible / {details.Players.Count} returned (unnamed players hidden)."
                : $"{players.Count} player(s)";
        }
        if (details.RulesComplete)
        {
            RulesGrid.ItemsSource = details.Rules;
            RulesStatus.Text = !details.RulesSucceeded ? "Rules query failed or timed out."
                : details.Rules.Count == 0 ? "No rules reported." : $"{details.Rules.Count} rule(s)";
        }
    }

    private void UpdateActionStates()
    {
        if (StopButton is null || RefreshToolButton is null) return;
        bool available = !lifetime.IsCancellationRequested && active is not null;
        bool canFind = available && !busy;
        bool hasServers = active is { } tab && (tab.Source switch
        {
            ServerSource.Favorites => settings.Favorites.Count > 0,
            ServerSource.Custom => tab.Addresses.Count > 0 || Rows(tab).Count > 0,
            _ => Rows(tab).Count > 0
        });
        bool canRefresh = canFind && hasServers;
        bool canStop = (queryCancellation is { IsCancellationRequested: false } || detailCancellation is { IsCancellationRequested: false }) && !lifetime.IsCancellationRequested;
        foreach (var control in new Control[] { QueryButton, QueryToolButton, FindMenuItem }) control.IsEnabled = canFind;
        foreach (var control in new Control[] { RefreshButton, RefreshToolButton, StaticRefreshButton, UpdateMenuItem }) control.IsEnabled = canRefresh;
        foreach (var control in new Control[] { StopButton, StopMenuItem }) control.IsEnabled = canStop;
        bool canRefreshSelected = canFind && ServersGrid.SelectedItems.OfType<ServerEntry>().Any();
        SelectedUpdateMenuItem.IsEnabled = SelectedUpdateContextItem.IsEnabled = canRefreshSelected;
    }
}
