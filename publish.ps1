<#
    publish.ps1 - build + publish Dash Connect to GitHub (source + release).

    Run AFTER each meaningful change so GitHub always stays current.

    Examples:
      .\publish.ps1                     # rebuild, push source, refresh current release assets
      .\publish.ps1 -Message "fix dns"  # custom commit message
      .\publish.ps1 -Version 1.0.1      # bump version -> NEW release v1.0.1 (auto-update fires)
      .\publish.ps1 -ZapretSrc <path>   # re-sync zapret assets from an external copy first

    Steps: (opt. version bump) -> build -> SelfTest -> dist exe -> (opt. sync zapret) ->
           MSI -> commit+push source -> upload assets to release.

    -ZapretSrc is optional. Omit it to publish the zapret assets already committed to the repo
    (the normal case); pass a folder only when refreshing them from an external Zapret checkout.
    It can also be supplied via the DASHCONNECT_ZAPRET_SRC environment variable.
#>
param(
    [string]$Version,
    [string]$Message,
    [string]$ZapretSrc = $env:DASHCONNECT_ZAPRET_SRC
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

# 5. Optionally re-sync zapret assets into the repo from an external copy (presets/lists may have
#    changed there). Skipped unless -ZapretSrc is given — normally we ship what's committed.
if ($ZapretSrc) {
    if (-not (Test-Path $ZapretSrc)) { throw "ZapretSrc not found: $ZapretSrc" }
    robocopy $ZapretSrc (Join-Path $root 'zapret') /MIR /NFL /NDL /NJH /NJS /NC /NS /NP | Out-Null
    if ($LASTEXITCODE -ge 8) { throw "robocopy zapret failed ($LASTEXITCODE)" }
    $global:LASTEXITCODE = 0
    Write-Host "[publish] zapret assets synced from $ZapretSrc" -ForegroundColor Cyan
} else {
    Write-Host '[publish] using committed zapret assets (no -ZapretSrc)' -ForegroundColor DarkGray
}

# 6. MSI + its SHA-256 sidecar (the in-app updater verifies the download against this before running
#    msiexec, so a corrupt or tampered installer is never executed).
$msi = Join-Path $root "dist\DashConnect-$cur.msi"
wix build $wxsPath -arch x64 -o $msi
if ($LASTEXITCODE -ne 0) { throw "wix build failed" }
$sha = "$msi.sha256"
$hash = (Get-FileHash $msi -Algorithm SHA256).Hash.ToLower()
Set-FileText $sha "$hash  DashConnect-$cur.msi`n"
Write-Host "[publish] sha256: $hash" -ForegroundColor Cyan

# 7. Commit + push source (no co-author). "Nothing to commit" is fine.
# NB: git/gh write warnings and progress (e.g. "LF will be replaced by CRLF", upload bars) to
# stderr; under EAP=Stop PowerShell turns that into a terminating NativeCommandError even on exit 0.
# Relax EAP for the rest of the script and gate on real exit codes instead.
$ErrorActionPreference = 'SilentlyContinue'

git add -A 2>&1 | Out-Null
$dirty = ((git status --porcelain 2>$null) | Measure-Object).Count -gt 0
if ($dirty) {
    $msg = if ($Message) { $Message } else { "build: publish $cur" }
    git commit -m $msg 2>&1 | Out-Null
    Write-Host "[publish] commit: $msg" -ForegroundColor Cyan
} else {
    Write-Host "[publish] source unchanged" -ForegroundColor DarkGray
}
git push origin main 2>&1 | Out-Null
if ($LASTEXITCODE -ne 0) { throw "git push failed ($LASTEXITCODE)" }

# 8. Release: update existing tag or create a new one.
$tag = "v$cur"
gh release view $tag > $null 2>&1
$exists = ($LASTEXITCODE -eq 0)
if ($exists) {
    # Only the MSI installer is published — the portable exe confused users (they'd run it without the
    # bundled assets/elevation and report "doesn't work"), so we no longer attach it.
    gh release upload $tag $msi $sha --clobber 2>&1 | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "gh release upload failed ($LASTEXITCODE)" }
    Write-Host "[publish] assets refreshed on release $tag" -ForegroundColor Green
} else {
    gh release create $tag $msi $sha --title "Dash Connect $cur" --notes "Build $cur." 2>&1 | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "gh release create failed ($LASTEXITCODE)" }
    Write-Host "[publish] created release $tag" -ForegroundColor Green
}
# Safety: flaky GitHub uploads can leave the release as a draft — force it published.
gh release edit $tag --draft=false 2>&1 | Out-Null

Write-Host "[publish] DONE -> https://github.com/dutow20162007-create/DashConnect/releases/tag/$tag" -ForegroundColor Green
