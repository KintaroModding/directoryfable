# Builds DirectoryFable (Release) as one framework-dependent DirectoryFable.exe in .\dist, and the
# release zip .\dist\release\DirectoryFable_<version>.zip (the exe, LICENSE, NOTICE.md).
# A GitHub release tagged v<version> with that zip attached is what the app's Update button installs.
# Running it needs the .NET 10 Desktop Runtime (x64); building needs the .NET 10 SDK.
param(
    [string]$Output = (Join-Path $PSScriptRoot 'dist')
)

$ErrorActionPreference = 'Stop'
$project = Join-Path $PSScriptRoot 'src\DirectoryFable\DirectoryFable.csproj'
$version = ([xml](Get-Content $project)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1

# A full build first: an incremental build keeps resources of files that were removed (for example
# pictures), and they would end up in the exe. Then publish exactly that build.
$settings = @('--configuration', 'Release', '--runtime', 'win-x64', '--self-contained', 'false',
    '-p:PublishSingleFile=true', '-p:DebugType=embedded')
dotnet build $project @settings --no-incremental
if ($LASTEXITCODE -eq 0) {
    dotnet publish $project @settings --no-build --output $Output
}
if ($LASTEXITCODE -ne 0) {
    Write-Host "Build failed (exit code $LASTEXITCODE)." -ForegroundColor Red
    exit $LASTEXITCODE
}
Write-Host "Built: $(Join-Path $Output 'DirectoryFable.exe') (version $version)" -ForegroundColor Green

# The zip name is part of the update: the app takes exactly DirectoryFable_<version>.zip from the release.
$release = Join-Path $Output 'release'
New-Item -ItemType Directory -Force $release | Out-Null
$zip = Join-Path $release "DirectoryFable_$version.zip"
if (Test-Path $zip) { Remove-Item $zip }
Compress-Archive -DestinationPath $zip -Path `
    (Join-Path $Output 'DirectoryFable.exe'), `
    (Join-Path $PSScriptRoot 'LICENSE'), `
    (Join-Path $PSScriptRoot 'NOTICE.md')
Write-Host "Release zip: $zip (attach it to the GitHub release tagged v$version)" -ForegroundColor Green
