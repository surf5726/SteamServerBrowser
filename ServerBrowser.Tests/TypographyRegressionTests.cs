using System.Net;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.VisualTree;
using QueryMaster;
using ServerBrowser.Core;
using ServerBrowser.Desktop;

namespace ServerBrowser.Tests;

internal static partial class Program
{
    private static void RunTypographyRegressions(string[] args)
    {
        Run("Best Fit fits complete text and headers, including long Unicode and larger column fonts", () =>
        {
            using var game = new UdpMock(q => ReplyWithDetails(q, "Sizing fixture"));
            var store = TempStore(); store.Save(new BrowserSettings { RefreshSelected = false, TimeoutMs = 200, DoubleClickConnect = false });
            var window = new MainWindow(store); window.Show();
            try
            {
                string longName = "4th World #6 单挑模式（QQ群：539130993）| Crowning Kings Fistful of Frags – Western Australia";
                var row = new ServerEntry { Endpoint = game.Endpoint, Country = "CN",
                    Info = new ServerInfo { Name = longName, Description = "Fistful of Frags – 4 Teams Shootout",
                        Map = "fof_cripplecreek", Players = 26, Bots = 2, MaxPlayers = 32,
                        Extra = new ExtraInfo { Keywords = new string('W', 320) + "中文结束" } } };
                window.LoadPreviewRows([row]); PumpUi();
                var grid = window.FindControl<DataGrid>("ServersGrid")!;
                ((DataGridTextColumn)Column(grid, "Description")).FontSize = 18;
                foreach (var theme in new[] { "DarkThemeItem", "LightThemeItem" })
                {
                    ClickMenu(window, theme);
                    ClickMenu(window, "BestFitAllMenuItem");
                    foreach (var id in new[] { "Address", "Name", "Description", "Players", "Map", "Ping", "Tags" })
                        AssertEntireCellAndHeader(grid, row, Column(grid, id));
                }
                Check(Column(grid, "Tags").ActualWidth > 1500, "Long content was limited by the old 1500-pixel cap");
                double tagWidth = Column(grid, "Tags").ActualWidth;
                Check(grid.GetVisualDescendants().OfType<ScrollBar>().Any(b => b.Orientation == Avalonia.Layout.Orientation.Horizontal && b.Maximum > b.Minimum), "Wide fitted columns have no horizontal scrollbar");
                ((DataGridTextColumn)Column(grid, "Description")).FontSize = 12;
                grid.ScrollIntoView(row, Column(grid, "Name")); PumpUi();
                ClickMenu(window, "BestFitAllMenuItem");
                CapturePreview(window, args, "--screenshot-fit=");
                window.Close(); PumpUi();
                Check(store.Load().Tabs[0].Columns.Single(c => c.Id == "Tags").Width >= tagWidth - 1, "Saved wide column was clamped");
                var restored = new MainWindow(store); restored.Show(); restored.LoadPreviewRows([row]); PumpUi();
                Check(Column(restored.FindControl<DataGrid>("ServersGrid")!, "Tags").ActualWidth > 1500, "Restart truncated the fitted width");
                restored.Close(); PumpUi();
            }
            finally { if (window.IsVisible) { window.Close(); PumpUi(); } }
            return Task.CompletedTask;
        });
        Run("Players default to descending scores and support mouse Best Fit menus", () =>
        {
            using var game = new UdpMock(q =>
            {
                if (q[4] != 0x55) return ReplyWithDetails(q, "Players fixture");
                return [Packet(w =>
                {
                    w.Write((byte)0x44); w.Write((byte)3);
                    w.Write((byte)0); Text(w, "Low score"); w.Write(-5); w.Write(5f);
                    w.Write((byte)1); Text(w, "High score · 玩家名称很长 | Long player name for complete sizing"); w.Write(120); w.Write(125.875f);
                    w.Write((byte)2); Text(w, "Middle score"); w.Write(20); w.Write(60f);
                })];
            });
            var store = TempStore(); store.Save(new BrowserSettings { RefreshSelected = false, DoubleClickConnect = false });
            var window = new MainWindow(store); window.Show();
            try
            {
                window.LoadPreviewRows([Row(game.Endpoint, "Players fixture")]); PumpUi();
                var players = window.FindControl<DataGrid>("PlayersGrid")!;
                PumpUntil(() => players.ItemsSource?.Cast<Player>().Count() == 3);
                Equal(120L, players.ItemsSource.Cast<Player>().First().Score);
                var winner = players.ItemsSource.Cast<Player>().First();
                var time = players.Columns.Single(c => c.SortMemberPath == "Time");
                players.ScrollIntoView(winner, time); PumpUi();
                Equal("00:02:05", ((TextBlock)time.GetCellContent(winner)!).Text);
                Equal(125.875, winner.Time.TotalSeconds);
                var name = players.Columns.Single(c => c.SortMemberPath == "Name");
                players.ScrollIntoView(winner, name); PumpUi();
                ClickHeaderMenu(players, "Name", "Best Fit");
                AssertEntireCellAndHeader(players, winner, name);
                players.ScrollIntoView(winner, name); PumpUi();
                ClickHeaderMenu(players, "Name", "Best Fit All Columns");
                foreach (var col in players.Columns) AssertEntireCellAndHeader(players, winner, col);
                players.ScrollIntoView(winner, name); PumpUi();
                CapturePreview(window, args, "--screenshot-player-fit=");
                window.Close(); PumpUi();
                Equal("Score", store.Load().PlayerSortColumn);
                Check(store.Load().PlayerSortDescending && store.Load().PlayerColumnWidths["Name"] > 300, "Player sorting or width was not saved");
            }
            finally { if (window.IsVisible) { window.Close(); PumpUi(); } }
            return Task.CompletedTask;
        });
        Run("Compact table fonts and ellipsis keep long detail text inside its column", () =>
        {
            using var game = new UdpMock(q => ReplyWithDetails(q, "Details fixture"));
            var store = TempStore(); store.Save(new BrowserSettings { RefreshSelected = false });
            var window = new MainWindow(store); window.Show();
            try
            {
                window.LoadPreviewRows([Row(game.Endpoint, "Details fixture")]); PumpUi();
                PumpUntil(() => !window.FindControl<Button>("StopButton")!.IsEnabled);
                window.FindControl<TabControl>("DetailsTabs")!.SelectedIndex = 1; PumpUi();
                var details = window.FindControl<DataGrid>("DetailItemsGrid")!;
                var item = new KeyValuePair<string, string>("Game Address / very long setting name", "alltalk,nocrits,respawntimes,matcha Jump (Hong Kong) " + new string('W', 100));
                details.ItemsSource = new[] { item }; PumpUi();
                foreach (var column in details.Columns)
                {
                    var text = column.GetCellContent(item) as TextBlock ?? throw new Exception("Missing detail text");
                    Equal(12d, text.FontSize);
                    Equal(TextTrimming.CharacterEllipsis, text.TextTrimming);
                    Check(text.TextLayout.TextLines.Any(l => l.HasCollapsed), "Long detail text has no ellipsis");
                    var cell = text.GetVisualAncestors().OfType<DataGridCell>().First();
                    Check(cell.ClipToBounds, "Detail text can paint over the adjacent column");
                    var left = text.TranslatePoint(default, cell)!.Value.X;
                    Check(left >= 0 && left + text.Bounds.Width <= cell.Bounds.Width + 0.5, "Text extends beyond its column");
                }
                foreach (var text in window.FindControl<DataGrid>("ServersGrid")!.GetVisualDescendants().OfType<TextBlock>().Where(t => t.Name == "CellTextBlock"))
                    Equal(12d, text.FontSize);
                CapturePreview(window, args, "--screenshot-ellipsis=");
            }
            finally { window.Close(); PumpUi(); }
            return Task.CompletedTask;
        });
    }

