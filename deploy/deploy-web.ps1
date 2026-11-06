<#
.SYNOPSIS
  Build and deploy the MineClaw Web Explorer (Nest api + Next ui + C# ViewerBridge) to CT 114 on alice. Idempotent.
  Also reachable as `pwsh deploy/deploy-alice.ps1 -Web` / `-WebStatus`. Runbook: docs/web/deploy.md.

.DESCRIPTION
  Deploy steps (the bot is never built, restarted or touched):
    1. tests unless -SkipTests: `turbo run test` in web/ and tests/MineClaw.ViewerBridge.Tests
    2. web/: npm ci, then `turbo run build --force` for @mineclaw/api and @mineclaw/web (and what they depend on)
    3. assemble deploy/out/web/mineclaw-web:
         apps/api/{package.json,dist}  + node_modules: the api's production deps from the lockfile, installed for
                                         linux-x64-gnu (npm --os/--cpu/--libc), so @node-rs/argon2 carries the Linux binary;
                                         @mineclaw/contracts copied in as a real package (no workspace links). node:sqlite is built in.
         apps/web/.next/standalone      the Next standalone server, with .next/static and public/ copied beside server.js
                                         (Windows-only sharp binaries dropped; sharp falls back to its wasm build, unused anyway)
         RELEASE.json                   id, commit, build time, Node version
    4. tools/ViewerBridge: dotnet publish linux-x64, self-contained single file (like the bot; the CT has no .NET runtime),
       plus shards/defaults (the bot's release does not carry them) -> deploy/out/web/mineclaw-bridge
    5. two tarballs + units + helper scripts scp'd to <PveHost>:/tmp, pct push'ed into the CT, and
       /usr/local/sbin/mineclaw-web-release install: unpack to /opt/mineclaw-web/releases/<id> and /opt/mineclaw-bridge/releases/<id>,
       refresh units, daemon-reload, enable, swap both `current` symlinks, keep 3, restart mineclaw-bridge, mineclaw-web-ui,
       mineclaw-web-api (only those), health check: units active, /api/health ok with bridge "up" (Nest's signed ping of the
       bridge's /v1/health), the UI answering on 127.0.0.1:3000 and through :8080
    6. status, then a LAN check of http://<CtIp>:8080/api/health from this PC (a warning only: needs the firewall rule)

.EXAMPLE
  pwsh deploy/deploy-web.ps1 -Install                 # ONE TIME: Node 22, users, dirs, keys, config.json, units (install-ct114.sh)
  pwsh deploy/deploy-web.ps1 -BotConfig               # dry run of the bot config step (Supervisor + Viewer), changes nothing
  pwsh deploy/deploy-web.ps1 -BotConfig -Apply        # ... write it (backup first); add -RestartBot to restart the bot now
  pwsh deploy/deploy-web.ps1                          # test, build, deploy, restart the web units, health check
  pwsh deploy/deploy-web.ps1 -SkipTests
  pwsh deploy/deploy-web.ps1 -Status                  # = deploy-alice.ps1 -WebStatus
  pwsh deploy/deploy-web.ps1 -Rollback [-DryRun]      # both trees back to the previous release
  pwsh deploy/deploy-web.ps1 -DryRun                  # print the plan and the PVE-side script; nothing built or run
  pwsh deploy/deploy-web.ps1 -BuildOnly -Smoke        # local only: build + assemble, then run the assembled bundle here
                                                      # (Next standalone + Nest with --mock-bridge) and hit /api/health
#>
#Requires -Version 7.0
[CmdletBinding()]
param(
    [switch]$SkipTests,
    [switch]$Status,
    [switch]$Rollback,
    [switch]$DryRun,
    [switch]$NoStart,
    [switch]$BuildOnly,
    [switch]$Smoke,
    [switch]$Install,
    [string[]]$Origin,
    [switch]$BotConfig,
    [switch]$Apply,
    [switch]$RestartBot,
    [switch]$AdminWrites,
    [string]$PveHost = 'alice',
    [int]$Vmid = 114,
    [string]$CtIp = '10.1.2.73',
    [int]$HealthTimeoutSec = 120
)
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$webDir = Join-Path $repo 'web'
$viewer = Join-Path $PSScriptRoot 'viewer'
$outRoot = Join-Path $repo 'deploy/out/web'
$ssh = @('-o', 'BatchMode=yes', '-o', 'ConnectTimeout=15')
$releaseCmd = '/usr/local/sbin/mineclaw-web-release'
$unitSources = [ordered]@{
    'mineclaw-bridge.service'  = 'bridge/mineclaw-bridge.service'
    'mineclaw-web-ui.service'  = 'web/mineclaw-web-ui.service'
    'mineclaw-web-api.service' = 'web/mineclaw-web-api.service'
}
# Helper scripts refreshed on every deploy and install (source in deploy/viewer -> path in the CT).
$helpers = [ordered]@{
    'web-release.sh'     = '/usr/local/sbin/mineclaw-web-release'
    'mineclaw-web-users' = '/usr/local/sbin/mineclaw-web-users'
    'enable-viewer.sh'   = '/usr/local/sbin/mineclaw-viewer-config'
}
# The Node pin lives in install-ct114.sh only (one place to bump it).
$installSh = [IO.File]::ReadAllText((Join-Path $viewer 'install-ct114.sh'))
$NodeVersion = [regex]::Match($installSh, '(?m)^NODE_VERSION=(\S+)').Groups[1].Value
$NodeSha256 = [regex]::Match($installSh, '(?m)^NODE_SHA256=([0-9a-f]{64})').Groups[1].Value
if (-not $NodeVersion -or -not $NodeSha256) { throw "could not read NODE_VERSION / NODE_SHA256 from deploy/viewer/install-ct114.sh" }

function Invoke-Pve([string]$Command, [switch]$AllowFail, [switch]$Quiet) {
    if ($Quiet) { & ssh @ssh $PveHost $Command 2>&1 | Out-Null } else { & ssh @ssh $PveHost $Command | Out-Host }
    if (-not $AllowFail -and $LASTEXITCODE -ne 0) { throw "on ${PveHost}: '$Command' failed ($LASTEXITCODE)" }
    return $LASTEXITCODE
}
function Copy-Lf([string]$From, [string]$To) {   # bash and systemd want LF; git on Windows may have checked out CRLF
    [IO.File]::WriteAllText($To, ([IO.File]::ReadAllText($From) -replace "`r`n", "`n"))
}
function Invoke-Step([string]$What, [scriptblock]$Block) {
    & $Block | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "$What failed ($LASTEXITCODE)" }
}
function Remove-Tree([string]$Path) {
    if (-not (Test-Path -LiteralPath $Path)) { return }
    # Workspace links are junctions on Windows: delete the link itself, never what it points at.
    Get-ChildItem -LiteralPath $Path -Recurse -Force -Attributes ReparsePoint -ErrorAction SilentlyContinue |
        Sort-Object { $_.FullName.Length } -Descending | ForEach-Object { [IO.Directory]::Delete($_.FullName, $false) }
    Remove-Item -LiteralPath $Path -Recurse -Force
}
function New-Tarball([string]$Archive, [string]$Parent, [string]$Name) {
    # Windows' bsdtar; GNU tar (e.g. Git Bash first on PATH) would read "C:" as a remote host.
    $tar = Join-Path $env:SystemRoot 'System32\tar.exe'
    $tarArgs = @('-czf', $Archive, '-C', $Parent, $Name)
    if (Test-Path $tar) { & $tar @tarArgs } else { & tar --force-local @tarArgs }
    if ($LASTEXITCODE -ne 0) { throw "tar of $Name failed ($LASTEXITCODE)" }
}
function Test-Elf([string]$File) {
    $b = [byte[]]::new(4)
    $s = [IO.File]::OpenRead($File); try { $null = $s.Read($b, 0, 4) } finally { $s.Dispose() }
    return ($b[0] -eq 0x7F -and $b[1] -eq 0x45 -and $b[2] -eq 0x4C -and $b[3] -eq 0x46)
}

# Stage files locally, scp them to <PveHost>:$Remote, run $Body there (bash, set -euo pipefail, $v = CT, $d = $Remote).
# $Files: published name -> local path; text files get LF line endings. -DryRun prints the script and stops.
function Invoke-Staged([string]$Label, [System.Collections.IDictionary]$Files, [string[]]$Binary, [string]$Remote, [string]$Body) {
    $script = @"
set -euo pipefail
v=$Vmid; d=$Remote
trap 'rm -rf `$d' EXIT
$Body
"@ -replace "`r`n", "`n"
    if ($DryRun) {
        Write-Host "== dry run ($Label): nothing is uploaded or run" -ForegroundColor Cyan
        Write-Host "files -> ${PveHost}:${Remote}: $(@($Files.Keys) -join ', ')"
        Write-Host "--- run.sh"
        Write-Host $script
        return 0
    }
    $stage = Join-Path ([IO.Path]::GetTempPath()) "mineclaw-web-$Label-$(Get-Date -Format 'yyyyMMddHHmmss')"
    New-Item -ItemType Directory -Force $stage | Out-Null
    try {
        foreach ($name in $Files.Keys) {
            $dst = Join-Path $stage $name
            if ($Binary -contains $name) { Copy-Item -LiteralPath $Files[$name] $dst } else { Copy-Lf $Files[$name] $dst }
        }
        [IO.File]::WriteAllText((Join-Path $stage 'run.sh'), $script)
        $null = Invoke-Pve "rm -rf $Remote && install -d -m 0700 $Remote"
        $paths = @(@($Files.Keys) + 'run.sh' | ForEach-Object { Join-Path $stage $_ })
        & scp @ssh -q @paths "${PveHost}:$Remote/"
        if ($LASTEXITCODE -ne 0) { throw "scp failed ($LASTEXITCODE)" }
        return (Invoke-Pve "bash $Remote/run.sh" -AllowFail)
    }
    finally { Remove-Item -Recurse -Force $stage -ErrorAction SilentlyContinue }
}
function Assert-Ct {
    if ((Invoke-Pve "pct status $Vmid" -AllowFail -Quiet) -ne 0) { throw "CT $Vmid not found on $PveHost" }
}
function Get-HelperPushes([string]$From) {
    return (($helpers.Keys | ForEach-Object { "pct push `$v $From/$_ $($helpers[$_]) --perms 0755" }) -join "`n")
}

# ---- remote-only modes ----
if ($Status) { $null = Invoke-Pve "pct exec $Vmid -- $releaseCmd status"; return }
if ($Rollback) {
    $flags = if ($DryRun) { ' --dry-run' } else { '' }
    $rc = Invoke-Pve "pct exec $Vmid -- $releaseCmd rollback --timeout $HealthTimeoutSec$flags" -AllowFail
    if ($rc -ne 0) { throw "web rollback refused or failed (exit $rc); see the lines above" }
    return
}
if ($BotConfig) {
    if ($RestartBot -and -not $Apply) { throw "-RestartBot goes with -Apply" }
    $flags = @(if ($Apply) { '--apply' }; if ($RestartBot) { '--restart' }; if ($AdminWrites) { '--admin-writes' }) -join ' '
    if (-not $DryRun) { Assert-Ct }
    $remote = '/tmp/mineclaw-web-botconfig'
    $body = @"
pct exec `$v -- test -x /usr/local/bin/node || { echo 'Node is not installed in the CT: run deploy-web.ps1 -Install first' >&2; exit 1; }
pct push `$v `$d/enable-viewer.sh /usr/local/sbin/mineclaw-viewer-config --perms 0755
pct exec `$v -- /usr/local/sbin/mineclaw-viewer-config $flags
"@
    $rc = Invoke-Staged 'botconfig' ([ordered]@{ 'enable-viewer.sh' = (Join-Path $viewer 'enable-viewer.sh') }) @() $remote $body
    if ($rc -ne 0) { throw "bot config step failed (exit $rc)" }
    return
}
if ($Install) {
    $nodeFile = "node-v$NodeVersion-linux-x64.tar.xz"
    $cache = Join-Path $repo 'deploy/out/cache'
    $nodeLocal = Join-Path $cache $nodeFile
    if (-not $DryRun) {
        Assert-Ct
        New-Item -ItemType Directory -Force $cache | Out-Null
        if (-not (Test-Path $nodeLocal)) {
            Write-Host "== downloading $nodeFile" -ForegroundColor Cyan
            Invoke-WebRequest "https://nodejs.org/dist/v$NodeVersion/$nodeFile" -OutFile "$nodeLocal.part" -TimeoutSec 300
            Move-Item -Force "$nodeLocal.part" $nodeLocal
        }
        $hash = (Get-FileHash -Algorithm SHA256 $nodeLocal).Hash.ToLowerInvariant()
        if ($hash -ne $NodeSha256) { Remove-Item -Force $nodeLocal; throw "sha256 of $nodeFile is $hash, pinned ${NodeSha256}: deleted, not shipped" }
        Write-Host "node tarball: $nodeFile, sha256 matches the pin"
    }
    $files = [ordered]@{
        'install-ct114.sh'          = Join-Path $viewer 'install-ct114.sh'
        'config.example.json'       = Join-Path $viewer 'web/config.example.json'
        'mineclaw-web.env.example'  = Join-Path $viewer 'web/mineclaw-web.env.example'
    }
    foreach ($h in $helpers.Keys) { $files[$h] = Join-Path $viewer $h }
    foreach ($u in $unitSources.Keys) { $files[$u] = Join-Path $viewer $unitSources[$u] }
    $files[$nodeFile] = $nodeLocal
    $origins = if ($Origin) { $Origin } else { @("http://${CtIp}:8080") }
    $originArgs = ($origins | ForEach-Object { "--origin '$_'" }) -join ' '
    $c = '/root/mineclaw-web-install'
    $pushes = ($files.Keys | ForEach-Object { "pct push `$v `$d/$_ $c/$_" }) -join "`n"
    $body = @"
trap 'rm -rf `$d; pct exec `$v -- rm -rf $c || true' EXIT
pct exec `$v -- install -d -m 0700 $c
$pushes
pct exec `$v -- bash $c/install-ct114.sh --from $c $originArgs
"@
    $rc = Invoke-Staged 'install' $files @($nodeFile) '/tmp/mineclaw-web-install' $body
    if ($rc -ne 0) { throw "install-ct114.sh failed (exit $rc); see the lines above" }
    return
}

# ---- deploy (or -BuildOnly / -Smoke) ----
$id = Get-Date -Format 'yyyyMMdd-HHmmss'
$remote = '/tmp/mineclaw-web-deploy'
$ctDir = '/root/mineclaw-web-deploy'
if ($Smoke) { $BuildOnly = $true }
$noStartFlag = if ($NoStart) { ' --no-start' } else { '' }
$deployBody = @"
trap 'rm -rf `$d; pct exec `$v -- rm -rf $ctDir || true' EXIT
pct exec `$v -- test -s /etc/mineclaw-web/bridge.key || { echo 'CT $Vmid has no web install yet: run deploy-web.ps1 -Install first' >&2; exit 1; }
pct exec `$v -- install -d -m 0700 $ctDir
$((@('mineclaw-web.tgz', 'mineclaw-bridge.tgz') + @($unitSources.Keys) | ForEach-Object { "pct push `$v `$d/$_ $ctDir/$_" }) -join "`n")
$(Get-HelperPushes '$d')
pct exec `$v -- $releaseCmd install $ctDir/mineclaw-web.tgz $ctDir/mineclaw-bridge.tgz $id --units $ctDir --timeout $HealthTimeoutSec$noStartFlag
"@

if ($DryRun) {
    Write-Host "== dry run: nothing is tested, built, uploaded or run" -ForegroundColor Cyan
    Write-Host "release $id -> /opt/mineclaw-web/releases/$id + /opt/mineclaw-bridge/releases/$id in CT $Vmid on $PveHost"
    Write-Host "build: $(if ($SkipTests) { 'no tests' } else { 'turbo test + ViewerBridge tests' }); npm ci; turbo build --force (api, web); npm ci --omit=dev for linux-x64-gnu; dotnet publish linux-x64"
    Write-Host "units after: $(if ($NoStart) { 'installed, not (re)started' } else { 'mineclaw-bridge, mineclaw-web-ui, mineclaw-web-api enabled + restarted (the bot is not touched)' })"
    Write-Host "--- run.sh (on $PveHost)"
    Write-Host ("set -euo pipefail`nv=$Vmid; d=$remote`n" + ($deployBody -replace "`r`n", "`n"))
    return
}
if (-not $BuildOnly) {
    Assert-Ct
    if ((Invoke-Pve "pct exec $Vmid -- test -s /etc/mineclaw-web/bridge.key" -AllowFail -Quiet) -ne 0) {
        throw "CT $Vmid has no web install yet: run pwsh deploy/deploy-web.ps1 -Install first (docs/web/deploy.md)"
    }
}

# 1-2. Tests and build
Push-Location $webDir
try {
    Write-Host "== web: npm ci" -ForegroundColor Cyan
    Invoke-Step 'npm ci' { npm ci --no-audit --no-fund }
    if (-not $SkipTests) {
        Write-Host "== web: tests" -ForegroundColor Cyan
        Invoke-Step 'web tests' { npx turbo run test --concurrency=2 }
    }
    Write-Host "== web: build" -ForegroundColor Cyan
    Invoke-Step 'web build' { npx turbo run build --force --concurrency=2 --filter=@mineclaw/api --filter=@mineclaw/web }
}
finally { Pop-Location }
if (-not $SkipTests) {
    Write-Host "== bridge: tests" -ForegroundColor Cyan
    Invoke-Step 'ViewerBridge tests' { dotnet test (Join-Path $repo 'tests/MineClaw.ViewerBridge.Tests/MineClaw.ViewerBridge.Tests.csproj') -c Release --nologo -v q }
}

# 3. Assemble the web release
Write-Host "== assemble web release" -ForegroundColor Cyan
New-Item -ItemType Directory -Force $outRoot | Out-Null
$stageWeb = Join-Path $outRoot 'mineclaw-web'
Remove-Tree $stageWeb
$api = Join-Path $webDir 'apps/api'
$apiOut = Join-Path $stageWeb 'apps/api'
if (-not (Test-Path (Join-Path $api 'dist/main.js'))) { throw "no web/apps/api/dist/main.js after the build" }
New-Item -ItemType Directory -Force $apiOut | Out-Null
Copy-Item (Join-Path $api 'package.json') $apiOut
Copy-Item -Recurse (Join-Path $api 'dist') $apiOut

# The api's production dependencies, from the lockfile, for linux-x64 glibc (the Ubuntu CT): a scratch copy of the workspace
# manifests so `npm ci -w @mineclaw/api --omit=dev` resolves exactly what the lockfile pins.
$deps = Join-Path $outRoot 'api-deps'
Remove-Tree $deps
foreach ($m in @('package.json', 'package-lock.json') + @(Get-ChildItem (Join-Path $webDir 'apps'), (Join-Path $webDir 'packages') -Directory |
        Where-Object { Test-Path (Join-Path $_.FullName 'package.json') } |
        ForEach-Object { [IO.Path]::GetRelativePath($webDir, (Join-Path $_.FullName 'package.json')) })) {
    $dst = Join-Path $deps $m
    New-Item -ItemType Directory -Force (Split-Path -Parent $dst) | Out-Null
    Copy-Item (Join-Path $webDir $m) $dst
}
Push-Location $deps
try {
    Invoke-Step 'npm ci (api production deps, linux-x64-gnu)' {
        npm ci --omit=dev --workspace=@mineclaw/api --include-workspace-root=false --os=linux --cpu=x64 --libc=glibc --ignore-scripts --no-audit --no-fund
    }
}
finally { Pop-Location }
$nm = Join-Path $deps 'node_modules'
$scope = Join-Path $nm '@mineclaw'
if (Test-Path $scope) {   # workspace links (junctions) -> drop; contracts comes back as a real package below
    Get-ChildItem -LiteralPath $scope -Force | ForEach-Object {
        if ($_.Attributes -band [IO.FileAttributes]::ReparsePoint) { [IO.Directory]::Delete($_.FullName, $false) } else { Remove-Item -Recurse -Force $_.FullName }
    }
}
$contractsOut = Join-Path $scope 'contracts'
New-Item -ItemType Directory -Force $contractsOut | Out-Null
Copy-Item (Join-Path $webDir 'packages/contracts/package.json') $contractsOut
Copy-Item -Recurse (Join-Path $webDir 'packages/contracts/dist') $contractsOut
# Only the glibc binding: the CT is Ubuntu.
Get-ChildItem (Join-Path $nm '@node-rs') -Directory -Filter 'argon2-*' | Where-Object Name -ne 'argon2-linux-x64-gnu' | Remove-Item -Recurse -Force
$argon = Join-Path $nm '@node-rs/argon2-linux-x64-gnu/argon2.linux-x64-gnu.node'
if (-not (Test-Path $argon) -or -not (Test-Elf $argon)) { throw "no linux-x64-gnu argon2 binding at $argon" }
Move-Item $nm (Join-Path $stageWeb 'node_modules')
Remove-Tree $deps

# Next standalone + static + public (the standalone output leaves those two out on purpose).
$webApp = Join-Path $webDir 'apps/web'
$sa = Join-Path $webApp '.next/standalone'
if (-not (Test-Path (Join-Path $sa 'apps/web/server.js'))) { throw "no Next standalone server at $sa/apps/web/server.js (output: 'standalone' in next.config.ts?)" }
$saOut = Join-Path $stageWeb 'apps/web/.next/standalone'
New-Item -ItemType Directory -Force (Split-Path -Parent $saOut) | Out-Null
Copy-Item -Recurse $sa $saOut
Copy-Item -Recurse (Join-Path $webApp '.next/static') (Join-Path $saOut 'apps/web/.next/static')
Copy-Item -Recurse (Join-Path $webApp 'public') (Join-Path $saOut 'apps/web/public')
$img = Join-Path $saOut 'node_modules/@img'
if (Test-Path $img) {
    Get-ChildItem $img -Directory | Where-Object { $_.Name -match '^sharp-(libvips-)?(win32|darwin)' } | Remove-Item -Recurse -Force
}

# Nothing platform-bound may be left except the argon2 Linux binding, and no links (tar would carry junctions badly).
$links = @(Get-ChildItem -LiteralPath $stageWeb -Recurse -Force -Attributes ReparsePoint -ErrorAction SilentlyContinue)
if ($links) { throw "the web release still has links: $($links[0..4].FullName -join ', ')" }
$natives = @(Get-ChildItem -LiteralPath $stageWeb -Recurse -Force -Filter '*.node' -File)
$bad = @($natives | Where-Object { -not (Test-Elf $_.FullName) })
if ($bad) { throw "non-Linux native modules in the web release: $($bad.FullName -join ', ')" }
Write-Host "native modules: $(@($natives | ForEach-Object { $_.Name }) -join ', ') (all ELF)"

$commit = (& git -C $repo rev-parse --short HEAD 2>$null)
$dirty = [bool](& git -C $repo status --porcelain -- web tools/ViewerBridge src 2>$null)
[ordered]@{ id = $id; commit = $commit; dirty = $dirty; built = (Get-Date).ToUniversalTime().ToString('o'); node = $NodeVersion } |
    ConvertTo-Json | Set-Content -NoNewline (Join-Path $stageWeb 'RELEASE.json')
Write-Host "web release: $stageWeb ($([math]::Round(((Get-ChildItem -LiteralPath $stageWeb -Recurse -File -Force | Measure-Object Length -Sum).Sum / 1MB), 1)) MB)"

# 4. Bridge: self-contained linux-x64 single file (deploy/publish-linux.ps1 publishes the bot the same way)
Write-Host "== publish ViewerBridge (linux-x64, self-contained)" -ForegroundColor Cyan
$stageBridge = Join-Path $outRoot 'mineclaw-bridge'
Remove-Tree $stageBridge
Invoke-Step 'dotnet publish ViewerBridge' {
    dotnet publish (Join-Path $repo 'tools/ViewerBridge/MineClaw.ViewerBridge.csproj') -c Release -r linux-x64 --self-contained true -m:1 -p:UseSharedCompilation=false `
        -p:PublishSingleFile=true -p:DebugType=none -p:GenerateDocumentationFile=false -o $stageBridge --nologo -v q
}
Get-ChildItem $stageBridge -Filter 'appsettings*.json' | Remove-Item -Force   # only the .example ships; config comes from /etc
$bin = Join-Path $stageBridge 'MineClaw.ViewerBridge'
if (-not (Test-Path $bin) -or -not (Test-Elf $bin)) { throw "publish produced no Linux MineClaw.ViewerBridge in $stageBridge" }
$packs = Join-Path $stageBridge 'shards/defaults'
New-Item -ItemType Directory -Force $packs | Out-Null
Get-ChildItem (Join-Path $repo 'shards/defaults') -File | Where-Object { $_.Extension -in '.mcshard', '.json' } | Copy-Item -Destination $packs
Copy-Item (Join-Path $stageWeb 'RELEASE.json') (Join-Path $stageBridge 'RELEASE.json')

# 5. Tarballs
Write-Host "== pack release $id" -ForegroundColor Cyan
$webTgz = Join-Path $outRoot 'mineclaw-web.tgz'
$bridgeTgz = Join-Path $outRoot 'mineclaw-bridge.tgz'
New-Tarball $webTgz $outRoot 'mineclaw-web'
New-Tarball $bridgeTgz $outRoot 'mineclaw-bridge'
Write-Host ("tarballs: mineclaw-web.tgz {0:N1} MB, mineclaw-bridge.tgz {1:N1} MB" -f ((Get-Item $webTgz).Length / 1MB), ((Get-Item $bridgeTgz).Length / 1MB))

if ($Smoke) {
    # Local smoke run of the ASSEMBLED bundle: the Next standalone server and Nest (--mock-bridge: a real signed loopback bridge
    # serving fixtures), wired like on the CT (Nest proxies non-/api to Next). argon2's Linux binding can't load on Windows, so
    # a scratch copy gets this PC's binding added; everything else runs exactly as shipped.
    Write-Host "== smoke run (local)" -ForegroundColor Cyan
    $smokeDir = Join-Path ([IO.Path]::GetTempPath()) "mineclaw-web-smoke-$id"
    Copy-Item -Recurse $stageWeb $smokeDir
    $hostBinding = Get-ChildItem (Join-Path $webDir 'node_modules/@node-rs') -Directory -Filter "argon2-$(if ($IsWindows) { 'win32' } elseif ($IsMacOS) { 'darwin' } else { 'linux' })-*" | Select-Object -First 1
    if ($hostBinding) { Copy-Item -Recurse $hostBinding.FullName (Join-Path $smokeDir 'node_modules/@node-rs') }
    $free = { $l = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, 0); $l.Start(); $p = $l.LocalEndpoint.Port; $l.Stop(); $p }
    $apiPort = & $free; $uiPort = & $free
    $data = Join-Path $smokeDir 'data'
    New-Item -ItemType Directory -Force $data | Out-Null
    $smokeCfg = Join-Path $smokeDir 'smoke-config.json'
    @{ Web = @{ Port = $apiPort; Bind = '127.0.0.1'; NextUrl = "http://127.0.0.1:$uiPort"; ProxyNext = $true
                AllowedOrigins = @("http://127.0.0.1:$apiPort"); DataDir = $data; DbPath = (Join-Path $data 'web.db')
                UsersFile = (Join-Path $data 'users.json'); LogLevel = 'warn' } } | ConvertTo-Json -Depth 5 | Set-Content $smokeCfg
    $saved = @{}
    foreach ($k in 'NODE_ENV', 'PORT', 'HOSTNAME', 'MINECLAW_WEB_CONFIG', 'NEXT_TELEMETRY_DISABLED') { $saved[$k] = [Environment]::GetEnvironmentVariable($k) }
    $procs = @()
    $client = [Net.Http.HttpClient]::new([Net.Http.HttpClientHandler]@{ AllowAutoRedirect = $false })
    $client.Timeout = [TimeSpan]::FromSeconds(10)
    $get = {
        param([string]$Url)
        try {
            $r = $client.GetAsync($Url).GetAwaiter().GetResult()
            $csp = $null
            $null = $r.Headers.TryGetValues('Content-Security-Policy', [ref]$csp)
            [pscustomobject]@{ Status = [int]$r.StatusCode; Body = $r.Content.ReadAsStringAsync().GetAwaiter().GetResult()
                               Csp = (@($csp) -join ' '); Location = "$($r.Headers.Location)" }
        }
        catch { [pscustomobject]@{ Status = 0; Body = $_.Exception.InnerException.Message; Csp = ''; Location = '' } }
    }
    $results = [Collections.Generic.List[string]]::new()
    $failed = $false
    try {
        $env:NEXT_TELEMETRY_DISABLED = '1'
        $env:NODE_ENV = 'production'; $env:PORT = "$uiPort"; $env:HOSTNAME = '127.0.0.1'
        $uiDir = Join-Path $smokeDir 'apps/web/.next/standalone/apps/web'
        $procs += Start-Process node -ArgumentList 'server.js' -WorkingDirectory $uiDir -PassThru -NoNewWindow `
            -RedirectStandardOutput (Join-Path $smokeDir 'ui.out.log') -RedirectStandardError (Join-Path $smokeDir 'ui.err.log')
        # Nest refuses --mock-bridge under NODE_ENV=production (the CT units always set it), so the smoke run leaves it unset.
        Remove-Item Env:NODE_ENV, Env:PORT, Env:HOSTNAME -ErrorAction SilentlyContinue
        $env:MINECLAW_WEB_CONFIG = $smokeCfg
        $procs += Start-Process node -ArgumentList '--disable-warning=ExperimentalWarning', 'dist/main.js', '--mock-bridge' `
            -WorkingDirectory (Join-Path $smokeDir 'apps/api') -PassThru -NoNewWindow `
            -RedirectStandardOutput (Join-Path $smokeDir 'api.out.log') -RedirectStandardError (Join-Path $smokeDir 'api.err.log')
        $base = "http://127.0.0.1:$apiPort"
        $deadline = (Get-Date).AddSeconds(60)
        do {
            Start-Sleep -Milliseconds 500
            $h = & $get "$base/api/health"
            $hj = try { $h.Body | ConvertFrom-Json } catch { $null }
        } until (($h.Status -eq 200 -and $hj.ok -and $hj.bridge -eq 'up') -or (Get-Date) -gt $deadline -or ($procs | Where-Object HasExited))
        $results.Add("GET /api/health -> $($h.Status) $($h.Body)")
        if (-not ($h.Status -eq 200 -and $hj.ok -and $hj.bridge -eq 'up')) { $failed = $true }
        $root = & $get "$base/"
        $results.Add("GET / (via Nest -> Next) -> $($root.Status)$(if ($root.Location) { " Location $($root.Location)" })")
        if ($root.Status -eq 0 -or $root.Status -ge 500) { $failed = $true }
        # The smoke run has no users, so it is in first-run setup mode: /login redirects to /setup, which must render.
        $toSetup = & $get "$base/login"
        $results.Add("GET /login (via Nest, no users) -> $($toSetup.Status) Location $($toSetup.Location)")
        if ($toSetup.Status -ne 302 -or $toSetup.Location -ne '/setup') { $failed = $true }
        $login = & $get "$base/setup"
        $nonce = $login.Csp -match "'nonce-"
        $results.Add("GET /setup (via Nest -> Next) -> $($login.Status), $($login.Body.Length) bytes, CSP nonce: $nonce")
        if ($login.Status -ne 200 -or -not $nonce) { $failed = $true }
        $asset = [regex]::Match($login.Body, '/_next/static/[^"''\s]+\.(js|css)').Value
        if ($asset) {
            $a = & $get "$base$asset"
            $results.Add("GET $asset (copied .next/static) -> $($a.Status), $($a.Body.Length) bytes")
            if ($a.Status -ne 200) { $failed = $true }
        }
        else { $results.Add('no /_next/static asset referenced by /setup'); $failed = $true }
        $worker = Get-ChildItem (Join-Path $uiDir 'public/assets') -Filter 'worker-*.js' -ErrorAction SilentlyContinue | Select-Object -First 1
        if ($worker) {
            $w = & $get "$base/assets/$($worker.Name)"
            $results.Add("GET /assets/$($worker.Name) (copied public/) -> $($w.Status)")
            if ($w.Status -ne 200) { $failed = $true }
        }
        $direct = & $get "http://127.0.0.1:$uiPort/login"
        $results.Add("GET /login on Next directly (127.0.0.1:$uiPort) -> $($direct.Status)")
        if ($direct.Status -ne 200) { $failed = $true }
    }
    finally {
        foreach ($p in $procs) { if (-not $p.HasExited) { & taskkill /PID $p.Id /T /F 2>&1 | Out-Null } }
        foreach ($k in $saved.Keys) { [Environment]::SetEnvironmentVariable($k, $saved[$k]) }
        $client.Dispose()
    }
    $results | ForEach-Object { Write-Host "  $_" }
    if ($failed) {
        foreach ($log in 'api.err.log', 'api.out.log', 'ui.err.log', 'ui.out.log') {
            $f = Join-Path $smokeDir $log
            if ((Test-Path $f) -and (Get-Item $f).Length) { Write-Host "--- $log"; Get-Content $f -Tail 20 | Out-Host }
        }
        throw "smoke run failed (logs in $smokeDir)"
    }
    Remove-Item -Recurse -Force $smokeDir -ErrorAction SilentlyContinue
    Write-Host "smoke run: OK" -ForegroundColor Green
}
if ($BuildOnly) { Write-Host "built release $id in $outRoot (not deployed: -BuildOnly)" -ForegroundColor Green; return }

