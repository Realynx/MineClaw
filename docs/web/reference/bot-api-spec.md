# MineClaw bot: HTTP surface, live state, and the chat-injection path (for the web viewer)

Repo: `C:\Users\dev\source\repos\mineclaw` at `d41ef40` (main, clean). This was a read-only pass, and no repo files were changed.
All paths below are repo-relative. Line numbers are from `d41ef40`.

---------------------------------------------------------------------------------------------------------------------------
## 0. Summary

- The running bot exposes exactly ONE network listener: the **Supervisor** HTTP server, built on `System.Net.HttpListener` and hosted
  by the main process (`src/MineClaw.App/Supervisor/SupervisorServer.cs`). There is no Kestrel, no ASP.NET, no `MapGet`, no SignalR,
  no WebSocket and no SSE anywhere in `src/` or `tools/`. A grep for `EventSource|WebSocket|text/event-stream|MapHub|Kestrel` found nothing.
- Everything is a **poll**. The live parts are read as JSON snapshots (`/api/status`, `/api/events?since=`, `/api/world/entities?since=`,
  `/api/brains/{id}/context?since=`, `/api/bodies/{name}/script`).
- Writes exist only under `/api/admin/*` (memory edits, rebuilds, backups, shard import). They are off by default. **No route
  can chat, move a bot or inject a message.** The class doc says so explicitly ("GET only: no commands, chat or movement",
  `SupervisorOptions.cs:4-6`).
- `tools/HiveSupervisor` is NOT a server. It is a PowerShell/sh launcher that opens `ssh -N -L 8765:10.1.2.73:8765 alice` and
  then the browser.
- The hive IPC between main and clones runs over named pipes or in-memory transports (`src/MineClaw.Hive/Ipc/HiveTransport.cs:58`,
  `HiveLink.cs:93`). It is not network-exposed.
- Auth is one shared secret (`Supervisor:Token`), compared in constant time, sent in the `X-Supervisor-Token` header or `?token=`.
  There is no TLS, no users and no roles. The intended way in is an SSH tunnel through alice.

---------------------------------------------------------------------------------------------------------------------------
## 1. Endpoints

### 1.1 Listener, config, auth

- Started from `AppHost.StartSupervisor` (`src/MineClaw.App/Hosting/AppHost.cs:338-389`), which is called at `AppHost.cs:243`.
  - It does nothing unless `Supervisor:Enabled` is true (`:341`).
  - It refuses to start without `Bot:Vision` (no `VisionHub`, `:342-346`).
  - A bind failure is only logged, and the bot keeps running (`:383-388`).
- Config binding: `config.GetSection("Supervisor").Get<SupervisorOptions>()` (`src/MineClaw.App/Hosting/AppSettings.cs:33`).
- `SupervisorOptions` (`src/MineClaw.App/Supervisor/SupervisorOptions.cs`). Key names and defaults:
  - `Supervisor:Enabled` (false)
  - `Supervisor:Bind` ("*"; the example config uses `10.1.2.73`)
  - `Supervisor:Port` (8765)
  - `Supervisor:Token` (null/empty = no auth at all!)
  - `Supervisor:AllowAdminWrites` (false)
  - Render caps: `CacheMs` 1000, `HiveCacheMs` 2000, `MinRenderIntervalMs` 120, `MaxQueuedRenders` 12
  - Request cap: `MaxConnections` 8
  - Views: `Radius` 6, `EyeWidth`/`EyeHeight` 480/360, `CameraMaxWidth`/`CameraMaxHeight` 800/600, `CameraDistance` 48, `PoseCacheMs` 2500
- Prefix: `http://{Bind or *}:{Port}/` (`SupervisorServer.cs:73-84`). Plain HTTP only.
- Request handling (`SupervisorServer.cs:98-133`):
  - It holds a semaphore of `MaxConnections` slots and waits up to 2 s for one, then answers 503 `{"error":"busy"}`.
  - The slot is released before the body is written (`:116`).
  - It sets `Cache-Control: no-store`, `X-Content-Type-Options: nosniff` and `Referrer-Policy: no-referrer` on every reply.
  - There are **no CORS headers**, so browsers on another origin can't read the replies. The NestJS side must proxy server-side.
  - It never logs the URL, because the URL may carry the token (`:128`).
- Token check: `SupervisorServer.Authorized` (`:234-241`). It is null-token-open, reads the header `X-Supervisor-Token` or `?token=`, and
  compares in constant time with `CryptographicOperations.FixedTimeEquals`.
  - Script, Vision and Controller routes each carry their own copy of the same check (`SupervisorScriptRoutes.cs:91-98`,
    `SupervisorVisionRoutes.cs:116-123`, `SupervisorControllerRoutes.cs:81-88`).
  - Those three answer **403**, and the core routes answer **401**.
- Routing order (`SupervisorServer.HandleAsync`, `:137-224`):
  1. Admin paths (own auth).
  2. Non-GET/HEAD → 405.
  3. `/` and `/favicon.ico` with NO auth.
  4. Scripts, then Vision, then Controller (own auth, 403).
  5. `Authorized` → 401.
  6. World routes.
  7. The core switch.
- JSON: System.Text.Json, **snake_case**, nulls omitted, NaN/Infinity allowed as named literals (`SupervisorModels.cs:54-64`).
- Errors: `{ "error": "..." }` with 400/404/405/429/500/503. 503 is sent for `RenderBusyException` and carries `Retry-After: 1`.

### 1.2 Core routes (`SupervisorServer.cs:143-213`). GET/HEAD, token → 401

