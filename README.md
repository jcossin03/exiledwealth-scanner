# Exiled Wealth scanner

The Windows tray app behind [Exiled Wealth](https://exiledwealth.com), a stash net
worth tracker for Path of Exile 2. GGG has no stash API for PoE2, so the scanner
reads your special stash tabs (Currency, Essences, Runes and so on) off the screen
when you press its read key and sends the **item counts** to your Exiled Wealth account.

This is the complete source of the app you install, public so you can check what
runs on your PC. (The website, and the script that packs and signs releases, are
in a separate private repository.)

## What it does

- It reads only when you press the **read key**: Ctrl+Shift+F12 unless you pick
  another (tray menu → Read key). It's a registered hotkey, not a keyboard hook:
  Windows tells the app about that one key combination and nothing else.
- Each press makes **one** copy of the game window's stash side (`CopyFromScreen`),
  and only while Path of Exile 2 is the window in front. There is no timer: between
  presses it reads nothing. This follows GGG's rule for third-party tools: "Reading
  from the screen or clipboard may only be performed once per manual key press"
  ([developer docs](https://www.pathofexile.com/developer/docs)). Before 0.5.0 it
  checked the screen every 2 s on its own.
- From that copy, on your PC, it checks the stash is open, works out which tab it
  is by matching item pictures against tab layouts from the game data, and reads
  each stack count by matching digit templates. The tab's name is read with
  Windows' built-in OCR.
- **Item copies, only if you turn them on** (tray menu → Record waystone and tablet
  copies; off unless you say yes to what it reads). The game puts an item's text on
  the clipboard when you press Ctrl+C on it. With this on, the scanner asks Windows
  to tell it when the clipboard changes (`AddClipboardFormatListener`: no keyboard
  hook, it never learns what you type). On a change it reads the clipboard once, and
  only if Path of Exile 2 is the window in front and the clipboard wasn't set by
  another program. If the text is a waystone or tablet, it's sent to your account so
  your sessions show what you ran; anything else is dropped on the spot, never kept,
  logged or sent ([`ItemCopy.cs`](EwCapture.Core/ItemCopy.cs)). The read follows your
  own key press, as GGG's rule above asks. The scanner never presses Ctrl+C itself.

## What it sends

Everything goes to exiledwealth.com over https, except item pictures, which come
from GGG's image server (`web.poecdn.com`).

| When | Request | Contents |
|---|---|---|
| Once, when you link | `POST /api/scanner/link` | the code from the website, and this PC's name (so you can tell your PCs apart on the website) |
| Reads waiting (30 s after your last press, at least every 2 min while you keep pressing) | `POST /api/scanner/sync` | for each tab you read: its type, name, position and when it was read, and per slot a slot number and a count (the slot says what item it holds) ([`Snapshots.cs`](EwCapture.Core/Snapshots.cs)) |
| Item copies waiting, with the reads (only with item copies on) | `POST /api/scanner/items` | for each waystone or tablet you copied: the item's text as the game wrote it, and when you copied it |
| At start and hourly | `GET /api/scanner/layouts` | nothing: downloads the tab layouts |
| At start and after errors | `GET /api/scanner/whoami` | nothing: checks the link still works |
| At start and every 6 h | `GET /api/scanner/releases/…` | nothing: checks for updates |
| Only on the dev server (below), when you press the read key | `POST /api/scanner/captures` | the same screen copy the read used, and its metadata |

Every request carries the app's version in its `User-Agent`.

## What it never does

- **No input.** It never clicks, presses keys or switches tabs.
- **No game memory or traffic, and it never changes game files.** It doesn't open the
  game's process or read its memory or network traffic. It reads one game file, and only
  in the diagnostics mode below: the display and UI sections of the game's settings file
  (`production_Config.ini`), never its login or network sections
  ([`GameConfig.cs`](EwCapture.Core/GameConfig.cs)).
- **No screenshots leave your PC.** The one exception is a diagnostics mode we use in
  development: the read key also uploads its copy of the whole game window (still one
  copy per press), with the display and UI sections of the game's settings file, to
  help fix misreads. Only the game, only while it's in front: if another window is in
  front, nothing is taken. The mode only exists while the scanner is pointed at our
  test server, `dev.exiledwealth.com` (tray menu → Server, which asks first and says
  this; `appsettings.json` next to the exe can only name the live site or that one).
  On the live site it's off, its menu items are hidden, and any queued screenshots are
  deleted at start.
- **No account details.** You link it with a code from the website.

## What it keeps on your PC

In `%LocalAppData%\ExiledWealth`: the device token (`token-exiledwealth.com.bin`,
encrypted for your Windows user with DPAPI), `log.txt` (up to 1 MB, includes tab
names), the tab layouts, cached item pictures (`icons/`), reads waiting to upload
(`snapshots/`), your read key if you changed it (`read-key.txt`), and, with item
copies on, a marker file (`item-copies.txt`) and the copies waiting to upload
(`item-copies/`; turning the option off deletes them). Unlink from the tray menu or the website to stop it; delete the folder
to remove everything.

## Updates

The scanner updates itself with [Velopack](https://velopack.io): it downloads an
update in the background and installs it when you quit or pick "Restart to update".
It only installs an update whose release list is signed with the Exiled Wealth
release key ([`SignedFeed.cs`](EwCapture.Core/SignedFeed.cs); public key in
[`Resources/update-key.pem`](EwCapture.Core/Resources/update-key.pem)). The list
holds each package's SHA-256, which Velopack checks on download. The private key is
kept offline, not on GitHub.

## Check your install

From 0.4.6 on, each version is built by GitHub Actions from a tag in this repository
([`release.yml`](.github/workflows/release.yml)) and published as an immutable
[release](https://github.com/jcossin03/exiledwealth-scanner/releases). The build's files carry a
signed provenance attestation, and the installer and updates carry them unchanged. With the
[GitHub CLI](https://cli.github.com), check your installed copy:

```powershell
gh attestation verify "$env:LOCALAPPDATA\ExiledWealthScanner\current\ew-capture.dll" `
  -R jcossin03/exiledwealth-scanner --source-ref refs/tags/v0.4.6
```

It prints the commit and workflow that built the file; a changed file has no attestation.
Our code is `ew-capture.dll` and `EwCapture.Core.dll` (`ew-capture.exe` is .NET's launcher).
The release script that packs the installer and signs the update feed is private: this
check is what ties your install to the public source.

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
