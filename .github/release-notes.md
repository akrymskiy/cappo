*Sign-in page? I know a guy.*

Sign in to captive portals (hotel, airport, library and café Wi-Fi) while Windows keeps your DNS encrypted. Only the sign-in window uses the portal's DNS. See [cappo.krymskiy.net](https://cappo.krymskiy.net/).

### Install

1. Download the zip for your PC: **x64** for most PCs, **ARM64** for PCs with an ARM processor such as Snapdragon (Settings > System > About > System type says which).
2. Unzip it anywhere, open the CapPo folder, and run `CapPo.exe`. It adds itself to the Start menu.

**Windows may warn you the first time** ("Windows protected your PC"), because this build isn't code-signed: click **More info**, then **Run anyway**. If Smart App Control blocks it, or you'd rather skip the warning, get the Microsoft Store version (coming soon). `SHA256SUMS.txt` has the zips' checksums.

To remove CapPo, run `CapPo.exe --unregister` (it removes its Start menu shortcut, startup entry and scheduled task), then delete the folder and `%LocalAppData%\CapPo`.

Needs Windows 10 version 2004 or later, or Windows 11. Questions or problems: [Support](https://cappo.krymskiy.net/support).
