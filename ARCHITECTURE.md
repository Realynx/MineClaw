# MineClaw — architecture

MineClaw is a headless Minecraft **Beta 1.8.1** client (protocol version **17**) written in C# (.NET 10),
driven by a Claude agent over the Anthropic API. Target: a Skyblock world on a b1.8.1 server, playing
alongside humans and talking to them through in-game chat.

## Layering (lower layers never reference higher ones)

```
MineClaw.App       console host: config, logging, wires everything, runs the bot
   │
MineClaw.Agent     Claude tool-use loop, game tools, chat routing, persistent memory, Skyblock knowledge
   │
MineClaw.Client    TCP connection + packet dispatch, 20 TPS tick, physics, player actions
   │        (dig, place, use, window clicks, crafting, smelting, chest ops, pathfind/goto, chat)
   ├── MineClaw.Protocol   packets + big-endian codec for protocol 17 (no deps)
   └── MineClaw.World      world/entity/inventory state, collision, A* pathfinding
            └── MineClaw.Data  static b1.8.1 data: blocks, items, recipes, smelting, tool rules (no deps)
```

`tests/MineClaw.Tests` (xUnit) covers all layers.

## Project ownership / contracts

| Project | Owns | Public contract (others code against this) |
|---|---|---|
| Protocol | `IPacket`, every packet class (both directions), `MinecraftReader/Writer` (big-endian, UTF-16 "string16"), `PacketRegistry`, `ItemStack` wire form, entity metadata, chunk payload (still compressed) | `PacketRegistry.Read(MinecraftReader, PacketDirection)` / `packet.Write(writer)` |
| Data | `BlockInfo`, `ItemInfo`, `Blocks`, `Items`, `Recipes`, `Smelting`, `Tools` (dig-time math) | `Blocks.Get(id)`, `Blocks.ByName(name)`, `Items.Get(id)`, `Items.ByName(name)`, `Recipes.*`, `Smelting.*`, `Tools.*` |
| World | chunk columns (16x128x16: ids, metadata, block light, sky light), `WorldState`, entity tracker, inventory/window model, AABB collision, A* pathfinder | `WorldState.GetBlock(x,y,z)`, `SetBlock`, `LoadChunk(...)`, `Pathfinder.FindPath(...)` |
| Client | `MinecraftClient`, packet → world/event wiring, physics tick, action primitives | async action API used by Agent tools |
| Agent | Anthropic client, tool loop, tools, SQLite memory + local RAG (`AgentDb`, `RagIndex`, `ObservationStore`), self-profile, prompts | `BotAgent.RunAsync(ct)`; world ingestion via `AgentRuntime.Observations` |
| App | `Program.cs`, `appsettings.json` | — |

## Conventions

- File-scoped namespaces matching the project (`MineClaw.World`, ...). One public type per file unless tiny.
- Coordinates: block coords are `int`, entity positions are `double`. Beta's wire position packets send
  **X, Y(feet), Stance(eyes = Y+1.62), Z** client→server; server→client 0x0D sends X, **Stance, Y**, Z (order differs!).
- Async APIs take a `CancellationToken`. No blocking `.Result`.
- Build only your own project while other workstreams are active (`dotnet build src/MineClaw.X`), to avoid
  file locks; tests live in `tests/MineClaw.Tests/<Area>/`.

## Token-efficiency design (Agent layer) — a hard requirement

The LLM must never be on the hot path. Claude is a *planner and conversationalist*; C# does the playing.

1. **Event-driven, not tick-driven.** The model is invoked only on meaningful events: chat addressed to the
   bot, a task finishing/failing, danger (low health, falling toward void, death), or a timed idle check-in.
   Everything else (movement, entity updates, block changes) is absorbed by C# state and never sent.
2. **Macro tools.** One tool call = many game actions executed deterministically in C#
   (`goto`, `mine`, `collect`, `craft` with recipe-tree resolution, `smelt`, `deposit`, `withdraw`, ...).
   Tool results are terse one-liners (`ok: mined 12 cobblestone (inv 44)`), never raw dumps.
