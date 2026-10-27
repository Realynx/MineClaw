# Deploying MineClaw on a Proxmox LXC (systemd)

MineClaw runs the same binary two ways:

- **Interactive** (`./MineClaw.App` in a terminal, SSH included): a full-screen TUI with a header, an ASCII map, token/cost
  bars, the brain's current thoughts, chat, and an input line. `q` or Ctrl+C quits cleanly.
- **Headless** (`--no-tui`, or automatic when stdout/stdin isn't a TTY, under systemd, or with `TERM=dumb`): one log
  line per event on stdout with no colours. Under journald each line carries a `<N>` priority prefix (systemd formatter),
  so `journalctl -p warning` works. stdin is never read unless you pass `--stdin-commands`.

## 1. Build (on your dev box)

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

## 2. Container

In Proxmox, create an unprivileged CT (Debian 12 or Ubuntu 24.04, 1 vCPU, 1 GB RAM and 4 GB disk are plenty) with
**`nesting=1`** (Options → Features). Without nesting, systemd can't set up the mount namespaces that the hardening
options need, and the unit fails with `status=226/NAMESPACE`. If you can't enable nesting, comment out `PrivateTmp`,
`PrivateDevices`, `ProtectSystem`, `ProtectHome` and the `Protect*` lines in the unit.

```bash
apt update && apt install -y ca-certificates libicu72 libssl3   # Ubuntu 24.04: libicu74
useradd --system --home /var/lib/mineclaw --shell /usr/sbin/nologin mineclaw
```

## 3. Install

```bash
# from the dev box
scp -r deploy/out/mineclaw root@CT:/opt/

# in the CT
chmod +x /opt/mineclaw/MineClaw.App
install -d -m 0750 -o root -g mineclaw /etc/mineclaw
install -m 0640 -o root -g mineclaw /path/to/appsettings.json /etc/mineclaw/appsettings.json   # NO secrets in it
install -m 0600 -o root -g root /opt/mineclaw/mineclaw.env.example /etc/mineclaw/mineclaw.env
nano /etc/mineclaw/mineclaw.env                                                               # put the API key here
cp /opt/mineclaw/mineclaw.service /etc/systemd/system/
systemctl daemon-reload
systemctl enable --now mineclaw
# optional, the first time: copy the clean-state seed db into the state dir
install -d -o mineclaw -g mineclaw /var/lib/mineclaw/seed && cp -n /opt/mineclaw/seed/* /var/lib/mineclaw/seed/ 2>/dev/null || true
```

### Config and secrets

- Start from `src/MineClaw.App/appsettings.example.json`, delete the `ApiKey` line, and save it as
  `/etc/mineclaw/appsettings.json`. It holds only non-secret settings (server, persona, budget, hive names).
- Secrets live in `/etc/mineclaw/mineclaw.env` (mode 0600, owned by root), loaded through systemd's
  `EnvironmentFile=`. systemd reads the file before it drops to `User=mineclaw`.
- Any key can be overridden with an env var named `MINECLAW_<Section>__<Key>`, for example
  `MINECLAW_Agent__Llm__ApiKey`, `MINECLAW_Minecraft__Host` or `MINECLAW_Host__ShutdownTimeoutSeconds`.
- Precedence, lowest to highest:
  1. `appsettings.json` next to the binary
  2. `--config` (or `MINECLAW_CONFIG`)
  3. plain env vars (legacy `Agent__Llm__ApiKey`)
  4. `MINECLAW_` env vars
  5. CLI `--data-dir`, `--log-dir`, `--log-format` and `--shutdown-timeout`
- The unit uses these paths:

  | Path | Contents |
  |---|---|
  | `/var/lib/mineclaw` | db, `usage.json`, seed |
  | `/var/log/mineclaw` | `mineclaw-*.log`, `usage-YYYY-MM-DD.jsonl` |
  | `/opt/mineclaw` | the binary, read-only |
  | `/etc/mineclaw` | config, read-only |

## 4. Operate

```bash
journalctl -u mineclaw -f                     # live log
journalctl -u mineclaw -p warning --since today
systemctl stop mineclaw                       # SIGTERM → graceful shutdown, exit 0
tail -f /var/log/mineclaw/usage-$(date +%F).jsonl | jq -c '{bot,tier,purpose,prompt_tokens,cost_usd,sections}'
```

**Shutdown.** SIGTERM or SIGINT triggers this sequence: stop the current action, stop the agent loop, run the hive
"deposit items before logout" step (`Host:PreLogoutSeconds`, 0 until the hive work lands), disconnect from the server,
then flush and close the db and the logs. The whole sequence is bounded by `Host:ShutdownTimeoutSeconds` (default 30).
Each step gets whatever time is left, and a slow step is abandoned instead of starving the flush. `TimeoutStopSec=90`
leaves headroom above that budget. A second SIGTERM kills the process immediately.

**Exit codes and restarts.**

| Code | Meaning | Restarted? |
|---|---|---|
| 0 | clean shutdown | no |
| 1 | crash | yes |
| 2 | config error | no |
| 3 | auth: key rejected or unknown model | no |
| 64 | bad command line | no |
| 75 | transient failure at startup, e.g. API unreachable | yes |

`Restart=on-failure` with `RestartPreventExitStatus=2 3 64` retries crashes and transient failures but won't
restart-loop a broken config. Connection drops to the Minecraft server never exit: the client reconnects by itself
with backoff.

**Watching it live.** The service is headless. To get the TUI, run a second copy interactively on your desktop against
the same server with a different username, or wait for the hive IPC. For a quick look at the TUI without any server:

```bash
/opt/mineclaw/MineClaw.App --demo                 # fake bots, no server, no API key
/opt/mineclaw/MineClaw.App --demo --frame 120x40  # print one frame and exit
```
