# Exiled Wealth scanner

The Windows tray app behind [Exiled Wealth](https://exiledwealth.com), a stash net
worth tracker for Path of Exile 2. GGG has no stash API for PoE2, so the scanner
reads your special stash tabs (Currency, Essences, Runes and so on) off the screen
while you play and sends the **item counts** to your Exiled Wealth account.

This is the complete source of the app you install, public so you can check what
runs on your PC. (The website, and the script that packs and signs releases, are
in a separate private repository.)

## What it does

- Every 2 s, if Path of Exile 2 is the window in front, it copies a thin strip of
  the screen to see whether the stash is open. Only then does it copy the stash
  panel (`CopyFromScreen`). With the game in the background or closed, it reads
  nothing (it just checks for the game's process).
- On your PC it works out which tab is open, by matching item pictures against
  tab layouts from the game data, and reads each stack count by matching digit
  templates. The tab's name is read with Windows' built-in OCR.
- **Ctrl+Shift+F12** (a registered hotkey, not a keyboard hook) reads the open tab
  right away. It too only reads the game while it's in front.

## What it sends

Everything goes to exiledwealth.com over https, except item pictures, which come
from GGG's image server (`web.poecdn.com`).

| When | Request | Contents |
|---|---|---|
| Once, when you link | `POST /api/scanner/link` | the code from the website, and this PC's name (so you can tell your PCs apart on the website) |
| A tab changed, or every 6 h | `POST /api/scanner/snapshots` | time, app version, game patch, the tab's type, name and match score, and per slot: item name, count, read confidence ([`Snapshots.cs`](EwCapture.Core/Snapshots.cs)) |
| You opened a tab that didn't change (at most every 2 min per tab) | `POST /api/scanner/seen` | the tab's type, name and when |
| At start and hourly | `GET /api/scanner/layouts` | nothing: downloads the tab layouts |
| At start and after errors | `GET /api/scanner/whoami` | nothing: checks the link still works |
| At start and every 6 h | `GET /api/scanner/releases/…` | nothing: checks for updates |

## What it never does

- **No input.** It never clicks, presses keys or switches tabs.
- **No game memory, files or traffic.** It doesn't open the game's process, read its
  memory or its network traffic, or read its files.
- **No screenshots leave your PC.** The one exception is a diagnostics mode we use in
  development: it can upload whole-window screenshots, with the display and UI
  sections of the game's settings file, to help fix misreads. It only exists when the
  scanner is pointed at our dev server (`dev.exiledwealth.com`, set in
  `appsettings.json` next to the exe); on the live site it's off, its menu items are
  hidden, and any queued screenshots are deleted at start.
- **No account details.** You link it with a code from the website.

## What it keeps on your PC

In `%LocalAppData%\ExiledWealth`: the device token (`token-exiledwealth.com.bin`,
encrypted for your Windows user with DPAPI), `log.txt` (up to 1 MB, includes tab
names), the tab layouts, cached item pictures (`icons/`), and reads waiting to upload
(`snapshots/`). Unlink from the tray menu or the website to stop it; delete the folder
to remove everything.

## Updates

The scanner updates itself with [Velopack](https://velopack.io): it downloads an
update in the background and installs it when you quit or pick "Restart to update".
It only installs an update whose release list is signed with the Exiled Wealth
release key ([`SignedFeed.cs`](EwCapture.Core/SignedFeed.cs); public key in
[`Resources/update-key.pem`](EwCapture.Core/Resources/update-key.pem)). The list
holds each package's SHA-256, which Velopack checks on download. The private key is
kept offline, not on GitHub.

## Check that a build matches this source

From 0.4.5 on, each version is built by GitHub Actions from a tag in this repository
([`release.yml`](.github/workflows/release.yml)) and published as a zip on the
[Releases](https://github.com/jcossin03/exiledwealth-scanner/releases) page with a
signed build provenance attestation. Check a zip with:

```
gh attestation verify ew-capture-<version>-win-x64.zip -R jcossin03/exiledwealth-scanner
```

The installer and updates carry that zip's files unchanged: after installing, compare
them with `%LocalAppData%\ExiledWealthScanner\current` (Velopack adds its own small
files there, such as `sq.version`). The release script that packs and signs them is
private, so this check is what ties your install to the public source.

## Build it yourself

You need the .NET 10 SDK. It builds on Windows, macOS or Linux (it only runs on Windows).

```
dotnet build EwCapture
dotnet test EwCapture.Tests --filter "FullyQualifiedName!~RecognitionTests&FullyQualifiedName!~TabStripTests&FullyQualifiedName!~StashGateTests&FullyQualifiedName!~StripCoverTests"
```

The skipped tests read real stash screenshots, which aren't in the repository:
they contain game art, which only GGG may distribute.

## License

[AGPL-3.0](LICENSE). You may read, build, change and share it. If you distribute a
changed version, you must publish its source under the same license.

Exiled Wealth isn't affiliated with or endorsed by Grinding Gear Games in any way.
Path of Exile is a trademark of Grinding Gear Games.