3. **Pull, don't push, observations.** The model asks for what it needs (`look`, `inventory`, `nearby`);
   each returns a compact, bounded summary. A tiny state header (pos, hp, food, held item, current task) rides
   along with each event.
   Spatial work pulls pictures: `see {view: top|iso|side|eye}` returns a small PNG (≤~384 px, ≈100-200 image tokens;
   MineClaw.Render: grid + coordinates + compass + labels from memory; b1.8.1 textures read at runtime from the user's own
   client jar, never committed) only in the newest tool_result; compaction swaps it for a one-line stub and a rolling hourly
   cap (`Bot:Vision:ImagesPerHour`, 30) bounds spend. `look {map:true}` is the cheap ASCII grid.
4. **Prompt caching.** Order: tools + static system + *core* knowledge (breakpoint 1) → identity block
   (breakpoint 2, 5m) → append-only history with a moving breakpoint on the last message. Per-turn data
   (state header, `[self]`, `[memory]`, events) lives only in the newest user message, so each request is the previous one
   plus new messages. The grown personality (`[self]`, ≤~180 tokens) is sent only when it changed or left the window.
5. **Bounded context + local retrieval.** History compacts rarely and in one go (30k → 20k tokens main, 16k → 10k
   cheap) into a short journal; old tool results are truncated only then. Everything (memory, episodes, chat log,
   knowledge chunks, world observations) is indexed in one SQLite db (FTS5 + cached vectors + R*Tree) and pulled
   back by a zero-token auto-retrieval `[memory]` block (≤~250 tokens) or the `recall`/`where_is` tools.
   Knowledge docs: `<!-- core -->…<!-- /core -->` stays in the prompt, the rest is retrieved.
6. **Model tiers.** Three roles, all configurable (`Agent:Llm:MainModel/WorkerModel/CheapModel`), small `max_tokens`:
   - **Brain (Opus, main tier)**: planning, decisions and *all* player conversation (`Triage:CheapFirstForChat` is off by
     default). Its tool set is only cheap single-shot tools (status, inventory, look, where_is, stop, memory,
     identity, self, ask_player/ask_stand/watch_demo, run_routine) plus `do`, `delegate` and `ask_memory`. `do {action, args,
     then?, queue?}` (≈230 tokens) runs a single-step macro directly — goto, follow, mine, harvest_generator, chop_tree,
     deposit, withdraw, move_items, craft, act, see — so one job costs no Sonnet round; the brain loop expands it into the
     real tool calls (guard, body lock, safety gates unchanged). Tool-definition budgets: `Agent/Tools/ToolBudgets` (brain
     2,800 incl. hive, worker 2,150), the one constant every budget test uses. Its context never
     exceeds `Context:MaxTokens` (100K): compaction 30K→20K as usual, and a hard guard before every call cuts the current
     turn, folds history mechanically, and skips the call if it still can't fit.
   - **Worker (Sonnet)**: `delegate {task, done_when, max_minutes}` runs as the bot's activity (inline wait, then background
     with `[task ok]`/`[task failed]`; `stop`/interrupts cancel it). The worker has its own cached prefix (worker system
     prompt + game macro tools), a small private context and a rounds/minutes/USD budget, and returns ONE ≤300-char line
     (`ok:` / `failed:` / `needs_input:`). The brain never sees the worker's tool chatter.
   - **Memory (Haiku, cheap tier)**: compaction, self-reflection, `ask_memory {question}` (a ≤4-round recall/where_is/
     recall_detail loop that returns a ≤300-char answer with #ids), and budget-fallback chat. Episode summaries are
     mechanical (zero tokens). Free per-turn auto-retrieval (`[memory]`) is unchanged.
   Tool results are capped per tool (default 400 chars; look-with-detail/window 1200; `Agent:ToolResultCaps`).
7. **Routines.** The model can save a working sequence of tool calls as a named routine (e.g. "cobble_loop")
   and later run it in pure C# with zero tokens per iteration.
8. **Budget guard.** Usage + estimated USD tracked per call, per day and per tier (opus/worker/memory, shown by `/usage`);
   hard daily/monthly caps ($50/day, $200/month by default) apply to the sum of all tiers and are the ONLY throttle: Normal
   until a cap is reached → **Paused**. Optional soft stages stay off by default (`WorkerBrainFraction` → **Economy**: brain on
   the worker model; `CheapOnlyFraction` < 1 → **CheapOnly**: Haiku chat only). There is no hourly call cap (`MaxCallsPerHour`
   is ignored: background RAPTOR summaries once burned it and paused the brain mid-conversation).
   Test chat injected with console `/as` is tagged (`test_*` chat-log kinds, `eptest:` episode docs, source=test), never
   changes the self-profile and is skipped by reflection.
9. **Personality growth (`Agent/Self`).** The config persona is the base. Every committed episode yields deterministic
   signals (`self_signals`: treatment, emotes, requests, task outcomes, danger/EdgeGuard saves, spars, firsts, recurring
   phrases; zero tokens), folded in C# into bounded decaying aggregates (`self_acc`: per-player affinity/trust/playful,
   preferences, hours-scale mood), each explainable by its signals. Reflection (Haiku, every 30 episodes/24h) reads the
   RAPTOR self subtree (`selfnote` docs → `self_abstractions`) + aggregates and must cite signal ids. Scopes: `main` |
   clone username (shard: frozen seeds `self_seeds` + own growth) | `hive` (promoted when ≥2 clones show a trait).
   Multi-process safe: short IMMEDIATE transactions on the shared WAL db.
   No Semantic Kernel or similar frameworks — a hand-rolled minimal loop on the official Anthropic SDK.

