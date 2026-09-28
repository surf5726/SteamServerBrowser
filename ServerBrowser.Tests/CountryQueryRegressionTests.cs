using System.Net;
using Avalonia.Controls;
using ServerBrowser.Core;
using ServerBrowser.Desktop;

namespace ServerBrowser.Tests;

internal static partial class Program
{
    private static void RunCountryQueryRegressions()
    {
        Run("Country query plan filters unknown IPs, ignores case and caches each IP", () =>
        {
            int lookups = 0;
            using var geo = new FakeCountryLookup(ip => { lookups++; return TestCountry(ip); });
            IPEndPoint[] endpoints = [new(IPAddress.Loopback, 27015), new(IPAddress.Loopback, 27016),
                new(IPAddress.Parse("127.0.0.2"), 27015), new(IPAddress.Parse("127.0.0.3"), 27015)];
            var plan = CountryQueryPlan.Create(endpoints.Append(endpoints[0]), "cn; US", geo, default);
            Equal(3, plan.Targets.Count); Equal(1, plan.Excluded); Equal(3, lookups);
            Equal("CN", plan.Targets[0].Country); Equal("US", plan.Targets[2].Country);
            return Task.CompletedTask;
        });
        Run("An unfiltered query works without MMDB; a country filter requires it", () =>
        {
            using var geo = new GeoIp("");
            var endpoints = new[] { new IPEndPoint(IPAddress.Loopback, 27015) };
            Equal(1, CountryQueryPlan.Create(endpoints, "", geo, default).Targets.Count);
            Throws<InvalidOperationException>(() => CountryQueryPlan.Create(endpoints, "CN", geo, default));
            using var broken = new FakeCountryLookup(_ => throw new InvalidDataException("Invalid record"));
            var plan = CountryQueryPlan.Create(endpoints, "", broken, default);
            Equal(1, plan.Targets.Count); Check(plan.Warning is not null, "Lookup failure was not reported");
            return Task.CompletedTask;
        });
        Run("Master discovery sends no A2S info, player or rule requests to excluded countries", () =>
        {
            int allowedInfo = 0, allowedPlayers = 0, allowedRules = 0, excluded = 0, unknown = 0;
            using var allowed = new UdpMock(q =>
            {
                if (q[4] == 0x54) Interlocked.Increment(ref allowedInfo);
                if (q[4] == 0x55) Interlocked.Increment(ref allowedPlayers);
                if (q[4] == 0x56) Interlocked.Increment(ref allowedRules);
                return ReplyWithDetails(q, "Allowed");
            });
            using var foreign = new UdpMock(q => { Interlocked.Increment(ref excluded); return ReplyWithDetails(q, "Foreign"); }, IPAddress.Parse("127.0.0.2"));
            using var unmapped = new UdpMock(q => { Interlocked.Increment(ref unknown); return ReplyWithDetails(q, "Unknown"); }, IPAddress.Parse("127.0.0.3"));
            using var master = new UdpMock(_ => [MasterPacket(allowed.Endpoint.ToString(), foreign.Endpoint.ToString(), unmapped.Endpoint.ToString(), "0.0.0.0:0")]);
            var store = CountryStore(master, "cn");
            var window = new MainWindow(store, _ => new FakeCountryLookup(TestCountry)); window.Show(); PumpUi();
            try
            {
                ClickButton(window, "QueryButton"); WaitForUpdate(window); WaitForIdleDetails(window);
                var rows = VisibleRows(window.FindControl<DataGrid>("ServersGrid")!);
                Equal(1, rows.Count); Equal(allowed.Endpoint, rows[0].Endpoint); Equal("CN", rows[0].Country);
                Check(allowedInfo > 0 && allowedPlayers > 0 && allowedRules > 0, "Allowed server details were not queried");
                Equal(0, excluded); Equal(0, unknown);
                Check(window.FindControl<TextBlock>("StatusText")!.Text!.Contains("2 excluded by country before querying"), "Excluded count is missing");
                Check(window.FindControl<TextBlock>("CountText")!.Text!.Contains("Servers: 1 / 1"), "Excluded endpoints entered the server list");
            }
            finally { window.Close(); PumpUi(); }
            return Task.CompletedTask;
        });
        Run("A country with no matches makes zero server requests and clears the old discovery list", () =>
        {
            int requests = 0;
            using var game = new UdpMock(q => { Interlocked.Increment(ref requests); return ReplyWithDetails(q, "Old"); });
            using var master = new UdpMock(_ => [MasterPacket(game.Endpoint.ToString(), "0.0.0.0:0")]);
            var window = new MainWindow(CountryStore(master, ""), _ => new FakeCountryLookup(TestCountry)); window.Show(); PumpUi();
            try
            {
                ClickButton(window, "QueryButton"); WaitForUpdate(window); WaitForIdleDetails(window);
                int before = requests;
                window.FindControl<TextBox>("CountriesBox")!.Text = "JP";
                ClickButton(window, "QueryButton"); WaitForUpdate(window); WaitForIdleDetails(window);
                Equal(before, requests); Equal(0, VisibleRows(window.FindControl<DataGrid>("ServersGrid")!).Count);
            }
            finally { window.Close(); PumpUi(); }
            return Task.CompletedTask;
        });
        foreach (bool missingDatabase in new[] { true, false })
            Run(missingDatabase ? "Missing MMDB with a selected country makes zero A2S requests"
                : "MMDB lookup failure aborts the whole plan before any A2S request", () =>
            {
                int requests = 0;
                using var a = new UdpMock(q => { Interlocked.Increment(ref requests); return ReplyWithDetails(q, "First"); });
                using var b = new UdpMock(q => { Interlocked.Increment(ref requests); return ReplyWithDetails(q, "Second"); }, IPAddress.Parse("127.0.0.2"));
                using var master = new UdpMock(_ => [MasterPacket(a.Endpoint.ToString(), b.Endpoint.ToString(), "0.0.0.0:0")]);
                var window = new MainWindow(CountryStore(master, "CN"), _ => missingDatabase ? new GeoIp("")
                    : new FakeCountryLookup(ip => ip.Equals(IPAddress.Loopback) ? "CN" : throw new InvalidDataException("Invalid record")));
                window.Show(); PumpUi();
                try
                {
                    ClickButton(window, "QueryButton");
                    PumpUntil(() => window.FindControl<Button>("QueryButton")!.IsEnabled && window.FindControl<TextBlock>("StatusText")!.Text!.StartsWith("Query failed:"));
                    WaitForIdleDetails(window); Equal(0, requests);
                    Equal(0, VisibleRows(window.FindControl<DataGrid>("ServersGrid")!).Count);
                }
                finally { window.Close(); PumpUi(); }
                return Task.CompletedTask;
            });
        Run("Status and selected updates filter countries before A2S; clearing the filter restores discovery", () =>
        {
            int foreignRequests = 0, masterRequests = 0, allowedRequests = 0;
            using var allowed = new UdpMock(q => { Interlocked.Increment(ref allowedRequests); return ReplyWithDetails(q, "Allowed"); });
            using var foreign = new UdpMock(q => { Interlocked.Increment(ref foreignRequests); return ReplyWithDetails(q, "Foreign"); }, IPAddress.Parse("127.0.0.2"));
            using var master = new UdpMock(_ =>
            {
                Interlocked.Increment(ref masterRequests);
                return [MasterPacket(allowed.Endpoint.ToString(), foreign.Endpoint.ToString(), "0.0.0.0:0")];
            });
            var window = new MainWindow(CountryStore(master, ""), _ => new FakeCountryLookup(TestCountry)); window.Show(); PumpUi();
            try
            {
                var grid = window.FindControl<DataGrid>("ServersGrid")!;
                ClickButton(window, "QueryButton"); WaitForUpdate(window); WaitForIdleDetails(window);
                grid.SelectedItem = VisibleRows(grid).Single(r => r.Endpoint.Equals(foreign.Endpoint)); PumpUi(); WaitForIdleDetails(window);
                int foreignBefore = foreignRequests, masterBefore = masterRequests, allowedBefore = allowedRequests;
                window.FindControl<TextBox>("CountriesBox")!.Text = "CN";
                ClickMenu(window, "SelectedUpdateMenuItem");
                PumpUntil(() => window.FindControl<Button>("QueryButton")!.IsEnabled && window.FindControl<TextBlock>("StatusText")!.Text!.StartsWith("Updated 0"));
                WaitForIdleDetails(window); Equal(foreignBefore, foreignRequests);
                ClickButton(window, "RefreshButton"); WaitForUpdate(window); WaitForIdleDetails(window);
                Equal(foreignBefore, foreignRequests); Equal(masterBefore, masterRequests);
                Check(allowedRequests > allowedBefore, "Matching server was not refreshed");
                Equal(1, VisibleRows(grid).Count);
                window.FindControl<TextBox>("CountriesBox")!.Text = "";
                ClickButton(window, "QueryButton"); WaitForUpdate(window); WaitForIdleDetails(window);
                Equal(2, VisibleRows(grid).Count); Check(foreignRequests > foreignBefore, "Clearing country filter still excluded servers");
            }
            finally { window.Close(); PumpUi(); }
            return Task.CompletedTask;
        });
        Run("Stop during country lookup prevents all subsequent A2S requests", () =>
        {
            int requests = 0;
            using var entered = new ManualResetEventSlim();
            using var release = new ManualResetEventSlim();
            using var game = new UdpMock(q => { Interlocked.Increment(ref requests); return ReplyWithDetails(q, "Allowed"); });
            using var master = new UdpMock(_ => [MasterPacket(game.Endpoint.ToString(), "0.0.0.0:0")]);
            var window = new MainWindow(CountryStore(master, "CN"), _ => new FakeCountryLookup(ip =>
            {
                entered.Set(); release.Wait(TimeSpan.FromSeconds(5)); return TestCountry(ip);
            }));
            window.Show(); PumpUi();
            try
            {
                ClickButton(window, "QueryButton"); PumpUntil(() => entered.IsSet);
                ClickButton(window, "StopButton"); release.Set();
                PumpUntil(() => window.FindControl<Button>("QueryButton")!.IsEnabled); WaitForIdleDetails(window);
                Equal(0, requests); Equal(0, VisibleRows(window.FindControl<DataGrid>("ServersGrid")!).Count);
                Check(window.FindControl<TextBlock>("StatusText")!.Text!.StartsWith("Update stopped"), "Cancellation was not reported");
            }
            finally { release.Set(); window.Close(); PumpUi(); }
            return Task.CompletedTask;
        });
    }

    private static SettingsStore CountryStore(UdpMock master, string countries)
    {
        var store = TempStore();
        store.Save(new BrowserSettings { RefreshSelected = false, TimeoutMs = 500,
            Tabs = [new BrowserTab { Master = master.Endpoint.ToString(), AppId = 440, Countries = countries }] });
        return store;
    }
    private static void WaitForIdleDetails(MainWindow window) => PumpUntil(() => !window.FindControl<Button>("StopButton")!.IsEnabled);
    private static string TestCountry(IPAddress ip) => ip.ToString() switch { "127.0.0.1" => "CN", "127.0.0.2" => "US", _ => "" };
    private sealed class FakeCountryLookup(Func<IPAddress, string> lookup) : ICountryLookup
    {
        public bool IsAvailable => true;
        public string Lookup(IPAddress ip) => lookup(ip);
        public void Dispose() { }
    }
}
