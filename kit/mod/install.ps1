# SWYF Vietnamese mod installer (Windows PowerShell 5.1). Run through CAI-DAT.bat.
#   -GameDir <folder>   folder holding "Scam With Your Friends.exe" (found automatically when omitted)
#   -VieNeu / -NoVieNeu install the VieNeu-TTS voices or not (asked when omitted)
#   -CacheDir <folder>  where downloads are kept (default: <mod kit>\downloads)
#   -DevReuseCaches     developer test only: reuse this PC's uv / Hugging Face caches
param(
    [string]$GameDir = '',
    [switch]$VieNeu,
    [switch]$NoVieNeu,
    [string]$CacheDir = '',
    [switch]$DevReuseCaches
)
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
. (Join-Path $PSScriptRoot 'common.ps1')

$KitDir = Split-Path -Parent $PSScriptRoot
if (-not $CacheDir) { $CacheDir = Join-Path $KitDir 'downloads' }

$Downloads = @{
    Whisper   = @{ Url = 'https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-large-v3-turbo-q5_0.bin'; File = 'ggml-large-v3-turbo-q5_0.bin'; Sha = '394221709CD5AD1F40C46E6031CA61BCE88931E6E088C188294C6D5A55FFA7E2'; Label = 'Whisper large-v3-turbo (547 MB)' }
    Piper     = @{ Url = 'https://github.com/rhasspy/piper/releases/download/2023.11.14-2/piper_windows_amd64.zip'; File = 'piper_windows_amd64.zip'; Sha = 'F3C58906402B24F3A96D92145F58ACBA6D86C9B5DB896D207F78DC80811EFCEA'; Label = 'Piper TTS (21 MB)' }
    VoiceOnnx = @{ Url = 'https://huggingface.co/rhasspy/piper-voices/resolve/main/vi/vi_VN/vais1000/medium/vi_VN-vais1000-medium.onnx'; File = 'vi_VN-vais1000-medium.onnx'; Sha = '0567643F67B8329A46B3F0B549BEFC58E1E7E21EF558677768068D23A41D8471'; Label = 'giong Piper tieng Viet (60 MB)' }
    VoiceJson = @{ Url = 'https://huggingface.co/rhasspy/piper-voices/resolve/main/vi/vi_VN/vais1000/medium/vi_VN-vais1000-medium.onnx.json'; File = 'vi_VN-vais1000-medium.onnx.json'; Sha = '19240AB1EB13E5EB9C7DD00F9D1DE5DB08FF88D591C5008CBD24F1A25F51E13A'; Label = 'cau hinh giong Piper' }
    Uv        = @{ Url = 'https://github.com/astral-sh/uv/releases/download/0.11.6/uv-x86_64-pc-windows-msvc.zip'; File = 'uv-0.11.6-x86_64-pc-windows-msvc.zip'; Sha = '99AA60EDD017A256DBF378F372D1CFF3292DBC6696E0EA01716D9158D773AB77'; Label = 'uv (22 MB)' }
    VieNeuSrc = @{ Url = 'https://github.com/pnnbao97/VieNeu-TTS/archive/refs/tags/v3.8.5.zip'; File = 'VieNeu-TTS-v3.8.5.zip'; Sha = ''; Label = 'ma nguon VieNeu-TTS v3.8.5' }
}
$VieNeuVersion = 'v3.8.5'

function Get-Download([hashtable]$d) {
    New-Item -ItemType Directory -Force -Path $CacheDir | Out-Null
    $dest = Join-Path $CacheDir $d.File
    if ((Test-Path -LiteralPath $dest) -and $d.Sha -and ((Get-Sha256 $dest) -eq $d.Sha)) { Ok "$($d.Label): da co san"; return $dest }
    if ((Test-Path -LiteralPath $dest) -and -not $d.Sha -and (Get-Item -LiteralPath $dest).Length -gt 0) { Ok "$($d.Label): da co san"; return $dest }
    for ($attempt = 1; $attempt -le 2; $attempt++) {
        Say "  Dang tai $($d.Label) ..."
        if ($attempt -eq 1) { & curl.exe -L --fail --retry 3 --retry-delay 3 -C - -o $dest $d.Url }
        else { if (Test-Path -LiteralPath $dest) { Remove-Item -LiteralPath $dest -Force }; & curl.exe -L --fail --retry 3 --retry-delay 3 -o $dest $d.Url }
        if ($LASTEXITCODE -eq 0 -and (Test-Path -LiteralPath $dest)) {
            if (-not $d.Sha -or (Get-Sha256 $dest) -eq $d.Sha) { Ok $d.Label; return $dest }
            Warn "File tai ve bi loi (sai SHA256), tai lai..."
        }
    }
    throw "Khong tai duoc $($d.Label) tu $($d.Url). Kiem tra mang roi chay lai CAI-DAT.bat (file da tai se duoc giu)."
}

