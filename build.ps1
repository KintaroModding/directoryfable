# Builds DirectoryFable (Release) as one framework-dependent DirectoryFable.exe in .\dist.
# Running it needs the .NET 10 Desktop Runtime (x64); building needs the .NET 10 SDK.
param(
    [string]$Output = (Join-Path $PSScriptRoot 'dist')
)

$ErrorActionPreference = 'Stop'
$project = Join-Path $PSScriptRoot 'src\DirectoryFable\DirectoryFable.csproj'

dotnet publish $project `
    --configuration Release `
    --runtime win-x64 `
    --self-contained false `
    -p:PublishSingleFile=true `
    -p:DebugType=embedded `
    --output $Output
if ($LASTEXITCODE -ne 0) {
    Write-Host "Build failed (exit code $LASTEXITCODE)." -ForegroundColor Red
    exit $LASTEXITCODE
}
Write-Host "Built: $(Join-Path $Output 'DirectoryFable.exe')" -ForegroundColor Green