| Route | Response |
|---|---|
| `GET /` , `/index.html` | Embedded single-file UI `MineClaw.App.Supervisor.supervisor.html`. **No auth.** It polls bots 3 s, status 3 s, events 2 s, designs 5 s, brain view 4 s, a 250 ms tile tick, and the camera at 20 Hz (`supervisor.html:467-474`). The token comes from the `?token=` query, is kept in sessionStorage and is sent as the header (`:103`). |
| `GET /api/bots` | `{at, bots:[BotTile]}`. BotTile = `{name, short, main, dim, x, y, z, hp, food, job, brain, age_seconds, claims[]}` (`SupervisorModels.cs:7-8`, built at `LiveSupervisorData.cs:57-65`). |
| `GET /api/hive/tree` | `HiveHierarchy.Snapshot()` (`AppHost.cs:372`), or `{id:"main",name,kind:"main",state:"running",children:[]}`. |
| `GET /api/status?bot=<name\|short\|main>` | The TUI panels (`LiveSupervisorData.Status`, `:181-220`): `{at, main, bots:[…], hive:{list, clones_today_usd, clones_month_usd, slots:[{name, number, state, connected, task, pending, progress, last_progress, result, deaths, last_exit}]}, claims:[{owner, what}]}`. Each bot is `{name, status(offline\|connecting\|online\|dead\|paused), at, pos "x,y,z", dim, hp, food, task, brain, budget_mode, header*, usage{today_usd, month_usd, caps, mode, cache_hit, today[], month[], calls_last_hour, calls_per_hour[24], last{at, tier, model, purpose, cost_usd}, latency}, thought*, chat[]}` (`:223-245`). Fields marked * appear only for the selected `bot`. `chat` holds the last 60 lines (selected bot) or 12 (others). **This is the existing "current thought + chat + body" endpoint.** |
| `GET /api/events?since=<id>` | `{events:[{id, at, kind, level, text}], last}`. At most 200 per call, from a 400-entry ring (`SupervisorFeed.cs`). Kinds: danger, death, conn, move, players, hive (`AppHost.cs:347-359`). |
| `GET /api/locate?q=` | `{name, dim, x, y, z}` or 404. |
| `GET /view/{bot}?mode=sheet\|brain\|eye\|iso\|shoulder\|orbit\|high\|top\|side\|layers&radius=3..12&xray=on\|off` | PNG with `X-Tokens`, `X-Width`, `X-Height`, `X-Seen`. `&meta=1` returns `{width, height, tokens, bytes, legend, facts[], seen, note}` instead (`:254-270`). |
| `GET /view/hive` | Hive overview PNG (same meta form). |
| `GET /camera?x&y&z&yaw&pitch&dim&w&h` | Free-camera PNG (yaw 0 = south, b1.8.1). Cached per quantised pose. |
| `GET /api/blueprints` | `{designs:[{id, name, status, summary, requester, rev, cost_usd, picture, updated}]}` |
| `GET /api/blueprints/{id}` | The design bill/eval JSON (`BlueprintService.BillJson`). |
| `GET /view/blueprint/{id}[?meta=1]` | Preview PNG, or the bill. |
| `GET /view/buildplan/{id}` | HTML page. `/api/buildplans/{id}` or `?meta=1` returns the plan parts as JSON. |

### 1.3 World routes, the "Live Hive" (`SupervisorWorldRoutes.cs`). GET, token → 401, rate-limited

Each route has its own token bucket and answers 429 `{error, retry_after_ms}` + `Retry-After` when it's empty (`:56-60`, `:137-140`).
At most 2 reads run at once on the pool (`:194-200`).

| Route | Response |
|---|---|
| `/api/world/index?dim&x&z&r(16..256)[&light=1]` | `{dim, at(ms), columns:[{cx, cz, hash, seen, by, live}], version("b1.8.1"\|"1.12.2"), time, rain, thunder}`. 8/s, burst 16, cached 400 ms. |
| `/api/world/chunks?dim&cols=cx,cz;…(≤64)[&light=1]` | `application/octet-stream` "MCWC" payload (`MineClaw.World.ColumnPayload`; format in `tools/HiveSupervisor/README.md:85-88`). 25/s. |
| `/api/world/entities?dim` | `{at, bots:[WorldBot], entities:[WorldEntity]}`. 12/s, cached 100 ms. |
| `/api/world/entities?since=REV` | The delta stream: `{at, rev, full, bots, entities(changed), removed:[[dim,id]]}` (`EntityDeltaLog`, min interval 100 ms). |
| `/api/world/overlays?dim` | `{at, dim, claims, places, points, plans:[{id, name, status, part, blocks:[[x,y,z,id,meta,done]]}], dangers, notes}`. 1/s, cached 3 s. |

- WorldBot = `{name, short, main, dim, x, y, z, yaw, pitch, pose_age, hp, food, job, brain, held, claims, live, age_seconds}` (`:10-11`).
- WorldEntity = `{kind, name, dim, x, y, z, id, type, item, damage, count, vx, vy, vz, age, yaw, by, flags, variant}` (`:16-18`).
- Data comes from `LiveSupervisorWorld.Routes(...)` (`AppHost.cs:366`).

### 1.4 Script debugger routes (`SupervisorScriptRoutes.cs`). GET, token → **403**

| Route | Response |
|---|---|
| `/api/scripts` | `{at, scripts:[ScriptLibraryEntry]}` (the saved library) |
| `/api/scripts/{name}[/{v\|vN}]` | `ScriptVersionDetail` (code, lint, params, history, runs) |
| `/api/bodies/scripts` | `{at, bodies:[{body, short, script, status, line, paused, version, role}]}` (who is running what) |
| `/api/bodies/{name}/script?code=1` | `BodyScriptReply{body, running, snapshot:ScriptDebugSnapshot, note}`. Asking keeps the session's ≈5 Hz sampling on for a few seconds (WatchLease). Answers are shared for 150 ms (`LiveScriptData.cs:32`). Clones answer over IPC with a 1.5 s timeout. |

- ScriptDebugSnapshot fields (`src/MineClaw.Scripting/ScriptDebugSnapshot.cs:30-113`), all camelCase in C# and snake_case on the wire:
  - Identity: session_id, name, version, lang, status, live, body, author, role, crew, coordinator, at, started, seq
  - Position: line, column, line_live, in_sdk, snippet, stack[], stack_age_seconds
  - Actions: action{label, args, seconds, atomic, running, ok}, actions, elapsed_seconds, timings[]
  - Inputs: params, args, checkpoint, checkpoints_passed/total
  - Pause: paused, pause_pending, pause_kind, pause_reason, paused_seconds
  - Patching: patching, patch_note, patches
  - State: machine, locals[], locals_note, locals_age_seconds, lanes[]
  - Progress: progress_pct, progress_note, last_failure, outcome, logs[]
  - Source and controls: code (with `code=1`), controls[]
- There is no pause/resume route. The TODO is at `SupervisorScriptRoutes.cs:37-38`.

### 1.5 Vision routes (`SupervisorVisionRoutes.cs`). GET, token → 403

| Route | Response |
|---|---|
| `/api/bodies/{name}/vision[?since=id]` | `{at, body, capacity, note, sheets:[VisionSheetInfo]}` (what the models were sent) |
| `/api/bodies/{name}/vision/{id}` | One VisionSheetInfo |
| `/api/bodies/{name}/vision/{id}.png` | The exact PNG the model got, with `X-Sha256`, `X-Tokens`, `X-Width`, `X-Height` |
| `/api/bodies/{name}/vision/render?see={json}[&meta=1]` | A fresh render of a `see` input (no model, through the render gate) |

### 1.6 Controller / brain-context routes (`SupervisorControllerRoutes.cs`). GET, token → 403

These need `Telemetry:Enabled` (capture), otherwise a note comes back (`SupervisorControllerData.cs:58-60`).

