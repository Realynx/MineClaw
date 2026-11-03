# MineClaw Web Explorer: architecture and contract

This is the web successor to `tools/MemoryExplorer`, the Avalonia app. It keeps every feature and adds a better layout,
calm live viewers, and chat to a bot as the signed-in player. The old app stays in the tree until the web app reaches parity.

Reference specs (read these; they hold the details this file only names):
- Explorer feature and data inventory: `docs/web/reference/explorer-spec.md`
- Bot HTTP surface, live state and chat path: `docs/web/reference/bot-api-spec.md`

## Topology

Everything runs on CT 114 (claudechan, 10.1.2.73), beside the bot. This was decided 2026-11-02 and replaces the earlier
plan for a separate CT.

```
 browser ──HTTP on the LAN (now) / HTTPS via cloudflared (later)──▶ CT 114 (10.1.2.73)
   ├─ apps/api   NestJS 0.0.0.0:8080   the only new port on the network
   │    ├─ /api/*            REST + SSE (/api/live/stream)
   │    └─ everything else ▶ Next standalone on 127.0.0.1:3000
   ├─ apps/web   Next.js standalone, 127.0.0.1:3000
   ├─ tools/ViewerBridge   ASP.NET Core 127.0.0.1:8766   HMAC-signed requests from Nest only
   │    ├─ reads the agent db READ-ONLY (live, no snapshots)
   │    ├─ runs the C#-only logic (search, RAPTOR, shards, lint, diff, renders)
   │    └─ proxies the Supervisor on 127.0.0.1:8765 (adds the token)
   └─ mineclaw (bot)   Supervisor HttpListener :8765 (existing)
```

Each of the three new processes has its own systemd unit:
- `mineclaw-web-api` and `mineclaw-web-ui`, running as the `mineclaw-web` user.
- `mineclaw-bridge`, running as the `mineclaw-bridge` user in group `mineclaw`, so it can read `/etc/mineclaw/appsettings.json`
  and open the db read-only.

Memory use:
- The web stack is budgeted at about 1 GB with `MemoryMax` on the units.
- The hive's `ResourceCeiling` (14336 MB) has to leave room for it.

Why a C# bridge: eight of the ten Explorer pages read the bot's SQLite db, and hybrid search, the RAPTOR tree, shard
export and import, script lint, design diff and the voxel and vision renderers exist only in C#
(explorer-spec §15). The bridge reuses those libraries rather than reimplementing them in Node. It also gives one
encrypted, signed channel for every piece of debug data that leaves CT 114: the Supervisor itself is plain HTTP with a
static token.

Dev on Fox's PC: `npm run dev` in `web/` starts Next (Turbopack) on :3000, which rewrites `/api/*` to Nest on :4000. Nest
points at a bridge URL: a local `dotnet run --project tools/ViewerBridge` with a local db and supervisor, or the real one.

## Repo layout

```
web/                         npm workspaces + Turborepo (turbo.json); Node 22
  apps/web/                  Next.js (App Router, Turbopack, React, TS, Tailwind v4)
  apps/api/                  NestJS (TS): auth, sessions, CSRF, RBAC, audit, bridge client, SSE fan-out, prod reverse proxy
  packages/contracts/        Shared TS types + zod schemas for every /api route and SSE event (single source of truth)
  packages/world-viewer/     three.js voxel world renderer (React component), consumes MCWC chunks / entities / overlays
tools/ViewerBridge/          C# ASP.NET Core minimal API (net10.0), references src/ libs
tests/MineClaw.ViewerBridge.Tests/
deploy/viewer/               systemd units, cloudflared notes, and the web + bridge steps in deploy-alice.ps1 (CT 114)
```

## Security model (design as if public)

- **Users.** A users file on CT 114 (`/etc/mineclaw-web/users.json`, 0600) holds username, argon2id hash,
  optional TOTP secret, roles and the linked Minecraft player. Fox maps to `poofimafox` and Robin to `kiwi`. A
  `web/apps/api` CLI adds a user, resets a password and enrolls TOTP. There is no self-signup.