function Expand-ZipTo([string]$zip, [string]$dest) {
    if (Test-Path -LiteralPath $dest) { Remove-Item -LiteralPath $dest -Recurse -Force }
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [IO.Compression.ZipFile]::ExtractToDirectory($zip, $dest)
}

# 1) Locate the game and check every original file before touching anything --------------------------------
Write-Host ''
Write-Host '  Scam With Your Friends - Mod tieng Viet (AI Vilao, Whisper GPU, giong doc VieNeu / Piper)' -ForegroundColor White
Step 'Kiem tra game'
Get-ChildItem -LiteralPath $KitDir -Recurse -File -ErrorAction SilentlyContinue | Unblock-File -ErrorAction SilentlyContinue
$GameDir = Find-GameDir $GameDir $KitDir
$Data = Join-Path $GameDir $DataDirName
$Managed = Join-Path $Data 'Managed'
$Streaming = Join-Path $Data 'StreamingAssets'
$ModDir = Join-Path $GameDir 'SwyfMod'
Ok "Game: $GameDir"
Assert-GameClosed
Assert-Writable $Managed
if (-not (Test-Path -LiteralPath (Join-Path $Data 'Plugins\x86_64\libwhisper.dll'))) { throw 'Thieu Plugins\x86_64\libwhisper.dll: ban game khong day du.' }

# For each patched file: its untouched original must be the file itself, <name>.swyf-orig (earlier install)
# or <name>.bak (manual backup). Returns the path of the original to back up, or $null if the backup exists.
function Resolve-Original([string]$path, [string]$sha) {
    $orig = $path + $OrigSuffix
    if (Test-Path -LiteralPath $orig) {
        if ((Get-Sha256 $orig) -eq $sha) { return $null }
        throw "Ban sao luu $orig khong phai file goc. Khong thay doi gi."
    }
    if ((Test-Path -LiteralPath $path) -and (Get-Sha256 $path) -eq $sha) { return $path }
    if ((Test-Path -LiteralPath ($path + '.bak')) -and (Get-Sha256 ($path + '.bak')) -eq $sha) { return $path + '.bak' }
    return 'MISMATCH'
}

$plan = [ordered]@{}
$mismatch = @()
foreach ($name in $OriginalManaged.Keys) {
    $p = Join-Path $Managed $name
    $plan[$p] = Resolve-Original $p $OriginalManaged[$name]
    if ($plan[$p] -eq 'MISMATCH') { $mismatch += "Managed\$name" }
}
$langPath = Join-Path $Streaming 'kolkata-speech-languages.json'
$plan[$langPath] = Resolve-Original $langPath $OriginalLanguagesJson
if ($plan[$langPath] -eq 'MISMATCH') { $mismatch += 'StreamingAssets\kolkata-speech-languages.json' }
if ($mismatch.Count -gt 0) {
    Say ''
    Say 'Cac file sau khong khop ban game 0.1.89-swyf ma mod nay danh cho:' 'Red'
    $mismatch | ForEach-Object { Say "   $_" 'Red' }
    Say 'Ban game cua ban khac ban cua nguoi gui mod (vi du ban Steam moi hon). Khong thay doi gi.' 'Red'
    exit 3
}
Ok 'Dung ban game 0.1.89-swyf'

