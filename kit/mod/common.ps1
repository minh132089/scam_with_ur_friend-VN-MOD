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
    try {
        return [IO.File]::Exists([IO.Path]::Combine($dir, $GameExeName)) -and
            [IO.Directory]::Exists([IO.Path]::Combine($dir, $DataDirName, 'Managed'))
    }
    catch { return $false }
}

# True when the folder holds build 0.1.89-swyf: Assembly-CSharp.dll (or its .swyf-orig / .bak copy) is the original.
function Test-GameVersion([string]$dir) {
    $a = [IO.Path]::Combine($dir, $DataDirName, 'Managed', 'Assembly-CSharp.dll')
    foreach ($p in ($a + $OrigSuffix), $a, ($a + '.bak')) {
        if ([IO.File]::Exists($p) -and (Get-Sha256 $p) -eq $OriginalManaged['Assembly-CSharp.dll']) { return $true }
    }
    return $false
}

# Folders up to $maxDepth below each root that hold the game. Skips Windows' own folders; stops after $seconds.
function Search-GameDirs([string[]]$roots, [int]$maxDepth, [int]$seconds) {
    $skip = 'Windows', '$Recycle.Bin', 'System Volume Information', 'Recovery', 'PerfLogs', '$WinREAgent', 'Config.Msi',
        'ProgramData', 'MSOCache', '$SysReset', '$Windows.~WS', '$Windows.~BT', 'AppData', 'node_modules', '.git'
    $sw = [Diagnostics.Stopwatch]::StartNew()
    $found = New-Object System.Collections.Generic.List[string]
    $level = New-Object System.Collections.Generic.List[string]
    foreach ($r in $roots) { if ($r -and [IO.Directory]::Exists($r)) { $level.Add($r) } }
    for ($depth = 0; $depth -le $maxDepth -and $level.Count -gt 0; $depth++) {
        $next = New-Object System.Collections.Generic.List[string]
        foreach ($dir in $level) {
            if ($sw.Elapsed.TotalSeconds -gt $seconds) { return $found }
            if (Test-GameDir $dir) { $found.Add($dir); continue }
            if ($depth -eq $maxDepth) { continue }
            try {
                foreach ($sub in [IO.Directory]::EnumerateDirectories($dir)) {
                    if ($skip -notcontains [IO.Path]::GetFileName($sub)) { $next.Add($sub) }
                }
            }
            catch { }   # access denied, junctions, removed drives
        }
        $level = $next
    }
    return $found
}

