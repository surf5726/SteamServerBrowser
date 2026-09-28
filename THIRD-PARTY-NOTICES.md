# Third-party components

This project is derived from [SteamServerBrowser](https://github.com/PredatH0r/SteamServerBrowser) 2.71, its modified QueryMaster library, and its original PNG/ICO assets.
Original copyrights and any applicable licenses continue to apply; this port does not relicense that code or those assets.
QueryMaster upstream history is preserved in `QueryMaster/UPSTREAM.md`.
The 241 country flag PNGs in `ServerBrowser.Desktop/Assets/Flags` are extracted unchanged from the original `ServerBrowserForm.resx` / `imgFlags.ImageStream` and mapped using the original designer's image keys. `tools/extract-original-flags.ps1` reproduces that extraction without deserializing DevExpress objects.

- Avalonia / Avalonia DataGrid — MIT, https://github.com/AvaloniaUI/Avalonia
- SharpZipLib 1.4.2 — MIT, https://github.com/icsharpcode/SharpZipLib
- MaxMind.Db 4.1.0 — Apache-2.0, https://github.com/maxmind/MaxMind-DB-Reader-dotnet
- .NET runtime — MIT and bundled notices, https://github.com/dotnet/runtime
- SkiaSharp / Skia and HarfBuzzSharp / HarfBuzz — their upstream and bundled licenses
- MicroCom.Runtime and Tmds.DBus.Protocol — their upstream licenses

Resolved dependencies and hashes are recorded in the project `packages.lock.json` files. Runtime license and notice files from the self-contained publish are retained.
No commercial DevExpress binaries or GeoIP databases are included.