| Route | Response |
|---|---|
| `/api/bodies/{name}/controller` | `BodyControllerInfo{body, kind(main\|hive\|subhive\|script), owner, brain, brain_kind, model, provider, has_script, script, tabs[], filter, links{context, script, owner}, note}` |
| `/api/brains/{id}/context?since=V&body=` | `BrainContextPage{brain, version, at, capacity, enabled, in_flight:{seq, turn, lane, purpose, model, since, body}, turns:[CtxTurnSummary], window:CtxWindow?, evicted[], filter, note}`. Pass `version` back as `since`. An equal version means nothing changed. |
| `/api/brains/{id}/turn/{n}?body=` | `BrainTurnDetail{brain, turn, system[], tool_names[], calls:[CtxCall{…, added[], response:[CtxBlock]}], children[]}`. Comes from a 30-turn ring (`BrainCaptureOptions.Turns`). |

- Shapes: `src/MineClaw.Agent/Telemetry/BrainContextModels.cs:29-109`.
- CtxBlock kinds: text, tool_use, tool_result, thinking, redacted_thinking, reasoning, image.
- Redaction: `BrainContextCapture.Redact`. The supervisor token is added as a secret (`AppHost.cs:375`).
- Clone brains answer over the `brain_ctx` IPC with a 1.5 s timeout and a 250 ms share window (`SupervisorControllerData.cs:28-30`).

### 1.7 Admin routes (`SupervisorAdminRoutes.cs`). The only POSTs

- Every route returns 403 unless `AllowAdminWrites` is on AND `Token` is non-empty AND the request sends the **header**
  (`?token=` is refused here, `:85-88`).
- Body cap is 256 KB (shard uploads 64 MB, and those are read only after auth, `:52-73`).
- Every call is audited to `<data>/admin-audit.jsonl`.

| Route | What it does |
|---|---|
| `POST /api/admin/memory` | `{op, kind, id, fields, who, reason}` → MemoryEditResult |
| `POST /api/admin/rebuild/estimate` | Cost estimate for a rebuild |
| `POST /api/admin/rebuild` | Returns 202 `{job_id}` |
| `GET /api/admin/rebuild/{id}` | The job |
| `POST /api/admin/rebuild/{id}/cancel` | Cancels a running job |
| `POST /api/admin/backup` | Returns `{path}` |
| `GET /api/admin/audit?max=` | The audit entries |
| `GET /api/admin/schema` | Edit-form fields per kind |
| `POST /api/admin/activity/compact` | `{dry_run, who}` |
| `POST /api/admin/shard/import/preview` | Import diff for a shard |
| `POST /api/admin/shard/import` | Applies a shard |

- **Pattern to copy for a chat-write route**: same gate (flag + token header only), body cap, audit line, and constant-time compare (`TokenOk`, `:75-81`).

### 1.8 Clients that already exist (useful for the NestJS proxy)

- `tools/MemoryExplorer/Services/SupervisorEndpoint.cs`:
  - Default base `http://10.1.2.73:8765`.
  - Token from `~/.mineclaw-supervisor-token` or env `MINECLAW_SUPERVISOR_TOKEN`, sent only as the header.
  - `StartTunnel()` = `ssh -N -L 8765:10.1.2.73:8765 alice`.
- `tools/HiveSupervisor/hive-supervisor.ps1` and `.sh`: the tunnel plus the browser. The token is passed once as `?token=`, and the page moves it
  to sessionStorage.

---------------------------------------------------------------------------------------------------------------------------
## 2. Live state: where it lives in memory, and what already exposes it

### 2.1 "Current thought" (latest model text, summarized thinking, tool calls)

- `ThoughtTracker` (`src/MineClaw.Agent/Telemetry/ThoughtContext.cs:48-171`) produces `ThoughtSnapshot` (`:14-30`):
  - Turn timing and input: `UpdatedAt`, `TurnAt`, `Header`, `Events`, `Digest`, `Memory`
  - Model output: `Output` (the last assistant text), `Thinking` (summarized thinking)
  - Calls: `Tools` (≤8 `ThoughtToolCall{At, Name, Args, Result, Ok}`)
  - Activity: `Task`, `Delegate`, `Journal`
- Everything is **capped**: Output 400 chars, Thinking 600, Tool args 60, Tool result 120 (`ThoughtCaps`, `:33-45`). The caps are configurable as
  `Telemetry:Thought:*` (`PromptTelemetry.cs:17`).
- Fed by `PromptTelemetry` (`src/MineClaw.Agent/Telemetry/PromptTelemetry.cs`):
  - `OnTurn` (`:100-107`)
  - `OnBrainRequest` on a ChatTurn (`:138`)
  - `OnBrainResponse` (`:185`)
  - Worker rounds (`:140`), compaction and commit (`:80-82`)
- Owner: `PromptTelemetry.Thoughts` (`:63`). It is read in `InProcessBotSource.Snapshot(full:true)` (`src/MineClaw.App/Sources/InProcessBotSource.cs:105`).
- **Already exposed** through `/api/status?bot=X` → `bots[i].thought`.
- It updates only when a model call **completes**. There is no token streaming.
- Full and uncapped: `BrainContextCapture` (`src/MineClaw.Agent/Telemetry/BrainContextCapture.cs`), owned by `PromptTelemetry.Capture` (`:65`).
  - `Begin`/`End` per call (`:82`, `:135`).
  - `LastBrainCall` (`:222`), `Page(since, body)` (`:162`), `Turn(id)` (`:188`).
  - `CtxInFlight` = "now thinking" (lane, purpose, model, since).
  - **Already exposed** via `/api/brains/main/context`.
- Streaming text deltas exist only inside the turn. `LlmRequest.OnTextDelta` (`src/MineClaw.Agent/Llm/LlmTypes.cs:107`) is set at `BotAgent.cs:486` to
  `StreamHook` (`BotAgent.Reply.cs:90-104`), which feeds chat segments. There is no public event.
  - A live "typing" feed would need a new tap, e.g. an event on `PromptTelemetry` or a wrapper in `TelemetryLlmClient`
    (`PromptTelemetry.cs:243-247`).
- Per-call stream: `PromptTelemetry.Recorded` event (`:69`), raised once per finished call with a `PromptUsageRecord`. The TUI subscribes to it
  (`InProcessBotSource.cs:69`).
- Turn lifecycle:
  - `BotAgent.EpisodeCompleted` (`BotAgent.cs:252`) gives an `EpisodeOutcome` with `Tier`, `Calls`, `CostUsd`, `Said[]` and `Skipped`. It is logged at `AppHost.cs:188`.
  - `BotAgent.TurnStartedAt` (`:749`) and `TurnFocus` (`:157`).
  - Ambient speaker: `TurnContext.Current` (`src/MineClaw.Agent/TurnContext.cs:7`), with `TurnInfo(Speaker, Players, IsTest)` set at `BotAgent.cs:398`.