$whisperPath = Join-Path $Streaming 'Whisper\ggml-base.bin'
$whisperState = 'skip'
if (Test-Path -LiteralPath $whisperPath) {
    $wh = Get-Sha256 $whisperPath
    if ($wh -eq $Downloads.Whisper.Sha) { $whisperState = 'done' }
    elseif ($wh -eq $OriginalWhisperModel) { $whisperState = 'replace' }
    else { $whisperState = 'unknown' }
}

# 2) Choose VieNeu --------------------------------------------------------------------------------------
Add-Type -AssemblyName Microsoft.VisualBasic
$ramGb = [math]::Round((New-Object Microsoft.VisualBasic.Devices.ComputerInfo).TotalPhysicalMemory / 1GB)
$useVieNeu = $false
if ($VieNeu) { $useVieNeu = $true }
elseif (-not $NoVieNeu) {
    Step 'Giong doc'
    Say '  Piper (luon cai): nhe, 1 giong. VieNeu-TTS (tuy chon): 25 giong nam/nu tu nhien hon,'
    Say '  tai them ~1.8 GB, dung them ~1.5 GB RAM khi choi, can CPU kha.'
    $hint = if ($ramGb -ge 16) { 'nen cai' } else { 'may it RAM, nen bo qua' }
    Say "  May nay co $ramGb GB RAM ($hint)."
    $ans = Read-Host '  Cai them VieNeu-TTS? (Y = co / N = khong)'
    $useVieNeu = $ans -match '^\s*[yYcC]'
}

# 3) Back up originals, patch -----------------------------------------------------------------------------
Step 'Sao luu file goc'
foreach ($p in @($plan.Keys)) {
    $src = $plan[$p]
    if ($null -eq $src) { Ok "$(Split-Path -Leaf $p): da co ban goc $OrigSuffix"; continue }
    Copy-Item -LiteralPath $src -Destination ($p + $OrigSuffix) -Force
    Ok "$(Split-Path -Leaf $p) -> $(Split-Path -Leaf $p)$OrigSuffix"
}

Step 'Va DLL cua game'
$staging = Join-Path $env:TEMP ('swyf-mod-' + [guid]::NewGuid().ToString('N'))
$patcher = Join-Path $PSScriptRoot 'SwyfPatcher.exe'
$compat = Join-Path $PSScriptRoot 'SwyfCompat.dll'
Push-Location $PSScriptRoot
try { & $patcher all $Managed $OrigSuffix $staging $compat | ForEach-Object { Say "  $_" 'DarkGray' } }
finally { Pop-Location }
if ($LASTEXITCODE -ne 0) { throw "Va DLL that bai (ma $LASTEXITCODE). Game chua bi thay doi gi ngoai ban sao luu." }
foreach ($name in $OriginalManaged.Keys) { Copy-Item -LiteralPath (Join-Path $staging $name) -Destination (Join-Path $Managed $name) -Force }
Copy-Item -LiteralPath $compat -Destination (Join-Path $Managed 'SwyfCompat.dll') -Force
Remove-Item -LiteralPath $staging -Recurse -Force -ErrorAction SilentlyContinue
Ok 'Da cai 5 DLL da va + SwyfCompat.dll'

Step 'Them tieng Viet vao danh sach ngon ngu cuoc goi'
$langText = [IO.File]::ReadAllText($langPath + $OrigSuffix, [Text.Encoding]::UTF8)
$nl = if ($langText.Contains("`r`n")) { "`r`n" } else { "`n" }
$viName = 'Ti' + [char]0x1EBF + 'ng Vi' + [char]0x1EC7 + 't'
$close = $langText.LastIndexOf(']')
$lastObj = $langText.LastIndexOf('}', $close)
$entry = ",$nl    {$nl      `"code`": `"vi`",$nl      `"nativeName`": `"$viName`",$nl      `"englishName`": `"Vietnamese`",$nl      `"hasPocket`": false,$nl      `"pocketFolder`": `"`"$nl    }"
Write-Utf8NoBom $langPath ($langText.Substring(0, $lastObj + 1) + $entry + $langText.Substring($lastObj + 1))
Ok 'kolkata-speech-languages.json: da them "vi"'

