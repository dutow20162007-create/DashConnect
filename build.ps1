# Builds Dash Connect into .\dist as a self-contained, single-file Windows executable.
# Requires the .NET 8 SDK (user-local install at %USERPROFILE%\.dotnet is auto-detected).
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path

$dotnet = Join-Path $env:USERPROFILE '.dotnet\dotnet.exe'
if (-not (Test-Path $dotnet)) {
    $cmd = Get-Command dotnet -ErrorAction SilentlyContinue
    if ($cmd) { $dotnet = $cmd.Source } else { throw 'dotnet SDK not found. Install .NET 8 SDK.' }
}
Write-Host "Using dotnet: $dotnet" -ForegroundColor Cyan

$proj = Join-Path $root 'src\DashConnect.App\DashConnect.App.csproj'
$out  = Join-Path $root 'dist'

& $dotnet publish $proj -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true `
    -o $out
if ($LASTEXITCODE -ne 0) { throw "publish failed ($LASTEXITCODE)" }

Write-Host "`nBuilt -> $out\DashConnect.exe" -ForegroundColor Green
Write-Host "Launch it elevated (right-click -> Run as administrator) or run .\run.ps1"
