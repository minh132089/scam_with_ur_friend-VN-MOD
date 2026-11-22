# Shared helpers for the SWYF Vietnamese mod installer / uninstaller (Windows PowerShell 5.1, ASCII only).

$GameExeName = 'Scam With Your Friends.exe'
$DataDirName = 'Scam With Your Friends_Data'
$OrigSuffix = '.swyf-orig'

# Original files of game build 0.1.89-swyf (SHA256). The patcher only runs on these exact files.
$OriginalManaged = [ordered]@{
    'Assembly-CSharp.dll'     = '3AAB14D5A9F517C7902E53E49F8B57C04E00607BF08AE4611332F793783A787B'
    'Kolkata.Speech.dll'      = '737E28564E6BE0F5D5CE28D679AF78B2D616FBF5591225E5E83EA0A26945D66D'
    'Kolkata.MossTtsNano.dll' = '7A01C755E57749B73F84B7296CACE378D002A81474EE8F8685BBCC67D965DC47'
    'com.whisper.unity.dll'   = '3912CE1585FE33097C1A597C951BD9D28D89E35AEE96B21BC80F94E97F26CAB4'
    'ScriptsAssDef.dll'       = '07E6BF770800EE75DDC3C5953232FE2CBBB2E85DD9933C0231092E0F3DB43403'
}
$OriginalLanguagesJson = 'B979F0A13834646DA85F7A3ECC83CD9528AE4583C22154C0D0619EA5F385657C'
$OriginalWhisperModel = '60ED5BC3DD14EEA856493D334349B405782DDCAF0028D4B5DF4088345FBA2EFE'

function Say([string]$text, [string]$color = 'Gray') { Write-Host $text -ForegroundColor $color }
function Step([string]$text) { Write-Host ''; Write-Host "== $text ==" -ForegroundColor Cyan }
function Ok([string]$text) { Write-Host "  [OK] $text" -ForegroundColor Green }
function Warn([string]$text) { Write-Host "  [!] $text" -ForegroundColor Yellow }

function Get-Sha256([string]$path) { return (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash }

function Test-GameDir([string]$dir) {
    if ([string]::IsNullOrWhiteSpace($dir)) { return $false }
    return (Test-Path -LiteralPath (Join-Path $dir $GameExeName)) -and (Test-Path -LiteralPath (Join-Path $dir "$DataDirName\Managed"))
}

# Finds the folder holding "Scam With Your Friends.exe": -GameDir, then around the mod folder, then asks.
function Find-GameDir([string]$given, [string]$kitDir) {
    if ($given) {
        if (Test-GameDir $given) { return (Resolve-Path -LiteralPath $given).Path }
        throw "Khong thay '$GameExeName' trong: $given"
    }
    $candidates = New-Object System.Collections.Generic.List[string]
    $dir = $kitDir
    for ($i = 0; $i -lt 4 -and $dir; $i++) {
        $candidates.Add($dir)
        Get-ChildItem -LiteralPath $dir -Directory -ErrorAction SilentlyContinue | ForEach-Object { $candidates.Add($_.FullName) }
        $dir = Split-Path -Parent $dir
    }
    foreach ($c in $candidates) { if (Test-GameDir $c) { return (Resolve-Path -LiteralPath $c).Path } }

    Say "Khong tu tim thay game. Hay chon thu muc co file '$GameExeName'." 'Yellow'
    Add-Type -AssemblyName System.Windows.Forms
    $dlg = New-Object System.Windows.Forms.FolderBrowserDialog
    $dlg.Description = "Chon thu muc chua '$GameExeName'"
    $dlg.ShowNewFolderButton = $false
    if ($dlg.ShowDialog() -ne [System.Windows.Forms.DialogResult]::OK) { throw 'Da huy: chua chon thu muc game.' }
    if (-not (Test-GameDir $dlg.SelectedPath)) { throw "Thu muc nay khong co '$GameExeName': $($dlg.SelectedPath)" }
    return $dlg.SelectedPath
}

function Assert-GameClosed {
    $running = Get-Process -Name 'Scam With Your Friends' -ErrorAction SilentlyContinue
    if ($running) { throw 'Game dang chay. Hay tat game roi chay lai.' }
}

function Assert-Writable([string]$dir) {
    $probe = Join-Path $dir ('.swyf-write-test-' + [guid]::NewGuid().ToString('N'))
    try { [IO.File]::WriteAllText($probe, 'x'); [IO.File]::Delete($probe) }
    catch { throw "Khong ghi duoc vao thu muc game ($dir). Chuot phai file .bat -> Run as administrator." }
}

# Stops VieNeu servers / uv helpers started from <game>\SwyfMod (the game normally stops them itself).
# With -Since, also uv-managed Pythons started after that time (they may live outside SwyfMod).
# Stop-Process only: taskkill /T needs WMI, which can hang on a broken Windows install.
function Stop-ModServers([string]$modDir, [datetime]$since = [datetime]::MinValue) {
    Get-Process -Name python, pythonw, uv -ErrorAction SilentlyContinue | Where-Object {
        $_.Path -and ($_.Path.StartsWith($modDir, [StringComparison]::OrdinalIgnoreCase) -or
            ($since -ne [datetime]::MinValue -and $_.StartTime -ge $since -and ($_.Path -like '*\uv\python\*' -or $_.ProcessName -eq 'uv')))
    } | Stop-Process -Force -ErrorAction SilentlyContinue
}

# Python 3.12+ asks WMI for platform info; when WMI is stuck, uv and VieNeu hang forever. Restarting Windows fixes it.
function Test-WmiResponsive([int]$seconds = 30) {
    $job = Start-Job { (Get-CimInstance Win32_OperatingSystem -ErrorAction Stop).Caption }
    $done = Wait-Job $job -Timeout $seconds
    $ok = [bool]$done -and $job.State -eq 'Completed'
    Remove-Job $job -Force -ErrorAction SilentlyContinue
    return $ok
}

function Write-Utf8NoBom([string]$path, [string]$text) {
    [IO.File]::WriteAllText($path, $text, (New-Object Text.UTF8Encoding($false)))
}