$vilaoCfg = Join-Path $Streaming 'swyf-vilao.json'
if (-not (Test-Path -LiteralPath $vilaoCfg)) { Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'swyf-vilao.json') -Destination $vilaoCfg }
Ok 'swyf-vilao.json (AI: chi can khi ban lam chu phong)'

# 4) Whisper large-v3-turbo -------------------------------------------------------------------------------
Step 'Whisper (nhan giong noi tieng Viet)'
if ($whisperState -eq 'done') { Ok 'Model large-v3-turbo da co' }
elseif ($whisperState -eq 'unknown') { Warn 'ggml-base.bin la model la (khong phai ban goc, khong phai turbo): giu nguyen.' }
else {
    $model = Get-Download $Downloads.Whisper
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $whisperPath) | Out-Null
    if ($whisperState -eq 'replace' -and -not (Test-Path -LiteralPath ($whisperPath + $OrigSuffix))) {
        Move-Item -LiteralPath $whisperPath -Destination ($whisperPath + $OrigSuffix)
    }
    Copy-Item -LiteralPath $model -Destination $whisperPath -Force
    Ok 'Da thay model Whisper bang large-v3-turbo (ban goc: ggml-base.bin.swyf-orig)'
}

# 5) Piper (always: fallback voice) -------------------------------------------------------------------------
Step 'Piper (giong doc du phong)'
$piperDir = Join-Path $Streaming 'piper'
$piperZip = Get-Download $Downloads.Piper
$onnx = Get-Download $Downloads.VoiceOnnx
$voiceJson = Get-Download $Downloads.VoiceJson
$extract = Join-Path $CacheDir 'piper-extract'
Expand-ZipTo $piperZip $extract
New-Item -ItemType Directory -Force -Path $piperDir | Out-Null
Copy-Item -Path (Join-Path $extract 'piper\*') -Destination $piperDir -Recurse -Force
Remove-Item -LiteralPath $extract -Recurse -Force
$voicesDir = Join-Path $piperDir 'voices'
New-Item -ItemType Directory -Force -Path $voicesDir | Out-Null
Copy-Item -LiteralPath $onnx -Destination (Join-Path $voicesDir 'vi_VN-vais1000-medium.onnx') -Force
# This Piper 1.2 build rejects the voice's 5 multi-codepoint English diphthong entries; Vietnamese is unaffected.
# (Case-sensitive parser: the map has both "x" and "X", which ConvertFrom-Json rejects.)
Add-Type -AssemblyName System.Web.Extensions
$ser = New-Object System.Web.Script.Serialization.JavaScriptSerializer
$ser.MaxJsonLength = 10MB
$j = $ser.DeserializeObject([IO.File]::ReadAllText($voiceJson, [Text.Encoding]::UTF8))
$map = $j['phoneme_id_map']
$multi = @($map.Keys | Where-Object { $_.Length -gt 1 })
foreach ($k in $multi) { [void]$map.Remove($k) }
Write-Utf8NoBom (Join-Path $voicesDir 'vi_VN-vais1000-medium.onnx.json') ($ser.Serialize($j))
Write-Utf8NoBom (Join-Path $piperDir 'README-swyf.txt') ("Piper (https://github.com/rhasspy/piper, release 2023.11.14-2, MIT License) - offline text-to-speech.`r`n" +
    "Voice: vi_VN-vais1000-medium from https://huggingface.co/rhasspy/piper-voices (VAIS-1000 corpus, CC BY 4.0).`r`n" +
    "The voice's .onnx.json had $($multi.Count) multi-codepoint English diphthong entries removed so this Piper 1.2 build can load it.`r`n" +
    "Installed by the SWYF Vietnamese mod; removed by GO-CAI-DAT.bat.`r`n")
Ok "Piper + giong vi_VN-vais1000 (bo $($multi.Count) am tieng Anh)"

