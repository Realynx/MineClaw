# Deploying MineClaw on a Proxmox LXC (systemd)

MineClaw runs the same binary two ways:

- **Interactive** (`./MineClaw.App` in a terminal, SSH included): a full-screen TUI with a header, an ASCII map, token/cost
  bars, the brain's current thoughts, chat, and an input line. `q` or Ctrl+C quits cleanly.
- **Headless** (`--no-tui`, or automatic when stdout/stdin isn't a TTY, under systemd, or with `TERM=dumb`): one log
  line per event on stdout with no colours. Under journald each line carries a `<N>` priority prefix (systemd formatter),
  so `journalctl -p warning` works. stdin is never read unless you pass `--stdin-commands`.

## Production: CT 114 "claudechan" on alice (24/7)

| | |
|---|---|
| Container | **CT 114** `claudechan` on PVE node **alice**: unprivileged, `nesting=1`, 4 cores, 4 GB RAM, 512 MB swap, 16 GB on `local-lvm`, `onboot=1`, `startup: order=90,up=15`, Ubuntu 26.04 |
| Network | `10.1.2.73/24`, VLAN 3, gw `10.1.2.1`, PVE firewall on |
| Firewall | `/etc/pve/firewall/114.fw`: `GROUP allow-ssh`, then `GROUP isolated` (isolated stays LAST). `10.1.2.73` is in `[IPSET mc-servers]` in `cluster.fw`, so the Minecraft server (CT 160, 10.1.2.69:25565 on phoenix) accepts it. |
| Bot | `maid_chan` on `10.1.2.69:25565` |
| Paths | `/opt/mineclaw/releases/<yyyyMMdd-HHmmss>/` with the `/opt/mineclaw/current` symlink (newest 3 kept); `/etc/mineclaw/appsettings.json` (0640 root:mineclaw, holds the API keys); `/var/lib/mineclaw` (db, seed, usage); `/var/log/mineclaw` (text logs, `usage-*.jsonl`) |

**The CT was created once with `deploy/provision-alice.sh`.** That script is the reproducible record (VMID, IP and
resources are parameters) for disaster recovery. Don't run it against the live setup: it aborts if the VMID or IP is
already taken.

> **Stop the service before any dev live test as `maid_chan`.**
> `pct exec 114 -- systemctl stop mineclaw`, then `start` again afterwards.
> If you forget: your dev login kicks the service bot with "You logged in from another location". It then waits
> `Minecraft:DuplicateLoginBackoffSeconds` (default 600 s) before rejoining, and that rejoin will kick your dev
> session. The reverse also happens: starting a dev client while the bot is online kicks the bot.

### Operations (from the Windows dev box, repo root)

```powershell
pwsh deploy/deploy-alice.ps1                 # tests, publish, upload, swap release, refresh unit, restart, health check
pwsh deploy/deploy-alice.ps1 -SkipTests      # same, without running the test suite
pwsh deploy/deploy-alice.ps1 -PushConfig     # also replace /etc/mineclaw/appsettings.json with src/MineClaw.App/appsettings.json
pwsh deploy/deploy-alice.ps1 -NoStart        # install everything, don't (re)start
pwsh deploy/deploy-alice.ps1 -Status         # release, systemd state, restarts, db size, last join, last 15 log lines
pwsh deploy/deploy-alice.ps1 -Rollback       # current -> previous release, restart, health check
```

- **Config.** It is pushed only with `-PushConfig`, or when the CT has none yet. Its contents are never printed.
- **Data.** The first deploy starts **fresh from the committed seed** `data/seed/mineclaw-seed.db`: the seed is staged
  in `/var/lib/mineclaw/seed/` and the app bootstraps `mineclaw.db` from it on first start. An existing
  `/var/lib/mineclaw/mineclaw.db` is never overwritten. The local `data/mineclaw.db` (test chatter) is never shipped.
- **Health check.** The service must be `active` and log `joined 10.1.2.69:25565 as maid_chan` within
  `-HealthTimeoutSec` (default 180). If it doesn't, the script prints the recent log and fails, and you can run `-Rollback`.
- **Other parameters.** `-PveHost alice` and `-Vmid 114`.

### Operations (on alice, or `ssh alice '…'`)

```bash
pct exec 114 -- journalctl -u mineclaw -f                       # live log
pct exec 114 -- journalctl -u mineclaw -p warning --since today
pct exec 114 -- systemctl stop mineclaw                         # SIGTERM → graceful shutdown (do this before dev tests!)
pct exec 114 -- systemctl start mineclaw
pct exec 114 -- systemctl restart mineclaw
pct exec 114 -- /usr/local/sbin/mineclaw-release status                         # same as -Status
pct exec 114 -- /usr/local/sbin/mineclaw-release rollback                       # same as -Rollback
pct exec 114 -- bash -c 'tail -f /var/log/mineclaw/usage-$(date +%F).jsonl'
```

`mineclaw-release` is `deploy/ct-release.sh`, installed on every deploy. It can do `install`, `rollback`, `restart`,
`health` and `status`.

### Staying up

- **Joining the server.** The app joins on start and retries the first connect with backoff (3 s doubling to 60 s,
  forever).
- **Drops while playing.** A server restart, "server is full", or a network drop all trigger the client's reconnect:
  5 s doubling to 2 min, forever.
- **"You logged in from another location".** Someone else is on the account, so the first retry waits
  `Minecraft:DuplicateLoginBackoffSeconds` (default 600, settable in appsettings or via
  `MINECLAW_Minecraft__DuplicateLoginBackoffSeconds`). The journal logs
  `another session is using maid_chan; backing off 600s`.
- **Every (re)join** logs `joined <host>:<port> as <user>`.
- **systemd** uses `Restart=always`, `RestartSec=10` growing to 5 min (`RestartSteps`/`RestartMaxDelaySec`), and
  `StartLimitIntervalSec=0`, so it never gives up. The exceptions are exit 2 (config error) and exit 64 (bad command
  line), which need a fix and a redeploy.
- **Reboots.** The CT has `onboot=1` and the unit is enabled (`WantedBy=multi-user.target`,
  `After=network-online.target`).

## Build (any target)

```bash
deploy/publish-linux.sh          # or: pwsh deploy/publish-linux.ps1
```

This produces `deploy/out/mineclaw/`, a self-contained `linux-x64` build that needs no .NET install:

- `MineClaw.App` is a single-file executable. Trimming is off.
- `libe_sqlite3.so` (SQLite with FTS5 and R*Tree) sits next to it. Native libs are deliberately **not** bundled into the
  single file (`IncludeNativeLibrariesForSelfExtract=false`), so nothing has to be extracted to `$HOME` or `/tmp` at
  startup. That keeps it compatible with `ProtectHome=` and `PrivateTmp=`. Use `--multi` for a classic multi-file
  layout.
- `docs/knowledge/` and `seed/` ship with it, plus `mineclaw.service` and `mineclaw.env.example`.
- The gitignored `appsettings.json` (your API key) is **never** copied into the output.

Runtime deps on the host: `libicu`, `libssl3`, `zlib1g`, `ca-certificates`, `tzdata`.

## Manual install on another container

Create an unprivileged CT with **`nesting=1`**. Without it, systemd can't set up the mount namespaces that the
hardening needs, and the unit fails with `status=226/NAMESPACE`. If you can't enable nesting, comment out
`PrivateTmp`, `PrivateDevices`, `ProtectSystem`, `ProtectHome` and the `Protect*` lines. `deploy/provision-alice.sh`
does all of this for a PVE node. By hand:

```bash
apt update && apt install -y --no-install-recommends ca-certificates tzdata libicu78 libssl3t64 zlib1g   # Ubuntu 26.04
useradd --system --home-dir /var/lib/mineclaw --no-create-home --shell /usr/sbin/nologin mineclaw
install -d -m 0750 -o root -g mineclaw /etc/mineclaw
install -d -m 0750 -o mineclaw -g mineclaw /var/lib/mineclaw /var/log/mineclaw
mkdir -p /opt/mineclaw/releases && cp -r mineclaw /opt/mineclaw/releases/manual && ln -sfn releases/manual /opt/mineclaw/current
chmod +x /opt/mineclaw/current/MineClaw.App
install -m 0640 -o root -g mineclaw appsettings.json /etc/mineclaw/appsettings.json
install -d -o mineclaw -g mineclaw /var/lib/mineclaw/seed && cp /opt/mineclaw/current/seed/* /var/lib/mineclaw/seed/   # first time only
cp /opt/mineclaw/current/mineclaw.service /etc/systemd/system/ && systemctl daemon-reload && systemctl enable --now mineclaw
```

### Config and secrets

- **The config file.** `/etc/mineclaw/appsettings.json` (0640 root:mineclaw) is the full config, keys included. It is
  readable by the service user only, and `/etc/mineclaw` is 0750.
- **Keeping keys out of the JSON (optional).** Put them in `/etc/mineclaw/mineclaw.env` (0600 root, loaded with
  `EnvironmentFile=` before systemd drops privileges) as `MINECLAW_<Section>__<Key>=…`.
- **Precedence**, lowest to highest:
  1. `appsettings.json` next to the binary
  2. `--config` (or `MINECLAW_CONFIG`)
  3. plain env vars (legacy `Agent__Llm__ApiKey`)
  4. `MINECLAW_` env vars
  5. CLI `--data-dir`, `--log-dir`, `--log-format` and `--shutdown-timeout`
- **Knowledge dir.** The unit sets `MINECLAW_Agent__KnowledgeDirectory=/opt/mineclaw/current/docs/knowledge`.
- **Player lists (migration).** The code no longer names any player by default: who is guarded, sparred with, trusted for
  void parkour, called by a nickname or known as someone else's bot comes only from config. A config written before that
  change must set `Minecraft:GuardPlayers`, `Minecraft:SparPlayers`, `Minecraft:ParkourTrustedPlayers`,
  `Agent:Chat:PlayerNicknames` and `Agent:Chat:OtherBots` (see `src/MineClaw.App/appsettings.example.json`), or the bots
  stop guarding and sparring, lose the nicknames, and treat a friend's bot as a human. Startup logs a warning when
  `Agent:Self:TrustedPlayers` is set but one of the `Minecraft:` lists is empty.

### Shutdown and exit codes

**Shutdown.** SIGTERM or SIGINT triggers this sequence: stop the current action, stop the agent loop, run the hive
"deposit items before logout" step (`Host:PreLogoutSeconds`), disconnect from the server, then flush and close the db
and the logs. The whole sequence is bounded by `Host:ShutdownTimeoutSeconds` (default 30). `TimeoutStopSec=120` leaves
headroom above that budget, including for future clone deposits. A second SIGTERM kills the process immediately.

| Code | Meaning | Restarted? |
|---|---|---|
| 0 | clean shutdown (not via `systemctl stop`) | yes |
| 1 | crash | yes |
| 2 | config error | no |
| 3 | auth: key rejected or unknown model | yes, with backoff up to 5 min |
| 64 | bad command line | no |
| 75 | transient failure at startup, e.g. API unreachable | yes |

**Watching it live.** The service is headless. For the TUI, run a second copy on your desktop with a **different
username**, not `maid_chan` (see the warning above). For a quick look at the TUI without any server:

```bash
/opt/mineclaw/current/MineClaw.App --demo                 # fake bots, no server, no API key
/opt/mineclaw/current/MineClaw.App --demo --frame 120x40  # print one frame and exit
```
