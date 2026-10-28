# Hive Supervisor (read-only viewer)

A graphical take on the TUI. The main bot serves it, so it takes no game login and uses no clone slot.

- One live tile per bot: main plus every online clone. Each tile shows the bot's name, job, hp and food, and its claims.
  Pick a view per tile: eye, top+roles, iso, side, layers, or the full contact sheet.
- Layouts: grid 1/2/4/6, focus+strip (double-click a thumbnail to focus it), or tabs. Drag a tile to reorder.
  The layout is saved in your browser.
- Free camera: click the view, then fly with WASD, Q/E for down/up, and drag to look. It has a speed slider, a jump box
  (x,y,z, a landmark like "mob chamber", or a bot), snap to a bot, and a dimension picker. While you move it renders
  cheap 240x180 frames at about 4 fps, then one 640x480 frame once you stop. Frames come from the shared world memory,
  and each one says how stale it is.
- "What the brain receives": the exact sheet PNG a `see` gives the selected bot, its token estimate and byte size,
  the legend text, and the facts the VisionRouter answers without any model.
- Panels from the TUI: tokens and cost (today/month vs caps, per tier, cache %), the selected brain's thought and tool
  calls, the hive job/claim board, the danger/event feed, and the chat log.

It is GET only. It never moves a bot or chats, and it writes memory only through the opt-in admin routes below.

## Server side (the main bot)

In `src/MineClaw.App/appsettings.json` on the bot:

```json
"Supervisor": { "Enabled": true, "Bind": "10.1.2.73", "Port": 8765, "Token": "<optional shared secret>" }
```

`Bind` is the CT's LAN address. Use `*` for every interface. The token is never logged. If it is set, the API needs the
`X-Supervisor-Token` header or `?token=`; the launcher passes it once and the page keeps it for the tab.

Renders run one at a time on a single below-normal background thread, at most about 8 per second, with a bounded
queue. When the queue is full the server answers 503 and the page retries. Each bot view is cached for about 1 s,
and a clone is asked for its view over IPC at most every 2.5 s. The bot's tick and brain never wait on the viewer.

## Open it (your PC)

Tunnel through alice, which can reach CT 114 at 10.1.2.73. No firewall changes are needed.

```powershell
./tools/HiveSupervisor/hive-supervisor.ps1                    # ssh -L 8765:10.1.2.73:8765 alice, then the browser
$env:MINECLAW_SUPERVISOR_TOKEN = "..."; ./tools/HiveSupervisor/hive-supervisor.ps1
```

```sh
tools/HiveSupervisor/hive-supervisor.sh                       # same on Linux/macOS
```

You can also run the tunnel by hand: `ssh -L 8765:10.1.2.73:8765 alice`, then open http://localhost:8765/.

## Textures

The bot renders on the server with the same b1.8.1 textures its `see` uses. They are read at runtime from a client jar
on that machine (`Bot:Vision:ClientJar`, `MINECLAW_CLIENT_JAR`, or the PrismLauncher / `.minecraft` folders), or from
the PNGs extracted into `data/assets/`, or from Mojang's official jar (SHA-1 verified) when `Bot:Vision:AllowDownload` is
set. Textures are never committed; `data/assets/` and `*.jar` are gitignored. Without them the views use flat colours.

## Endpoints

| Route | What |
|---|---|
| `GET /` | the UI |
| `GET /api/bots` | tiles: name, short, main, dim, x/y/z, hp, food, job, brain, age_seconds, claims |
| `GET /view/{bot}?mode=sheet\|brain\|eye\|top\|iso\|side\|layers&radius=3..12` | PNG; `&meta=1` returns the legend, tokens, bytes and router facts of the same render |
| `GET /view/hive` | the hive overview |
| `GET /camera?x&y&z&yaw&pitch&dim&w&h` | a free-camera frame; the `X-Seen` header gives staleness |
| `GET /api/status?bot=` | the TUI panels (full thought for `bot`) |
| `GET /api/events?since=<id>` | the event feed (poll) |
| `GET /api/locate?q=` | a camera jump target |
| `GET /api/world/index?dim&x&z&r` | Live Hive: the known columns around block (x, z), r 16..256: `{dim, at, columns: [{cx, cz, hash, seen, by, live}]}` (no blocks) |
| `GET /api/world/chunks?dim&cols=cx,cz;cx,cz` | Live Hive: ≤ 64 columns as one binary payload (`application/octet-stream`, see below) |
| `GET /api/world/entities?dim` | Live Hive: `{at, bots: [{name, short, main, dim, x, y, z, yaw, pitch, pose_age, hp, food, job, brain, held, claims, live, age_seconds}], entities: [{kind, name, dim, x, y, z}]}` |
| `GET /api/world/overlays?dim` | Live Hive: `{at, dim, claims, places, points, plans, dangers, notes}`; marks are `{kind, text, min: [x,y,z], max: [x,y,z], owner}`, plans `{id, name, status, part, blocks: [[x, y, z, id, meta, done]]}` |

