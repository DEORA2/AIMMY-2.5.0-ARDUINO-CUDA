<#
.SYNOPSIS
    Builds the two release zips Whales' updater expects.

.DESCRIPTION
    - Whales-v<version>-Debug-x64.zip: everything, for new installs.
    - Whales-v<version>-app-only-ort<onnxruntime version>.zip: everything except the ONNX Runtime / CUDA native
      files (over 300 MB, unchanged unless the ONNX Runtime package is updated). The in-app updater downloads this
      small zip when the installed ONNX Runtime matches the version in its name, and the full zip otherwise.

    Upload the full zip first, so older updaters (v2.5.6 and v2.5.7 take the first .zip) get the full one.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File tools\Make-Release.ps1 -OutDir C:\temp\release
#>
param(
    [Parameter(Mandatory)] [string]$OutDir
)
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
$project = Join-Path $repo 'Aimmy2\Aimmy2.csproj'

$version = ([xml](Get-Content $project)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
if (-not $version) { throw "No <Version> in $project" }

$fullName = "Whales-v$version-Debug-x64"
$fullDir = Join-Path $OutDir $fullName
if (Test-Path $fullDir) { Remove-Item -Recurse -Force $fullDir }
New-Item -ItemType Directory -Force $OutDir | Out-Null

dotnet build $project -c Debug -p:Platform=x64 --no-incremental -o $fullDir
if ($LASTEXITCODE -ne 0) { throw "Build failed" }

# Not needed on Windows: Linux runtimes, and duplicate ONNX Runtime DLLs the build copies next to the exe
Remove-Item -Recurse -Force (Join-Path $fullDir 'runtimes\linux-x64') -ErrorAction SilentlyContinue
Get-ChildItem $fullDir -Filter 'onnxruntime*.dll' | Remove-Item -Force

$built = (Get-Item (Join-Path $fullDir 'WhaleCumOrganizer.dll')).VersionInfo.FileVersion
if (-not $built.StartsWith($version)) { throw "Built version $built doesn't match project version $version" }

$ortDll = Join-Path $fullDir 'runtimes\win-x64\native\onnxruntime.dll'
$ort = [regex]::Match((Get-Item $ortDll).VersionInfo.FileVersion, '^\d+(?:\.\d+)*').Value
if (-not $ort) { throw "Couldn't read the ONNX Runtime version from $ortDll" }

# App-only: same files minus the ONNX Runtime native folder
$appName = "Whales-v$version-app-only"
$appDir = Join-Path $OutDir $appName
if (Test-Path $appDir) { Remove-Item -Recurse -Force $appDir }
$native = Join-Path $fullDir 'runtimes\win-x64\native'
Get-ChildItem $fullDir -Recurse -File | Where-Object { -not $_.FullName.StartsWith($native + '\') } | ForEach-Object {
    $dest = Join-Path $appDir $_.FullName.Substring($fullDir.Length + 1)
    New-Item -ItemType Directory -Force (Split-Path $dest) | Out-Null
    Copy-Item $_.FullName $dest
}

$fullZip = Join-Path $OutDir "$fullName.zip"
$appZip = Join-Path $OutDir "$appName-ort$ort.zip"
Remove-Item $fullZip, $appZip -ErrorAction SilentlyContinue
Compress-Archive -Path $fullDir -DestinationPath $fullZip -CompressionLevel Optimal
Compress-Archive -Path $appDir -DestinationPath $appZip -CompressionLevel Optimal

"Version $version, ONNX Runtime $ort"
"{0}  {1:0} MB" -f $fullZip, ((Get-Item $fullZip).Length / 1MB)
"{0}  {1:0.0} MB" -f $appZip, ((Get-Item $appZip).Length / 1MB)
