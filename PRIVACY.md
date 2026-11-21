# CapPo privacy policy

_Last updated: 21 November 2026_

CapPo does not collect, store or transmit any personal data. It has no telemetry, analytics, crash reporting or advertising, and it has no accounts.

## What CapPo does on your PC

- **Reads your network settings.** CapPo finds your active network connection and the DNS servers that network handed out (DHCP). It never changes Windows' network or DNS settings.
- **Checks for a sign-in page.** CapPo fetches Microsoft's connectivity test page (`http://www.msftconnecttest.com/connecttest.txt`), the same page Windows itself uses to check for internet access. That request goes to Microsoft, as Windows' own check does. It contains nothing about you beyond what any web request does, such as your IP address.
- **Shows the sign-in page.** When a network wants you to sign in, CapPo shows its page in a window. What you type there goes to that network's sign-in page, not to CapPo. The page is loaded through a small proxy that runs on your PC (127.0.0.1) and is only for that window.
- **Keeps a few files on your PC.** Settings, a short log of what CapPo checked (network state, adapter names, DNS server addresses, sign-in page hosts), and the sign-in window's browser data (such as cookies a sign-in page sets). They stay on your PC:
  - GitHub version: `%LocalAppData%\CapPo`
  - Microsoft Store version: CapPo's app data folder, which Windows removes when you uninstall CapPo

Nothing in these files is ever sent anywhere.

## Microsoft Store version

The Store version asks the Microsoft Store whether you own CapPo or are on a trial. That's handled by Windows and the Microsoft Store under [Microsoft's privacy statement](https://privacy.microsoft.com/privacystatement). CapPo receives only the answer.

## Contact

Questions about this policy: open an issue at https://github.com/akrymskiy/cappo/issues.
