# CapPo

**Keep encrypted DNS on, and still sign in to hotel, library and café Wi-Fi.**

You've set Windows to encrypted DNS (DNS over HTTPS, e.g. NextDNS or Cloudflare), with fallback to plain DNS turned off. Then you join hotel Wi-Fi, and the sign-in page never appears: the network blocks encrypted DNS until you sign in, and you can't sign in without its DNS. The usual fix is to turn encrypted DNS off, sign in, and remember to turn it back on.

CapPo does that dance for you, without touching your settings. It notices the sign-in page, opens it in its own window, and closes the window once you're online.

## How it works

- **The network's DNS, inside the sign-in window only.** CapPo reads the DNS servers the network handed out (DHCP) and runs a small proxy on your PC (127.0.0.1) that looks names up with them. Only CapPo's sign-in window uses that proxy. Everything else keeps using your encrypted DNS.
- **Your system DNS is never touched.** CapPo doesn't change Windows' DNS settings, and it needs no administrator rights.
- **It checks the way Windows does.** CapPo fetches Microsoft's connectivity test page through its proxy. If something else answers, a portal is in the way, and CapPo opens the sign-in window. It also supports networks that announce their portal ([RFC 8910](https://www.rfc-editor.org/rfc/rfc8910) / [RFC 8908](https://www.rfc-editor.org/rfc/rfc8908)).
- **It notices when you're through.** CapPo keeps checking every few seconds while you sign in. Once you're online, it closes the window and flushes Windows' DNS cache, which still holds the failed lookups from before. Later it checks again every 15 to 60 minutes (your choice), for portals that capture you again when a session runs out.

CapPo runs in one of two modes (Settings):

- **Always running** (default): stays in the notification area and reacts to network changes at once.
- **On demand**: doesn't stay running. Windows starts CapPo when a network connects, and every so often, for a check that takes about a second. CapPo shows up only when there's a sign-in page.

## Install

| | Microsoft Store | GitHub releases |
|---|---|---|
| Price | Paid, with a free trial | Free |
| Updates | Automatic | Manual |
| On demand mode uses | A background task | A Task Scheduler task (per user) |
| License | GPL-3.0 | GPL-3.0 |

Both versions are built from this code and work the same way. The Store version is how you can support development if you like it. The GitHub version has no license checks, reminders or limits.

**GitHub version:** download the zip for your PC (x64 or ARM64) from [Releases](https://github.com/akrymskiy/cappo/releases), unzip it anywhere, and run `CapPo.exe`. Before deleting the folder, run `CapPo.exe --unregister` to remove its startup entry and scheduled task. CapPo's settings and log are in `%LocalAppData%\CapPo`.

Requirements: Windows 10 version 2004 or later, or Windows 11. The sign-in window needs the Microsoft Edge WebView2 Runtime, which comes with Windows 11.

## Support the project

If CapPo saves you some hassle, you can [sponsor it on GitHub](https://github.com/sponsors/akrymskiy) or buy it from the Microsoft Store.

## Building

Needs the .NET 10 SDK.

```
dotnet test --solution CapPo.slnx                                                   # tests
dotnet run --project src/CapPo.Poc                                                  # console proof of concept: proxy, DHCP DNS, probe
dotnet build src/CapPo.App -p:Platform=x64                                          # GitHub version (unpackaged)
dotnet publish src/CapPo.App -c Release -p:Platform=x64                             # GitHub release
dotnet build src/CapPo.App -p:Platform=x64 -p:StoreBuild=true                       # Store version (MSIX, defines STORE_BUILD)
```

There's a simulated captive portal for trying CapPo without a hotel: in a Debug build, run `CapPo.exe --simulate-portal` (or `--simulate-portal=2` for sign-ins that expire after 2 minutes).

| Path | What it is |
|---|---|
| `src/CapPo.Core` | Finding the network and its DHCP DNS, the proxy, the probe, and the monitor that ties them together |
| `src/CapPo.App` | The WinUI 3 app: tray icon, sign-in window (WebView2), Settings and About, both run modes |
| `src/CapPo.Poc` | Console proof of concept |
| `tests/CapPo.Core.Tests` | Tests, including the full sign-in cycle against the simulated portal |

## Name

"CapPo" is this project's name. If you fork it and publish your fork, please give it a different name and icon, so people can tell the two apart.

## Privacy

CapPo has no telemetry or analytics and collects nothing. See [PRIVACY.md](PRIVACY.md).

## License

[GPL-3.0](LICENSE). Copyright © 2026 Alex Krymskiy.