- **First-run setup (added 2026-11-03).** While the users file has no users the app is an installation step: every page
  redirects to `/setup`, login is off, and the page creates the first account (preset admin + chat + commands + story + viewer),
  offers an optional TOTP enrolment, and signs it in. The first visit is expected to be open; whoever completes it first
  becomes the admin. It is fenced: direct LAN/loopback clients only (never through a trusted proxy or with Cloudflare
  headers; `Web:Setup:AllowProxied` defaults to false), CSRF + Origin, rate limited, audited (`setup`), and an atomic
  create-if-empty under the users file's lock, so two simultaneous setups can't both win. Once any user exists, `/setup`
  redirects to `/login` and `POST /api/setup` answers 409, checked on every request. The CLI stays for manual management.
- **Roles.**
  - `viewer`: read only.
  - `chat`: can talk to bots as their linked player.
  - `commands`: their viewer lines may arm CommandGate request grants.
  - `admin`: memory edits, rebuilds, backups, shard import.
  - `story` (added 2026-11-03): plays Story Mode (colony writes); `admin` holds it too. See `docs/web/story-mode.md`.
- **Sessions.**
  - Opaque random id in a `__Host-mcx` cookie (HttpOnly, Secure when served over HTTPS, SameSite=Lax), held in a server
    store (SQLite at `/var/lib/mineclaw-web/web.db` on CT 114).
  - Idle timeout is 12 h and absolute timeout 7 d. The id rotates on login.
- **CSRF.** Every non-GET needs the `X-CSRF-Token` header to match the session's token, and `Origin` must be in
  `Web:AllowedOrigins`.
- **Login.** Rate limited per IP and per user, with exponential lockout. Errors are generic.
- **Headers.** HSTS when HTTPS, a strict CSP (self plus inline styles only where Tailwind needs them), `X-Frame-Options:
  DENY`, `Referrer-Policy: same-origin`.
- **Cloudflare.**
  - `Web:TrustProxy` makes Nest honor `CF-Connecting-IP` / `X-Forwarded-*`, but only from the configured cloudflared
    source (`Web:TrustedProxies`).
  - Optional `Web:CloudflareAccess:{TeamDomain,Aud}` verifies `Cf-Access-Jwt-Assertion` as a second gate before login.
- **Audit.** Every write, every chat line and every login is recorded in `web.db` with user, IP and time, and shown on the
  Audit page.
- **Nest ↔ bridge.**
  - Loopback only (127.0.0.1:8766), so no TLS is needed.
  - Each request is HMAC-SHA256 signed over `method\npath?query\ntimestamp\nnonce\nsha256(body)` with `Bridge:Key`.
    Headers: `X-MCX-Ts`, `X-MCX-Nonce`, `X-MCX-Sig`.
  - The bridge rejects skew over 30 s and replayed nonces (5 min LRU).
  - Source IP must be loopback.
  - Nest also sends `X-MCX-User` (username) and `X-MCX-Player` (linked player) and `X-MCX-Roles`. They are covered by the
    signature (included in the signed string after the body hash, newline-separated), and the bridge uses them for
    audit and chat identity.
- **Bridge ↔ supervisor.** Loopback only. The bridge reads `Supervisor:Token` from the bot's own config
  (`/etc/mineclaw/appsettings.json`; the bridge runs as the `mineclaw` group). Admin passthrough requires the `admin` role in
  the signed headers, and the bridge sets `who` from `X-MCX-User`, never from the body.
- **Secrets.** Never commit or print secrets. Config values come from files or env on the CTs. Dev uses a gitignored
  `.env.local` / `appsettings.Development.json`.

## Bot-side changes (keep them small; someone else is working on optimisation code)

These go in new files under `src/MineClaw.App/Supervisor/` plus minimal hooks. Don't touch CommandGate internals beyond
the one observe hook, and don't touch BotAgent.Quiet, Prompts, EventQueue/GameEvent internals, or budget/optimisation code.

