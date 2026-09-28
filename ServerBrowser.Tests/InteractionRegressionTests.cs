using System.Net;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.VisualTree;
using QueryMaster;
using ServerBrowser.Core;
using ServerBrowser.Desktop;

namespace ServerBrowser.Tests;

internal static partial class Program
{
    private static void RunInteractionRegressions(string[] args)
    {
        Run("Servers are mouse-selectable and details load before Find Servers finishes", () =>
        {
            using var a = new UdpMock(q => ReplyWithDetails(q, "Alpha"));
            using var b = new UdpMock(q => ReplyWithDetails(q, "Beta"));
            using var slow = new UdpMock(_ => []);
            using var master = new UdpMock(_ => [MasterPacket(a.Endpoint.ToString(), b.Endpoint.ToString(), slow.Endpoint.ToString(), "0.0.0.0:0")]);
            var store = TempStore();
            store.Save(new BrowserSettings { TimeoutMs = 2000, RefreshSelected = false, DoubleClickConnect = false,
                Tabs = [new BrowserTab { Master = master.Endpoint.ToString(), AppId = 440 }] });
            var window = new MainWindow(store); window.Show(); PumpUi();
            try
            {
                MouseClick(window.FindControl<Button>("QueryToolButton")!);
                var grid = window.FindControl<DataGrid>("ServersGrid")!;
                PumpUntil(() => VisibleRows(grid).Count == 2);
                Check(!window.FindControl<Button>("QueryToolButton")!.IsEnabled, "Discovery ended before interaction test");
                var view = grid.ItemsSource;
                var target = VisibleRows(grid).First(r => r.Address != (grid.SelectedItem as ServerEntry)?.Address);
                var visual = grid.GetVisualDescendants().OfType<DataGridRow>().Single(r => r.IsEffectivelyVisible && ReferenceEquals(r.DataContext, target));
                MouseClick(visual);
                PumpUntil(() => window.FindControl<DataGrid>("PlayersGrid")!.ItemsSource?.Cast<Player>().Any(p => p.Name == target.Name + " player") == true);
                PumpUntil(() => window.FindControl<DataGrid>("RulesGrid")!.ItemsSource?.Cast<Rule>().Any(r => r.Name == "test_rule") == true);
                Check(!window.FindControl<Button>("QueryToolButton")!.IsEnabled, "Details were delayed until the entire scan completed");
                Check(ReferenceEquals(view, grid.ItemsSource), "Streaming results replaced the table view");
                Equal(target.Address, ((ServerEntry)grid.SelectedItem!).Address);
                CapturePreview(window, args, "--screenshot-players=");
                window.FindControl<TabControl>("DetailsTabs")!.SelectedIndex = 2; PumpUi();
                CapturePreview(window, args, "--screenshot-rules=");
                MouseClick(window.FindControl<Button>("StopButton")!);
                PumpUntil(() => window.FindControl<Button>("QueryToolButton")!.IsEnabled);
            }
            finally { window.Close(); PumpUi(); }
            return Task.CompletedTask;
        });
        Run("Partial player results survive a row replacement while rules are still loading", () =>
        {
            using var gate = new ManualResetEventSlim();
            using var game = new UdpMock(q =>
            {
                if (q[4] == 0x56) gate.Wait(TimeSpan.FromSeconds(4));
                return ReplyWithDetails(q, "Delayed");
            });
            var store = TempStore(); store.Save(new BrowserSettings { RefreshSelected = false, TimeoutMs = 2500, DoubleClickConnect = false });
            var window = new MainWindow(store); window.Show();
            try
            {
                window.LoadPreviewRows([Row(game.Endpoint, "Before refresh")]); PumpUi();
                PumpUntil(() => window.FindControl<DataGrid>("PlayersGrid")!.ItemsSource?.Cast<Player>().Any() == true);
                Check(window.FindControl<TextBlock>("RulesStatus")!.Text!.Contains("Loading"), "Player results waited for the rules response");
                var grid = window.FindControl<DataGrid>("ServersGrid")!; var view = grid.ItemsSource;
                window.LoadPreviewRows([Row(game.Endpoint, "After refresh")]); PumpUi();
                gate.Set();
                PumpUntil(() => window.FindControl<DataGrid>("RulesGrid")!.ItemsSource?.Cast<Rule>().Any() == true);
                Check(ReferenceEquals(view, grid.ItemsSource), "A replacement row replaced the table view");
                Equal("After refresh", ((ServerEntry)grid.SelectedItem!).Name);
                Equal("Delayed player", window.FindControl<DataGrid>("PlayersGrid")!.ItemsSource.Cast<Player>().Single().Name);
                PumpUntil(() => !window.FindControl<Button>("StopButton")!.IsEnabled);
            }
            finally { gate.Set(); window.Close(); PumpUi(); }
            return Task.CompletedTask;
        });
        Run("No response and valid empty details are visibly distinguished", () =>
        {
            using var silent = new UdpMock(_ => []);
            var store = TempStore(); store.Save(new BrowserSettings { RefreshSelected = false, TimeoutMs = 200 });
            var window = new MainWindow(store); window.Show();
            try
            {
                window.LoadPreviewRows([Row(silent.Endpoint, "Silent")]); PumpUi();
                PumpUntil(() => !window.FindControl<Button>("StopButton")!.IsEnabled);
                Check(window.FindControl<TextBlock>("PlayersStatus")!.Text!.Contains("failed or timed out"), "Timeout was shown as an empty player list");
                Check(window.FindControl<TextBlock>("RulesStatus")!.Text!.Contains("failed or timed out"), "Timeout was shown as an empty rules list");
                using var empty = new UdpMock(q => q[4] == 0x55
                    ? [Packet(w => { w.Write((byte)0x44); w.Write((byte)0); })]
                    : [Packet(w => { w.Write((byte)0x45); w.Write((ushort)0); })]);
                window.LoadPreviewRows([Row(empty.Endpoint, "Empty")]); PumpUi();
                PumpUntil(() => window.FindControl<TextBlock>("RulesStatus")!.Text == "No rules reported.");
                Equal("No players reported.", window.FindControl<TextBlock>("PlayersStatus")!.Text);
            }
            finally { window.Close(); PumpUi(); }
            return Task.CompletedTask;
        });
        Run("Toolbar, panel and menu action states agree; disabled buttons ignore mouse clicks", () =>
        {
            int masterRequests = 0;
            using var master = new UdpMock(_ => { Interlocked.Increment(ref masterRequests); return []; });
            var store = TempStore(); store.Save(new BrowserSettings { Tabs = [new BrowserTab { Master = master.Endpoint.ToString() }] });
            var window = new MainWindow(store); window.Show(); PumpUi();
            try
            {
                foreach (string name in new[] { "RefreshToolButton", "RefreshButton", "StaticRefreshButton", "StopButton" })
                    Check(!window.FindControl<Button>(name)!.IsEnabled, name + " must be disabled before any query");
                Check(!window.FindControl<MenuItem>("UpdateMenuItem")!.IsEnabled && !window.FindControl<MenuItem>("StopMenuItem")!.IsEnabled, "Menus are enabled when buttons are disabled");
                var stop = window.FindControl<Button>("StopButton")!;
                Check(stop.GetVisualDescendants().OfType<Image>().Where(i => i.Classes.Contains("activeGlyph")).All(i => !i.IsVisible), "Disabled Stop still shows its active colored icon");
                string status = window.FindControl<TextBlock>("StatusText")!.Text!;
                MouseClick(window.FindControl<Button>("RefreshToolButton")!);
                MouseClick(stop);
                Equal(status, window.FindControl<TextBlock>("StatusText")!.Text);
                CapturePreview(window, args, "--screenshot-disabled=");
                MouseClick(window.FindControl<Button>("QueryToolButton")!);
                PumpUntil(() => masterRequests > 0);
                Check(stop.IsEnabled, "Stop is disabled while discovery is active");
                Check(!window.FindControl<Button>("RefreshToolButton")!.IsEnabled && !window.FindControl<Button>("RefreshButton")!.IsEnabled, "Update Status was not disabled during discovery");
                MouseClick(stop);
                Check(!stop.IsEnabled, "Stop remained enabled after cancellation was requested");
                PumpUntil(() => window.FindControl<Button>("QueryToolButton")!.IsEnabled);
                Check(!window.FindControl<Button>("RefreshToolButton")!.IsEnabled, "Update Status enabled with no existing servers");
            }
            finally { window.Close(); PumpUi(); }
            return Task.CompletedTask;
        });
        Run("Best Fit and Best Fit All change actual on-screen widths through mouse menus", () =>
        {
            using var game = new UdpMock(q => ReplyWithDetails(q, "Fit"));
            var store = TempStore(); store.Save(new BrowserSettings { RefreshSelected = false, DoubleClickConnect = false });
            var window = new MainWindow(store); window.Show();
            try
            {
                window.LoadPreviewRows([new ServerEntry { Endpoint = game.Endpoint,
                    Info = new ServerInfo { Name = new string('W', 75), Players = 1, MaxPlayers = 16, Extra = new ExtraInfo { Keywords = new string('W', 70) } } }]);
                PumpUi();
                var grid = window.FindControl<DataGrid>("ServersGrid")!;
                var nameColumn = Column(grid, "Name");
                double beforeName = nameColumn.ActualWidth;
                ClickHeaderMenu(grid, "Name", "Best Fit");
                Check(nameColumn.ActualWidth > beforeName + 200, $"Best Fit changed metadata but not rendered width ({beforeName} -> {nameColumn.ActualWidth})");
                var tagColumn = Column(grid, "Tags");
                double beforeTags = tagColumn.ActualWidth;
                ClickHeaderMenu(grid, "Name", "Best Fit All Columns");
                Check(tagColumn.ActualWidth > beforeTags + 200, "Best Fit All did not resize the visible tag column");
                Check(nameColumn.Width.DisplayValue > beforeName + 200, "Measured display width was not updated");
            }
            finally { window.Close(); PumpUi(); }
            return Task.CompletedTask;
        });
        Run("MMDB paths resolve relative to executable; launcher and window use the original icon", () =>
        {
            string root = Path.Combine(Path.GetTempPath(), "ssb portable app " + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            string database = Path.Combine(root, "GeoIp", "ipinfo_lite.mmdb");
            Equal(database, GeoIp.ResolvePath("GeoIp/ipinfo_lite.mmdb", root));
            Equal("GeoIp/ipinfo_lite.mmdb", GeoIp.RelativePath(database, root));
            string launcher = PortableLauncher.Write(root);
            string desktop = File.ReadAllText(launcher);
            Check(desktop.Contains("Icon=" + PortableLauncher.DesktopString(Path.Combine(root, "SteamServerBrowser.png"))), "Launcher is missing its app icon path");
            Check(desktop.Contains("Exec=" + PortableLauncher.ExecArgument(Path.Combine(root, "SteamServerBrowser"))), "Launcher path is not quoted");
            Check(!desktop.Contains("run.sh"), "Launcher still uses run.sh");
            var window = new MainWindow(TempStore()); window.Show(); PumpUi();
            Check(window.Icon is not null, "Window has no application icon");
            window.Close(); PumpUi(); return Task.CompletedTask;
        });
    }

    private static IEnumerable<byte[]> ReplyWithDetails(byte[] request, string name)
    {
        return request[4] switch
        {
            0x54 => [InfoPacket(name)],
            0x55 => [Packet(w => { w.Write((byte)0x44); w.Write((byte)1); w.Write((byte)0); Text(w, name + " player"); w.Write(42); w.Write(60f); })],
            0x56 => [Packet(w => { w.Write((byte)0x45); w.Write((ushort)1); Text(w, "test_rule"); Text(w, name + " value"); })],
            _ => []
        };
    }
    private static void MouseClick(Control control, MouseButton button = MouseButton.Left)
    {
        PumpUi();
        var root = control.GetVisualRoot() as TopLevel ?? throw new Exception("Control has no root");
        var point = control.TranslatePoint(new Point(Math.Min(control.Bounds.Width / 2, 150), control.Bounds.Height / 2), root)!.Value;
        root.MouseMove(point); root.MouseDown(point, button); root.MouseUp(point, button); PumpUi();
    }
    private static void ClickHeaderMenu(DataGrid grid, string title, string action)
    {
        var header = grid.GetVisualDescendants().OfType<Border>().First(b => b.IsEffectivelyVisible && b.Child is TextBlock text && text.Text == title && b.ContextMenu is not null);
        MouseClick(header, MouseButton.Right);
        PumpUntil(() => header.ContextMenu!.IsOpen);
        var item = header.ContextMenu!.Items.OfType<MenuItem>().Single(i => i.Header as string == action);
        MouseClick(item); PumpUi();
    }
}