# 6) VieNeu-TTS (optional) --------------------------------------------------------------------------------
$engine = 'piper'
if ($useVieNeu) {
    Step 'VieNeu-TTS (25 giong)'
    try {
        $vnDir = Join-Path $ModDir 'VieNeu-TTS'
        New-Item -ItemType Directory -Force -Path $ModDir | Out-Null
        $marker = Join-Path $vnDir 'swyf-version.txt'
        $haveSrc = (Test-Path -LiteralPath $marker) -and ((Get-Content -LiteralPath $marker -Raw).Trim() -eq $VieNeuVersion)
        if (-not $haveSrc) {
            Stop-ModServers $ModDir
            $srcZip = Get-Download $Downloads.VieNeuSrc
            $tmp = Join-Path $ModDir '_vieneu-extract'
            Expand-ZipTo $srcZip $tmp
            $inner = Get-ChildItem -LiteralPath $tmp -Directory | Select-Object -First 1
            if (-not $inner -or -not (Test-Path -LiteralPath (Join-Path $inner.FullName 'apps\openai_speech.py'))) { throw 'Ma nguon VieNeu tai ve khong dung cau truc.' }
            if (Test-Path -LiteralPath $vnDir) { Remove-Item -LiteralPath $vnDir -Recurse -Force }
            Move-Item -LiteralPath $inner.FullName -Destination $vnDir
            Remove-Item -LiteralPath $tmp -Recurse -Force
            Set-Content -LiteralPath $marker -Value $VieNeuVersion -Encoding ASCII
        }
        Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'swyf_launch.py') -Destination (Join-Path $vnDir 'swyf_launch.py') -Force
        Ok "Ma nguon VieNeu-TTS $VieNeuVersion"

        $uv = (Get-Command uv -ErrorAction SilentlyContinue).Source
        if (-not $uv) { foreach ($c in (Join-Path $env:USERPROFILE '.local\bin\uv.exe'), (Join-Path $ModDir 'tools\uv\uv.exe')) { if (Test-Path -LiteralPath $c) { $uv = $c; break } } }
        if (-not $uv) {
            $uvZip = Get-Download $Downloads.Uv
            Expand-ZipTo $uvZip (Join-Path $ModDir 'tools\uv')
            $uv = (Get-ChildItem -LiteralPath (Join-Path $ModDir 'tools\uv') -Recurse -Filter uv.exe | Select-Object -First 1).FullName
        }
        Ok "uv: $uv"

        if (-not $DevReuseCaches) {
            $env:UV_CACHE_DIR = Join-Path $ModDir 'uv-cache'
            $env:UV_PYTHON_INSTALL_DIR = Join-Path $ModDir 'python'
            New-Item -ItemType Directory -Force -Path (Join-Path $ModDir 'hf-cache') | Out-Null   # swyf_launch.py keeps models here
        }
        $env:UV_PYTHON_PREFERENCE = 'only-managed'
        if (-not (Test-WmiResponsive)) {
            throw 'WMI cua Windows dang bi treo (Python se treo theo). Khoi dong lai may roi chay lai CAI-DAT.bat.'
        }
        Say '  Dang cai Python + thu vien cho VieNeu (lan dau ~1 GB, vai phut)...'
        $uvStart = Get-Date
        $uvProc = Start-Process -FilePath $uv -ArgumentList @('sync', '--frozen', '--no-progress', '--project', "`"$vnDir`"") -NoNewWindow -PassThru
        $null = $uvProc.Handle   # keeps ExitCode available after exit
        if (-not $uvProc.WaitForExit(30 * 60 * 1000)) {
            Stop-Process -Id $uvProc.Id -Force -ErrorAction SilentlyContinue
            Stop-ModServers $ModDir $uvStart
            throw 'uv sync qua 30 phut khong xong (mang cham, hoac WMI treo: khoi dong lai may roi chay lai).'
        }
        if ($uvProc.ExitCode -ne 0) { throw "uv sync that bai (ma $($uvProc.ExitCode))" }
        Ok 'Moi truong Python cho VieNeu'

        Say '  Dang tai model VieNeu va thu khoi dong server (lan dau co the mat vai phut)...'
        $py = Join-Path $vnDir '.venv\Scripts\pythonw.exe'
        $warmLog = Join-Path $ModDir 'vieneu-install-test.log'
        $port = 18765
        $warmStart = Get-Date
        $proc = Start-Process -FilePath $py -ArgumentList @("`"$(Join-Path $vnDir 'swyf_launch.py')`"", "`"$warmLog`"", '0', "$port") -WorkingDirectory $vnDir -WindowStyle Hidden -PassThru
        $ready = $false
        $sw = [Diagnostics.Stopwatch]::StartNew()
        try {
            while ($sw.Elapsed.TotalMinutes -lt 25 -and -not $proc.HasExited) {
                Start-Sleep -Seconds 3
                try {
                    $r = Invoke-WebRequest -Uri "http://127.0.0.1:$port/health" -UseBasicParsing -TimeoutSec 3
                    if ($r.StatusCode -eq 200) { $ready = $true; break }
                } catch { }
                if ([int]$sw.Elapsed.TotalSeconds % 30 -lt 3) { Say ("  ... {0:N0} giay" -f $sw.Elapsed.TotalSeconds) 'DarkGray' }
            }
            if ($ready) {
                $voices = Invoke-RestMethod -Uri "http://127.0.0.1:$port/v1/voices" -TimeoutSec 10
                $count = @($voices.data).Count
                if ($count -eq 0) { $count = @($voices.voices).Count }
                Ok ("Server VieNeu chay duoc sau {0:N0} giay ({1} giong)" -f $sw.Elapsed.TotalSeconds, $count)
            }
        }
        finally {
            Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue
            Stop-ModServers $ModDir $warmStart   # the venv launcher's child interpreter
        }
        if (-not $ready) {
            if (Test-Path -LiteralPath $warmLog) { Get-Content -LiteralPath $warmLog -Tail 15 | ForEach-Object { Say "    $_" 'DarkGray' } }
            throw 'Server VieNeu khong khoi dong duoc.'
        }
        $engine = 'vieneu'
    }
    catch {
        Warn "Cai VieNeu loi: $($_.Exception.Message)"
        Warn 'Game se dung giong Piper. Co the chay lai CAI-DAT.bat de thu lai (file da tai duoc giu).'
    }
}

