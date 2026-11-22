# SWYF Vietnamese mod uninstaller (Windows PowerShell 5.1). Run through GO-CAI-DAT.bat.
#   -GameDir <folder>  folder holding "Scam With Your Friends.exe" (found automatically when omitted)
#   -KeepVieNeu        keep <game>\SwyfMod (VieNeu-TTS, its Python and models)
#   -RemoveVieNeu      delete it without asking
param(
    [string]$GameDir = '',
    [switch]$KeepVieNeu,
    [switch]$RemoveVieNeu
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'common.ps1')

$KitDir = Split-Path -Parent $PSScriptRoot
Step 'Go mod tieng Viet'
$GameDir = Find-GameDir $GameDir $KitDir
$Data = Join-Path $GameDir $DataDirName
$Managed = Join-Path $Data 'Managed'
$Streaming = Join-Path $Data 'StreamingAssets'
$ModDir = Join-Path $GameDir 'SwyfMod'
Ok "Game: $GameDir"
Assert-GameClosed
Assert-Writable $Managed
Stop-ModServers $ModDir

# Every file the installer replaced has its untouched original next to it as <name>.swyf-orig.
$restored = 0
foreach ($dir in $Managed, $Streaming, (Join-Path $Streaming 'Whisper')) {
    Get-ChildItem -LiteralPath $dir -Filter ('*' + $OrigSuffix) -File -ErrorAction SilentlyContinue | ForEach-Object {
        $target = $_.FullName.Substring(0, $_.FullName.Length - $OrigSuffix.Length)
        Move-Item -LiteralPath $_.FullName -Destination $target -Force
        Ok "Khoi phuc $($_.Name.Substring(0, $_.Name.Length - $OrigSuffix.Length))"
        $restored++
    }
}
if ($restored -eq 0) { Warn 'Khong thay ban sao luu .swyf-orig nao (mod chua cai, hoac da go).' }

foreach ($f in (Join-Path $Managed 'SwyfCompat.dll'), (Join-Path $Streaming 'swyf-vilao.json'), (Join-Path $Streaming 'swyf-voice.json')) {
    if (Test-Path -LiteralPath $f) { Remove-Item -LiteralPath $f -Force; Ok "Xoa $(Split-Path -Leaf $f)" }
}
$piperDir = Join-Path $Streaming 'piper'
if (Test-Path -LiteralPath (Join-Path $piperDir 'README-swyf.txt')) { Remove-Item -LiteralPath $piperDir -Recurse -Force; Ok 'Xoa StreamingAssets\piper' }

if (Test-Path -LiteralPath $ModDir) {
    $remove = $RemoveVieNeu
    if (-not $RemoveVieNeu -and -not $KeepVieNeu) {
        $ans = Read-Host "  Xoa luon thu muc SwyfMod (VieNeu-TTS, Python, model giong; vai GB)? (Y = xoa / N = giu)"
        $remove = $ans -match '^\s*[yYcC]'
    }
    if ($remove) { Remove-Item -LiteralPath $ModDir -Recurse -Force; Ok 'Xoa SwyfMod' } else { Ok 'Giu SwyfMod' }
}

Step 'XONG'
Say '  Game da ve trang thai goc. Thu muc mod (va thu muc downloads trong do) co the xoa tay.' 'Green'
exit 0