# Steam library folders: <library>\steamapps\common\Scam With Your Friends
function Get-SteamGameDirs {
    $dirs = @()
    try {
        $steam = (Get-ItemProperty -Path 'HKCU:\Software\Valve\Steam' -Name SteamPath -ErrorAction Stop).SteamPath
        $libs = @($steam)
        $vdf = [IO.Path]::Combine($steam, 'steamapps', 'libraryfolders.vdf')
        if ([IO.File]::Exists($vdf)) {
            $libs += [regex]::Matches([IO.File]::ReadAllText($vdf), '"path"\s+"([^"]+)"') | ForEach-Object { $_.Groups[1].Value.Replace('\\', '\') }
        }
        $dirs = $libs | ForEach-Object { [IO.Path]::Combine($_.Replace('/', '\'), 'steamapps', 'common', 'Scam With Your Friends') }
    }
    catch { }
    return $dirs
}

# A hidden always-on-top window that owns our dialogs, so they open in front of the console.
function New-DialogOwner {
    Add-Type -AssemblyName System.Windows.Forms
    $f = New-Object System.Windows.Forms.Form
    $f.TopMost = $true
    $f.ShowInTaskbar = $false
    $f.FormBorderStyle = 'None'
    $f.Opacity = 0
    $f.StartPosition = 'CenterScreen'
    $f.Size = New-Object System.Drawing.Size(1, 1)
    $f.Show()
    $f.Activate()
    return $f
}

# Yes/No dialog. Returns $true for Yes.
function Ask-YesNo([string]$text, [string]$title, [bool]$defaultYes) {
    $owner = New-DialogOwner
    try {
        $default = if ($defaultYes) { [System.Windows.Forms.MessageBoxDefaultButton]::Button1 } else { [System.Windows.Forms.MessageBoxDefaultButton]::Button2 }
        $r = [System.Windows.Forms.MessageBox]::Show($owner, $text, $title, [System.Windows.Forms.MessageBoxButtons]::YesNo,
            [System.Windows.Forms.MessageBoxIcon]::Question, $default)
        return $r -eq [System.Windows.Forms.DialogResult]::Yes
    }
    finally { $owner.Dispose() }
}

# The game folder: -GameDir when given, otherwise the player points at "Scam With Your Friends.exe" in a file
# picker. The picker opens where the game probably is: the folder used last time (game-path.txt), else the
# first match around the mod folder, in Steam libraries, on every fixed drive (3 levels deep) or in
# Desktop / Downloads / Documents (a folder with build 0.1.89 wins).
function Find-GameDir([string]$given, [string]$kitDir) {
    $saved = Join-Path $kitDir 'game-path.txt'
    if ($given) {
        if (-not (Test-GameDir $given)) { throw "Khong thay '$GameExeName' trong: $given" }
        $result = (Resolve-Path -LiteralPath $given).Path
    }
    else {
        $start = $null
        if (Test-Path -LiteralPath $saved) {
            $last = (Get-Content -LiteralPath $saved -Raw -ErrorAction SilentlyContinue)
            if ($last -and (Test-GameDir $last.Trim())) { $start = $last.Trim() }
        }
        if (-not $start) { $start = Find-LikelyGameDir $kitDir }
        Say "  -> Chon file '$GameExeName' trong cua so vua mo roi bam Open." 'White'
        if ($start) { Say "     (cua so da mo san o: $start)" }
        $result = Select-GameExe $start
    }
    try { Set-Content -LiteralPath $saved -Value $result -Encoding UTF8 } catch { }   # remembered for next time / uninstall
    return $result
}

function Find-LikelyGameDir([string]$kitDir) {
    $fallback = $null
    $sources = @(
        { $near = @(); $dir = $kitDir; for ($i = 0; $i -lt 4 -and $dir; $i++) { $near += $dir; $dir = Split-Path -Parent $dir }; Search-GameDirs $near 1 10 },
        { @(Get-SteamGameDirs | Where-Object { Test-GameDir $_ }) },
        { $drives = [IO.DriveInfo]::GetDrives() | Where-Object { $_.DriveType -eq 'Fixed' -and $_.IsReady } | ForEach-Object { $_.RootDirectory.FullName }
          Search-GameDirs $drives 3 20 },
        { $u = $env:USERPROFILE; Search-GameDirs @("$u\Desktop", "$u\Downloads", "$u\Documents", "$u\Games") 3 10 }
    )
    foreach ($source in $sources) {
        foreach ($c in @(& $source)) {
            if (-not $c) { continue }
            if (Test-GameVersion $c) { return $c }
            if (-not $fallback) { $fallback = $c }
        }
    }
    return $fallback
}

# File picker filtered to "Scam With Your Friends.exe". Picking another file explains why and asks again.
function Select-GameExe([string]$startDir) {
    $owner = New-DialogOwner
    try {
        while ($true) {
            $dlg = New-Object System.Windows.Forms.OpenFileDialog
            $dlg.Title = "Chon file $GameExeName"
            $dlg.Filter = "$GameExeName|$GameExeName|File .exe khac|*.exe"
            $dlg.FileName = $GameExeName
            $dlg.CheckFileExists = $true
            $dlg.Multiselect = $false
            if ($startDir -and [IO.Directory]::Exists($startDir)) { $dlg.InitialDirectory = $startDir }
            if ($dlg.ShowDialog($owner) -ne [System.Windows.Forms.DialogResult]::OK) { throw 'Da huy: chua chon file game.' }
            $dir = [IO.Path]::GetDirectoryName($dlg.FileName)
            if (Test-GameDir $dir) { return $dir }
            [void][System.Windows.Forms.MessageBox]::Show($owner,
                ("File nay khong phai game:`n$($dlg.FileName)`n`nHay chon dung file '$GameExeName' " +
                 "(nam canh thu muc '$DataDirName')."), 'Mod tieng Viet', [System.Windows.Forms.MessageBoxButtons]::OK,
                [System.Windows.Forms.MessageBoxIcon]::Warning)
            $startDir = $dir
        }
    }
    finally { $owner.Dispose() }
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