1. **`POST /api/viewer/chat`.** Body `{bot, text, player, allow_commands, request_id}`. It is accepted only with the
   supervisor token and only when `Supervisor:Viewer:Enabled` is true.
   - `player` must be in `TrustedPlayers`.
   - It enters via a new `ChatRouter.OnRemoteChat(player, text, origin:"viewer", targetBot)`. It builds the `ChatMessage`
     directly and never fabricates a `<name>` raw line. It is explicitly addressed to the target bot.
   - It skips `_online.Add`, uses its own rate bucket, and logs to chat_log as kind `viewer_chat`.
   - In hives, the line is broadcast like `/as`, with an origin field and without IsTest.
   - Command grants are armed only when `allow_commands` and `Supervisor:Viewer:AllowCommands` are both true. The audit
     then reads `poofimafox (viewer)`.
   - Replies still go to in-game chat as today. The viewer sees them through the feed below.
   - Returns `{accepted, id}`.
2. **`GET /api/viewer/feed?since=<seq>`.** A ring of structured lines (1000 entries):
   - `{seq, at, bot, kind: chat|said|whisper|internal|muted|system|viewer, from, text, origin}`.
   - Built from the router's chat events plus `ChatRouter.Said`, `BotAgent.InternalSpeech` and `Muted`.
   - The response is `{lines, last}`.
3. **`GET /api/viewer/now?bot=`.** One compact "what is it doing right now" snapshot:
   - `{bot, at, brain_state, in_flight{purpose, model, since}, thought{text, thinking, at}, tool{name, args_short, at},
     task, script{name, version, status, line, paused}, body{hp, food, pos, dim, held}}`.
   - Assembled from what `/api/status`, the context page and `/api/bodies/scripts` already know.

4. **Every body in the bot lists (added 2026-11-03).** A hive clone's telemetry snapshot carries its subhive bodies
   (`BotSnapshot.Bodies`: name, position, vitals, job, script), and `Supervisor/HiveBodyList` merges main, the hive slots,
   main's own bodies, those streamed bodies and still-spawning tree nodes for `/api/bots`, `/api/status`,
   `/api/world/entities`, `/api/viewer/now` and `/api/bodies/scripts`, with `kind`, `owner` and `script` (api.md §6). The
   brain's own hive overview (`HiveBots()`) is unchanged.

Tests for these go in `tests/MineClaw.App.Tests` (or the closest existing test project).

## Bridge API (`/v1`, JSON snake_case, all signed)