## Brainstem (Agent/Brainstem + Bot/Brainstem) — the body under the brain

A small deterministic behaviour controller that always runs under the LLM (zero tokens). The LLM sets jobs; the brainstem
fills the gaps so the bot reacts at once instead of standing around while Opus thinks.

**Priority stack** (`PriorityStack`, highest first; a layer acts only while nothing above it is active, and a higher one preempts):
1. **Safety reflexes** — client tick: EdgeGuard, survival (heal retreat, eat), combat defence (`IGameClient.SafetyEngaged`).
2. **Player-commanded job / script** — brain/worker activities, a running script, reflex commands, standing orders.
3. **Speculative step** — the brainstem's first step of a clear job while the brain plans (walk/look only).
4. **Social idle** — head tracking, glances, looking at pointed blocks, loose follow, thinking fidget.

- **Instant acknowledgement**: addressed chat turns the head to the speaker on the next tick (`MinecraftClient.FaceIdle`, no
  action lock) plus a nod, before the debounce or any model call. No auto text: the persona speaks through the brain.
- **Reflexes** (`IntentMatcher.Reflex`, `Agent:Brainstem:Reflexes`): the whole line must be an exact command — stop/wait/halt,
  come here, follow me, jump/spin/wave/crouch, look at me, give/drop me <held item> [n] (trusted players). They run through
  the normal gated tools; the brain's turn gets `[reflex] ...` and may comment or stay silent. Anything ambiguous goes to the
  brain. A live script is never stopped by a reflex (its own gate pauses it on a trusted stop/wait).
- **Speculative start**: "grab cobble", "chop wood", "go to the gen", "bring me X", "come here and ..." start a plain walk
  (no digging/placing/parkour) toward the gen/tree/place/chest/player as an `ActivityKind.Speculative` filler. The brain's
  next chain adopts it (same place: the walk becomes its first step), retargets it (nearby, or the job it was heading for)
  or cancels it; a turn that adopts nothing cancels it. Telemetry counts adopted vs cancelled.
- **Move first, think while walking**: a brain `goto` is an `ActivityKind.Move` that answers after ~1.5 s ("walking there");
  job tools called meanwhile queue behind it and never run if the move fails; read-only tools run freely. `do {goto, then}`
  chains walk-then-work in one call. Long jobs answer "running in background" at once (no 20 s inline hold).
- **Streaming execution**: the Anthropic client streams brain calls and hands each completed `tool_use` block to the loop;
  read-only lookups and a leading walk start before the message ends (anything else keeps model order).
- **Body lock, not brain lock**: chat never pauses a script; it wakes the brain while the script keeps the body. Body tools
  answer `body busy: script cobble_loop 40%` (pause/replace it, or `do{...,queue:true}` to run after it); chat, memory,
  look/see/status, hive (spawn/assign/recall clones, answer their needs_input), ask_player and script ops stay free. A
  `delegate` drives this body too, so it answers busy as well (a superseding worker would kill the script). `[body: script ...]` rides in each turn
  header. Only danger, a trusted stop/wait and the brain's own `script op=pause|replace|cancel` pause a script.
- **Pre-observation**: chat turns carry `[here]` (≤~250 tokens, uncached tail, built live each turn, deduped against the
  header): landmark, inventory, the speaker's place and facing, nearby builds, void edges — so the brain acts in round 0.
- **Effort**: simple chat turns (a reflex, one clear job, small talk) run at `Brainstem:SimpleTurnEffort` (low) for the
  whole turn; planning turns keep the tier's effort. Debounce: 1.2 s when the line looks complete, else 1.8 s.
