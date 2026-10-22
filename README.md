# Exiled Wealth scanner

The Windows tray app behind [Exiled Wealth](https://exiledwealth.com), a stash net
worth tracker for Path of Exile 2. GGG has no stash API for PoE2, so the scanner
reads your special stash tabs (Currency, Essences, Runes and so on) off the screen
while you play and sends the **item counts** to your Exiled Wealth account.

This repository is the complete source of the app you install. It's public so you
can check what runs on your PC instead of taking our word for it.

## What it does

- While Path of Exile 2 is the foreground window and a stash tab is open, it
  copies the stash panel's pixels from the screen (`CopyFromScreen`) every 2 s.
  When the stash is closed it does nothing.
- On your PC it works out which tab is open, by matching item pictures against
  the tab layouts from the game data, and reads each stack count, by matching
  digit templates. The tab's name is read with Windows' built-in OCR.
- It sends a small JSON record per tab: the tab's type, name and position, and
  for each slot the item name and count. See [`Snapshots.cs`](EwCapture.Core/Snapshots.cs).
  When you open a tab that hasn't changed, it sends a short "seen" note instead,
  at most once per tab every 2 minutes.
- It downloads tab layouts from exiledwealth.com, item pictures from GGG's image
  server (`web.poecdn.com`), and its own updates from exiledwealth.com.

## What it never does

- **No input.** It never clicks, presses keys or switches tabs. You play; it watches.
- **No game memory or files.** It doesn't read or touch the game process, its
  memory, its network traffic or its files.
- **No screenshots leave your PC.** Only the counts above are sent. (The one
  exception is a diagnostics mode for development, which can upload a tab's
  screenshot to help fix misreads. It exists only when the scanner points at our
  dev server, `dev.exiledwealth.com`; on the live site it's switched off and any
  queued screenshots are deleted.)
- **No account details.** You link it once with a code from the website; it keeps
  a device token, encrypted for your Windows user (DPAPI), in
  `%LocalAppData%\ExiledWealth`. You can unlink it from the website at any time.

## Updates

The scanner updates itself with [Velopack](https://velopack.io). It only installs
an update whose release list is signed with the Exiled Wealth release key
([`SignedFeed.cs`](EwCapture.Core/SignedFeed.cs); public key in
[`Resources/update-key.pem`](EwCapture.Core/Resources/update-key.pem)). The release
list holds the SHA-256 of every package, which Velopack checks on download.

## Check that a build matches this source

Each release is built by GitHub Actions from a tag in this repository
([`release.yml`](.github/workflows/release.yml)) and carries a signed build
provenance attestation. To check a download:

```
gh attestation verify ew-capture-<version>-win-x64.zip -R jcossin03/exiledwealth-scanner
```

The installer carries the files in that zip unchanged. After installing, they are in
`%LocalAppData%\ExiledWealthScanner\current` (next to Velopack's own updater), and
their hashes match the zip's.

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