# 6. Upload + install inside the CT
Write-Host "== upload + install release $id in CT $Vmid" -ForegroundColor Cyan
$files = [ordered]@{ 'mineclaw-web.tgz' = $webTgz; 'mineclaw-bridge.tgz' = $bridgeTgz }
foreach ($u in $unitSources.Keys) { $files[$u] = Join-Path $viewer $unitSources[$u] }
foreach ($h in $helpers.Keys) { $files[$h] = Join-Path $viewer $h }
$rc = Invoke-Staged 'deploy' $files @('mineclaw-web.tgz', 'mineclaw-bridge.tgz') $remote $deployBody

# 7. Status and a LAN check from this PC
Write-Host "== status" -ForegroundColor Cyan
$null = Invoke-Pve "pct exec $Vmid -- $releaseCmd status" -AllowFail
if ($rc -ne 0) { throw "web deploy of $id finished with errors (exit $rc). Roll back: pwsh deploy/deploy-web.ps1 -Rollback" }
try {
    $lan = Invoke-RestMethod "http://${CtIp}:8080/api/health" -TimeoutSec 5
    Write-Host "LAN: http://${CtIp}:8080/api/health -> ok=$($lan.ok) bridge=$($lan.bridge) version=$($lan.version)"
    $setup = try { Invoke-RestMethod "http://${CtIp}:8080/api/setup" -TimeoutSec 5 } catch { $null }
    if ($setup -and $setup.needed) {
        Write-Host "FIRST-RUN SETUP IS OPEN: open http://${CtIp}:8080 now and create your account (whoever completes it first becomes the admin)." -ForegroundColor Yellow
    }
}
catch {
    Write-Warning "LAN: http://${CtIp}:8080 is not reachable from this PC ($($_.Exception.Message)). Healthy inside the CT; is the CT 114 firewall rule for TCP 8080 in place? (docs/web/deploy.md)"
}
Write-Host "deployed web release $id to CT $Vmid on $PveHost" -ForegroundColor Green
