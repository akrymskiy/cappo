# CapPo

*Sign-in page? I know a guy.*

Keep encrypted DNS on, and still sign in to **Cap**tive **Po**rtals (airport, hotel, library and café Wi-Fi).

You've set Windows to encrypted DNS (DNS over HTTPS, e.g. NextDNS or Cloudflare), with fallback to plain DNS turned off. Then you join hotel Wi-Fi, and the sign-in page never appears: the network blocks encrypted DNS until you sign in, and you can't sign in without its DNS. The usual fix is to turn encrypted DNS off, sign in, and remember to turn it back on.

**CapPo** does that dance for you, without touching your settings. It notices the sign-in page, opens it in its own window, and closes the window once you're online.

## Do you need it?

Only if encrypted DNS is required on **every** network, including ones you haven't joined yet. Windows 11 keeps DNS settings per Wi-Fi network, so where you set it decides whether you need CapPo:

| Where encrypted DNS is set | On a new hotel network | CapPo |
|---|---|---|
| For all Wi-Fi networks (Settings > Network & internet > Wi-Fi > Hardware properties), unencrypted fallback off | Encrypted DNS is blocked, and the sign-in page never loads | **Needed** |
| By policy, "Require DoH" (Group Policy or Intune) | Same | **Needed** |
| Only for your home network | Windows uses the network's plain DNS, so the sign-in page loads, but nothing you look up there is encrypted | Not needed |
| Any network, with unencrypted fallback allowed | Windows quietly falls back to plain DNS when encrypted DNS fails, so the page loads, and anyone who can block encrypted DNS can switch you to plain DNS | Not needed |

So CapPo is for people who want encrypted DNS everywhere, with no exceptions. Apps that take over DNS themselves (VPNs, DNS filtering apps) have their own ways around portals; CapPo is for Windows' built-in encrypted DNS.

When CapPo opens the sign-in window, Windows may open its own browser at the portal too. That one can't load the page, because it uses your encrypted DNS; close it. CapPo's window stays on top until you click it.

## How it works

- **The network's DNS, inside the sign-in window only.** CapPo reads the DNS servers the network handed out (DHCP) and runs a tiny proxy inside the app that looks names up with them. Nothing outside your PC can reach it (it listens on 127.0.0.1 only). Only CapPo's sign-in window uses that proxy. Everything else keeps using your encrypted DNS.
- **Your system DNS is never touched.** CapPo doesn't change Windows' DNS settings, and it needs no administrator rights.
- **It checks the way Windows does.** CapPo fetches Microsoft's connectivity test page through its proxy. If something else answers, a portal is in the way, and CapPo opens the sign-in window. It also supports networks that announce their portal ([RFC 8910](https://www.rfc-editor.org/rfc/rfc8910) / [RFC 8908](https://www.rfc-editor.org/rfc/rfc8908)).
- **It notices when you're through.** CapPo keeps checking every few seconds while you sign in. Once you're online, it closes the window and flushes Windows' DNS cache, which still holds the failed lookups from before. Later it checks again every 5 to 60 minutes (your choice; at least 15 in the Store version's On demand mode), for portals that capture you again when a session runs out.

CapPo runs in one of two modes (Settings):

- **Always running** (default): stays in the notification area and reacts to network changes at once.
- **On demand**: doesn't stay running. Windows starts CapPo when a network connects, and every so often, for a check that takes about a second. CapPo shows up only when there's a sign-in page.

## Install

| | Microsoft Store | GitHub releases |
|---|---|---|
| Price | Paid, with a free trial | Free |
| Updates | Automatic | Manual |
| On demand mode uses | A background task | A Task Scheduler task (per user) |
| Code signed | Yes (by the Store) | No |
| License | GPL-3.0 | GPL-3.0 |

Both versions are built from this code and work the same way. The Store version is how you can support development if you like it. The GitHub version has no license checks, reminders or limits.

**GitHub version:** download the zip for your PC (x64 or ARM64) from [Releases](https://github.com/akrymskiy/cappo/releases), unzip it anywhere, and run `CapPo.exe`. It adds itself to the Start menu. Before deleting the folder, run `CapPo.exe --unregister` to remove its Start menu shortcut, startup entry and scheduled task. CapPo's settings and log are in `%LocalAppData%\CapPo`.

The GitHub version isn't code-signed, so Windows may warn you the first time you run it: click **More info**, then **Run anyway**. If Smart App Control blocks it (it offers no way past), or you'd rather skip the warning, get the Microsoft Store version: it's signed, updates itself, and supports development.

Requirements: Windows 10 version 2004 or later, or Windows 11. The sign-in window uses the Microsoft Edge WebView2 Runtime, which comes with Windows 11 and is on most Windows 10 PCs. If it's missing, CapPo says so when it starts, so you can [install it](https://go.microsoft.com/fwlink/p/?LinkId=2124703) while you're online.

Questions or problems: see [Support](https://cappo.krymskiy.net/support).

## Support the project

If CapPo saves you some hassle, you can [buy me a coffee](https://buymeacoffee.com/akrymskiy) or buy it from the Microsoft Store.

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

## Privacy

CapPo has no telemetry or analytics and collects nothing. See the [privacy policy](https://cappo.krymskiy.net/privacy).

## License

[GPL-3.0](LICENSE). Copyright © 2026 Alex Krymskiy. CapPo and its icon are trademarks of Alex Krymskiy.

*Made with love in Brooklyn.*
