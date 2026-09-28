using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ICSharpCode.SharpZipLib.BZip2;
using ICSharpCode.SharpZipLib.Checksum;
using QueryMaster;
using ServerBrowser.Core;
using ServerBrowser.Desktop;

namespace ServerBrowser.Tests;

internal static partial class Program
{
    private static int passed;
    private static int failed;
    [STAThread]
    public static int Main(string[] args)
    {
        Run("A2S_INFO UTF-8 / EDF game port", async () =>
        {
            using var mock = new UdpMock(_ => [InfoPacket()]);
            var result = await Query(mock);
            Equal("本地测试服务器", result.Name); Equal(27016, result.GameEndpoint.Port); Equal(2, result.Bots); Equal("cp_test", result.Map);
        });
        Run("A2S_INFO challenge negotiation", async () =>
        {
            int count = 0;
            using var mock = new UdpMock(data =>
            {
                Interlocked.Increment(ref count);
                if (data.Length == 25) return [Packet(w => { w.Write((byte)0x41); w.Write(12345); })];
                Equal(12345, BitConverter.ToInt32(data, data.Length - 4));
                return [InfoPacket()];
            });
            Equal("本地测试服务器", (await Query(mock)).Name); Equal(2, count);
        });
        Run("A2S_PLAYER and A2S_RULES challenge", async () =>
        {
            using var mock = new UdpMock(data =>
            {
                if (data[4] == 0x54) return [InfoPacket()];
                if (BitConverter.ToInt32(data, 5) == -1) return [Packet(w => { w.Write((byte)0x41); w.Write(12345); })];
                if (data[4] == 0x55) return [Packet(w => { w.Write((byte)0x44); w.Write((byte)1); w.Write((byte)0); Text(w, "玩家甲"); w.Write(42); w.Write(60.5f); })];
                return [Packet(w => { w.Write((byte)0x45); w.Write((ushort)1); Text(w, "sv_test"); Text(w, "中文值"); })];
            });
            var row = await Query(mock);
            var details = await new ServerBrowserService().DetailsAsync(row, 500, default);
            Equal("玩家甲", details.Players.Single().Name); Equal(42L, details.Players.Single().Score); Equal("中文值", details.Rules.Single().Value);
        });
        Run("Source split / reorder / duplicate / unrelated ID", async () =>
        {
            var parts = Split(InfoPacket());
            var unrelated = parts[0].ToArray(); unrelated[4]++;
            using var mock = new UdpMock(_ => [parts[1], unrelated, parts[1], parts[0]]);
            Equal("本地测试服务器", (await Query(mock)).Name);
        });
        Run("Source compressed split / CRC / uint32 size", async () =>
        {
            var parts = Split(InfoPacket(), true);
            using var mock = new UdpMock(_ => [parts[1], parts[0]]);
            Equal("本地测试服务器", (await Query(mock)).Name);
        });
        Run("Reject corrupt compressed checksum", async () =>
        {
            var parts = Split(InfoPacket(), true); parts[0][16] ^= 1;
            using var mock = new UdpMock(_ => parts);
            Check((await Query(mock)).Info is null, "Corrupt checksum was accepted");
        });
        Run("Large single UDP packet (>1400 bytes)", async () =>
        {
            var name = new string('x', 2000);
            using var mock = new UdpMock(_ => [InfoPacket(name)]);
            Equal(name, (await Query(mock)).Name);
        });
        Run("Reject malformed packet and unterminated string", async () =>
        {
            using var mock = new UdpMock(_ => [[255, 255, 255, 255, 0x49, 17, 65]]);
            Check((await Query(mock)).Info is null, "Malformed string was accepted");
            Throws<ParseException>(() => new Parser([]).ReadString());
        });
        Run("GoldSource legacy response", async () =>
        {
            using var mock = new UdpMock(_ => [Packet(w =>
            {
                w.Write((byte)0x6d); Text(w, "127.0.0.1:27015"); Text(w, "GoldSource"); Text(w, "de_dust2");
                Text(w, "cstrike"); Text(w, "Counter-Strike"); w.Write((byte)3); w.Write((byte)32); w.Write((byte)48);
                w.Write((byte)'D'); w.Write((byte)'L'); w.Write((byte)0); w.Write((byte)0); w.Write((byte)1); w.Write((byte)0);
            })]);
            var row = await new ServerBrowserService().QueryAsync(mock.Endpoint, 10, 200, false, default);
            Equal("GoldSource", row.Name); Check(row.Info?.IsObsolete == true, "Not identified as GoldSource");
        });
        Run("UDP timeout completes and cancellation releases socket", async () =>
        {
            using var mock = new UdpMock(_ => []);
            var timeout = await new ServerBrowserService().QueryAsync(mock.Endpoint, 440, 100, false, default);
            Check(timeout.Info is null && timeout.Error.Length > 0, "Timeout not reported");
            using var cancel = new CancellationTokenSource(80);
            await ThrowsAsync<OperationCanceledException>(() => new ServerBrowserService().QueryAsync(mock.Endpoint, 440, 2000, false, cancel.Token));
        });
        Run("Master UDP pagination, region, unique addresses", async () =>
        {
            int page = 0;
            using var mock = new UdpMock(request =>
            {
                Equal((byte)0x31, request[0]); Equal((byte)4, request[1]);
                int current = Interlocked.Increment(ref page);
                Check(Encoding.UTF8.GetString(request).Contains("\\appid\\440"), "App filter missing");
                return current == 1 ? [MasterPacket("127.0.0.1:27015", "127.0.0.2:27015")] :
                    [MasterPacket("127.0.0.2:27015", "127.0.0.3:27015", "0.0.0.0:0")];
            });
            var result = await new ServerBrowserService().DiscoverAsync(new BrowserTab { Master = mock.Endpoint.ToString(), Region = 4 }, "", default);
            Equal(3, result.Addresses.Count); Equal(2, page);
        });
        Run("Master UDP query limit and bad headers", async () =>
        {
            using var mock = new UdpMock(_ => [MasterPacket("127.0.0.1:27015", "127.0.0.2:27015")]);
            var result = await new ServerBrowserService().DiscoverAsync(new BrowserTab { Master = mock.Endpoint.ToString(), Limit = 1 }, "", default);
            Equal(1, result.Addresses.Count);
            Throws<InvalidDataException>(() => ServerBrowserService.ParseMasterPacket([1, 2, 3, 4, 5, 6, 7]));
        });
        Run("Master repeated seed terminates with partial list", async () =>
        {
            using var mock = new UdpMock(_ => [MasterPacket("127.0.0.1:27015")]);
            var result = await new ServerBrowserService().DiscoverAsync(new BrowserTab { Master = mock.Endpoint.ToString() }, "", default);
            Equal(1, result.Addresses.Count); Check(result.Warning is not null, "Repeated seed not reported");
        });
        Run("Steam Web API JSON, filters and valid empty response", async () =>
        {
            using var http = new HttpClient(new FakeHandler(request =>
            {
                Check(request.RequestUri!.Query.Contains("%5Cregion%5C4"), "Region not sent to Web API");
                return """{"response":{"servers":[{"addr":"127.0.0.1:27015"},{"addr":"invalid"},{"addr":"127.0.0.1:27015"}]}}""";
            }));
            var service = new ServerBrowserService(http);
            Equal(1, (await service.DiscoverAsync(new BrowserTab { UseWebApi = true, Region = 4 }, "test-key", default)).Addresses.Count);
            using var empty = new HttpClient(new FakeHandler(_ => """{"response":{"servers":[]}}"""));
            Equal(0, (await new ServerBrowserService(empty).DiscoverAsync(new BrowserTab { UseWebApi = true }, "test", default)).Addresses.Count);
        });
        Run("API authentication failure never includes key", async () =>
        {
            using var http = new HttpClient(new FakeHandler(_ => "", HttpStatusCode.Forbidden));
            try { await new ServerBrowserService(http).DiscoverAsync(new BrowserTab { UseWebApi = true }, "secret-test-key", default); throw new Exception("Expected failure"); }
            catch (InvalidOperationException ex) { Check(!ex.Message.Contains("secret-test-key"), "Key leaked"); }
        });
        Run("Configuration round-trip, Unicode and corrupt-file recovery", () =>
        {
            var store = TempStore();
            var settings = new BrowserSettings { GeoIpPath = "/路径/国家.mmdb" };
            settings.Favorites["127.0.0.1:27015"] = "我的服务器";
            store.Save(settings); Equal("我的服务器", store.Load().Favorites.Single().Value);
            Equal("/路径/国家.mmdb", store.Load().GeoIpPath);
            File.WriteAllText(store.FilePath, "{ broken");
            var recovered = store.Load();
            Check(store.LoadWarning is not null && recovered.Tabs.Count > 0, "Corrupt config recovery failed");
            Check(Directory.GetFiles(store.DirectoryPath, "*.broken-*").Length == 1, "Corrupt config was not preserved");
            return Task.CompletedTask;
        });
        Run("Windows INI migration retains favorites, tabs and countries", () =>
        {
            var config = SettingsStore.ImportWindowsIni("[FavoriteServers]\n127.0.0.1:27015=测试\n[Tab0]\nTabName=国服\nType=0\nInitialGameID=550\nFilterCountry=CN,JP\nGetEmptyServers=0\n[Tab1]\nTabName=自定义\nType=1\n[Tab1_Servers]\n127.0.0.2:27016=第二个");
            Equal(2, config.Tabs.Count); Equal(550, config.Tabs[0].AppId); Equal("CN,JP", config.Tabs[0].Countries);
            Check(config.Tabs[0].HideEmpty, "Empty filter lost"); Equal("127.0.0.2:27016", config.Tabs[1].Addresses.Single());
            Equal("测试", config.Favorites.Single().Value); return Task.CompletedTask;
        });
        Run("Country MMDB schemas and multi-country filters", () =>
        {
            Equal("CN", GeoIp.ReadCountry(new Dictionary<string, object> { ["country"] = "CN" }));
            Equal("JP", GeoIp.ReadCountry(new Dictionary<string, object> { ["country_code"] = "jp" }));
            Equal("DE", GeoIp.ReadCountry(new Dictionary<string, object> { ["country"] = new Dictionary<string, object> { ["iso_code"] = "DE" } }));
            var row = SampleRow();
            Check(ServerFilter.Matches(row, new BrowserTab { Countries = "US,CN" }), "Multi-country filter failed");
            Check(!ServerFilter.Matches(row, new BrowserTab { Countries = "JP" }), "Country filter ignored");
            return Task.CompletedTask;
        });
        Run("Wildcard, tags, bot counts and latency filtering", () =>
        {
            var row = SampleRow();
            Check(ServerFilter.Matches(row, new BrowserTab { Search = "*测试*", IncludeTags = "alltalk", MinPlayers = 4, MaxPing = 50 }), "Expected filter match");
            Check(!ServerFilter.Matches(row, new BrowserTab { MinPlayers = 5 }), "Bots incorrectly counted");
            Check(ServerFilter.Matches(row, new BrowserTab { MinPlayers = 5, IncludeBots = true }), "Include bots ignored");
            Check(!ServerFilter.Matches(row, new BrowserTab { ExcludeTags = "alltalk" }), "Exclude tags ignored");
            Check(!ServerFilter.MatchText("a", "[a]"), "Search unexpectedly treated as regex");
            return Task.CompletedTask;
        });
        Run("Endpoint parsing and Steam password URI encoding", async () =>
        {
            Equal(27015, (await Endpoints.ResolveAsync("127.0.0.1")).Port);
            Equal(27016, (await Endpoints.ResolveAsync("steam://connect/127.0.0.1:27016")).Port);
            await ThrowsAsync<FormatException>(() => Endpoints.ResolveAsync("127.0.0.1:70000"));
            var uri = SteamLauncher.ConnectionUri(new IPEndPoint(IPAddress.Loopback, 27016), "a b/&");
            Equal("steam://connect/127.0.0.1:27016/a%20b%2F%26", uri);
        });
        Run("Source RCON auth and fragmented TCP response", async () =>
        {
            using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
            var peer = Task.Run(async () =>
            {
                using var client = await listener.AcceptTcpClientAsync();
                using var stream = client.GetStream();
                var auth = await ReadRcon(stream); Equal(3, auth.Type);
                await WriteRcon(stream, 1, 0, ""); await WriteRcon(stream, 1, 2, "");
                var command = await ReadRcon(stream); Equal("status", command.Text);
                await ReadRcon(stream);
                await WriteRcon(stream, 2, 0, "part one "); await WriteRcon(stream, 2, 0, "part two");
                await WriteRcon(stream, 3, 0, "");
            });
            var text = await SourceRcon.SendAsync((IPEndPoint)listener.LocalEndpoint, "password", "status", default);
            Equal("part one part two", text); await peer;
        });
        Run("Avalonia UI render / filters / favorites / live local query", () =>
        {
            AppBuilder.Configure<App>().UseSkia().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false }).SetupWithoutStarting();
            var store = TempStore();
            store.Save(new BrowserSettings { ActiveTab = 0, RefreshSelected = false });
            var window = new MainWindow(store);
            window.Show();
            window.LoadPreviewRows([SampleRow()]);
            Dispatcher.UIThread.RunJobs();
            var grid = window.FindControl<DataGrid>("ServersGrid")!;
            Equal(1, grid.ItemsSource.Cast<ServerEntry>().Count());
            var countryFlag = grid.GetVisualDescendants().OfType<Image>().Single(i => i.Classes.Contains("countryFlag") && i.IsEffectivelyVisible);
            Check(countryFlag.Source is not null, "Location flag was not loaded from the bundled original assets");
            var search = window.FindControl<TextBox>("SearchBox")!;
            search.Text = "no-match"; Dispatcher.UIThread.RunJobs();
            Equal(0, grid.ItemsSource.Cast<ServerEntry>().Count());
            search.Text = "测试"; Dispatcher.UIThread.RunJobs();
            Equal(1, grid.ItemsSource.Cast<ServerEntry>().Count());
            var playersGrid = window.FindControl<DataGrid>("PlayersGrid")!;
            Check(playersGrid.TranslatePoint(new Point(0, 0), window)!.Value.X > grid.TranslatePoint(new Point(0, 0), window)!.Value.X + grid.Bounds.Width,
                "Original right-docked details layout was lost");
            Check(window.FindControl<Border>("PreferencesPanel")!.IsVisible && window.FindControl<Border>("QuickFilterPanel")!.IsVisible,
                "Original preferences and quick filters must be visible by default");
            grid.SelectedItem = grid.ItemsSource.Cast<ServerEntry>().Single();
            grid.ContextMenu!.Items.OfType<MenuItem>().Single(m => m.Header as string == "Add to Favorites").RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            Check(store.Load().Favorites.ContainsKey("127.0.0.1:27015"), "Favorite not saved from original context menu");
            window.FindControl<RadioButton>("AddressGameBox")!.IsChecked = true;
            Equal("127.0.0.1:27016", grid.ItemsSource.Cast<ServerEntry>().Single().DisplayAddress);
            var screenshot = args.FirstOrDefault(a => a.StartsWith("--screenshot="))?[13..];
            if (screenshot is not null)
            {
                AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                using var bitmap = window.CaptureRenderedFrame() ?? throw new Exception("No rendered frame");
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(screenshot))!);
                bitmap.Save(screenshot);
            }
            window.Close();
            Dispatcher.UIThread.RunJobs();

            using var mock = new UdpMock(_ => [InfoPacket()]);
            var liveStore = TempStore();
            liveStore.Save(new BrowserSettings { Tabs = [new BrowserTab { Source = ServerSource.Custom, Addresses = [mock.Endpoint.ToString()] }] });
            var live = new MainWindow(liveStore); live.Show();
            live.FindControl<Button>("QueryButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var end = DateTime.UtcNow.AddSeconds(8);
            while (DateTime.UtcNow < end)
            {
                Dispatcher.UIThread.RunJobs();
                if (live.FindControl<TextBlock>("StatusText")!.Text?.StartsWith("Complete") == true) break;
                Thread.Sleep(10);
            }
            Equal(1, live.FindControl<DataGrid>("ServersGrid")!.ItemsSource.Cast<ServerEntry>().Count());
            Check(live.FindControl<TextBlock>("StatusText")!.Text!.StartsWith("Complete"), "UI query did not finish");
            live.Close(); Dispatcher.UIThread.RunJobs();
            return Task.CompletedTask;
        });
        Run("Tab close buttons preserve active tab and favorites; closing last tab stays usable", () =>
        {
            var store = TempStore();
            var initial = new BrowserSettings { RefreshSelected = false, Tabs = [
                new() { Name = "Quake Live", AppId = 282440 }, new() { Name = "Reflex", AppId = 328070 },
                new() { Name = "Toxikk", AppId = 324810 }, new() { Name = "Counter-Strike", AppId = 730 },
                new() { Name = "Team Fortress 2", AppId = 440 }, new() { Name = "Favorites", Source = ServerSource.Favorites, AppId = 0 }] };
            initial.Favorites["127.0.0.1:27015"] = "Preserve me";
            store.Save(initial);
            var window = new MainWindow(store); window.Show(); PumpUi();
            var tabs = window.FindControl<TabControl>("Tabs")!;
            var selected = tabs.SelectedItem;
            void Close(string name)
            {
                int index = tabs.Items.OfType<BrowserTab>().ToList().FindIndex(t => t.Name == name);
                var container = tabs.ContainerFromIndex(index) ?? throw new Exception("Tab was not realized");
                var close = container.GetVisualDescendants().OfType<Button>().Single(b => b.IsEffectivelyVisible && b.Classes.Contains("tabClose"));
                var point = close.TranslatePoint(new Point(close.Bounds.Width / 2, close.Bounds.Height / 2), window)!.Value;
                window.MouseDown(point, MouseButton.Left); window.MouseUp(point, MouseButton.Left); PumpUi();
                for (int i = 0; i < tabs.Items.Count; i++)
                {
                    var item = tabs.ContainerFromIndex(i)!;
                    var label = item.GetVisualDescendants().OfType<TextBlock>().First(t => t.Text != "×").Text;
                    Equal(((BrowserTab)tabs.Items[i]!).Name, label);
                }
            }
            try
            {
                Close("Reflex");
                Equal(5, tabs.Items.Count); Check(ReferenceEquals(selected, tabs.SelectedItem), "Closing an inactive tab changed selection");
                Close("Quake Live");
                Equal(4, tabs.Items.Count); Equal("Toxikk", ((BrowserTab)tabs.SelectedItem!).Name);
                Close("Favorites");
                Check(store.Load().Favorites.ContainsKey("127.0.0.1:27015"), "Closing favorites tab deleted favorite servers");
                while (tabs.Items.Count > 1) Close(((BrowserTab)tabs.Items[0]!).Name);
                Close(((BrowserTab)tabs.Items[0]!).Name);
                Equal(1, tabs.Items.Count); Equal("Master Server", ((BrowserTab)tabs.SelectedItem!).Name);
                Equal(1, store.Load().Tabs.Count);
            }
            finally { window.Close(); PumpUi(); }
            var restored = new MainWindow(store); restored.Show(); PumpUi();
            Equal(1, restored.FindControl<TabControl>("Tabs")!.Items.Count);
            restored.Close(); PumpUi();
            return Task.CompletedTask;
        });
        Run("Single Game field accepts dropdown games and custom IDs in actual master queries", () =>
        {
            var requested = new List<string>();
            using var gameServer = new UdpMock(_ => [InfoPacket()]);
            using var master = new UdpMock(packet =>
            {
                lock (requested) requested.Add(Encoding.UTF8.GetString(packet));
                return [MasterPacket(gameServer.Endpoint.ToString(), "0.0.0.0:0")];
            });
            var store = TempStore();
            store.Save(new BrowserSettings { RefreshSelected = false, Tabs = [new BrowserTab { Name = "Test", Master = master.Endpoint.ToString(), AppId = 282440 }] });
            var window = new MainWindow(store); window.Show(); PumpUi();
            try
            {
                var game = window.FindControl<AutoCompleteBox>("GameBox")!;
                Equal("Quake Live", game.Text);
                window.FindControl<Button>("GameDropDownButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); PumpUi();
                Check(game.IsDropDownOpen, "Game dropdown button did not open the list");
                game.IsDropDownOpen = false;
                game.Text = "265630"; PumpUi();
                window.FindControl<Button>("QueryButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                PumpUntil(() => window.FindControl<TextBlock>("StatusText")!.Text!.StartsWith("Complete"));
                Check(requested.Any(r => r.Contains("\\appid\\265630")), "Custom ID did not reach the master query");
                Equal(265630, store.Load().Tabs[0].AppId);
                game.SelectedItem = "Team Fortress 2"; PumpUi();
                Equal("Team Fortress 2", game.Text);
                window.FindControl<Button>("QueryButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                PumpUntil(() => window.FindControl<TextBlock>("StatusText")!.Text!.StartsWith("Complete"));
                Check(requested.Any(r => r.Contains("\\appid\\440")), "Dropdown selection did not reach the master query");
                game.Text = "2147483647"; PumpUi();
                window.Close(); PumpUi();
                Equal(2147483647, store.Load().Tabs[0].AppId);
            }
            finally { if (window.IsVisible) { window.Close(); PumpUi(); } }
            var restored = new MainWindow(store); restored.Show(); PumpUi();
            Equal("2147483647", restored.FindControl<AutoCompleteBox>("GameBox")!.Text);
            restored.Close(); PumpUi();
            return Task.CompletedTask;
        });
        Run("Light theme works with preferences hidden and survives restart", () =>
        {
            var store = TempStore(); store.Save(new BrowserSettings { RefreshSelected = false });
            var window = new MainWindow(store); window.Show(); PumpUi();
            try
            {
                window.FindControl<Border>("PreferencesPanel")!.IsVisible = false;
                window.FindControl<MenuItem>("LightThemeItem")!.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent)); PumpUi();
                Equal(ThemeVariant.Light, window.ActualThemeVariant);
                Equal(Colors.White, ((ISolidColorBrush)window.FindControl<DataGrid>("ServersGrid")!.Background!).Color);
                Check(!store.Load().DarkTheme, "Light theme was not saved");
                window.LoadPreviewRows([SampleRow()]); PumpUi();
                string prefix = "--screenshot-light=";
                var screenshot = args.FirstOrDefault(a => a.StartsWith(prefix))?[prefix.Length..];
                if (screenshot is not null)
                {
                    using var bitmap = window.CaptureRenderedFrame() ?? throw new Exception("No light frame");
                    bitmap.Save(screenshot);
                }
            }
            finally { window.Close(); PumpUi(); }
            var restored = new MainWindow(store); restored.Show(); PumpUi();
            Equal(ThemeVariant.Light, restored.ActualThemeVariant);
            restored.FindControl<MenuItem>("DarkThemeItem")!.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent)); PumpUi();
            Equal(ThemeVariant.Dark, restored.ActualThemeVariant); Check(store.Load().DarkTheme, "Dark selection was not saved");
            restored.Close(); PumpUi();
            return Task.CompletedTask;
        });
        RunPersistenceRegressions(args);
        RunInteractionRegressions(args);
        RunTypographyRegressions(args);
        RunCountryQueryRegressions();
        Console.WriteLine($"\n{passed} passed; {failed} failed.");
        return failed == 0 ? 0 : 1;
    }

    private static void Run(string name, Func<Task> test)
    {
        try { test().GetAwaiter().GetResult(); passed++; Console.WriteLine("PASS " + name); }
        catch (Exception ex) { failed++; Console.WriteLine("FAIL " + name + "\n" + ex); }
    }
    private static void PumpUi() { Dispatcher.UIThread.RunJobs(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(); }
    private static void PumpUntil(Func<bool> complete)
    {
        var end = DateTime.UtcNow.AddSeconds(8);
        while (DateTime.UtcNow < end) { PumpUi(); if (complete()) return; Thread.Sleep(10); }
        throw new Exception("UI operation did not complete");
    }
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static void Equal<T>(T expected, T actual) { if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception($"Expected {expected}; got {actual}"); }
    private static void Throws<T>(Action action) where T : Exception { try { action(); } catch (T) { return; } throw new Exception($"Expected {typeof(T).Name}"); }
    private static async Task ThrowsAsync<T>(Func<Task> action) where T : Exception { try { await action(); } catch (T) { return; } throw new Exception($"Expected {typeof(T).Name}"); }
    private static SettingsStore TempStore() => new(Path.Combine(Path.GetTempPath(), "ssb-tests-" + Guid.NewGuid().ToString("N")));
    private static Task<ServerEntry> Query(UdpMock mock) => new ServerBrowserService().QueryAsync(mock.Endpoint, 440, 500, false, default);
    private static ServerEntry SampleRow() => new()
    {
        Endpoint = new(IPAddress.Loopback, 27015), Country = "CN",
        Info = new ServerInfo { Name = "本地测试服务器", Map = "cp_test", Players = 6, Bots = 2, MaxPlayers = 24, Ping = 28,
            Description = "Team Fortress 2", Extra = new ExtraInfo { Keywords = "alltalk,custom", Port = 27016 } }
    };
    private static byte[] Packet(Action<BinaryWriter> body)
    {
        using var stream = new MemoryStream(); using var writer = new BinaryWriter(stream);
        writer.Write(-1); body(writer); return stream.ToArray();
    }
    private static void Text(BinaryWriter writer, string value) { writer.Write(Encoding.UTF8.GetBytes(value)); writer.Write((byte)0); }
    private static byte[] InfoPacket(string name = "本地测试服务器") => Packet(w =>
    {
        w.Write((byte)0x49); w.Write((byte)17); Text(w, name); Text(w, "cp_test"); Text(w, "tf"); Text(w, "Team Fortress 2");
        w.Write((ushort)440); w.Write((byte)6); w.Write((byte)24); w.Write((byte)2); w.Write((byte)'d'); w.Write((byte)'l');
        w.Write((byte)0); w.Write((byte)1); Text(w, "1.0"); w.Write((byte)0xa0); w.Write((ushort)27016); Text(w, "alltalk,custom");
    });
    private static byte[][] Split(byte[] original, bool compress = false)
    {
        byte[] data = original;
        var crc = new Crc32(); crc.Update(original);
        if (compress)
        {
            using var input = new MemoryStream(original); using var output = new MemoryStream();
            BZip2.Compress(input, output, false, 9); data = output.ToArray();
        }
        int half = data.Length / 2;
        return Enumerable.Range(0, 2).Select(i =>
        {
            using var stream = new MemoryStream(); using var w = new BinaryWriter(stream);
            w.Write(-2); w.Write(compress ? unchecked((int)0x80001234) : 0x1234);
            w.Write((byte)2); w.Write((byte)i); w.Write((ushort)1248);
            if (compress && i == 0) { w.Write(original.Length); w.Write((uint)crc.Value); }
            w.Write(i == 0 ? data[..half] : data[half..]); return stream.ToArray();
        }).ToArray();
    }
    private static byte[] MasterPacket(params string[] addresses)
    {
        using var output = new MemoryStream(); output.Write([255, 255, 255, 255, 0x66, 0x0a]);
        foreach (var address in addresses)
        {
            var ep = IPEndPoint.Parse(address); output.Write(ep.Address.GetAddressBytes());
            output.WriteByte((byte)(ep.Port >> 8)); output.WriteByte((byte)ep.Port);
        }
        return output.ToArray();
    }
    private static async Task<(int Type, string Text)> ReadRcon(NetworkStream stream)
    {
        byte[] head = new byte[4]; await stream.ReadExactlyAsync(head); byte[] body = new byte[BitConverter.ToInt32(head)];
        await stream.ReadExactlyAsync(body); return (BitConverter.ToInt32(body, 4), Encoding.UTF8.GetString(body, 8, body.Length - 10));
    }
    private static async Task WriteRcon(NetworkStream stream, int id, int type, string text)
    {
        using var output = new MemoryStream(); using var w = new BinaryWriter(output);
        byte[] body = Encoding.UTF8.GetBytes(text); w.Write(body.Length + 10); w.Write(id); w.Write(type); w.Write(body); w.Write((ushort)0);
        var data = output.ToArray();
        foreach (var b in data) await stream.WriteAsync(new byte[] { b });
    }

    private sealed class FakeHandler(Func<HttpRequestMessage, string> respond, HttpStatusCode status = HttpStatusCode.OK) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(respond(request)) });
    }
    private sealed class UdpMock : IDisposable
    {
        private readonly UdpClient udp;
        private readonly CancellationTokenSource cancellation = new();
        private readonly Task loop;
        public IPEndPoint Endpoint => (IPEndPoint)udp.Client.LocalEndPoint!;
        public UdpMock(Func<byte[], IEnumerable<byte[]>> respond, IPAddress? bindAddress = null)
        {
            udp = new UdpClient(new IPEndPoint(bindAddress ?? IPAddress.Loopback, 0));
            loop = Task.Run(async () =>
            {
                while (!cancellation.IsCancellationRequested)
                {
                    var request = await udp.ReceiveAsync(cancellation.Token);
                    foreach (var response in respond(request.Buffer)) await udp.SendAsync(response, request.RemoteEndPoint, cancellation.Token);
                }
            });
        }
        public void Dispose()
        {
            cancellation.Cancel(); udp.Dispose();
            try { loop.GetAwaiter().GetResult(); } catch (OperationCanceledException) { } catch (ObjectDisposedException) { }
            cancellation.Dispose();
        }
    }
}