# 7) Voice config -----------------------------------------------------------------------------------------
$voiceCfg = Join-Path $Streaming 'swyf-voice.json'
$guide = 'engine: vieneu (VieNeu-TTS, 25 giong) hoac piper (giong cu). Game tu bat server VieNeu o cau tieng Viet dau tien; trong luc server khoi dong (vai giay) Piper doc thay. Muon gan giong rieng cho caller: them vao voices, vi du \"wei_kevin_chen\": \"Thien Minh\" (ten file giong goc cua caller, khong co .wav). Sua file la ap dung ngay o cau tiep theo.'
if (Test-Path -LiteralPath $voiceCfg) {
    $t = [IO.File]::ReadAllText($voiceCfg, [Text.Encoding]::UTF8)
    $t = [regex]::Replace($t, '"engine"\s*:\s*"[^"]*"', "`"engine`": `"$engine`"")
    if ($engine -eq 'vieneu') { $t = [regex]::Replace($t, '"vieneu_dir"\s*:\s*"(?:[^"\\]|\\.)*"', '"vieneu_dir": "SwyfMod\\VieNeu-TTS"') }
    Write-Utf8NoBom $voiceCfg $t
}
else {
    Write-Utf8NoBom $voiceCfg ("{`r`n  `"engine`": `"$engine`",`r`n  `"vieneu_url`": `"http://127.0.0.1:8000`",`r`n  `"vieneu_dir`": `"SwyfMod\\VieNeu-TTS`",`r`n" +
        "  `"autostart`": true,`r`n  `"voices`": {`r`n  },`r`n  `"_huong_dan`": `"$guide`"`r`n}`r`n")
}
Ok "swyf-voice.json: engine = $engine"

Step 'XONG'
Say "  Mod da cai vao: $GameDir" 'Green'
Say "  Giong doc: $engine.  Vao game, chon ngon ngu cuoc goi Tieng Viet." 'Green'
Say '  Chi nguoi lam chu phong (host) can nhap API key Vilao trong game; nguoi vao phong thi khong can.'
Say '  Go mod: chay GO-CAI-DAT.bat.'
exit 0
