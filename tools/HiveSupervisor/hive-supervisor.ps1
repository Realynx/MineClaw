<#
.SYNOPSIS
  Opens the read-only Hive Supervisor viewer: an SSH tunnel to the main bot's endpoint, then the browser.

.DESCRIPTION
  The main bot (MineClaw.App in CT 114 on alice) serves the viewer on its LAN address. This forwards
  localhost:<LocalPort> through alice to <Target>:<Port>, so no firewall changes are needed. Ctrl+C closes the tunnel.

.EXAMPLE
  ./tools/HiveSupervisor/hive-supervisor.ps1
  ./tools/HiveSupervisor/hive-supervisor.ps1 -Token (Get-Content ~/.mineclaw-supervisor-token)
#>
param(
    [string]$SshHost = "alice",
    [string]$Target = "10.1.2.73",
    [int]$Port = 8765,
    [int]$LocalPort = 8765,
    # Supervisor:Token, if one is configured. Also read from $env:MINECLAW_SUPERVISOR_TOKEN. Never printed.
    [string]$Token = $env:MINECLAW_SUPERVISOR_TOKEN,
    [switch]$NoBrowser
)

$ErrorActionPreference = "Stop"
$ssh = Get-Command ssh -ErrorAction SilentlyContinue
if (-not $ssh) { Write-Error "ssh not found (Windows: Settings > Optional features > OpenSSH Client)"; exit 1 }

Write-Host "tunnel: localhost:$LocalPort -> $Target`:$Port via $SshHost (Ctrl+C to close)"
$tunnel = Start-Process -FilePath $ssh.Source -ArgumentList @("-N", "-o", "ExitOnForwardFailure=yes", "-o", "ServerAliveInterval=30",
    "-L", "$LocalPort`:$Target`:$Port", $SshHost) -NoNewWindow -PassThru

try {
    $up = $false
    for ($i = 0; $i -lt 40 -and -not $tunnel.HasExited; $i++) {
        try {
            $c = [System.Net.Sockets.TcpClient]::new()
            $c.Connect("127.0.0.1", $LocalPort)
            $c.Dispose()
            $up = $true
            break
        } catch { Start-Sleep -Milliseconds 250 }
    }
    if (-not $up) { Write-Error "the tunnel didn't come up (is Supervisor:Enabled true on the bot, and does 'ssh $SshHost' work?)"; exit 1 }

    $url = "http://localhost:$LocalPort/"
    # The page stores the token in sessionStorage and removes it from the address bar at once.
    $open = if ($Token) { "$url`?token=$([uri]::EscapeDataString($Token))" } else { $url }
    Write-Host "viewer: $url"
    if (-not $NoBrowser) { Start-Process $open }
    Wait-Process -Id $tunnel.Id
}
finally {
    if (-not $tunnel.HasExited) { Stop-Process -Id $tunnel.Id -Force -ErrorAction SilentlyContinue }
    Write-Host "tunnel closed"
}
