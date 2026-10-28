<#
.SYNOPSIS
  Build and deploy MineClaw to its Proxmox LXC (default: CT 114 "claudechan" on node alice). Idempotent.

.DESCRIPTION
  Steps: optional tests, then a self-contained linux-x64 publish (deploy/publish-linux.ps1), then a tarball scp'd to
  <PveHost>:/tmp and pct push'ed into the CT. Inside the CT (deploy/ct-release.sh, installed as
  /usr/local/sbin/mineclaw-release) it unpacks to /opt/mineclaw/releases/<ts>, atomically swaps the
  /opt/mineclaw/current symlink, keeps the newest 3 releases, installs/refreshes the systemd unit, runs daemon-reload,
  enables and restarts the service, then health-checks it (service active + a "joined ... as" log line).

  Config: src/MineClaw.App/appsettings.json (gitignored, holds the API keys) goes to /etc/mineclaw/appsettings.json
  as 0640 root:mineclaw, but only with -PushConfig or when the CT doesn't have one yet. Its contents are never printed.
  Data: the first deploy starts from the committed seed (data/seed/mineclaw-seed.db). An existing
  /var/lib/mineclaw/mineclaw.db is never overwritten, and the local data/mineclaw.db is never copied.

.EXAMPLE
  pwsh deploy/deploy-alice.ps1                  # test, build, deploy, restart, health check
  pwsh deploy/deploy-alice.ps1 -SkipTests -PushConfig
  pwsh deploy/deploy-alice.ps1 -Status
  pwsh deploy/deploy-alice.ps1 -Rollback        # restores the matching db backup, or refuses when the old build can't open the db
  pwsh deploy/deploy-alice.ps1 -Rollback -DryRun  # print the rollback plan (releases, db schemas, backup) and change nothing
  pwsh deploy/deploy-alice.ps1 -NoStart         # install everything but leave the service (and whether it is enabled) as it is
  pwsh deploy/deploy-alice.ps1 -ResetMemory -Pack shards/defaults/1.12.2-gamedata.mcshard
                                                # first deploy into a NEW world: stop, install, back up /var/lib/mineclaw to
                                                # /var/lib/mineclaw-backup-<utc>, fresh db with only the pack (+ sdk-js.mcshard
                                                # beside it, no mode), server-version.txt; the service stays stopped
  pwsh deploy/deploy-alice.ps1 -ResetMemory -Pack shards/defaults/1.12.2-gamedata.mcshard -Start   # ... and start it after
  pwsh deploy/deploy-alice.ps1 -ResetMemory -Pack shards/defaults/1.12.2-gamedata.mcshard -DryRun  # print the CT script, touch nothing

.NOTES
  -ResetMemory runs MineClaw.App --reset-memory inside the CT as the service user while the service is stopped (see
  src/MineClaw.App/Hosting/MemoryReset.cs and ct-release.sh reset-memory). It never touches /etc/mineclaw or usage.json.
  -DryRun without -Rollback prints the plan and the script that would run on the PVE host, then stops: no tests, no publish, no ssh.
#>
#Requires -Version 7.0
[CmdletBinding()]
param(
    [switch]$SkipTests,
    [switch]$PushConfig,
    [switch]$Rollback,
    [switch]$Status,
    [switch]$NoStart,
    [switch]$DryRun,
    [switch]$Force,
    [switch]$ResetMemory,
    [string]$Pack,
    [switch]$Start,
    [string]$PveHost = 'alice',
    [int]$Vmid = 114,
    [int]$HealthTimeoutSec = 180
)
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$ssh = @('-o', 'BatchMode=yes', '-o', 'ConnectTimeout=15')

function Invoke-Pve([string]$Command, [switch]$AllowFail, [switch]$Quiet) {
    if ($Quiet) { & ssh @ssh $PveHost $Command 2>&1 | Out-Null } else { & ssh @ssh $PveHost $Command | Out-Host }
    if (-not $AllowFail -and $LASTEXITCODE -ne 0) { throw "on ${PveHost}: '$Command' failed ($LASTEXITCODE)" }
    return $LASTEXITCODE
}
function Copy-Lf([string]$From, [string]$To) {   # bash and systemd want LF; git on Windows may have checked out CRLF
    [IO.File]::WriteAllText($To, ([IO.File]::ReadAllText($From) -replace "`r`n", "`n"))
}

$release = "/usr/local/sbin/mineclaw-release"
if ($Status) { $null = Invoke-Pve "pct exec $Vmid -- $release status"; return }
if ($Rollback) {
    # The CT side checks the live db schema against the older build's (releases/<id>/SCHEMA_VERSION) and restores the db backup
    # taken under that release at install time, or refuses (live 10-27: the old build crash-looped on schema v6).
    $flags = @(if ($DryRun) { '--dry-run' }; if ($Force) { '--force' }) -join ' '
    $rc = Invoke-Pve "pct exec $Vmid -- $release rollback --timeout $HealthTimeoutSec $flags" -AllowFail
    if ($rc -ne 0) { throw "rollback refused or failed (exit $rc); see the lines above" }
    return
}

