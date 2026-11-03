# MineClaw viewer bridge on CT 114

The bridge (`tools/ViewerBridge`) is the web explorer's C# side: loopback only (`127.0.0.1:8766`), every request HMAC-signed by
Nest (docs/web/api.md §3). It opens the bot's db **read-only**, runs search / RAPTOR / shards / lint / diffs / the texture atlas,
and proxies the bot's Supervisor (`127.0.0.1:8765`) with the token. It never writes the db: edits go through the Supervisor's
admin routes.

## Install

```sh
# 1. user: its own account, in the bot's group (reads /etc/mineclaw/appsettings.json and the db)
useradd --system --no-create-home --shell /usr/sbin/nologin --gid mineclaw mineclaw-bridge

# 2. binaries (on Fox's PC: dotnet publish tools/ViewerBridge -c Release -r linux-x64 --self-contained -o out/bridge)
install -d -o root -g root -m 0755 /opt/mineclaw-bridge/releases/<id>
cp -r out/bridge/* /opt/mineclaw-bridge/releases/<id>/
ln -sfn /opt/mineclaw-bridge/releases/<id> /opt/mineclaw-bridge/current

# 3. the HMAC key (the same value Nest has as Bridge:Key; ≥ 32 chars). Never commit or print it.
install -d -o root -g mineclaw -m 0750 /etc/mineclaw-bridge
umask 027; openssl rand -hex 32 | sed 's/.*/{"Bridge":{"Key":"&"}}/' > /etc/mineclaw-bridge/bridge.json
chown root:mineclaw /etc/mineclaw-bridge/bridge.json; chmod 0640 /etc/mineclaw-bridge/bridge.json
#    (or MINECLAW_BRIDGE_KEY=... in /etc/mineclaw-bridge/bridge.env, 0600 root: systemd reads it before dropping privileges)

# 4. the bot must have the viewer routes and a token: in /etc/mineclaw/appsettings.json
#      "Supervisor": { "Enabled": true, "Bind": "127.0.0.1" (or the CT IP), "Token": "<secret>", "AllowAdminWrites": true,
#                      "Viewer": { "Enabled": true, "AllowCommands": false } }

# 5. unit
cp deploy/viewer/bridge/mineclaw-bridge.service /etc/systemd/system/
systemctl daemon-reload && systemctl enable --now mineclaw-bridge
journalctl -u mineclaw-bridge -f
```

The start line names where every setting came from (never a value): `config: bot config /etc/mineclaw/appsettings.json, key file
/etc/mineclaw-bridge/bridge.json; key: key file; supervisor token: config; embeddings: configured (deployment …)`. A missing or
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
| `Bridge:SupervisorConnections` | `4` | pooled connections to the Supervisor (its `MaxConnections` is 8) |
| `Bridge:PacksDir` | `/opt/mineclaw/current/shards/defaults` | default knowledge packs |
| `Bridge:AuditLog` | `/var/log/mineclaw-bridge/audit.jsonl` | one line per write (no bodies) |
| `Bridge:ServerVersion` | `server-version.txt` beside the db, else `b1.8.1` | block names, atlas default |
| `Bridge:AssetsDir` | `<DataDir>/assets` | texture cache for the atlas (terrain.png / client jars) |
| `Bridge:ModernJar` | `Render:Assets:Jar` | the 1.12.2 client jar for block models |
| `Bridge:AllowAssetDownload` | `false` | fetch the official client jar when none is on disk |
| `Bridge:HeatHalfLifeS` | `120` | memory heat decay half-life |
| `Bridge:ReloadCheckSeconds` | `60` | how often the in-memory vectors / RAPTOR tree are checked against the db |

Any key can also come as env `MINECLAW_Section__Key` (the bot's `/etc/mineclaw/mineclaw.env` is read by the unit, which is how
the embeddings key reaches search).

## Notes

- **WAL.** The db is opened `Mode=ReadOnly` + `query_only`. SQLite reads the bot's `-wal` / `-shm`; with the bot running (it
  holds the `-shm`), group read on those files is enough.
- **Memory.** The bridge keeps the vectors and the RAPTOR tree in memory (like the explorer) and re-opens them when the bot
  changed them; `MemoryMax=600M` in the unit.
- **Atlas.** No Mojang assets are committed. The atlas is built from what is on CT 114 (`AssetsDir`, the 1.12.2 jar, launcher
  folders); with nothing there it carries only the procedural cells and flat colours. Copy the jars into `AssetsDir` or set
  `Bridge:AllowAssetDownload=true` once.
- **Dev on Fox's PC.** `dotnet run --project tools/ViewerBridge` with `tools/ViewerBridge/appsettings.Development.json` (gitignored;
  start from `appsettings.Development.example.json`) pointing at a local db and supervisor.
