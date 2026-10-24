# Starts the built app the way a player does and fails unless it gets through startup and stays up.
# 0.4.7 to 0.4.10 shipped with a static initializer that threw, so every launch died before the tray
# icon (and before the update check that could have fixed it). The tests never load App, so only
# running the exe catches that. Meant for a fresh Windows runner: the app starts unlinked, so no
# dialog waits for a click, and it makes one update check against the live feed.
param(
    [Parameter(Mandatory)] [string] $AppDir,
    [int] $Seconds = 20
)
$ErrorActionPreference = 'Stop'

$exe = Join-Path $AppDir 'ew-capture.exe'
if (-not (Test-Path $exe)) { throw "no ew-capture.exe in $AppDir" }
$info = (Get-Item $exe).VersionInfo
# App logs "start ew-capture X.Y.Z" as the last line of its constructor: the statics and the
# constructor both finished.
$version = '{0}.{1}.{2}' -f $info.FileMajorPart, $info.FileMinorPart, $info.FileBuildPart
$marker = "start ew-capture $version"
# One copy per user session (a mutex): a second one quits at once, which would look like a crash.
if (Get-Process ew-capture -ErrorAction SilentlyContinue) {
    throw 'ew-capture is already running: quit it first, the app allows one copy'
}

$log = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'ExiledWealth\log.txt'
$before = if (Test-Path $log) { @(Get-Content $log).Count } else { 0 }
function NewLogLines { if (Test-Path $log) { @(Get-Content $log | Select-Object -Skip $before) } else { @() } }

$started = Get-Date
$p = Start-Process $exe -WorkingDirectory $AppDir -PassThru
Write-Host "started $exe ($version), pid $($p.Id); watching for $Seconds s"
$failure = $null
for ($i = 0; $i -lt $Seconds; $i++) {
    Start-Sleep -Seconds 1
    if ($p.HasExited) { $failure = "exited after ${i} s with code $($p.ExitCode)"; break }
}
if (-not $failure -and -not ((NewLogLines) -match [regex]::Escape($marker))) {
    $failure = "still running, but the log never said '$marker'"
}

Write-Host '--- app log:'
NewLogLines | ForEach-Object { Write-Host "  $_" }
if (-not $p.HasExited) { Stop-Process -Id $p.Id -Force }

if ($failure) {
    Write-Host '--- crash reports since start:'
    Get-WinEvent -FilterHashtable @{ LogName = 'Application'; StartTime = $started } -ErrorAction SilentlyContinue |
        Where-Object { $_.ProviderName -in '.NET Runtime', 'Application Error' } |
        ForEach-Object { Write-Host $_.Message }
    Write-Host "::error::ew-capture $version failed to launch: $failure"
    exit 1
}
Write-Host "ew-capture $version launched and stayed up for $Seconds s"