### Live Hive world routes

The MemoryExplorer's Live Hive tab renders the hive itself from these (textured eye tiles and a free camera). The columns are
the merged world memory: the live columns of main and every in-process body, then every bot's remembered `world_chunks`.
A client keeps a hash cache and fetches only new or changed columns. The routes are GET only and use the same token. Each one
has a token bucket, and over the cap it answers 429 with `Retry-After`: index 8/s, chunks 25/s, entities 12/s, overlays 1/s
(bursts are twice that). At most two reads run at once, on the thread pool, never on the game tick. A live column is hashed
straight off its arrays and copied only when it is fetched. A remembered column ships its stored blob as it is. An
out-of-process clone's yaw and pitch come from the existing view IPC, refreshed in the background at most every
`PoseCacheMs`.

The `chunks` payload is little-endian: `"MCWC"`, u16 version (1), u16 flags (0), i32 dim, i32 count. Then, for each column:
i32 cx, i32 cz, i64 hash, i64 seen_unix_ms, u8 live, u8 by_len, the `by` name in UTF-8, i32 data_len, and the data. The data
is `ChunkSnapshot.Encode()`, and an empty one means the column is not known. `MineClaw.World.ColumnPayload` encodes and
decodes it.

## Admin writes (opt-in)

`"AllowAdminWrites": true` in the `Supervisor` section turns on `/api/admin/*`, the only routes that accept POST. They
are used by the MemoryExplorer admin tool. It is off by default. Every admin route answers 403 unless the flag is on,
a `Token` is configured, and the request sends it in the `X-Supervisor-Token` header. `?token=` is not accepted here.
Edits go through the running bot's own stores, so FTS, vectors and the RAPTOR tree stay consistent. Rebuilds run in the
bot under its budget caps on the cheap tier. Every write, whether it succeeds or fails, is appended to
`<data dir>/admin-audit.jsonl` with the record before and after, and to the app log. JSON is snake_case.

| Route | What |
|---|---|
| `POST /api/admin/memory` | body `{op: add\|update\|delete, kind: fact\|place\|structure\|claim\|trait\|lesson\|observation, id, fields: {..}, who, reason}`; returns `{ok, message, id, before, after, reembedded}` (200 ok, 400 rejected) |
| `POST /api/admin/rebuild/estimate` | body `{scope: node\|kind, node, kind, max_usd (0.50), who}`; returns `{summaries, input_tokens, output_tokens, embedding_tokens, usd, model, note}` |
| `POST /api/admin/rebuild` | same body; returns 202 `{job_id}`. Refused when the estimate exceeds `max_usd` or the budget is Paused. 409 while another job runs |
| `GET /api/admin/rebuild/{job_id}` | `{job_id, state: queued\|running\|done\|failed\|cancelled, started, finished, request, progress: {stage, done, total, message}, result: {ok, message, diffs: [{index, layer, old, new}], spent_usd, elapsed}}` |
| `POST /api/admin/rebuild/{job_id}/cancel` | cancels a running job |
| `POST /api/admin/backup` | a consistent copy (`VACUUM INTO`) in `<data dir>/backups/`; returns `{path}` |
| `GET /api/admin/audit?max=200` | `{target, entries: [...]}` |
| `GET /api/admin/schema` | the edit-form fields for each kind: `{target, kinds: {fact: {ops, fields: [{name, label, type, required, options, help}]}}}` |