- **Telemetry** (`LatencyTracker`): per chat `reaction_ms`, `first_move_ms`, `first_action_ms`, `rounds_before_action`,
  speculation adopted/cancelled → `latency-YYYY-MM-DD.jsonl` and the TUI tokens panel's p50 line.

## Providers, profiles (Agent/Llm)

- **Per-tier provider**: `Agent:Llm:MainProvider|WorkerProvider|CheapProvider` = `anthropic` (default) | `ollama`; the
  tier's `*Model` is then an Ollama model name. `Agent:Llm:Ollama { Endpoint, ApiKey (bearer, for a proxy), DefaultNumCtx,
  KeepAlive, TimeoutSeconds, VisionModels[] }` — one local endpoint (it proxies cloud models itself). Empty endpoint and no
  tier on ollama = `LlmClientFactory` returns the plain `AnthropicLlmClient` (behaviour unchanged); otherwise a
  `RoutingLlmClient` sends each request by profile → model override → tier.
- **OllamaLlmClient** (plain HttpClient, `/api/chat`): system blocks → one system message in the same order (Ollama reuses
  its KV cache for an identical prefix while the model stays loaded; there is no explicit caching, so cache usage is 0);
  tool_use ↔ `tool_calls` (generated ids) / `role:"tool"` results; malformed arguments get one repair pass, else the
  registry answers an error result; images only for vision models; `think` only when `/api/show` lists "thinking";
  thinking blocks are never sent/returned; usage = `prompt_eval_count`/`eval_count`. Interruptible calls stream (NDJSON).
- **Startup**: a tier on ollama with an empty endpoint = config error (exit 2); `GET /api/tags` + `/api/show` check the
  tier models exist and support tools (tool-less models are refused for every tier). Anthropic key check covers only
  Anthropic tiers. A brain that isn't Opus 5.5 logs a warning: Lua authorship is keyed to the brain tier.
- **Budget**: `Agent:Prices:Ollama` (per MTok, default $0) prices Ollama models; usage and telemetry record provider and
  profile per call; hard caps apply to the sum as before.
- **Profiles** (`Agent:Llm:Profiles`: opus/sonnet/haiku built in, `local` example = ollama with no model, ignored): the
  brain picks one per `delegate {profile}` or `hive spawn {profile}` (the clone's brain tier; worker/memory too when the
  profile names `Worker`/`Memory`). Only usable profiles (model set, provider configured, passed the startup check) are
  offered or accepted. A clone brain on a non-Opus profile runs saved scripts but never writes them.
- **Embeddings**: `Agent:Embeddings:Provider = ollama` + `Model` uses `POST /api/embed` (endpoint from Embeddings or
  Llm:Ollama). Vectors are keyed by (model, dims): a different model re-embeds; a different *dimension count* than the db
  holds is refused (FTS-only + warning) unless `Agent:Embeddings:Reembed=true` drops the old vectors. Azure stays default.

## Hive (MineClaw.Hive) — clones as supervised processes

`App → Hive → Bot`. Up to `Hive:MaxClones` clones (`maid_chan_67/69/420/1337`), each a **separate OS process**: the same App
binary as `--clone <name> --parent-pipe <pipe>` (headless, own login, same three-tier brain, own personality shard via
`AgentOptions.Hive` scope, own `usage-<clone>.json` and `logs/hive/<clone>/`). The main process is the **supervisor**; a clone
connects its pipe before opening anything and exits 75 without one (never unsupervised).

- **IPC**: one named pipe per clone (`System.IO.Pipes`; Unix sockets on Linux), 4-byte LE length + snake_case JSON frames
  `{t,id,re,d,err}` with request/response (`Ipc/HiveMessages.cs` lists every type: hello/welcome, hb, task/progress/result,
  recall/bye, claim/release/claims, roster/chat_ok/inject, spend_auth/spend, snap/usage/cmd, inv/death/handoff).
  Heartbeats both ways from each link's own threads (blocking reader + heartbeat writer: never stalled by an Opus turn, tools,
  the db or a starved thread pool); every frame counts as liveness; after `HeartbeatTimeoutSeconds` (45) of silence a
  ping/pong probe (answered by the other side's link itself) decides; pipe closed or a failed probe = that side is dead.
- **Failure handling**: clone crash/kill → claims + grants released at once, last inventory snapshot marked *stranded*, quiet
  event to main, restart with exponential backoff (resuming its unfinished, uncancelled task with its loadout and re-granted
  leases), retired after `MaxCrashes` in `CrashWindowMinutes`. Spawns are serialized (parallel tool calls never share a slot), launches are spaced by `LaunchGapSeconds` (4.5 s, the server's login throttle), a config-error exit (2) retires the slot at once, and a task given to a clone being recalled (same turn, either order) relaunches it with that task
  and a superseded launch never takes over a slot's link. A task's loadout (pickaxe/axe/food from its text) is fetched from
  storage before the brain starts; what storage lacks is reported to main. Main gone → each clone
  deposits only if quick and safe (`ParentLossDepositSeconds`), records leftovers as stranded, logs out. Next spawn of a
  stranded clone deposits first.
- **Logout gate**: every planned logout (task done, recall, budget, making room, main SIGTERM) deposits the ENTIRE inventory
  (WorldIndexer chests with free-slot estimates → hand-off to main by tossing at its feet, confirmed by its collect events →
  place a chest, labelled as a shared-memory waypoint) and logs out only when the inventory window is verified empty (or the
  deposit timeout hits → stranded). SIGTERM: main recalls clones first (`ShutdownRecallSeconds`, under systemd's 90 s).
- **Shared memory**: one SQLite file (WAL, busy_timeout, the per-process AgentDb gate = single writer, IMMEDIATE txs). Hive
  tables (`hive_inventory`, `hive_events`) and a `clone_id` column on docs/chat_log/notes/observations/waypoints/structures/
  routines filled by per-connection TEMP triggers. `PRAGMA data_version` polling reloads memory + vector cache (RAPTOR tree
  debounced); RAPTOR maintenance runs only in main.
- **Claim board** (main, `Claims/ClaimBoard.cs`): leases with TTLs on blocks, areas, chests, furnaces, tasks. Contract
  `MineClaw.World.Claims.IClaimBoard` (`IsFree` local/fast, `TryClaimAsync`, `ReleaseAsync`); clones use a pushed mirror.
  BotHost wires it into `IsProtected` (claimed blocks are skipped) and `ClaimAwareGameClient` (chest/furnace/dig/place leases).
- **Chat arbiter**: one deterministic owner per player message in every process — named clone > thread lease (the bot that asked the player something or is watching their demo owns their replies until resolved/timeout; `thread` IPC) > sticky partner > main; a clone is named anywhere in a line as a vocative ("also 67, ...", "hello 67?"), never as a mention/quantity/coordinate ("get 67 cobble", "with 67?", "6,67,8"); a line naming several bots goes to each of them with main coordinating; ownership covers a player's *conversation*: an unnamed spawn of the request the clone talking with that player is on (or any spawn while it is asking them about it) is refused without `hive op=handover`, while additional clones for the same player's new or parallel request (a spar needs two) go ahead and clones already on that job get a `note` with the newcomer's task; a clone's task is *pending* in main's turn hint until its result event arrives, and main's speech guard drops claims about pending results;
  hive bots' chat, /me, joins/leaves are internal. Main pushes live roster + sticky map.
- **Budget**: Agent:Budget caps are shared by the hive; main's ledger adds clone spend to main's tracker, grants clones spend in
  chunks (optional per-clone sub-caps; 0/unset = inherit main's hard caps), refuses at `RecallAtFraction` (default 1.0 = the cap), and recalls clones (furthest from finishing first) when the
  mode drops. Slots: a spawn must leave one free slot per offline regular; if the server fills up, the clone furthest from
  finishing logs out.
- **Script spawns (semi-sleep)**: `hive spawn {script|code, args, after, profile}` — inline code must come from main's Opus
  brain and is linted + saved on main first (lint errors block the spawn and go back as findings). The clone fetches the
  loadout, then `CloneScriptRunner` starts the script as its standing order with no brain turn; its brain makes zero calls
  until a real wake (script failed/limit, wake/ask/pause notices, danger, addressed chat, trusted stop, order change from
  main). Checkpoints (`cp`) are written into the order (`resume_from`), so a crash restart or respawn resumes there. A
  normal end sends main one result line, clears the order, then logs out (deposit first) or sleeps online (`after: sleep`).
  `hive list`: `67: script cobble_loop 42% (brain asleep, profile haiku)`; the TUI brain field shows the same state.
