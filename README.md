# Steam Server Browser for Linux

Native Linux desktop port of [SteamServerBrowser](https://github.com/PredatH0r/SteamServerBrowser) 2.71, built with C#, .NET 10 and Avalonia.

Current version: **2.71.8**. Target platforms: **Ubuntu 22.04/24.04 and Debian 12/13, x86_64**.

## Download and run

Download the Linux portable archive from this repository's Releases page, then extract it:

```sh
tar -xzf SteamServerBrowser-2.71.8-linux-x64.tar.gz
cd SteamServerBrowser-2.71.8-linux-x64
./SteamServerBrowser
```

The archive includes the .NET runtime. No separate .NET installation or launcher script is required.

On the first Linux launch, the application creates a `SteamServerBrowser.desktop` file in its directory with the current executable and icon paths. Your desktop may require marking that file as trusted. Run the executable directly once after moving the directory to update those paths.

The desktop needs X11 or XWayland, fontconfig, ICU and OpenSSL. Common desktop libraries include `libx11-6 libice6 libsm6 libfontconfig1 libxext6 libxrender1 libxrandr2 libxi6 libxcursor1 libxfixes3 libgl1 xdg-utils`. Chinese text also benefits from `fonts-noto-cjk`. Steam and the relevant game must be installed to use Steam connection links.

## Features

- UDP master server and Steam Web API discovery.
- Source/GoldSource server information queries, players and rules, including challenge responses and compressed split packets.
- Live results: select a server and load its details while discovery is still running.
- Favorites, custom server lists, multiple tabs and Windows INI import.
- Filters for server name, map, country, tags, player count and latency.
- Offline MMDB country lookup with bundled flag icons; country filtering before server queries.
- Custom detail/rule columns, numeric sorting, column visibility, order, width and saved sorting.
- Best Fit for server and player columns, using complete rendered text and headers.
- Player lists sorted by score by default; durations displayed as hours, minutes and seconds.
- Light and dark themes, compact text and persistent panel visibility.
- Source TCP RCON and CSV export.

## Basic controls

**Find Servers** retrieves a new server list. **Update Status / F5** refreshes existing addresses only; it never falls back to master-server discovery. **Stop** cancels list and detail requests without discarding unqueried existing servers.

The **Game** dropdown is sorted by name and accepts custom App IDs. Click a tab's **×** to close it. A fresh configuration starts with one **Master Server** tab.

Right-click a column header to hide it, open **Choose Columns**, add a custom column, or use **Best Fit / Best Fit All Columns**. Custom fields include `Info.GameVersion`, `Info.Extra.SteamID` and `Rule.<rule name>`. Rules and server-details panels also offer column-creation menus. Wide fitted columns use horizontal scrolling.

**Options → Theme** selects Light or Dark. The General Preferences, Quick Filter and query-panel visibility settings survive restarts.

## API key and country database

Select or enter `<Steam Web API>` in the Master server field to use the Web API. Enter a key under **Options → GeoIP Service / Steam Web API Key** and save it. `STEAM_WEB_API_KEY` can supply an initial key if none is saved.

Configuration is stored at:

```text
${XDG_CONFIG_HOME:-$HOME/.config}/steam-server-browser/settings.json
```

The API key is stored in that local configuration file. On Linux the file is created with owner-only read/write permissions. RCON passwords are not saved. Do not commit local configuration files.

Country databases are not bundled. Select an existing ipinfo_lite, IP2Location or GeoLite2 MMDB file. Paths are relative to the executable directory, for example `GeoIp/ipinfo_lite.mmdb`; absolute paths from older configurations are converted on load. Unknown countries remain blank.

When **Country** is set, Find Servers first retrieves the master-server address list, checks those IPs against the local MMDB database, and sends A2S information, player and rule queries only to matching countries. Nonmatching and unknown countries are excluded before querying. Update Status and selected-server updates apply the same country check to their existing addresses. A selected country requires a working MMDB database; a missing database or failed lookup stops the query with an error. Clear Country to query all countries, including unknown locations.

## Build from source

Install the **.NET 10 SDK**, then run from the repository root:

```sh
sh build.sh
```

On Windows:

```powershell
.\build.ps1
# Or specify an SDK executable:
.\build.ps1 -Dotnet 'path\to\dotnet.exe'
```

The scripts run the automated tests, publish a self-contained `linux-x64` application and create the portable archive under `artifacts/packages/`.

Individual commands:

```sh
dotnet build SteamServerBrowser.sln -c Release
dotnet run --project ServerBrowser.Tests -c Release
dotnet publish ServerBrowser.Desktop -c Release -r linux-x64 --self-contained true -p:PublishTrimmed=false -o artifacts/linux-x64
dotnet run --project packaging/Packager.csproj -c Release -- artifacts/linux-x64 artifacts/packages .
```

Release builds omit debug symbols and use a stable source-path mapping. The repository does not require DevExpress or .NET Framework.

## Repository layout

```text
QueryMaster/             Reused query library and protocol fixes
ServerBrowser.Core/      Discovery, configuration, filters, GeoIP and RCON
ServerBrowser.Desktop/   Avalonia desktop UI and resources
ServerBrowser.Tests/     Loopback protocol and headless UI regression tests
packaging/               Portable archive builder
tools/                   Optional resource-maintenance utilities
licenses/                Third-party license texts
```

Country flags are already included. The optional extraction tool requires the original Windows source tree and is not part of a normal build.

## Testing and limitations

The automated suite contains 49 test groups covering UDP/TCP/HTTP protocol fixtures, country filtering before A2S requests, configuration, live selection, cancellation, column sizing, sorting and UI rendering. Country-filter tests count UDP requests and verify that excluded servers receive none. Network tests use local mock servers; they do not validate every public server or Linux desktop environment.

The port preserves the original compact arrangement, rather than reproducing DevExpress skins or floating docking. Windows key injection, global hotkeys and Steamworks IPC are not implemented. Game-specific Quake Live/Reflex/Toxikk statistics and integrations are not included. In-game auto-update pause uses best-effort Steam-process detection. RCON supports Source TCP; server lists currently use IPv4.

## Credits and licenses

See [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md), [licenses/](licenses/) and [QueryMaster/UPSTREAM.md](QueryMaster/UPSTREAM.md). Original copyright and license notices are retained. This repository does not relicense upstream code or assets.

Protocol and platform references: [Valve server queries](https://developer.valvesoftware.com/wiki/Server_queries), [Avalonia on Linux](https://docs.avaloniaui.net/docs/platform-specific-guides/linux).