$cfg = Join-Path $repo 'src/MineClaw.App/appsettings.json'
$id = Get-Date -Format 'yyyyMMdd-HHmmss'
$stage = Join-Path ([IO.Path]::GetTempPath()) "mineclaw-deploy-$id"
$remote = '/tmp/mineclaw-deploy'

# The fresh-world reset: the version pack (+ sdk-js.mcshard beside it) rides along and is applied after the install.
$packFiles = @()
if ($Start -and -not $ResetMemory) { throw "-Start goes with -ResetMemory (a plain deploy starts the service unless -NoStart)" }
if ($Pack -and -not $ResetMemory) { throw "-Pack goes with -ResetMemory" }
if ($ResetMemory) {
    if (-not $Pack) { throw "-ResetMemory needs -Pack <version pack .mcshard> (e.g. shards/defaults/1.12.2-gamedata.mcshard)" }
    $packPath = if ([IO.Path]::IsPathRooted($Pack)) { $Pack } else { Join-Path $repo $Pack }
    if (-not (Test-Path $packPath)) { throw "no pack at $packPath (build it: dotnet run --project tools/ShardPacks -- build --version 1.12.2)" }
    $packFiles = @($packPath)
    $sdkPack = Join-Path (Split-Path -Parent $packPath) 'sdk-js.mcshard'
    if ((Test-Path $sdkPack) -and ((Split-Path -Leaf $packPath) -ne 'sdk-js.mcshard')) { $packFiles += $sdkPack }
}