- Private "internal speech": quiet-turn text that is never chatted goes to `BotAgent.InternalSpeech` (`BotAgent.Quiet.cs:32`, raised at `:125`).
  **Nothing subscribes to it**, and it is not exposed today, only logged as `[internal]`.
  - `BotAgent.Muted` (`BotAgent.cs:199`) also carries gated text. Hive clones forward it to main.
  - A viewer would want both of these.

### 2.2 Current tool call / activity

- The current call is in `ThoughtSnapshot.Tools` (the tail entry with `Result == null` is in flight) and in `CtxInFlight`.
- The long-running action: `ActivityManager.Describe()`. Main's is `AgentRuntime.Activities` (`AgentRuntime.cs:47`), and each body has its own via
  `BotAgent.ActivitiesFor` / `BodyActivities`. Snapshot `Task` is filled at `InProcessBotSource.cs:101`, falling back to `IGameClient.CurrentAction`.
- Brain sleep state: `ScriptTools.BrainState(rt)` (`src/MineClaw.Bot/GameTools/ScriptTools.cs:1265`), e.g. "awake" or "asleep (script cobble_loop 34%)".
  It goes to `BotSnapshot.Brain`.

### 2.3 Current running script (name, source, state)

- `ScriptController.Current` / `.Last` (`src/MineClaw.Scripting/ScriptController.cs:42-43`). Getting there:
  - Main: `ScriptTools.Of(rt)?.Controller`.
  - In-process bodies: `bot.Bodies.All[i].Scripts`.
  - Clones: `HiveSupervisor.RequestScriptDebugAsync` (IPC).
- The snapshot is `ScriptSession.DebugSnapshot(code)` (`ScriptSession.Debug.cs`). The aggregation is in `LiveScriptData` (`src/MineClaw.App/Supervisor/LiveScriptData.cs:112-176`).
- **Already exposed**: `/api/bodies/scripts` (who is running what), `/api/bodies/{name}/script?code=1` (source + live state),
  `/api/scripts/{name}` (saved versions).

### 2.4 Recent chat lines

| Where | Member | What it holds | Exposed? |
|---|---|---|---|
| `InProcessBotSource._chat` (`InProcessBotSource.cs:52`, `:125-131`) | LinkedList, 100 | Every raw server chat line, colour-stripped, prefixed `HH:mm:ss `. Includes the bot's own echoed lines and system lines. | Yes, `/api/status` `chat[]` (60/12) |
| `ChatRouter._recentHeard` (`ChatRouter.cs:392`, filled `:258`) | Queue, 24 | (player, text, at) human lines. `RecentFrom(player)` (`:401`) gives the last 10 min. | No |
| `ChatRouter._recentSent` (`:28`, filled `:784`) | Queue, 20 | The bot's own sent lines | No |
| `GameBridge._recentChat` (`src/MineClaw.Bot/GameBridge.cs:441-445`) | Queue, 30 | Raw lines | No |
| SQLite `chat_log` (`RagIndex.LogChat`, `src/MineClaw.Agent/Rag/RagIndex.cs:720-725`) | Persistent | Kinds chat/whisper/emote/join/leave/bot/bot_whisper, `test_` prefixed for /as (`BotAgent.cs:1158-1186`). FTS in `chat_fts`. | No (the db is local to the CT) |

- Events you can hook:
  - `IGameClient.ChatReceived(string raw)`, from the chat packet at `src/MineClaw.Client/MinecraftClient.Dispatch.cs:67-68`.
  - `ChatRouter.Said(string)` (`:175`, raised `:764`), the bot's whole outgoing text.
  - `ChatRouter.SpokeTo(player)` (`:179`, `:804`).
  - `ChatRouter.Accepted(ChatMessage)` (`:116`), an addressed line this bot took.
  - `ChatRouter.AddressDecided` (logged at `AgentRuntime.cs:134-139`).

### 2.5 Body state

- `IGameClient.World` (`WorldState`) and `.Player.Snapshot`: Position, Health, Food, IsDead, Yaw/Pitch, Dimension.
- `GameBridge.StateHeader(game)` gives the brain's header line (pos, hp, held, day/night), surfaced as `BotSnapshot.Header` (full only).
- `BotSnapshot` (`src/MineClaw.Agent/Telemetry/BotSnapshot.cs:62-100`):
  - Identity and status: Name, Status, At
  - Position and vitals: Pos, Dimension, Hp, Food
  - Activity: Task, Header, BudgetMode, Brain
  - Panels: Thought, Usage, Viewport, Voxels, Chat
- `IBotTelemetrySource` (`:106-120`) is the per-bot handle. Main uses `InProcessBotSource`, in-process bodies use `BodyBotSource`, and clones use
  `mainHive.SourceFor(n)` (IPC-streamed snapshots). The list is built at `AppHost.cs:236-242` and handed to `LiveSupervisorData`.
- Richer per-bot data (yaw/pitch/held/claims) for the world view: `/api/world/entities` `bots[]`.

### 2.6 What the viewer still lacks (gaps)

1. No push: there is no SSE or WS, and `MaxConnections` = 8 makes long-poll slot hogging a risk. A streaming route must not hold the
   `_connections` semaphore for its lifetime. Today the semaphore wraps only `HandleAsync` (`SupervisorServer.cs:104-116`), so a stream needs its
   own path in `ServeAsync` with `res.SendChunked`. HttpListener's managed Linux implementation also has `AcceptWebSocketAsync`.
2. No uncapped "latest output" without pulling the whole context page. `/api/brains/main/turn/{n}` gives the full text.
3. No internal-speech or muted stream (`InternalSpeech` and `Muted` are unsubscribed or not exposed).
4. No structured chat feed with ids. `/api/status` `chat[]` is plain strings with no cursor. Use `ChatReceived`, `Said` and `Accepted` to build
   a ring with ids, like `SupervisorFeed`.
5. No chat-write route (see §3).

---------------------------------------------------------------------------------------------------------------------------
## 3. Chat input path (packet → brain → reply) and where to inject

### 3.1 The path for a real in-game line, e.g. `<poofimafox> claude, set it to day`

1. **Packet.** `MinecraftClient.Dispatch` `case ChatPacket chat` → `ChatReceived?.Invoke(chat.Message)`
   (`src/MineClaw.Client/MinecraftClient.Dispatch.cs:67-68`). 1.12.2 action-bar packets are skipped (`:65`). 1.12.2 JSON chat is translated to
   legacy text in `src/MineClaw.Protocol/Release1122/*`.
