<#
  Builds the release artifacts into ./artifacts:
    Nadfny-<version>-portable-win-x64.zip   unzip and run Nadfny.exe, nothing to install
    Nadfny-<version>-win-x64.msi            per-user installer with a Start menu shortcut
  Both are self-contained: the .NET runtime is bundled, so nothing else needs installing.
  Usage: pwsh build/release.ps1
#>
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
Set-Location $root

[xml]$proj = Get-Content src/Nadfny/Nadfny.csproj
$version = $proj.Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
$out = Join-Path $root 'artifacts'
$publish = Join-Path $out 'publish'
Remove-Item $out -Recurse -Force -ErrorAction SilentlyContinue

Write-Host "Testing..."
dotnet test tests/Nadfny.Tests -c Release --nologo -v q
if ($LASTEXITCODE -ne 0) { throw 'Tests failed; not building a release.' }

Write-Host "Publishing Nadfny $version (self-contained, win-x64)..."
dotnet publish src/Nadfny -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true `
  -p:DebugType=none -o $publish --nologo -v q
if ($LASTEXITCODE -ne 0) { throw 'Publish failed.' }

$zip = Join-Path $out "Nadfny-$version-portable-win-x64.zip"
Compress-Archive -Path (Join-Path $publish '*') -DestinationPath $zip
Write-Host "Portable: $zip"

dotnet tool restore | Out-Null
$msi = Join-Path $out "Nadfny-$version-win-x64.msi"
dotnet tool run wix build installer/Nadfny.wxs -arch x64 -d "Version=$version" -d "PublishDir=$publish" -o $msi
if ($LASTEXITCODE -ne 0) { throw 'Installer build failed.' }
Remove-Item (Join-Path $out '*.wixpdb') -ErrorAction SilentlyContinue
Write-Host "Installer: $msi"

Get-ChildItem $out -File | ForEach-Object { '{0,-44} {1,8:N1} MB' -f $_.Name, ($_.Length / 1MB) }
