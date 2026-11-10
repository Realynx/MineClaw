# MineClaw viewer bridge on CT 114

The bridge (`tools/ViewerBridge`) is the web explorer's C# side: loopback only (`127.0.0.1:8766`), every request HMAC-signed by
Nest (docs/web/api.md §3). It opens the bot's db **read-only**, runs search / RAPTOR / shards / lint / diffs,
and proxies the bot's Supervisor (`127.0.0.1:8765`) with the token. It never writes the db: edits go through the Supervisor's
admin routes.

## Install

Scripted: `pwsh deploy/deploy-web.ps1 -Install` once (runs `deploy/viewer/install-ct114.sh` in the CT), then
`pwsh deploy/deploy-web.ps1` for every release. Runbook: `docs/web/deploy.md`. The manual equivalent:

```sh
# 1. user: its own account, in the bot's group (reads /etc/mineclaw/appsettings.json and the db)
useradd --system --no-create-home --shell /usr/sbin/nologin --gid mineclaw mineclaw-bridge

# 2. binaries (on Fox's PC: dotnet publish tools/ViewerBridge -c Release -r linux-x64 --self-contained -p:PublishSingleFile=true
#    -o out/bridge, plus shards/defaults copied to out/bridge/shards/defaults: the unit points Bridge:PacksDir there)
install -d -o root -g root -m 0755 /opt/mineclaw-bridge/releases/<id>
cp -r out/bridge/* /opt/mineclaw-bridge/releases/<id>/
ln -sfn /opt/mineclaw-bridge/releases/<id> /opt/mineclaw-bridge/current

# 3. the HMAC key (the same value Nest has in /etc/mineclaw-web/bridge.key; ≥ 32 chars). Never commit or print it.
#    Root-only on disk; the unit hands it to the service as a systemd credential (LoadCredential + MINECLAW_Bridge__KeyFile).
install -d -o root -g root -m 0700 /etc/mineclaw-bridge
umask 077; printf '{"Bridge":{"Key":"%s"}}\n' "$(cat /etc/mineclaw-web/bridge.key)" > /etc/mineclaw-bridge/bridge.json

# 4. the bot must have the viewer routes and a token (deploy-web.ps1 -BotConfig [-Apply] does this, dry run first):
#      "Supervisor": { "Enabled": true, "Bind": "*" or "127.0.0.1", "Token": "<secret>", "AllowAdminWrites": true,
#                      "Viewer": { "Enabled": true, "AllowCommands": false } }
#    With Bind = the CT IP the Supervisor doesn't answer on loopback: then set MINECLAW_Bridge__SupervisorUrl in bridge.env.

# 5. unit
cp deploy/viewer/bridge/mineclaw-bridge.service /etc/systemd/system/
systemctl daemon-reload && systemctl enable --now mineclaw-bridge
journalctl -u mineclaw-bridge -f
```

The start line names where every setting came from (never a value): `config: bot config /etc/mineclaw/appsettings.json, key file
/run/credentials/mineclaw-bridge.service/bridge.json; key: key file; supervisor token: config; embeddings: configured (deployment …)`. A missing or
short key exits with code 2 (the unit does not restart it).

## Configuration keys

| Key | Default | Meaning |
|---|---|---|
| `Bridge:BotConfig` | `/etc/mineclaw/appsettings.json` | the bot's config: `Supervisor:Token`, `Supervisor:Port`, `Agent:*` (Embeddings, Llm, Rag) |
| `Bridge:KeyFile` | `/etc/mineclaw-bridge/bridge.json` | JSON holding `Bridge:Key` |
| `Bridge:Key` / env `MINECLAW_BRIDGE_KEY` | – | HMAC secret shared with Nest (secret, ≥ 32 chars) |
| env `MINECLAW_SUPERVISOR_TOKEN` | `Supervisor:Token` | overrides the token |
| `Bridge:Bind`, `Bridge:Port` | `127.0.0.1`, `8766` | listen address (non-loopback sources are refused regardless) |
| `Bridge:DataDir` | `/var/lib/mineclaw` | the bot's data dir (it runs with `--data-dir`, which the config file doesn't carry) |
| `Bridge:DbPath` | `<DataDir>/<Agent:Rag:DbFile>` | the agent db |
| `Bridge:SupervisorUrl` | `http://127.0.0.1:{Supervisor:Port}` | |
| `Bridge:SupervisorConnections` | `6` | total pooled connections to the Supervisor (its `MaxConnections` is 8; 1 to 7 are taken). The default six: two for entity/block updates and status so chunk loads cannot stall motion, two for what a click waits on (story commands, one chest's contents, a body's inventory, recipes), two for everything else. Seven also gives the story reads that wait on the colony (the list, a colony's state and Architect) one of their own. Five or fewer have no interactive lane: a pinned `MINECLAW_Bridge__SupervisorConnections=4` keeps the old split (two reserved, two ordinary). A limit of one remains shared |
| `Bridge:PacksDir` | `/opt/mineclaw/current/shards/defaults` | default knowledge packs |
| `Bridge:AuditLog` | `/var/log/mineclaw-bridge/audit.jsonl` | one line per write (no bodies) |
| `Bridge:ServerVersion` | `server-version.txt` beside the db, else `b1.8.1` | block names |
| `Bridge:HeatHalfLifeS` | `120` | memory heat decay half-life |
| `Bridge:ReloadCheckSeconds` | `60` | how often the in-memory vectors / RAPTOR tree are checked against the db |

Any key can also come as env `MINECLAW_Section__Key` (the bot's `/etc/mineclaw/mineclaw.env` is read by the unit, which is how
the embeddings key reaches search).

## Notes

- **WAL.** The db is opened `Mode=ReadOnly` + `query_only`. SQLite reads the bot's `-wal` / `-shm`; with the bot running (it
  holds the `-shm`), group read on those files is enough.
- **Memory.** The bridge keeps the vectors and the RAPTOR tree in memory (like the explorer) and re-opens them when the bot
  changed them; `MemoryMax=600M` in the unit. The published runtime config limits the managed heap to 384 MiB and enables
  memory conservation (level 5), leaving room for native allocations and HTTP buffers. The SSE replay ring also has an
  8 MiB text limit; a reader more than 64 frames behind reconnects instead of silently losing deltas or accumulating old movement.
- **Textures.** The browser world viewer ships its own textures (`@mineclaw/world-viewer`); the bridge serves no atlas.
- **Dev on Fox's PC.** `dotnet run --project tools/ViewerBridge` with `tools/ViewerBridge/appsettings.Development.json` (gitignored;
  start from `appsettings.Development.example.json`) pointing at a local db and supervisor.
