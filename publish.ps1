<#
    publish.ps1 - build + publish Dash Connect to GitHub (source + release).

    Run AFTER each meaningful change so GitHub always stays current.

    Examples:
      .\publish.ps1                     # rebuild, push source, refresh current release assets
      .\publish.ps1 -Message "fix dns"  # custom commit message
      .\publish.ps1 -Version 1.0.1      # bump version -> NEW release v1.0.1 (auto-update fires)

    Steps: (opt. version bump) -> build -> SelfTest -> dist exe -> sync zapret ->
           MSI -> commit+push source -> upload assets to release.
#>
param(
    [string]$Version,
    [string]$Message
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
Set-Location $root
$env:PATH += ";$env:USERPROFILE\.dotnet\tools"
$env:DOTNET_CLI_TELEMETRY_OPTOUT = 1
$env:DOTNET_NOLOGO = 1
$dn = "$env:USERPROFILE\.dotnet\dotnet.exe"

$ucPath  = Join-Path $root 'src\DashConnect.Core\Update\UpdateChecker.cs'
$wxsPath = Join-Path $root 'installer\DashConnect.wxs'
$zapretSrc = 'C:\Users\HU9O\Desktop\zapret-discord-youtube\zapret-discord-youtube-1.9.9c'

function Set-FileText([string]$path, [string]$text) { [IO.File]::WriteAllText($path, $text) }

# 1. Optional version bump (single source of truth: UpdateChecker.CurrentVersion + MSI version).
if ($Version) {
    Set-FileText $ucPath  (([IO.File]::ReadAllText($ucPath))  -replace 'CurrentVersion = "[\d\.]+"', ('CurrentVersion = "{0}"' -f $Version))
    # -creplace (case-sensitive): match only the Package `Version="..."`, NOT the lowercase
    # `<?xml version="1.0"?>` declaration on line 1.
    Set-FileText $wxsPath (([IO.File]::ReadAllText($wxsPath)) -creplace 'Version="[\d\.]+"', ('Version="{0}.0"' -f $Version))
    Write-Host "[publish] version -> $Version" -ForegroundColor Cyan
}

$cur = ([regex]'CurrentVersion = "([\d\.]+)"').Match([IO.File]::ReadAllText($ucPath)).Groups[1].Value
if (-not $cur) { throw "could not read version from UpdateChecker.cs" }
Write-Host "[publish] build version: $cur" -ForegroundColor Cyan

# 2. Stop running instances (else exe/MSI are locked).
Get-Process -Name DashConnect, winws -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue

# 3. Build + SelfTest (failure = stop, never publish a broken build).
& $dn build (Join-Path $root 'DashConnect.sln') -c Debug -v minimal
if ($LASTEXITCODE -ne 0) { throw "build failed" }
& (Join-Path $root 'src\DashConnect.SelfTest\bin\Debug\net8.0\DashConnect.SelfTest.exe')
if ($LASTEXITCODE -ne 0) { throw "SelfTest failed - publish aborted" }

# 4. Self-contained exe.
& (Join-Path $root 'build.ps1')
if ($LASTEXITCODE -ne 0) { throw "build.ps1 failed" }

# 5. Sync zapret assets into the repo (presets/lists may have changed externally).
robocopy $zapretSrc (Join-Path $root 'zapret') /MIR /NFL /NDL /NJH /NJS /NC /NS /NP | Out-Null
if ($LASTEXITCODE -ge 8) { throw "robocopy zapret failed ($LASTEXITCODE)" }
$global:LASTEXITCODE = 0

# 6. MSI.
$msi = Join-Path $root "dist\DashConnect-$cur.msi"
wix build $wxsPath -arch x64 -o $msi
if ($LASTEXITCODE -ne 0) { throw "wix build failed" }

# 7. Commit + push source (no co-author). "Nothing to commit" is fine.
git add -A
if ((git status --porcelain).Length -gt 0) {
    $msg = if ($Message) { $Message } else { "build: publish $cur" }
    git commit -m $msg | Out-Null
    Write-Host "[publish] commit: $msg" -ForegroundColor Cyan
} else {
    Write-Host "[publish] source unchanged" -ForegroundColor DarkGray
}
git push origin main
if ($LASTEXITCODE -ne 0) { throw "git push failed" }

# 8. Release: update existing tag or create a new one.
$tag = "v$cur"
gh release view $tag *> $null 2>&1
if ($LASTEXITCODE -eq 0) {
    gh release upload $tag $msi (Join-Path $root 'dist\DashConnect.exe') --clobber
    Write-Host "[publish] assets refreshed on release $tag" -ForegroundColor Green
} else {
    gh release create $tag $msi (Join-Path $root 'dist\DashConnect.exe') --title "Dash Connect $cur" --notes "Build $cur."
    Write-Host "[publish] created release $tag" -ForegroundColor Green
}

Write-Host "[publish] DONE -> https://github.com/dutow20162007-create/DashConnect/releases/tag/$tag" -ForegroundColor Green
