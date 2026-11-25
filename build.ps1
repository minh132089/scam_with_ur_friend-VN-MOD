# Builds the mod kit from source: SwyfCompat.dll, SwyfPatcher.exe and dist\SWYF-VietMod.zip.
# Needs: .NET SDK (any recent; only its Roslyn compiler is used), .NET Framework 4.x (built into Windows),
#        and the game's Managed folder (SwyfCompat.dll compiles against the game's own Unity / Kolkata DLLs).
# Usage: .\build.ps1 -Managed "<game>\Scam With Your Friends_Data\Managed"
param([Parameter(Mandatory = $true)][string]$Managed)
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$root = $PSScriptRoot
$build = Join-Path $root 'build'
$dist = Join-Path $root 'dist'
New-Item -ItemType Directory -Force -Path $build, $dist | Out-Null

$dotnet = Join-Path $env:ProgramFiles 'dotnet\dotnet.exe'
$sdk = Get-ChildItem (Join-Path $env:ProgramFiles 'dotnet\sdk') -Directory |
    Where-Object { Test-Path (Join-Path $_.FullName 'Roslyn\bincore\csc.dll') } |
    Sort-Object { [version]($_.Name -replace '-.*$', '') } -Descending | Select-Object -First 1
if (-not $sdk) { throw '.NET SDK not found (install it from https://dotnet.microsoft.com/download).' }
$csc = Join-Path $sdk.FullName 'Roslyn\bincore\csc.dll'
Write-Host "compiler: $csc"

# Mono.Cecil 0.11.6 (net40 build): local NuGet cache, else the package from nuget.org.
$cecil = Join-Path $env:USERPROFILE '.nuget\packages\mono.cecil\0.11.6\lib\net40\Mono.Cecil.dll'
if (-not (Test-Path $cecil)) {
    $pkg = Join-Path $build 'mono.cecil.0.11.6.nupkg'
    if (-not (Test-Path $pkg)) { & curl.exe -L --fail -o $pkg 'https://www.nuget.org/api/v2/package/Mono.Cecil/0.11.6'; if ($LASTEXITCODE -ne 0) { throw 'Mono.Cecil download failed' } }
    $x = Join-Path $build 'mono.cecil'
    if (Test-Path $x) { Remove-Item -LiteralPath $x -Recurse -Force }
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [IO.Compression.ZipFile]::ExtractToDirectory($pkg, $x)
    $cecil = Join-Path $x 'lib\net40\Mono.Cecil.dll'
}

# SwyfCompat.dll: loaded by the game, so it is compiled against the game's (stripped) mscorlib and modules.
$refs = 'mscorlib', 'System', 'System.Core', 'UnityEngine.CoreModule', 'UnityEngine.UnityWebRequestModule',
    'UnityEngine.UIElementsModule', 'Newtonsoft.Json', 'Kolkata.Speech', 'com.whisper.unity' |
    ForEach-Object { '-r:' + (Join-Path $Managed "$_.dll") }
& $dotnet $csc -nologo -nostdlib -noconfig -codepage:65001 -target:library -optimize -deterministic "-out:$build\SwyfCompat.dll" @refs (Get-ChildItem (Join-Path $root 'src\compat\*.cs')).FullName
if ($LASTEXITCODE -ne 0) { throw 'SwyfCompat build failed' }

# SwyfPatcher.exe: .NET Framework 4 console tool (present on every Windows 10/11).
$fw = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
& $dotnet $csc -nologo -nostdlib -noconfig -codepage:65001 -target:exe -optimize -deterministic -langversion:latest "-out:$build\SwyfPatcher.exe" "-r:$fw\mscorlib.dll" "-r:$fw\System.dll" "-r:$fw\System.Core.dll" "-r:$cecil" (Get-ChildItem (Join-Path $root 'src\patcher\*.cs')).FullName
if ($LASTEXITCODE -ne 0) { throw 'SwyfPatcher build failed' }

# Kit: kit\* + binaries -> dist\SWYF-VietMod.zip
$stage = Join-Path $build 'SWYF-VietMod'
if (Test-Path $stage) { Remove-Item -LiteralPath $stage -Recurse -Force }
Copy-Item -LiteralPath (Join-Path $root 'kit') -Destination $stage -Recurse
# Never ship a local run's state: the remembered game folder or the download cache.
foreach ($local in 'game-path.txt', 'downloads') {
    $p = Join-Path $stage $local
    if (Test-Path -LiteralPath $p) { Remove-Item -LiteralPath $p -Recurse -Force }
}
Copy-Item "$build\SwyfCompat.dll", "$build\SwyfPatcher.exe", $cecil -Destination (Join-Path $stage 'mod') -Force
$zip = Join-Path $dist 'SWYF-VietMod.zip'
if (Test-Path $zip) { Remove-Item -LiteralPath $zip -Force }
Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem
# Entries with '/' separators (ZipFile.CreateFromDirectory in Windows PowerShell writes '\', which some unzip tools mishandle).
$archive = [IO.Compression.ZipFile]::Open($zip, [IO.Compression.ZipArchiveMode]::Create)
try {
    Get-ChildItem -LiteralPath $stage -Recurse -File | ForEach-Object {
        $name = 'SWYF-VietMod/' + $_.FullName.Substring($stage.Length + 1).Replace('\', '/')
        [void][IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, $_.FullName, $name, [IO.Compression.CompressionLevel]::Optimal)
    }
}
finally { $archive.Dispose() }
Write-Host "kit: $zip ($([math]::Round((Get-Item $zip).Length / 1KB)) KB)"
