[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$receiverName = 'Durak-iPhone'
$dataRoot = if ($env:DURAK_LINK_HOME) { $env:DURAK_LINK_HOME } else { Join-Path $env:LOCALAPPDATA 'DurakLink' }
$receiver = if ($env:DURAK_UXPLAY_EXE) { $env:DURAK_UXPLAY_EXE } else {
    Join-Path $dataRoot 'uxplay-windows-2.0.0.1736/uxplay-windows.exe'
}
if (-not (Test-Path -LiteralPath $receiver -PathType Leaf)) {
    throw "Portable UxPlay is missing: $receiver"
}
$receiver = (Resolve-Path -LiteralPath $receiver).Path
$runtimePath = Split-Path -Parent $receiver

$running = Get-Process -Name 'uxplay-windows' -ErrorAction SilentlyContinue
if ($running) {
    if ($running.Path -eq $receiver) {
        Write-Output "$receiverName is already running."
        return
    }
    throw 'Another copy of UxPlay is running. Close it before starting Durak Link.'
}

$configPath = Join-Path $env:APPDATA 'leapbtw/uxplay-windows/arguments.txt'
$pin = Get-Random -Minimum 1000 -Maximum 10000
$arguments = "-n $receiverName -nh -pin $pin"
if (Test-Path -LiteralPath $configPath) {
    $existing = (Get-Content -LiteralPath $configPath -Raw).Trim()
    if ($existing -ne '-n uxplay-windows -nh' -and $existing -notmatch '^\-n Durak-iPhone -nh -pin(?: [0-9]{4})?$') {
        throw "Existing UxPlay configuration needs review: $configPath"
    }
}

New-Item -ItemType Directory -Force -Path (Split-Path -Parent $configPath) | Out-Null
[System.IO.File]::WriteAllText($configPath, $arguments + [Environment]::NewLine)
$process = Start-Process -FilePath $receiver -WorkingDirectory $runtimePath -WindowStyle Hidden -PassThru
if ($process.WaitForExit(2000)) { throw "UxPlay exited during startup ($($process.ExitCode))." }
Write-Output "$receiverName started (PID $($process.Id))."
Write-Output "AirPlay pairing code: $pin"
