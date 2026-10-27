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

It is GET only. It never moves a bot, chats, or writes memory.

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
