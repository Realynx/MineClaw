# Web Explorer: deploying to CT 114

The Web Explorer runs on CT 114 (claudechan, 10.1.2.73) on the Proxmox node alice, beside the bot (`mineclaw`). Design:
`docs/web/architecture.md`. Config keys: `deploy/viewer/web/README.md` and `deploy/viewer/bridge/README.md`.

Everything goes the way the bot's deploy goes: `ssh alice`, then `pct push` / `pct exec 114`. Nothing connects to the CT directly.

## What runs where

| Unit | User | Listens | Code | Memory |
|---|---|---|---|---|
| `mineclaw-web-api` (NestJS) | `mineclaw-web` | **0.0.0.0:8080**, the only new port on the network | `/opt/mineclaw-web/current/apps/api` | `MemoryMax=512M` |
| `mineclaw-web-ui` (Next standalone) | `mineclaw-web` | 127.0.0.1:3000 | `/opt/mineclaw-web/current/apps/web/.next/standalone/apps/web` | `MemoryMax=384M` |
| `mineclaw-bridge` (C# ViewerBridge) | `mineclaw-bridge`, group `mineclaw` | 127.0.0.1:8766 (HMAC-signed calls only) | `/opt/mineclaw-bridge/current` | `MemoryMax=600M` |
| `mineclaw` (the bot, unchanged) | `mineclaw` | Supervisor :8765 | `/opt/mineclaw/current` | |

| Path | Mode | What |
|---|---|---|
| `/opt/node/current` → `node-v22.23.4-linux-x64`, `/usr/local/bin/node` | 0755 root | Node 22 LTS, the official tarball pinned by sha256 in `deploy/viewer/install-ct114.sh` |
| `/opt/mineclaw-web/releases/<id>`, `/opt/mineclaw-bridge/releases/<id>` | 0755 root, read-only to the services | one release id for both, the newest 3 kept |
| `/etc/mineclaw-web/` | 0750 mineclaw-web | `config.json` (0640), `users.json` (0600: the setup page writes the first account, the users CLI the rest), `bridge.key` (0600 root) |
| `/etc/mineclaw-bridge/bridge.json` | 0600 root | `{"Bridge":{"Key":...}}`, the same key as `bridge.key` |
| `/var/lib/mineclaw-web/web.db` | 0700 dir, mineclaw-web | sessions, login counters, web audit |
| `/usr/local/sbin/mineclaw-web-release` | | release manager (install / rollback / restart / health / status) |
| `/usr/local/sbin/mineclaw-web-users` | | the users CLI, run as `mineclaw-web` (manual user management; needs a real terminal) |
| `/usr/local/sbin/mineclaw-viewer-config` | | the bot config step (dry run unless `--apply`) |

Both key files are root-only. systemd hands them to the services as credentials (`LoadCredential=`), so neither service user can
read the other's copy, and the key never shows up in env, argv or a log.

## Scripts

| From the dev box | Does |
|---|---|
| `pwsh deploy/deploy-web.ps1 -Install` | one time (re-runnable): `install-ct114.sh` inside the CT |
| `pwsh deploy/deploy-web.ps1 -BotConfig [-Apply] [-RestartBot] [-AdminWrites]` | the bot config step; a dry run without `-Apply` |
| `pwsh deploy/deploy-web.ps1` (= `deploy-alice.ps1 -Web`) | test, build, ship, restart the three web units, health check |
| `pwsh deploy/deploy-web.ps1 -Status` (= `deploy-alice.ps1 -WebStatus`) | releases, unit states and memory, a health probe, recent logs |
| `pwsh deploy/deploy-web.ps1 -Rollback [-DryRun]` | both trees back to the previous release |
| `pwsh deploy/deploy-web.ps1 -DryRun` | prints the plan and the PVE-side script; builds nothing, connects nowhere |
| `pwsh deploy/deploy-web.ps1 -BuildOnly [-Smoke]` | local only: build and assemble into `deploy/out/web`; `-Smoke` also runs the assembled bundle here and checks it |

`deploy-alice.ps1` without `-Web` / `-WebStatus` still deploys the bot exactly as before. The web deploy never builds,
restarts or reconfigures the bot.

## First-time runbook

### 0. What you must approve or do yourself

1. **The firewall rule for TCP 8080** (step 1). It changes alice's firewall; nothing here adds it.
2. **Running `-Install`** (step 2). It is a one-time root script in CT 114: it downloads Node from nodejs.org on the dev box,
   adds two system users, writes the key files and installs three units. It does not start anything.
3. **The bot config change** (step 3). It edits `/etc/mineclaw/appsettings.json`. The bot has to restart to pick it up,
   which you can do then or later.
4. **Creating your account** (step 5). Do this yourself in your browser on the LAN, right after the first deploy: whoever
   completes the setup page first becomes the admin.
5. **Optionally, the hive memory ceiling** (see Memory). This is only a recommendation; nothing changes it for you.

### 1. Firewall: allow TCP 8080 from the LAN

CT 114's `/etc/pve/firewall/114.fw` is `GROUP allow-ssh` then `GROUP isolated`, and `isolated` must stay last. Add one
inbound rule *above* both group lines, so it matches first. Use your PC's subnet (or a single /32), or an IPSET that already
holds the machines that should reach the explorer:

```
[RULES]

IN ACCEPT -source 192.168.1.0/24 -p tcp -dport 8080 -log nolog # MineClaw Web Explorer (LAN; later only the tunnel)
GROUP allow-ssh
GROUP isolated
```

In the GUI this is alice › CT 114 › Firewall › Add: direction `in`, action `ACCEPT`, source = your subnet or IPSET, protocol
`tcp`, dest. port `8080`, comment as above. Then move it to the top.

- `192.168.1.0/24` (your LAN) is only an example. Other networks need their own rule. Use the one your browser is on, and prefer the narrowest source that works.
- Check it: `pve-firewall compile >/dev/null && echo ok`, then `pve-firewall status` on alice.
- If the PC is on another VLAN, OPNsense must also route it to 10.1.2.73:8080. If it already reaches the CT's SSH, it reaches
  8080 too.
- Nothing else needs opening. 3000, 8766 and 8765 are loopback (or already firewalled), and the deploy itself goes through
  `ssh alice`.

### 2. Install the host side (one time, re-runnable)

```powershell
pwsh deploy/deploy-web.ps1 -Install -DryRun     # what it would push and run
pwsh deploy/deploy-web.ps1 -Install
```

What it does:

1. **Node.** Downloads `node-v22.23.4-linux-x64.tar.xz` to `deploy/out/cache`, checks it against the sha256 pinned in
   `install-ct114.sh`, then stages it with the units, `config.example.json` and the helper scripts in the CT.
2. **The install script.** Runs `install-ct114.sh` in the CT:
   - **Node 22** goes into `/opt/node`.
   - **Users.** `mineclaw-web` gets its own group. `mineclaw-bridge` is in group `mineclaw`.
   - **Directories** as in the table above.
   - **The bridge HMAC key.** 32 random bytes as hex go into both root-only files. An existing key is kept.
   - **`/etc/mineclaw-web/config.json`**, only when it is missing. It has `AllowedOrigins: ["http://10.1.2.73:8080"]`,
     `CookieSecure: "auto"`, `TrustProxy: false`, and a `_TunnelOriginTodo` placeholder for the cloudflared hostname.
   - **The helpers and the three units**, then `daemon-reload`. Nothing is enabled or started yet.

Re-running it is safe: keys, config and users are kept, and units and helpers are refreshed. Use `-Origin` (repeatable) to
change the origins of a *new* config.json. For an existing one, edit the file instead.

### 3. Bot config (Supervisor + Viewer), a separate step

The bridge proxies the bot's Supervisor with its token, and the viewer routes are off by default. The step makes sure of three
keys:

- `Supervisor:Enabled = true`
- `Supervisor:Token` is set. If it is missing, 32 random bytes as hex are generated. The value is never printed.
- `Supervisor:Viewer:Enabled = true`

```powershell
pwsh deploy/deploy-web.ps1 -BotConfig                 # dry run: prints what it WOULD change
pwsh deploy/deploy-web.ps1 -BotConfig -Apply          # writes it (backup appsettings.json.bak-<utc>, same owner and mode)
pwsh deploy/deploy-web.ps1 -BotConfig -Apply -RestartBot   # ... and restarts the bot (health-checked) and the bridge now
```

The step also reports these, without changing them:

- **`Viewer:AllowCommands`.** True lets web users with the `commands` role arm command grants. Set it by hand if you want that.
- **`AllowAdminWrites`.** The Admin page's writes. Pass `-AdminWrites` to turn it on.
- **`Supervisor:Bind`.** If it is the CT IP rather than `*` or `127.0.0.1`, the Supervisor does not answer on loopback. Set
  `MINECLAW_Bridge__SupervisorUrl=http://10.1.2.73:8765` in `/etc/mineclaw-bridge/bridge.env`, or change `Bind`.
- **Env overrides** in `/etc/mineclaw/mineclaw.env` (names only).
- **`Hive:Resources:LimitMb`.**

> **Warning: `-PushConfig` reverts this step.** `deploy-alice.ps1 -PushConfig` replaces `/etc/mineclaw/appsettings.json` with
> your local `src/MineClaw.App/appsettings.json`. Put the same `Supervisor` block in the local file, with its own token, or re-run
> `-BotConfig -Apply` after every `-PushConfig`. Restart the bridge after a token change: it reads the token at start.

The file must be plain JSON. If it has comments or trailing commas, the script stops and tells you what to set by hand.

### 4. First deploy

```powershell
pwsh deploy/deploy-web.ps1 -DryRun      # the plan
pwsh deploy/deploy-web.ps1              # or: pwsh deploy/deploy-alice.ps1 -Web
```

**Build** (on the dev box):

1. `npm ci` and `turbo run test` in `web/`, plus `tests/MineClaw.ViewerBridge.Tests`. `-SkipTests` skips both test runs.
2. `turbo run build --force` for the api and the web app.

> **Note:** `npm ci` deletes and reinstalls `web/node_modules`, so stop a running `npm run dev` before you deploy.

**Assemble** `deploy/out/web/mineclaw-web`:

- the api's `dist`
- the api's production dependencies, installed from the lockfile for linux-x64 glibc (`npm ci --os=linux --cpu=x64 --libc=glibc`),
  so `@node-rs/argon2` carries `argon2.linux-x64-gnu.node`. `node:sqlite` is built into Node.
- `@mineclaw/contracts` as a real package, with no workspace links
- the Next standalone tree, with `.next/static` and `public/` copied beside `server.js`

The script refuses to ship any link, or any native module that is not a Linux ELF.

**Publish the bridge.** ViewerBridge is published self-contained for linux-x64 as a single file, the way the bot is published
(the CT has no .NET runtime). `shards/defaults` is added for the bridge's packs page.

**Ship and switch:**

1. Two tarballs go to alice:/tmp, then `pct push` into the CT.
2. `mineclaw-web-release install` unpacks both trees and refreshes the units.
3. It enables the three units, swaps both `current` symlinks and prunes to 3 releases.
4. It restarts `mineclaw-bridge`, `mineclaw-web-ui` and `mineclaw-web-api`, and only those.

**Health check.** The deploy passes when:

- all three units are active
- `GET 127.0.0.1:8080/api/health` returns `ok: true` with `bridge: "up"`. That is Nest's signed ping of the bridge's
  `/v1/health`, so it also proves both sides hold the same key.
- `/login` answers on 127.0.0.1:3000 and through :8080 (a redirect to `/setup` counts while no account exists)

Finally, the PC tries `http://10.1.2.73:8080/api/health` itself. Failing that is only a warning: it means the firewall rule
from step 1 is missing. While no account exists it also prints **FIRST-RUN SETUP IS OPEN**: go to step 5 now.

### 5. First account: the setup page (yourself, in your browser)

Open **http://10.1.2.73:8080** and complete the setup page. With no users yet, the explorer is in setup mode: every page
goes to `/setup` and sign-in is off. It is an installation step, so this first visit is expected to be open:

1. **Your account.** Username, password (twice, with a strength hint; at least 12 characters), your Minecraft player
   (`poofimafox`) and roles, preset to admin + chat + commands + viewer.
2. **Authenticator (optional, recommended).** Scan the QR code (drawn in your browser, never sent anywhere) or type the key
   into an authenticator app, then enter the current code. **Skip for now** works too; you can enrol later from the account
   menu.
3. **Done.** You are signed in and land on the live hive.

- **Whoever completes it first becomes the admin.** Do it right after the deploy, before the explorer is reachable by
  anyone else.
- **LAN only.** A request through cloudflared or another proxy is refused (`Web:Setup:AllowProxied` stays false), and so is
  one from a non-LAN address.
- **Once.** As soon as an account exists, `/setup` redirects to `/login` for good and `POST /api/setup` answers 409. Two
  setups at the same moment can't both win: the users file is created atomically under its lock.
- **Audited** as `setup` on the Audit page, refusals included.

**More users, and manual management: the users CLI.** It stays for everything after the first account, and whenever you
prefer the terminal. Call it by its full path (`pct exec`'s PATH has no `/usr/local/sbin`) and from a real terminal:
`-t` gives the password prompt a TTY, or use `ssh -t alice` and then `pct enter 114`. Use a real terminal window, not Claude
Code's `!` prefix. `!` has no TTY, so the password prompt can't hide your typing, and the TOTP secret would land in the
transcript.

```sh
ssh -t alice pct exec 114 -- /usr/local/sbin/mineclaw-web-users add robin --roles chat,viewer --player kiwi
ssh -t alice pct exec 114 -- /usr/local/sbin/mineclaw-web-users totp robin enroll   # or Robin enrols herself after her first login
ssh -t alice pct exec 114 -- /usr/local/sbin/mineclaw-web-users list
```

- **TOTP from the CLI.** `totp <user> enroll` prints the otpauth URL and secret once, then asks for the current code to
  activate it. Don't run this through an agent or paste the output anywhere: the secret is the second factor.
- **Other commands:** `add <user> --roles <r,r> [--player <name>]`, `passwd <user>`, `roles <user> <r,r>`,
  `player <user> <name|->`, `totp <user> disable`, `disable|enable|remove <user>`.
- **Sessions.** `passwd`, `disable` and `remove` end that user's sessions.
- **Starting over.** Removing every user re-opens the setup page (LAN only, as above).

### 6. Verify

1. Run `pwsh deploy/deploy-web.ps1 -Status`. You should see:
   - both releases on the same id
   - all three units `active/running` and under their `MemoryMax`
   - `probe: api 200 ok v… bridge up; ui 200; ui via :8080 200`
2. Open `http://10.1.2.73:8080` in a browser. After the setup page you are signed in; sign out and in again once,
   with the TOTP code if you enrolled one.
3. Check that the bot grid shows live vitals: the bridge reaches the Supervisor.
4. Run a memory search: the bridge reads the db and has the embeddings key from `/etc/mineclaw/mineclaw.env`.
5. Send a chat line to a bot: the viewer routes are enabled.
6. Check the Audit page: the `setup` row and your logins are listed.
7. Run `journalctl -u mineclaw-bridge -n 5` in the CT. The start line names where every setting came from (never values):
   `key: key file`, `supervisor token: config`.

## Routine deploy

```powershell
pwsh deploy/deploy-web.ps1                 # or deploy-alice.ps1 -Web
pwsh deploy/deploy-web.ps1 -SkipTests      # when the tests just ran
pwsh deploy/deploy-web.ps1 -NoStart        # install and switch, but leave the units as they are (restart later)
```

Later: `pct exec 114 -- /usr/local/sbin/mineclaw-web-release restart`.

The bot keeps running throughout. Browsers reconnect their live stream by themselves after the api restarts. Sessions
survive, because they live in `web.db`.

## Rollback

```powershell
pwsh deploy/deploy-web.ps1 -Rollback -DryRun    # which release it would go back to
pwsh deploy/deploy-web.ps1 -Rollback            # or: deploy-alice.ps1 -Web -Rollback
```

- **Both trees together.** The web and the bridge trees go back together to the next older release id, then the stack
  restarts and is health-checked.
- **`web.db` is not rolled back.** Sessions and audit are kept, and an older api opens the db as it is.
- **The bot is not rolled back.** That is `deploy-alice.ps1 -Rollback`, which is separate.
- **Releases kept.** 3 releases are kept, so two rollbacks are possible.

## Logs

```sh
ssh alice pct exec 114 -- journalctl -u mineclaw-web-api -f      # pino JSON: one line per request (no cookies or secrets)
ssh alice pct exec 114 -- journalctl -u mineclaw-web-ui -f
ssh alice pct exec 114 -- journalctl -u mineclaw-bridge -f       # plus /var/log/mineclaw-bridge/audit.jsonl (one line per write)
ssh alice pct exec 114 -- journalctl -u mineclaw-web-api -u mineclaw-web-ui -u mineclaw-bridge --since -1h
ssh alice pct exec 114 -- /usr/local/sbin/mineclaw-web-release status
```

`/api/health` is not logged per request. Bridge up/down changes are logged once each.

## Memory

| | MemoryMax |
|---|---|
| api | 512M (`MemoryHigh` 448M, V8 heap capped at 384 MB) |
| ui | 384M (`MemoryHigh` 320M, V8 heap capped at 256 MB) |
| bridge | 600M total; published .NET managed heap limit 384 MiB, memory conservation level 5 |
| **total** | **about 1.5 GB** |

- **The ceiling.** The hive's `ResourceCeiling` measures the CT. With `Hive:Resources:LimitMb=14336` on the 16 GB CT, it
  would plan clones into memory the web units need.
- **Recommendation (not applied anywhere).** Lower it to about **12800** in the bot config (`"Hive": { "Resources": {
  "LimitMb": 12800 } }`, here and in the local appsettings.json), then restart the bot.
- **Checking.** `mineclaw-viewer-config` prints the current value, and `-Status` shows each unit's current memory against
  its max.

## Bot settings from the web (Administration, added 2026-11-03)

The **Administration** page (`/administration`, `admin` role) changes the bot's own settings: the Anthropic key and base URL,
per-tier providers and models (Claude ids limited to the 5.5 generation the startup check allows), Ollama, vision, budget caps,
chat identity, viewer access, hive limits, the Design Studio's limits and logging. The registry of what it may change is
`src/MineClaw.App/Supervisor/Admin/SettingsRegistry.cs`; the wire is api.md §9.

**Before deploying, confirm the server-side changes below with Fox.** Nothing here is applied by an agent.

### What changes on CT 114

| What | Where | Who creates it | Notes |
|---|---|---|---|
| Override file | `/var/lib/mineclaw/settings.override.json` | the bot, on the first save | 0600 `mineclaw:mineclaw`, plain JSON of config keys |
| Its backups | `/var/lib/mineclaw/settings-backups/settings.override.<utc>.json` | the bot, on every later save | dir 0700, files 0600, the newest 10 kept |
| Settings audit | `/var/lib/mineclaw/settings-audit.jsonl` | the bot | 0600; who, when, which keys (never values) |
| Step-up column | `web.db` `sessions.step_up_at` | `mineclaw-web-api`, on start | web.db schema 1 → 2, automatic, additive |

- **No new units, users, ports, firewall rules or credentials.** `/var/lib/mineclaw` is already the bot's `StateDirectory`
  (owned by `mineclaw`, 0750, writable under `ProtectSystem=strict`), so the unit file is unchanged. The files are written with
  an explicit 0600 regardless of the unit's `UMask=0027`.
- **`Supervisor:AllowAdminWrites` must be on** (it already gates the Admin page's memory edits): `pwsh deploy/deploy-web.ps1
  -BotConfig -Apply -AdminWrites`. With it off the page shows "admin writes are off" and changes nothing. `Supervisor:Token`
  must be set (step 3 above does that).
- **The base file stays untouched.** `/etc/mineclaw/appsettings.json` (0640 root:mineclaw, read-only to the bot) is never
  written. The override is layered on top of it.
- **Layer order** (lowest to highest): `appsettings.json` beside the binary → `/etc/mineclaw/appsettings.json` (`--config`) →
  env, including `/etc/mineclaw/mineclaw.env` → **the override file** → the unit's command-line paths. A value set on the page
  therefore wins over `mineclaw.env`. **Reset** on a field drops it from the override and the env/file value applies again.
- **Applying.** Live settings (the per-tier output budgets, effort and thinking, the author limits, the budget caps, the viewer's
  gates, the Design Studio's limits, the journal level) apply when saved. Everything else applies at the next bot start; the
  page lists those and offers **Restart bot**.
- **Restart bot** stops the bot gracefully (`Restart=always` in `mineclaw.service` starts it again after `RestartSec`, 10 s).
  It is refused while a body is outside at night or at a ledge over a long drop, unless the admin forces it with a reason
  (audited). In-process hives read the same override when they start. Process hives inherit `MINECLAW_SETTINGS_OVERRIDE` from
  main.
- **What the page never edits**, shown read-only: `Supervisor:Enabled`, `:Bind`, `:Port`, `:Token`, `:AllowAdminWrites` (they
  would lock the explorer out or break the bridge, which reads the token from the base file at start), `Agent:Embeddings:*` (the
  bridge reads them too), `Minecraft:Username`, `Hive:Accounts:Prefix` and `Agent:DataDirectory`.
- **Moving or disabling the override.** `Host:SettingsOverride` in the base file, or `MINECLAW_SETTINGS_OVERRIDE` in
  `mineclaw.env`: a path, or `off`.
- **`-PushConfig`** replaces only the base file. The override still applies on top of it.
- **Rollback.** `deploy-alice.ps1 -Rollback` to a build older than this feature ignores the override file: its settings stop
  applying (the file stays and applies again after a roll forward). Delete or rename it by hand to drop every web change at once
  (`pct exec 114 -- mv /var/lib/mineclaw/settings.override.json /var/lib/mineclaw/settings.override.off`, then restart the
  bot).
- **A broken override file** (hand-edited JSON that no longer parses) is ignored at start, so the bot always comes up on the base
  config. The page then refuses to save until the file is fixed or removed.

### Deploying it

1. `pwsh deploy/deploy-web.ps1 -BotConfig` (dry run): check `AllowAdminWrites`. If it is off and Fox agrees,
   `-BotConfig -Apply -AdminWrites` (that restarts nothing unless `-RestartBot`).
2. `pwsh deploy/deploy-alice.ps1`: the bot build that loads the override and serves the routes. It restarts the bot, so pick a
   safe moment (day, no body on a high build).
3. `pwsh deploy/deploy-web.ps1`: the bridge's node proxy and the web/api pieces (web.db migrates on start).
4. Verify: open Administration as an admin. It lists the settings with their sources. Change a live setting (for example
   *Chat lines per minute*), confirm the step-up, save: it reports "applied". `journalctl -u mineclaw -n 20` shows
   `admin settings: <user> (web) changed Supervisor:Viewer:PerMinute` and no value.

### More nodes later

The API is already per node (`/api/admin/nodes/:node/...`; the bridge's `/v1/admin/nodes/{node}/...`). Today the bridge knows
one node, `local` (its `Bridge:SupervisorUrl` and the token from `/etc/mineclaw/appsettings.json`). A second bot would plug in
as:

1. A `Bridge:Nodes` list in `/etc/mineclaw-bridge/bridge.json`: `{id, name, supervisor_url, token_file}` per node, with the
   token as a systemd credential, the way `bridge.key` is handed over.
2. `SettingsProxy` looking the node id up in that list (one `SupervisorClient` each) instead of accepting only `local`;
   `GET /v1/admin/nodes` listing them all with their reachability.
3. A remote supervisor reached over the network rather than loopback needs TLS or a tunnel (WireGuard/Tailscale) and its own
   firewall rule; the bot side is unchanged (token + the forwarded admin identity).

The web page needs no change: its node selector already lists whatever `admin.nodes` returns.

## Later: cloudflared

When the explorer goes public through a Cloudflare tunnel, run cloudflared on CT 114 or on a tunnel host.

**1. Point the ingress at the api.** Nest is the only listener:

   ```yaml
   ingress:
     - hostname: mineclaw.example.com
       service: http://10.1.2.73:8080       # http://127.0.0.1:8080 when cloudflared runs in CT 114 itself
     - service: http_status:404
   ```

**2. Update `/etc/mineclaw-web/config.json`** (or `mineclaw-web.env`, see `deploy/viewer/web/mineclaw-web.env.example`):

| Key | Value |
|---|---|
| `Web:AllowedOrigins` | add `https://mineclaw.example.com`; keep `http://10.1.2.73:8080` while the LAN way stays |
| `Web:TrustProxy` | `true` |
| `Web:TrustedProxies` | where cloudflared connects from: `["127.0.0.1", "::1"]` when it runs in the CT, else the tunnel host's IP. Only those may set `CF-Connecting-IP` / `X-Forwarded-*`. |
| `Web:CloudflareAccess:{TeamDomain,Aud}` | optional, a second gate before the login page: the Access application's team domain and AUD tag |
| `_TunnelOriginTodo` | delete it |

With `CookieSecure: "auto"`, the HTTPS origin gets `__Host-mcx` + Secure + HSTS automatically. LAN HTTP keeps `mcx`.

**3. Restart the api:** `pct exec 114 -- systemctl restart mineclaw-web-api`.

**4. Close the LAN way.** If cloudflared runs elsewhere, the 8080 rule's source becomes the tunnel host. Once the tunnel is
the only way in, narrow or drop the LAN rule from step 1.

## Local dry run (no CT)

```powershell
pwsh deploy/deploy-web.ps1 -BuildOnly -Smoke [-SkipTests]
```

1. **Build.** It builds and assembles exactly what would ship (`deploy/out/web/mineclaw-web`, `mineclaw-bridge`, both
   tarballs).
2. **Run.** It copies the web tree to a temp dir and adds this PC's argon2 binding, the only change. Then it starts the Next
   standalone `server.js` and Nest with `--mock-bridge` (a real signed loopback bridge serving fixtures) on free loopback
   ports, wired like the CT: Nest proxies non-`/api` to Next.
3. **Check.** It checks:
   - `/api/health` (ok, bridge up)
   - `/login` redirecting to `/setup` (the smoke run has no users), and `/setup` through Nest (with the CSP nonce)
   - a `/_next/static` asset (the copied static dir)
   - the viewer worker under `/assets` (the copied `public/`)
   - `/login` on Next directly
4. **Stop.** It stops both processes.