2. **Bridge.** `GameBridge.Attach` subscribes `OnChat` (`src/MineClaw.Bot/GameBridge.cs:56`), and `OnChat` (`:439-466`) does three things:
   - It records `_recentChat`.
   - In a **clone** it keeps only whispers to its own account, posting `GameEvent.Whisper` (`:446-458`). Only main hears public chat.
   - It checks the hunger acknowledgement, then calls **`_rt.Chat.OnServerChat(raw)`** (`:464`).
   - Other subscribers: the `AppHost.cs:191` log line, the whitelist note at `HiveHierarchyWiring.cs:30`, and the TUI ring.
3. **Router.** `ChatRouter.OnServerChat(raw)` → `OnServerChat(raw, test:false)` (`src/MineClaw.Agent/Chat/ChatRouter.cs:209-220`) → `Route` (`:226-249`).
   - **`Parse`** (`:181-206`) checks, in this order:
     1. Emote: `* name msg`
     2. Whisper patterns (`ChatOptions.cs:22-27`)
     3. Join and leave patterns
     4. Public patterns (`<name> msg`; loose `name: msg` only for known-online names)
     5. Otherwise System
   - `HandleMessage` (`:251-297`):
     - It drops self, hive bots, echoes and empty lines, then runs `Observe` and `Decide`.
     - The addressing decision (`ChatRouter.Addressee.cs`) covers name, whisper, conversation continuity, solo, group and the cheap
       classifier for regulars.
     - The hive **`Arbiter`** (`:266-270`, set by MainHive) gives exactly one replier across the hive.
     - It updates thread state (`_lastToUs`, …) and runs the optional classifier (`:285-289`).
   - `Deliver` (`:301-348`):
     - It raises `Accepted`.
     - **`Commands.Observe(msg, addressed, named)`** (`:307-315`).
     - Then come the ask_player waiters (`TryCompleteWaiter`), the per-player rate limit `AllowIncoming` (`:562-573`) and `DropBackgroundChat`.
     - Finally it posts `GameEvent.Chat(player, text, addressed)`, `GameEvent.Whisper` or `GameEvent.Emote` into `EventQueue` via `Post` (`:224`).
       `Post` adds `IsTest` when injecting a test line.
4. **CommandGate** (`src/MineClaw.Agent/Chat/CommandGate.cs`):
   - `Observe` (`:112-143`) records a grant only when ALL of these hold:
     - The player is in `Agent:Self:TrustedPlayers` (`AgentRuntime.cs:120`).
     - This bot owns the line (`mine`).
     - It is a whisper (the format is the proof), OR a Public line that is explicitly addressed (`named`) and passes **`IsOwnLine`** (raw is the
       vanilla `<name> ` form whose last token == player, `:146-153`).
   - Two kinds of grant:
     - A line containing `/cmd` gives an exact one-shot grant (`Agent:Commands:AuthorizationSeconds` 60).
     - A line with no command gives a **Request** (`RequestSeconds` 120, up to `RequestCommands` 8 commands, `:131-132`). The model may then
       write its own commands through `run_command` → `Authorize` (`:161-198`).
   - The outgoing choke point is `AllowOutgoing` (`:215-233`), wrapped around `sendChat` at `AgentRuntime.cs:123`.
   - `Blocked` is never sent (`:15-16`). `/me`, `/tell` and `/msg` are always allowed.
   - **Note: `Observe` does not look at `IsTest`.** An `/as` line from a trusted name therefore DOES arm grants, as long as its raw form is `<name> …`.
5. **EventQueue → BotAgent.**
   - `BotAgent.RunAsync` (`BotAgent.cs:254-280`) → `_events.WaitBatchAsync` (with chat debounce) → `HandleBatchAsync` (`:283`) → triage, quick
     reply (`TryQuickAsync`) and `RunEpisodeAsync` (`:385+`).
   - Speaker and audience come from the waking chat events (`:394-398`).
   - `whisperTo = batch.WhisperOnlyFrom` means every waking event was a whisper from one player (`EventQueue.cs:34-43`).
6. **Quiet / session logic** (`src/MineClaw.Agent/BotAgent.Quiet.cs`):
   - `IsQuietBatch` (`:47-52`): no player-facing wake means plain text stays internal (`KeepInternal` → log, `Muted`, `InternalSpeech`), and only
     the `say` tool can speak.
   - `IsPlayerFacing` (`:96-107`): Chat/Whisper/Emote/Join with a player, a waking Gesture, a Notice with a Player, an Activity ≥ Normal, and
     own dangers.
   - `Engaged` (`:110-118`), which decides whether a quiet turn's say may reach chat:
     - Any of: TaskFailed, a danger, MustWake.
     - OR `_chat.AddressedWithin(EngagedWindow = 2 min)`.
     - OR, within `SessionWindow = 30 min`, the text contains a `?`.
   - `AddressedWithin` reads `ChatRouter._lastAddressedAt`, set in `Deliver` (`:323`, `:347`) for any addressed human line, test lines included.
   - So **an injected viewer message opens the 2-minute engaged window and the 30-minute session window**, exactly like in-game chat.
7. **Reply out.**
   - Streamed segments (`BotAgent.Reply.cs:90-174`) and the end-of-round text (`:469-481`) go to `SayBrainAsync` (`:221-240`) and then
     `ChatRouter.SayAsync(text, whisperTo, audience)` (`ChatRouter.cs:705-806`).
   - `SayAsync` applies the food/greet/death filters, splits into ≤100-char lines, caps by `MaxLinesPerReply` and the startup cap, raises `Said`,
     paces the lines, and sends via `gatedSend`, i.e. **always in-game** (public, or `/tell {player}` when `whisperTo` is set,
     `ChatOptions.WhisperFormat`).
   - `LogSaid` records bot/bot_whisper lines in `chat_log`.
   - `HiveSupervisor` adds a `SpeechGate` and a relay guard on main (`MainHive.cs:148-149`).

### 3.2 Existing injection precedent: console `/as`

- `ConsoleCommands.ExecuteAsync` `/as <player> <msg>` (`src/MineClaw.App/Hosting/BotCommands.cs:258-265`):
  - It builds `raw = "<player> msg"`, then calls `rt.Chat.OnServerChat(raw, test:true)`, then `Injected?.Invoke(raw)`.
  - In a hive, `Injected` → `HiveSupervisor.InjectAsync(raw)` (`MainHive.cs:164`), which runs
    `BroadcastAsync(Msg.Inject, new InjectMsg(raw))` (`src/MineClaw.Hive/Supervisor/HiveSupervisor.cs:1741`, msg `"inject"` at
    `src/MineClaw.Hive/Ipc/HiveMessages.cs:55,142`).
  - Each clone then runs `CloneHost` `_body.InjectChat(inj.Raw)` (`src/MineClaw.Hive/Clone/CloneHost.cs:303`), which calls
    `rt.Chat.OnServerChat(raw, test:true)` (`src/MineClaw.App/Hosting/CloneMode.cs:390`). That way every hive process arbitrates the same line.
