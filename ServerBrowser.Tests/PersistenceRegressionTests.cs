using System.ComponentModel;
using System.Net;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using QueryMaster;
using ServerBrowser.Core;
using ServerBrowser.Desktop;

namespace ServerBrowser.Tests;

internal static partial class Program
{
    private static void RunPersistenceRegressions(string[] args)
    {
        Run("API key persists from English settings dialog across restart", () =>
        {
            var store = TempStore();
            var window = new MainWindow(store); window.Show(); PumpUi();
            var dialog = OpenDialog(window, "SettingsMenuItem");
            CapturePreview(dialog, args, "--screenshot-settings=");
            Check(dialog.GetVisualDescendants().OfType<TextBlock>().All(t => !(t.Text ?? "").Any(c => c is >= '\u4e00' and <= '\u9fff')),
                "Settings still contains Chinese labels");
            Named<TextBox>(dialog, "ApiKeyInput").Text = "test-persistent-api-key";
            AcceptDialog(dialog);
            PumpUntil(() => store.Load().SteamWebApiKey == "test-persistent-api-key");
            window.Close(); PumpUi();
            var restored = new MainWindow(store); restored.Show(); PumpUi();
            var reopened = OpenDialog(restored, "SettingsMenuItem");
            Equal("test-persistent-api-key", Named<TextBox>(reopened, "ApiKeyInput").Text);
            Named<Button>(reopened, "DialogCancel").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            PumpUi(); restored.Close(); PumpUi();
            return Task.CompletedTask;
        });
        Run("One default master tab; legacy duplicates migrate without losing favorites or custom lists", () =>
        {
            Equal(1, BrowserSettings.DefaultTabs().Count); Equal("Master Server", BrowserSettings.DefaultTabs()[0].Name);
            var store = TempStore(); Directory.CreateDirectory(store.DirectoryPath);
            File.WriteAllText(store.FilePath, """
            {"ActiveTab":0,"Tabs":[
              {"Name":"Quake Live","AppId":265630,"Source":0},
              {"Name":"Reflex","AppId":328070,"Source":0},
              {"Name":"Master Server Query","Source":0},
              {"Name":"Favorites","Source":1},{"Name":"Favorites","Source":1},
              {"Name":"My custom list","Source":2,"Addresses":["127.0.0.1:27015"]}],
             "Favorites":{"127.0.0.1:27015":"Keep this favorite"}}
            """);
            var settings = store.Load();
            Equal(2, settings.Tabs.Count); Equal("Master Server", settings.Tabs[0].Name); Equal(265630, settings.Tabs[0].AppId);
            Equal("My custom list", settings.Tabs[1].Name); Equal(1, settings.Favorites.Count);
            store.Save(settings); Equal(2, store.Load().Tabs.Count);
            return Task.CompletedTask;
        });
        Run("General preferences and quick filter visibility survive restart", () =>
        {
            var store = TempStore(); var window = new MainWindow(store); window.Show(); PumpUi();
            ClickMenu(window, "GeneralPreferencesMenuItem"); ClickMenu(window, "QuickFilterMenuItem");
            Check(!window.FindControl<Border>("PreferencesPanel")!.IsVisible, "General preferences was not hidden");
            Check(!window.FindControl<Border>("QuickFilterPanel")!.IsVisible, "Quick filter was not hidden");
            window.Close(); PumpUi();
            var restored = new MainWindow(store); restored.Show(); PumpUi();
            Check(!restored.FindControl<Border>("PreferencesPanel")!.IsVisible, "General preferences reappeared");
            Check(!restored.FindControl<Border>("QuickFilterPanel")!.IsVisible, "Quick filter reappeared");
            Equal(1, restored.FindControl<TabControl>("Tabs")!.Items.Count);
            restored.Close(); PumpUi(); return Task.CompletedTask;
        });
        Run("Update Status never discovers servers, ignores discovery limit, and retains sorting", () =>
        {
            int masterRequests = 0;
            using var alpha = new UdpMock(_ => [InfoPacket("Alpha")]);
            using var beta = new UdpMock(_ => [InfoPacket("Beta")]);
            using var master = new UdpMock(_ =>
            {
                Interlocked.Increment(ref masterRequests);
                return [MasterPacket(alpha.Endpoint.ToString(), beta.Endpoint.ToString(), "0.0.0.0:0")];
            });
            var store = TempStore();
            store.Save(new BrowserSettings { RefreshSelected = false, Tabs = [new() { Name = "Master Server", AppId = 440, Master = master.Endpoint.ToString() }, new() { Name = "Other tab", Source = ServerSource.Custom }] });
            var window = new MainWindow(store); window.Show(); PumpUi();
            ClickButton(window, "RefreshButton");
            Equal(0, masterRequests);
            Check(window.FindControl<TextBlock>("StatusText")!.Text!.Contains("No existing servers"), "Empty refresh did not report no existing servers");
            ClickButton(window, "QueryButton"); WaitForUpdate(window);
            Equal(1, masterRequests);
            var grid = window.FindControl<DataGrid>("ServersGrid")!;
            Column(grid, "Name").Sort(ListSortDirection.Descending); PumpUi();
            Equal("Beta", VisibleRows(grid)[0].Name);
            window.FindControl<TextBox>("LimitBox")!.Text = "1";
            ClickButton(window, "RefreshButton"); WaitForUpdate(window);
            Equal(1, masterRequests); Equal(2, VisibleRows(grid).Count);
            Equal("Beta", VisibleRows(grid)[0].Name);
            Equal("Name", store.Load().Tabs[0].SortColumn); Check(store.Load().Tabs[0].SortDescending, "Sort direction not saved");
            var tabs = window.FindControl<TabControl>("Tabs")!;
            tabs.SelectedIndex = 1; PumpUi(); tabs.SelectedIndex = 0; PumpUi();
            Equal("Beta", VisibleRows(grid)[0].Name);
            var search = window.FindControl<TextBox>("SearchBox")!;
            search.Text = "Alpha"; PumpUi(); search.Text = ""; PumpUi();
            Equal("Beta", VisibleRows(grid)[0].Name);
            var rows = VisibleRows(grid);
            window.Close(); PumpUi();
            var restored = new MainWindow(store); restored.Show(); restored.LoadPreviewRows(rows.AsEnumerable().Reverse()); PumpUi();
            Equal("Beta", VisibleRows(restored.FindControl<DataGrid>("ServersGrid")!)[0].Name);
            restored.Close(); PumpUi(); return Task.CompletedTask;
        });
        Run("Stopping an update retains unqueried existing servers", () =>
        {
            using var silentA = new UdpMock(_ => []);
            using var silentB = new UdpMock(_ => []);
            var store = TempStore(); store.Save(new BrowserSettings { RefreshSelected = false });
            var window = new MainWindow(store); window.Show();
            window.LoadPreviewRows([Row(silentA.Endpoint, "Alpha"), Row(silentB.Endpoint, "Beta")]); PumpUi();
            ClickButton(window, "RefreshButton");
            Equal(2, VisibleRows(window.FindControl<DataGrid>("ServersGrid")!).Count);
            ClickButton(window, "StopButton");
            PumpUntil(() => window.FindControl<Button>("QueryButton")!.IsEnabled);
            Equal(2, VisibleRows(window.FindControl<DataGrid>("ServersGrid")!).Count);
            window.Close(); PumpUi(); return Task.CompletedTask;
        });
        Run("Column visibility, custom numeric columns, sort, width and order persist", () =>
        {
            var store = TempStore(); store.Save(new BrowserSettings { RefreshSelected = false });
            var window = new MainWindow(store); window.Show();
            var a = Row(new(IPAddress.Loopback, 27015), "Alpha", "2");
            var b = Row(new(IPAddress.Loopback, 27016), "Beta", "10");
            window.LoadPreviewRows([a, b]); PumpUi();
            var grid = window.FindControl<DataGrid>("ServersGrid")!;
            var chooser = OpenDialog(window, "ColumnsMenuItem");
            CapturePreview(chooser, args, "--screenshot-columns=");
            foreach (var check in chooser.GetVisualDescendants().OfType<CheckBox>().Where(c => c.Tag as string is "Ping" or "Country" or "Tags"))
                check.IsChecked = false;
            AcceptDialog(chooser);
            Check(!Column(grid, "Ping").IsVisible && !Column(grid, "Country").IsVisible && !Column(grid, "Tags").IsVisible, "Column chooser did not hide columns");
            var add = OpenDialog(window, "AddColumnMenuItem");
            Named<AutoCompleteBox>(add, "ColumnFieldInput").Text = "Rule.test_value";
            Named<TextBox>(add, "ColumnTitleInput").Text = "Test numeric rule";
            Named<CheckBox>(add, "ColumnNumericInput").IsChecked = true;
            AcceptDialog(add);
            var custom = grid.Columns.Single(c => (c.Tag as ServerColumn)?.Custom == true);
            custom.Sort(ListSortDirection.Descending); PumpUi();
            Equal("Beta", VisibleRows(grid)[0].Name); // numeric 10 > 2, not lexical "2" > "10"
            window.LoadPreviewRows([b, a]); PumpUi();
            Equal("Beta", VisibleRows(grid)[0].Name);
            custom.Width = new DataGridLength(205); custom.DisplayIndex = 0;
            var savedRows = VisibleRows(grid);
            window.Close(); PumpUi();
            var restored = new MainWindow(store); restored.Show(); restored.LoadPreviewRows(savedRows.AsEnumerable().Reverse()); PumpUi();
            var newGrid = restored.FindControl<DataGrid>("ServersGrid")!;
            Check(!Column(newGrid, "Ping").IsVisible && !Column(newGrid, "Country").IsVisible && !Column(newGrid, "Tags").IsVisible, "Hidden columns reappeared");
            var restoredColumn = newGrid.Columns.Single(c => (c.Tag as ServerColumn)?.Custom == true);
            Equal(205d, restoredColumn.Width.Value); Equal(0, restoredColumn.DisplayIndex);
            Equal("Beta", VisibleRows(newGrid)[0].Name);
            var show = OpenDialog(restored, "ColumnsMenuItem");
            show.GetVisualDescendants().OfType<CheckBox>().Single(c => c.Tag as string == "Tags").IsChecked = true;
            AcceptDialog(show); Check(Column(newGrid, "Tags").IsVisible, "Hidden column cannot be restored");
            ClickMenu(restored, "BestFitAllMenuItem"); PumpUi();
            double fitted = Column(newGrid, "Tags").Width.Value;
            Check(fitted > 100, "Best Fit did not expand for long tags");
            var auto = restored.FindControl<MenuItem>("AutoFitMenuItem")!;
            auto.IsChecked = true; auto.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent)); PumpUi();
            Check(store.Load().Tabs[0].AutoFitColumns, "Auto-fit option not persisted");
            restored.Close(); PumpUi();
            Equal(fitted, store.Load().Tabs[0].Columns.Single(c => c.Id == "Tags").Width);
            return Task.CompletedTask;
        });
        Run("Rule columns populate from live A2S_RULES during status update", () =>
        {
            int rulesRequests = 0;
            using var game = new UdpMock(request =>
            {
                if (request[4] == 0x54) return [InfoPacket("Rules server")];
                Interlocked.Increment(ref rulesRequests);
                return [Packet(w => { w.Write((byte)0x45); w.Write((ushort)1); Text(w, "test_value"); Text(w, "27"); })];
            });
            var settings = new BrowserSettings { RefreshSelected = false, Tabs = [new BrowserTab { Source = ServerSource.Custom, Addresses = [game.Endpoint.ToString()],
                Columns = [new ServerColumn { Id = "CustomTest", Title = "Test rule", Custom = true, Field = "Rule.test_value", Numeric = true }] }] };
            var store = TempStore(); store.Save(settings);
            var window = new MainWindow(store); window.Show(); PumpUi();
            ClickButton(window, "RefreshButton"); WaitForUpdate(window);
            var row = VisibleRows(window.FindControl<DataGrid>("ServersGrid")!).Single();
            Equal(27m, (decimal)row.ColumnValues["CustomTest"]!); Check(rulesRequests > 0, "Rules were never queried");
            window.Close(); PumpUi(); return Task.CompletedTask;
        });
    }

    private static Window OpenDialog(MainWindow owner, string menuName)
    {
        ClickMenu(owner, menuName);
        PumpUntil(() => owner.OwnedWindows.Any(w => w.IsVisible));
        return owner.OwnedWindows.Last(w => w.IsVisible);
    }
    private static void CapturePreview(Window window, string[] args, string prefix)
    {
        var path = args.FirstOrDefault(a => a.StartsWith(prefix))?[prefix.Length..];
        if (path is null) return;
        PumpUi();
        using var frame = window.CaptureRenderedFrame() ?? throw new Exception("No rendered frame");
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        frame.Save(path);
    }
    private static void AcceptDialog(Window dialog) { ClickButton(dialog, "DialogAccept"); PumpUi(); }
    private static void ClickButton(Window window, string name)
    {
        (window is MainWindow ? window.FindControl<Button>(name)! : Named<Button>(window, name)).RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); PumpUi();
    }
    private static void ClickMenu(Window window, string name)
    {
        window.FindControl<MenuItem>(name)!.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent)); PumpUi();
    }
    private static void WaitForUpdate(MainWindow window) => PumpUntil(() => window.FindControl<Button>("QueryButton")!.IsEnabled
        && window.FindControl<TextBlock>("StatusText")!.Text!.StartsWith("Complete"));
    private static DataGridColumn Column(DataGrid grid, string id) => grid.Columns.Single(c => (c.Tag as ServerColumn)?.Id == id);
    private static T Named<T>(Window window, string name) where T : Control => window.GetVisualDescendants().OfType<T>().Single(c => c.Name == name);
    private static List<ServerEntry> VisibleRows(DataGrid grid) => grid.ItemsSource.Cast<ServerEntry>().ToList();
    private static ServerEntry Row(IPEndPoint endpoint, string name, string ruleValue = "2") => new()
    {
        Endpoint = endpoint, Country = "CN", Rules = [new Rule { Name = "test_value", Value = ruleValue }],
        Info = new ServerInfo { Name = name, Players = 4, MaxPlayers = 24, Ping = 25, GameVersion = "1.2",
            Extra = new ExtraInfo { Keywords = "A long tag value used to verify that Best Fit expands the column correctly" } }
    };
}
