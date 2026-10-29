<#
  Builds the release artifacts into ./artifacts:
    Nadfny-<version>-portable-win-x64.zip   unzip and run Nadfny.exe, nothing to install
    Nadfny-<version>-setup.exe              branded per-user installer (what the download buttons and updater use)
    Nadfny-<version>-win-x64.msi            the same installer as a bare MSI, for IT and winget
    SHA256SUMS.txt                          checksums the in-app updater verifies downloads against
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
Write-Host "MSI: $msi"

# The Bal extension ships its dll under a name `wix extension list` flags as damaged, so load it by path.
$balVersion = '5.0.2'
$balDir = Join-Path $HOME ".wix/extensions/WixToolset.Bal.wixext/$balVersion/wixext5"
if (-not (Test-Path $balDir)) {
  dotnet tool run wix extension add -g "WixToolset.Bal.wixext/$balVersion" | Out-Null
}
$balDll = Get-ChildItem $balDir -Filter 'WixToolset.BootstrapperApplications.wixext.dll' -ErrorAction SilentlyContinue | Select-Object -First 1
if (-not $balDll) { throw "WixToolset.Bal.wixext $balVersion was not installed (looked in $balDir)." }

$setup = Join-Path $out "Nadfny-$version-setup.exe"
dotnet tool run wix build installer/Bundle.wxs -arch x64 -ext $balDll.FullName -d "Version=$version" -d "MsiPath=$msi" -o $setup
if ($LASTEXITCODE -ne 0) { throw 'Setup bundle build failed.' }
Remove-Item (Join-Path $out '*.wixpdb') -ErrorAction SilentlyContinue
Write-Host "Setup: $setup"

$sums = Get-ChildItem $out -File | Where-Object { $_.Extension -in '.exe', '.msi', '.zip' } | ForEach-Object {
  '{0}  {1}' -f (Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLower(), $_.Name
}
Set-Content (Join-Path $out 'SHA256SUMS.txt') $sums -Encoding ascii

Get-ChildItem $out -File | ForEach-Object { '{0,-44} {1,8:N1} MB' -f $_.Name, ($_.Length / 1MB) }