- `test:true` effects: GameEvent.IsTest → batch.IsTest → `TurnContext.IsTest`. Consequences:
  - Memory: rules are not stored (`DirectiveStore.cs:126,199`), claims are not stored (`BeliefStore.cs:91,178`), episodes are tagged source=test
    (`EpisodeIndexer.cs:87-90`), and memory tools skip writes (`MemoryTools.cs:144,153`).
  - Self and logs: the self-profile and signals ignore it (`SelfGrowth.cs:87,171`, `SignalExtractor.cs:34`), self tools are refused
    (`SelfTools.cs:29,68`), and chat_log rows get the `test_` prefix.
  - Per the user's memory note ("/as test chat isn't the user"), **viewer chat is real user speech and must NOT reuse test:true**.
- Other sources of the "console line" interface:
  - `IBotTelemetrySource.SendAsync(line)` (`BotSnapshot.cs:118-119`) → `InProcessBotSource.SendAsync` → `ConsoleCommands.ExecuteAsync`
    (`InProcessBotSource.cs:118-123`). The TUI uses it at `TuiApp.cs:267`.
  - Plain text there makes the BOT say the text in chat (`BotCommands.cs:249-253`).
  - `/as`, `/tool`, `/pause`, `/quit` and the rest are operator commands. **Never expose `SendAsync` to the viewer as-is.**

### 3.3 Recommended (narrowest) injection point

- **Add a sibling of `OnServerChat` in ChatRouter**, e.g. `OnRemoteChat(string player, string text, RemoteOrigin origin)`. It would:
  - Build the `ChatMessage` directly. Do not hand-craft a raw `<name> msg` string for `Parse`, because a synthetic `<poofimafox>` raw is exactly what
    `CommandGate.IsOwnLine` treats as server-authenticated proof.
  - Call `HandleMessage(msg)` under a `[ThreadStatic]` origin flag, like `_injectingTest` (`ChatRouter.cs:215-224`).
  - Stamp the events, e.g. `GameEvent.Origin = "viewer"`. Do NOT set IsTest.
  - That keeps addressing, the hive arbiter, waiters, the rate limit, the session window and EventQueue debounce identical to in-game chat.
- **Hive.** It needs the same broadcast as `/as`: a new IPC message (or `InjectMsg` with an origin field) so in-process and process hives
  arbitrate it. Otherwise only main's router sees it, and clones' threads, waiters and `OwnedElsewhere` break. Clones normally ignore public chat
  (`GameBridge.cs:446-458`), so a viewer line has to reach each clone router the way `InjectMsg` does today.
- **Kind.**
  - Model it as `ChatKind.Public` (addressed per the normal rules) or as a direct/whisper-like address.
  - If you use `Whisper`, then `WhisperOnlyFrom` makes the bot reply with `/tell <player>`. That fails or is lost if the player is offline in-game,
    and `CommandGate.Observe` treats whispers as proof (`:122`).
  - Simplest: Public + "explicitly addressed" true for viewer lines, since the viewer *is* talking to that bot.
- **Body selection.** Pass a target bot so `Decide`/`Arbiter` route to the right body. There is the `ExplicitlyAddressed` hook (`ChatRouter.cs:128`)
  and `Arbiter`. Alternatively prefix the bot's name.

### 3.4 What must be careful

1. **CommandGate grants.**
   - Grants must come only from authenticated user lines. Today the proof is the server's `<name>` framing or the whisper format.
   - A viewer line needs an explicit decision:
     - **(a)** Never arm grants from the viewer. Call `Commands.Observe` with `mine:false`, or skip it. Recommended default.
     - **(b)** Arm grants only when the viewer session is strongly authenticated AS that trusted player (per-user secret or OIDC, mapped to
       exactly one of `TrustedPlayers`), behind a config flag (e.g. `Agent:Commands:AllowViewerRequests`), with `authorized_by = "poofimafox (viewer)"`
       in `command-audit.log` (`CommandGate.Audit`, `:284-299`).
   - Never pass the viewer text through `Parse` with a fabricated `<name>`.
   - Note that `Observe` with `mine:false` *clears* that player's open Requests (`:115-121`), which has side effects on a concurrent in-game request.
     Skipping the call entirely is cleaner.
2. **Identity.**
   - The single `Supervisor:Token` is a shared operator secret, not a player identity.
   - A "chat as Fox" or "chat as Robin" endpoint needs per-player credentials in the NestJS layer. The bot should get `player` from the authenticated
     principal, never from the request body.
   - Validate it against `TrustedPlayers`, or a separate `ViewerPlayers` list.
3. **Quiet and session windows.**
   - A viewer line counts as "addressed" and starts `EngagedWindow` (2 min) and `SessionWindow` (30 min), so later quiet turns may speak in
     public chat while Fox is not in-game.
   - Consider tagging `_lastAddressedAt` per origin, or excluding viewer lines from `AddressedWithin` when the player is offline (`ChatRouter.OnlinePlayers`).
   - Also: `HandleMessage` does `_online.Add(player)` (`:257`), which would mark an offline player as online and pollute `OnlinePlayers`, greetings
     and `HumansOnline` (`BotAgent.cs:175`). This must be skipped for viewer lines.
4. **Replies.**
   - Today every reply goes to in-game chat (`SayAsync` → `gatedSend`). If the player is offline, public chat goes to an empty server or other
     players, and `/tell` fails.
   - Recommend:
     - Return the reply to the viewer by correlating on the turn. Subscribe to `ChatRouter.Said` or `BotAgent.EpisodeCompleted` (`Said[]`) while
       `TurnContext.Current.Speaker == player`, plus `InternalSpeech`/`Muted` for private text.
     - Optionally add an origin-aware sink in `SayAsync`: when the turn's audience is only viewer-origin players who are not online, deliver to the
       viewer and skip the server send.
     - `Food`, `Greeted` and `Deaths` filters and `MaxLinesPerReply` still apply.
5. **Debounce and dedupe.** EventQueue merges and debounces chat per player (`EventQueue.cs:233-298`), and `IsEcho` drops a line equal to one the
   bot sent within 10 s (`ChatRouter.cs:577-584`). Both are fine, but the viewer UI should expect merged turns.
6. **Rate limit.** `AllowIncoming` (`PerPlayerWindowSeconds`/`PerPlayerMaxMessages`) applies per player name. A viewer flood would demote the real
   in-game lines to context. Add a viewer-side rate limit too.
