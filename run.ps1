# Builds (if needed) and launches Dash Connect elevated (UAC prompt).
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$exe  = Join-Path $root 'dist\DashConnect.exe'
if (-not (Test-Path $exe)) {
    Write-Host 'dist\DashConnect.exe not found — building first...' -ForegroundColor Yellow
    & (Join-Path $root 'build.ps1')
}
Write-Host "Launching $exe (Administrator)..." -ForegroundColor Cyan
Start-Process -FilePath $exe -Verb RunAs