    private static void AssertEntireCellAndHeader(DataGrid grid, object row, DataGridColumn column)
    {
        grid.ScrollIntoView(row, column); PumpUi();
        var text = column.GetCellContent(row) as TextBlock ?? throw new Exception($"Missing text for {column.Header}");
        Check(!text.TextLayout.TextLines.Any(l => l.HasCollapsed), $"{column.Header}: fitted cell still uses an ellipsis");
        var natural = new TextBlock
        {
            Text = text.Text, FontFamily = text.FontFamily, FontSize = text.FontSize, FontStyle = text.FontStyle,
            FontWeight = text.FontWeight, FontStretch = text.FontStretch, LetterSpacing = text.LetterSpacing,
            TextWrapping = TextWrapping.NoWrap, TextTrimming = TextTrimming.None
        };
        natural.Measure(Size.Infinity);
        Check(text.Bounds.Width + 0.5 >= natural.DesiredSize.Width,
            $"{column.Header}: content needs {natural.DesiredSize.Width}, available {text.Bounds.Width}");
        var header = grid.GetVisualDescendants().OfType<DataGridColumnHeader>().First(h => Equals(h.Content, column.Header));
        var label = header.GetVisualDescendants().OfType<TextBlock>().First();
        Check(!label.TextLayout.TextLines.Any(l => l.HasCollapsed), $"{column.Header}: fitted header still uses an ellipsis");
        var fullHeader = new TextBlock { Text = label.Text, FontFamily = label.FontFamily, FontSize = label.FontSize, FontWeight = label.FontWeight, TextTrimming = TextTrimming.None };
        fullHeader.Measure(Size.Infinity);
        Check(label.Bounds.Width + 0.5 >= fullHeader.DesiredSize.Width,
            $"{column.Header}: header needs {fullHeader.DesiredSize.Width}, available {label.Bounds.Width}");
    }
}