7. **Memory provenance.**
   - Non-test viewer lines become real rules, claims and memories (DirectiveStore treats trusted players' instructions as standing rules).
     That is intended.
   - chat_log rows should carry the origin (e.g. kind `viewer_chat`), so they can be audited and are never confused with server lines.
8. **Budget.** Each viewer line can wake Opus. Usage caps (`Agent:Budget:*`) still apply, but expect cost from chatty viewers.

---------------------------------------------------------------------------------------------------------------------------
## 4. Security primitives and deploy

### 4.1 What exists

- One shared token (`Supervisor:Token`):
  - It is compared in constant time with `FixedTimeEquals` (`SupervisorServer.cs:234-241`; `SupervisorAdminRoutes.cs:75-81`).
  - It is never logged, the URL is never logged (`:128`), and it is registered as a capture secret for redaction (`AppHost.cs:375`).
  - Clients keep it in `~/.mineclaw-supervisor-token` or env `MINECLAW_SUPERVISOR_TOKEN`.
- Admin writes are double-gated: the flag, plus the token in the header only.
- No TLS anywhere. No users, roles, sessions or CSRF protection (GET-only design). No CORS headers, so same-origin only. No request logging.
- The `/` page is unauthenticated. It holds no data until the token is supplied.
- Resource caps:
  - Requests: `MaxConnections` 8, then 503.
  - Renders: the `RenderGate` single below-normal thread, ≤ ~8/s, queue 12.
  - World routes: token buckets.
  - Bodies: capped before reading.
- Command safety: `CommandGate` (see §3), `Agent:Commands:{AlwaysAllowed, Blocked, AuthorizationSeconds, RequestSeconds, RequestCommands,
  AuditFile}`, audit at `<data>/command-audit.log`.

### 4.2 Deploy (alice, CT 114 "claudechan", 10.1.2.73)

- `deploy/deploy-alice.ps1`:
  - Steps: tests, `publish-linux.ps1` (self-contained linux-x64), tar, scp to `alice:/tmp/mineclaw-deploy`, `pct push` into CT 114, then
    `/usr/local/sbin/mineclaw-release install` (`deploy/ct-release.sh`). The release script swaps `/opt/mineclaw/current` to `releases/<ts>`,
    keeps 3 releases, refreshes the unit, restarts and health-checks (a "joined … as" log line).
  - Switches: `-PushConfig`, `-Rollback [-DryRun]`, `-Status`, `-NoStart`, `-ResetMemory -Pack`.
  - Config `src/MineClaw.App/appsettings.json` (gitignored) is pushed to `/etc/mineclaw/appsettings.json` 0640 root:mineclaw only with
    `-PushConfig` or on the first deploy (`:132-133`, `:168-171`). It refuses to ship if `appsettings.json` lands in the publish output (`:154`).
- `deploy/mineclaw.service`:
  - `User=mineclaw`.
  - `ExecStart=/opt/mineclaw/current/MineClaw.App --no-tui --config /etc/mineclaw/appsettings.json --data-dir /var/lib/mineclaw --log-dir /var/log/mineclaw`.
  - `EnvironmentFile=-/etc/mineclaw/mineclaw.env`, using `MINECLAW_Section__Key` overrides (e.g. `MINECLAW_Supervisor__Token`,
    `MINECLAW_Supervisor__Bind`, `MINECLAW_Agent__Llm__ApiKey`).
  - Hardening includes `ProtectSystem=strict`, `RestrictAddressFamilies=AF_INET AF_INET6 AF_UNIX`, `ReadOnlyPaths=/opt/mineclaw /etc/mineclaw`
    and `TimeoutStopSec=120`.
  - It does not restrict listening ports, so 8765 works under the unit.
- Config key names relevant here (names only):
  - Supervisor: `Supervisor:{Enabled, Bind, Port, Token, AllowAdminWrites, CacheMs, HiveCacheMs, MinRenderIntervalMs, MaxQueuedRenders,
    MaxConnections, Radius, EyeWidth, EyeHeight, CameraMaxWidth, CameraMaxHeight, CameraDistance, PoseCacheMs}`
  - Telemetry: `Telemetry:{Enabled, Directory, KeepDays, RecentRecords, Thought:*, Capture:{Enabled, Turns, BackgroundTurns, CallsPerTurn}}`
  - Agent: `Agent:Self:TrustedPlayers`, `Agent:Self:RegularPlayers`, `Agent:Chat:{Username, DisplayName, Aliases, WhisperFormat, WhisperPatterns,
    PublicPatterns, MaxLinesPerReply, LineDelayMs, PerPlayerWindowSeconds, PerPlayerMaxMessages}`, `Agent:Commands:*`
  - Hive: `Hive:{Enabled, Isolation (inproc|process), RegularPlayers, …}`, `Bot:Vision:*`
  - Example values: `src/MineClaw.App/appsettings.example.json:200-205` has `Supervisor` Enabled false, Bind 10.1.2.73, Port 8765, Token "".
- **Local** `src/MineClaw.App/appsettings.json` has no `Supervisor` section (top level: Minecraft, Agent, Hive). Whether the deployed
  `/etc/mineclaw/appsettings.json` or `mineclaw.env` enables it, and with a token, could NOT be verified without ssh to the CT. Check with
  `pct exec 114 -- jq '.Supervisor|keys' /etc/mineclaw/appsettings.json` (keys only) before relying on it.
- Network and firewall:
  - CT 114 sits on VLAN 3, `10.1.2.73/24`, gw `10.1.2.1`, with the PVE firewall on (`deploy/provision-alice.sh:20,62`).
  - `/etc/pve/firewall/114.fw` has `policy_in: ACCEPT` with `GROUP allow-ssh` then `GROUP isolated` (isolated must stay LAST,
    `provision-alice.sh:66-79`; `deploy/README.md:16-17`). The group definitions live in `cluster.fw` on PVE, not in the repo.
  - 10.1.2.73 is in `[IPSET mc-servers]` so it can reach MC 10.1.2.69:25565.
  - The documented access path is the SSH tunnel through alice (`tools/HiveSupervisor/README.md:37-48`): "no firewall changes are needed".
- Implications for the web viewer:
  - Run NestJS on a host that reaches 10.1.2.73:8765 (alice-pivot, or a CT on VLAN 3 allowed by `isolated`, which will need a rule), or have it
    hold an SSH tunnel.
  - Keep the bot token server-side in NestJS and never send it to the browser.
  - Terminate TLS and do user auth in front of NestJS.
  - Add a bot-side `Supervisor:Bind` that is specific (not `*`).
  - Chat-write should be a new POST gated like admin: a flag, a header token, a per-player principal, an audit line.

---------------------------------------------------------------------------------------------------------------------------
## 5. Recent activity and files to avoid

- `git log --since=2026-10-31` is **empty**. The newest commit on main is `d41ef40` (2026-10-30 01:36). `origin/main` = `d41ef40`
  (`git ls-remote` agrees, with no newer remote branches). The working tree is clean and there are no stashes.
- Worktrees:
  - `C:/Users/dev/source/repos/mineclaw` at d41ef40 [main]
  - `C:/Users/dev/source/repos/mineclaw-deploy` at d41ef40 (detached, clean; the deploy worktree)
- Branches: `main`, `remotes/origin/main`, `remotes/origin/lante/cleanup-tests`.
  - The lante branch has 1 unmerged commit, `5893ce4` (Lante, 2026-10-27), based on 5da1b4e. It touches:
    - Docs and solution: ARCHITECTURE.md, MineClaw.sln, a stray root `UsageTracker.cs`, src/MineClaw.Skills/CATALOG.md
    - Agent: src/MineClaw.Agent/Budget/UsageTracker.cs, src/MineClaw.Agent/Rag/Raptor/SeedCorpus.cs, src/MineClaw.Agent/Routines/ScriptLibrary.cs
    - App/Bot/Client: src/MineClaw.App/Tui/TuiFrame.cs, src/MineClaw.Bot/GameTools/VisionTools.cs, MinecraftClient.Gathering.cs/.Windows.cs
    - Data/Hive/Raptor/Render: src/MineClaw.Data/Drops.cs, src/MineClaw.Data/Percent.cs (new), src/MineClaw.Hive/Supervisor/HiveSupervisor.cs,
      src/MineClaw.Raptor/RaptorIndex.cs, src/MineClaw.Render/TextureAtlas.cs
    - Tests: VisionTests, PercentTests, and it removes tests/MineClaw.Tests/MineClaw.Tests.csproj
- The other person's "optimisations" work is not visible locally. It may be uncommitted elsewhere, or it may be the lante branch.
- Files touched most in the last active window (10-29 → 10-30). Treat these as hot:
  - **Agent / chat / brain** (most collision-prone for a chat-injection change):
    - `src/MineClaw.Agent/Chat/CommandGate.cs`, `Chat/CommandTools.cs` (380b5f3: trusted request → bot-written commands)
    - `src/MineClaw.Agent/BotAgent.Quiet.cs` (0b9d982, cc0b410), `BotAgent.Bodies.cs` (d50ac8e)
    - `src/MineClaw.Agent/Prompts.cs` (d50ac8e, 380b5f3)
    - `src/MineClaw.Agent/Chat/ChatOptions.cs`, `Chat/ChatRouter.Gestures.cs`, `Events/EventQueue.cs`, `Events/GameEvent.cs` (cc0b410 gestures)
    - `src/MineClaw.Agent/Tools/ToolBudgets.cs`, `Tools/DoTool.cs`, `Human/HumanTools.cs`, `Self/SelfTools.cs`
    - `src/MineClaw.Agent/Rag/ChestRoom.cs`, `Rag/ObservationStore.cs`
  - **App hosting / supervisor**:
    - `src/MineClaw.App/Hosting/{AppHost, AppSettings, CloneMode, HiveBuildJobs, HiveCrews, HiveHierarchyWiring, InProcBodies, InProcHives,
      MainHive, SubhiveWiring}.cs`, `MineClaw.App.csproj`, `appsettings.example.json`
    - `src/MineClaw.App/Supervisor/LiveSupervisorData.cs` (53861b4 viewer)
  - **Hive**:
    - `src/MineClaw.Hive/Supervisor/HiveSupervisor.cs` (36c5339, 380b5f3, 5fa7aaa; also on the lante branch), `Supervisor/ProcessLauncher.cs`
    - `Ipc/{HiveMessages, HiveTransport, HiveLink}.cs`
    - `Hierarchy/{HiveHierarchy, CloneHiveTool, Bus/BusMessages}.cs`, `HiveTools.cs`, `HiveConfig.cs`, `Clone/CloneHost.cs`, `Crew/HiveScriptHost.cs`,
      `Slots/ResourceCeiling.cs`
  - **Bot**:
    - `Bodies/{Body, CrewPorts, BodyRoutingGameClient, BodyFactory.Clone, BodyFactory.Character}.cs`
    - `GameTools/{ScriptTools, ScriptTools.Crews, ScriptTools.Bindings, CombatTools}.cs`
    - `Blueprints/*` (BlueprintService, BlueprintWiring, DesignDrift, DesignHistory, DesignRevisions, DesignCapture, BuildBody, ConstructionExecutor)
    - `ProtectionPolicy/{EditContext, ProtectionEdits}.cs`, `Protection.cs`, `BotHost.cs`, `GameBridge.cs`, `ClaimAwareGameClient.cs`, `Game/IGameClient.cs`
    - `Brainstem/GestureFeed.cs`, `Perception/{GestureDetector, GameSignals}.cs`, `Excavation/DigSiteStore.cs`, `Memory/MemoryAdapters.cs`, `Ledger/InventoryLedger.cs`
  - **Scripting**: `ScriptSession.cs`, `ScriptSession.Debug.cs`, `ScriptDebugSnapshot.cs`, `ScriptPrompts.cs`, `Crew/*`, `Runtime/Js/JsRuntime.{Crew,Character}.cs`,
    `Events/*`, `Sdk/js/{20-world,30-flow,40-machine,60-jobs,65-dig,70-crew,80-character}.js`
  - **Client / World / Skills / Protocol**:
    - `MinecraftClient.{Actions, Blocks, Building, Gestures, Trees, Combat, Shelter, Survival, Protection, Dispatch}.cs`, `Combat/*`, `PathfindGate.cs`, `ActionResult.cs`
    - `World/{WorldState, BlastLog, Designs/DesignSnapshot, Excavation/*, Entities/EntityTracker}.cs`
    - `Skills/{Excavation/*, Skills/Excavation, Core/SkillRegistry}.cs`
    - `Protocol/Release1122/*`
  - **Shards/tools/docs**: `shards/defaults/{index.json, sdk-js.mcshard, 1.12.2-gamedata.mcshard}`, `tools/MemoryExplorer/*` (Designs page,
    ScriptDebugViewModel), `ARCHITECTURE.md`, `docs/design/hive-hierarchy.md`, `docs/design/reactive-character.md`
- Low-collision places for viewer work (the Supervisor routes were not touched in this window, except LiveSupervisorData once):
  - New files under `src/MineClaw.App/Supervisor/` (e.g. `SupervisorChatRoutes.cs`, a `SupervisorStream` SSE route), wired with a few lines in
    `AppHost.StartSupervisor` (`AppHost.cs:338-389`).
  - The unavoidable core touches are small:
    - `ChatRouter.cs`: a new `OnRemoteChat` + origin flag.
    - `GameEvent.cs`: an origin field. It is hot; cc0b410 touched it.
    - `HiveMessages.cs` + `HiveSupervisor.cs`: an origin on `InjectMsg`. Both are hot.
    - Optionally `CommandGate.cs` (hot) for a viewer-request flag.
  - Coordinate those with the other worker.