- **Pass-through, allow-listed, GET only.** `/v1/sup/<path>` forwards to the supervisor `/api/<path>`. Allowed:
  bots, hive/tree, status, events, locate, blueprints, buildplans, world/*, scripts, bodies/*, brains/*, viewer/feed,
  viewer/now. Binary responses are streamed.
- **Supervisor writes.**
  - `POST /v1/sup/viewer/chat`: the bridge injects `player` from `X-MCX-Player`, and sets `allow_commands` only if
    `X-MCX-Roles` contains `commands`.
  - `POST /v1/sup/admin/*`: requires the `admin` role.
- **Db reads (the bridge's own code, ported from `tools/MemoryExplorer/Services`).**
  - `/v1/overview`
  - `/v1/memory/kinds`
  - `/v1/memory/{kind}?q&sort&offset&limit`: server-side paging; never whole tables.
  - `/v1/memory/{kind}/{id}`
  - `/v1/search?q&mode&k`
  - `/v1/raptor/tree?root&depth`, `/v1/raptor/node/{id}`, `/v1/raptor/graph?layout&color_by`: graph layout is computed
    server-side and returned as nodes with x, y and edges.
  - `/v1/personality`
  - `/v1/scripts`, `/v1/scripts/{name}`, `/v1/scripts/{name}/diff?a&b`, `/v1/scripts/{name}/lint`
  - `/v1/designs`, `/v1/designs/{id}`, `/v1/designs/{id}/diff?a&b`
  - `/v1/shards/packs`, `/v1/shards/export` (POST returns the `.mcshard` download), `/v1/shards/import/preview` (POST,
    multipart)
  - `/v1/admin/schema`
- **Renders.**
  - `/v1/render/tile/{bot}.png`: the per-bot tile.
  - The world viewer's textures ship with `@mineclaw/world-viewer` (committed assets, atlas built client-side), so the
    bridge serves no atlas.
- **Live.** `GET /v1/live/stream` is SSE. The bridge polls the supervisor on loopback (status 1 Hz, events 2 Hz, feed
  2 Hz, `viewer/now` 2 Hz per watched bot, entity deltas 5 Hz) and emits typed events: `bots`, `event`, `feed`, `now`,
  `entities`, `hive`. Nest holds one upstream stream and fans it out to browsers.

Nest's public `/api` mirrors these one-to-one under friendlier paths, typed by `packages/contracts`. Examples:
`/api/memory/:kind`, `/api/live/stream`, `/api/bots/:bot/chat`, `/api/world/chunks`. Nest adds auth, RBAC, CSRF, audit
and caching. It never reshapes domain data beyond camelCase conversion.

## UX principles for the web app

- **Live page.**
  - The default view of a bot is calm and shows only:
    - the current thought (latest text plus a collapsed thinking summary)
    - the in-flight call with an elapsed timer
    - the current tool
    - the current script with the running line highlighted
    - the body vitals
    - a chat pane
  - History is one click away (a turn timeline drawer, the full context on demand) and is never on screen by default.
- **Chat pane.**
  - It is the conversation with that bot: viewer, in-game and bot lines threaded together. Internal or muted speech is
    shown subtly behind a toggle.
  - The composer sends as the linked player, and a pending state resolves when the bot's reply arrives in the feed.
- **Layout.**
  - Hive-wide is a grid of bot cards (vitals, task, one-line thought) that opens the bot view. Every body has a card,
    grouped by the hive that drives it (main and main's own bodies, then each hive and its subhive bodies) in collapsible
    groups with counts; a folded group unmounts its cards and thumbnails.
  - The 3D world is a tab or split pane, not the default.
  - **In the world (added 2026-11-03).** The world stage goes fullscreen (button or `F`) with the one shared canvas. Free
    cam with the mouse locked shows the vanilla crosshair: it outlines and names the bot or block at the screen centre; a
    click selects bots (Shift adds, Ctrl clears) or sets a target block, `1`–`9` pick the nearest bots, `Tab` toggles the
    HUD, and `T`/`Enter` opens a chat bar (directive chips for the target block) that sends ordinary chat as the user's
    linked player to every selected bot, with their replies shown over the world. Esc: on HTTPS (Keyboard Lock) the first
    frees the mouse and the second leaves fullscreen; over plain HTTP the browser does both at once. World shortcuts never
    fire while a text field has focus.
  - **Chat bubbles (added 2026-11-03).** What players and bots say also shows as a speech bubble over the speaker's head
    in every world view: the pane in each camera, fullscreen, the hive-card thumbnails (one small bubble) and any page that
    shows the world under `WorldHost`. Lines come from the shared stream's `feed` frames only
    (`features/world/chatBubbles.ts`, one subscription per world runtime through `WorldSource.chat`): `chat`, `said` and
    `viewer` lines, and whispers only when the signed-in user's player sent them. Private speech, system lines and
    commands never bubble. The text chat stays as it was. A "Chat bubbles" layer toggle turns them off in the pane.
- **Connections.** Over plain HTTP/1.1 the browser allows six connections per host for every tab together, and each tab's
  live stream holds one for good. A hidden tab therefore closes its stream after 15 s and pauses the world's network loops
  (reopened and re-synced when shown). `npm run test:e2e` runs a `prod` project against the standalone build behind Nest
  (`apps/web/scripts/prod-stack.mjs`, mock bridge at production stream rates; `MOCK_REPLAY_DIR` replays a capture of a real
  bot) next to the mock-mode specs.
- **Tables.** Every table is server-paged and virtualised.
- **Status.** One connection-status indicator in the header, not one per poller.
- **Dev stats.** Hidden unless the user turns on "developer details".
- **Theme.** Dark and light, keyboard navigable, a command palette (Ctrl+K) to jump to bots, scripts, memories and designs.

## Memory activity tab (added 2026-11-02)

This tab shows the RAPTOR memory map (the server-laid-out graph) with regions lit by what each hive brain is using.
- **Brightness** is how recently a node was accessed. It is an exponential decay over time since the last access, and
  heat propagates up to its ancestors, so a region glows.
- **Staleness** is a ring around the node: how long the memory has sat in that brain's live context without a refresh.
  An evicted memory is shown hollow.
- **Filtering.** Per-brain colour and filter: all brains, or one hive or body.
- **Timeline.** A scrubber replays the last N minutes.
- **Sources.**
  - The bot route `GET /api/viewer/memory-activity?since&brain` gives the access ring plus the in-context set per brain.
  - The bridge-native `heat` aggregate per RAPTOR node feeds the graph colouring.
  - SSE event `memoryActivity`.

## World rendering is browser-only (decided 2026-11-02)

All world viewing renders in the browser with `@mineclaw/world-viewer` (three.js/WebGL), from the raw MCWC chunk,
entity, block-entity and overlay data and the package's bundled textures. No world images or video are rendered on the
server.
- The bot view's world pane is a follow camera. The hive grid's per-bot cards use the package's shared-renderer
  multi-view mode: one WebGL context for every card, so the browser's context limit is never hit.
- `/v1/render/tile/{bot}.png` is deprecated and unused by the UI.
- Vision sheet PNGs remain. They are the exact images a model was sent, kept as a debugging record rather than a view
  of the world.
- **Shared world data across views.**
  - Every view (the main pane and all the thumbnails) shares one world store per dimension: each column is fetched and
    decoded once. Each section is meshed once and its geometry is uploaded once.
  - Materials, the atlas texture, and the entity and overlay meshes are single shared instances.
  - A view owns only its camera, viewport and per-view uniforms (cutaway or x-ray through clipping uniforms, not
    rebuilt geometry). Interest regions are refcounted across views, so bots near each other share sections.
- **Live block updates.** Columns are downloaded once, then kept current in two layers:
  1. **Block deltas (fast path).** The bot logs every `WorldState.BlockChanged`, plus column load and unload events, in a
     rev-numbered `BlockDeltaLog`. It is deduplicated across bodies that share a column, and bulk chunk loads produce
     only a column event.
     - It is served by `GET /api/world/blocks?since=REV` and pushed as the SSE `blocks` event at about 5 Hz.
     - The browser patches the block in the shared store. It then re-meshes only that section, plus the neighbouring
       section when the block sits on a boundary. Changes are batched per animation frame, so a burst such as tunnel
       digging re-meshes each section once.
  2. **Hash diff (backstop).** Each column's hash in `/api/world/index` changes with any block change. On a rev gap,
     `full:true` (the ring overflowed) or a reconnect, the client refetches only the columns whose hash changed.
- **Render parity (decided 2026-11-02).** The repo is private and unreleased, so the Minecraft assets are committed:
  1.12.2 blockstates, models, textures and .mcmeta animations, the b1.8.1 terrain, items and mob textures, and both
  versions' default font (sign text is drawn with it). They live
  under `web/packages/world-viewer/assets/`, and the atlas is built client-side.
  - The browser must match the old freecam and every C# renderer: animated water and lava, real fluids, real player and
    mob models with animation, and every block shape.
  - Decompiled Java is never committed. Entity geometry is our own data files.
  - **The last looks (added 2026-11-03).** The wire now carries what the id cannot say, so the browser draws vanilla's
    layers too: the enchantment glint (equipment, held, framed and dropped items), an enderman's carried block, an iron
    golem's offered poppy, parrots on shoulders, arrows stuck in players, and styled 1.12.2 sign text. Wire shapes:
    api.md §6, "World looks". Only player skins and capes stay unsaid (an offline server has none to send).
- **Frame-rate targets.** At least 60 fps with a GPU. At least 40 fps sustained on software WebGL (SwiftShader), which
  is Fox's usual development browser, with hardware acceleration off.
  - Software rendering is detected automatically and selects a profile tuned by a frame-time controller that aims for
    25 ms per frame.
  - Benchmarks run in Chromium with `--disable-gpu`.