# The script that runs on the PVE host (pushes into the CT, installs, optional reset). Built before anything connects so -DryRun can show it.
function New-RunScript([string]$CfgPush, [string]$AssetPush) {
    $noStart = $NoStart -or $ResetMemory   # a reset needs the service stopped: never start the new build before it
    $startFlag = if ($noStart) { ' --no-start' } else { '' }
    $stop = if ($ResetMemory) { "pct exec `$v -- systemctl stop mineclaw || true; echo 'service: stopped for the memory reset'" } else { '' }
    $packPush = ($packFiles | ForEach-Object { "pct push `$v `$d/$(Split-Path -Leaf $_) /root/mineclaw-deploy/$(Split-Path -Leaf $_)" }) -join "`n"
    $reset = if ($ResetMemory) {
        "pct exec `$v -- $release reset-memory /root/mineclaw-deploy/$(Split-Path -Leaf $packFiles[0]) --timeout $HealthTimeoutSec$(if ($Start) { ' --start' })"
    } else { '' }
    return @"
set -euo pipefail
v=$Vmid; d=$remote
trap 'rm -rf `$d; pct exec `$v -- rm -rf /root/mineclaw-deploy || true' EXIT
pct exec `$v -- install -d -m 0700 /root/mineclaw-deploy
pct push `$v `$d/mineclaw.tgz /root/mineclaw-deploy/mineclaw.tgz
pct push `$v `$d/mineclaw.service /root/mineclaw-deploy/mineclaw.service
pct push `$v `$d/ct-release.sh $release --perms 0755
$CfgPush
$AssetPush
$packPush
$stop
pct exec `$v -- $release install /root/mineclaw-deploy/mineclaw.tgz $id --unit /root/mineclaw-deploy/mineclaw.service --timeout $HealthTimeoutSec$startFlag
$reset
"@ -replace "`r`n", "`n"
}

if ($DryRun) {
    Write-Host "== dry run: nothing is tested, published, uploaded or run" -ForegroundColor Cyan
    Write-Host "release $id -> CT $Vmid on $PveHost$(if ($ResetMemory) { "; memory reset with $($packFiles | ForEach-Object { Split-Path -Leaf $_ })" })"
    Write-Host "service after: $(if ($ResetMemory -and $Start) { 'started (after the reset)' } elseif ($ResetMemory -or $NoStart) { 'stopped / as it was (enabled state unchanged)' } else { 'enabled + restarted' })"
    Write-Host "--- run.sh"
    Write-Host (New-RunScript ($(if ($PushConfig) { '<config push>' } else { '' })) '')
    return
}

# 0. Is the CT there, and does it already have a config?
if ((Invoke-Pve "pct status $Vmid" -AllowFail -Quiet) -ne 0) { throw "CT $Vmid not found on $PveHost (run deploy/provision-alice.sh)" }
$hasCfg = (Invoke-Pve "pct exec $Vmid -- test -f /etc/mineclaw/appsettings.json" -AllowFail -Quiet) -eq 0
$sendCfg = $PushConfig -or -not $hasCfg
if ($sendCfg -and -not (Test-Path $cfg)) { throw "need to push config but $cfg is missing" }

# 1. Tests
if (-not $SkipTests) {
    Write-Host "== tests" -ForegroundColor Cyan
    # MineClaw.sln doesn't list the per-project test projects, so run each one.
    $failed = @()
    foreach ($p in Get-ChildItem (Join-Path $repo 'tests') -Recurse -Filter '*.Tests.csproj') {
        dotnet test $p.FullName -c Release --nologo -v q | Out-Host
        if ($LASTEXITCODE -ne 0) { $failed += $p.BaseName }
    }
    if ($failed) { throw "tests failed: $($failed -join ', '); fix them or pass -SkipTests" }
}

# 2. Publish (self-contained linux-x64)
Write-Host "== publish" -ForegroundColor Cyan
Push-Location $repo
try { & (Join-Path $PSScriptRoot 'publish-linux.ps1') | Out-Host } finally { Pop-Location }
$out = Join-Path $repo 'deploy/out/mineclaw'
if (-not (Test-Path (Join-Path $out 'MineClaw.App'))) { throw "publish produced no $out/MineClaw.App" }
if (Test-Path (Join-Path $out 'appsettings.json')) { throw "refusing to ship: publish output contains appsettings.json" }

# 3. Pack and upload
Write-Host "== pack + upload release $id" -ForegroundColor Cyan
New-Item -ItemType Directory -Force $stage | Out-Null
try {
    # Windows' bsdtar; GNU tar (e.g. Git Bash first on PATH) would read "C:" as a remote host.
    $tar = Join-Path $env:SystemRoot 'System32\tar.exe'
    $tarArgs = @('-czf', (Join-Path $stage 'mineclaw.tgz'), '-C', (Join-Path $repo 'deploy/out'), 'mineclaw')
    if (Test-Path $tar) { & $tar @tarArgs } else { & tar --force-local @tarArgs }
    if ($LASTEXITCODE -ne 0) { throw "tar failed ($LASTEXITCODE)" }
    Copy-Lf (Join-Path $PSScriptRoot 'mineclaw.service') (Join-Path $stage 'mineclaw.service')
    Copy-Lf (Join-Path $PSScriptRoot 'ct-release.sh') (Join-Path $stage 'ct-release.sh')
    $cfgPush = ''
    if ($sendCfg) {
        $cfgPush = "pct push $Vmid $remote/appsettings.json /etc/mineclaw/appsettings.json --user root --group mineclaw --perms 0640; " +
                   "rm -f $remote/appsettings.json; echo 'config: pushed /etc/mineclaw/appsettings.json (0640 root:mineclaw)'"
    }
    # b1.8.1 textures for the `see` tool (copyrighted: extracted locally from the user's own client jar into the gitignored
    # data/assets/, never in the repo or the build). Pushed to the CT's data dir when present; without them `see` uses flat colours.
    $assetPush = ''
    $assetFiles = @('terrain.png', 'items.png', 'char.png') | Where-Object { Test-Path (Join-Path $repo "data/assets/$_") }
    if ($assetFiles -contains 'terrain.png') {
        foreach ($a in $assetFiles) { Copy-Item (Join-Path $repo "data/assets/$a") (Join-Path $stage $a) }
        $assetPush = "pct exec `$v -- install -d -o mineclaw -g mineclaw -m 0755 /var/lib/mineclaw/assets; " +
            (($assetFiles | ForEach-Object { "pct push `$v `$d/$_ /var/lib/mineclaw/assets/$_ --user mineclaw --group mineclaw --perms 0644" }) -join '; ') +
            "; echo 'assets: pushed $($assetFiles -join ', ')'"
    }
    foreach ($p in $packFiles) { Copy-Item $p (Join-Path $stage (Split-Path -Leaf $p)) }
    $script = New-RunScript $cfgPush $assetPush
    [IO.File]::WriteAllText((Join-Path $stage 'run.sh'), $script)

    $null = Invoke-Pve "rm -rf $remote && install -d -m 0700 $remote"
    $files = @(@('mineclaw.tgz', 'mineclaw.service', 'ct-release.sh', 'run.sh') + @($assetFiles | Where-Object { $assetPush }) +
        @($packFiles | ForEach-Object { Split-Path -Leaf $_ }) | ForEach-Object { Join-Path $stage $_ })
    & scp @ssh -q @files "${PveHost}:$remote/"
    if ($LASTEXITCODE -ne 0) { throw "scp failed ($LASTEXITCODE)" }
    if ($sendCfg) {
        & scp @ssh -q $cfg "${PveHost}:$remote/appsettings.json"   # dir is 0700 root; deleted right after the push
        if ($LASTEXITCODE -ne 0) { throw "scp of config failed ($LASTEXITCODE)" }
    }

    # 4-7. Install inside the CT: swap, unit, daemon-reload, restart, health check
    Write-Host "== install in CT $Vmid" -ForegroundColor Cyan
    $rc = Invoke-Pve "bash $remote/run.sh" -AllowFail
}
finally {
    Remove-Item -Recurse -Force $stage -ErrorAction SilentlyContinue
}

# 8. Short status
Write-Host "== status" -ForegroundColor Cyan
$null = Invoke-Pve "pct exec $Vmid -- $release status" -AllowFail
if ($rc -ne 0) { throw "deploy of $id finished with errors (exit $rc). Roll back: pwsh deploy/deploy-alice.ps1 -Rollback" }
Write-Host "deployed release $id to CT $Vmid on $PveHost" -ForegroundColor Green
