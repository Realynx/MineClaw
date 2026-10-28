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
   Spatial work pulls pictures: `see {views[], focus, radius, question}` renders ONE contact sheet of a focused spot (vision v2,
   below) and, with a perception model configured, the brain gets ≈100 tokens of voxel-verified text instead of the image.
   In direct mode the sheet (≈280-310 image tokens; the legacy `see {view}` = one ≤~384 px picture, ≈100-200 tokens;
   MineClaw.Render: grid + coordinates + compass + labels from memory; b1.8.1 textures read at runtime from the user's own
   client jar, never committed) rides only in the newest tool_result; compaction swaps it for a one-line stub and a rolling
   hourly cap (`Bot:Vision:ImagesPerHour`, 30) bounds spend. `look {map:true}` is the cheap ASCII grid (role letters included).
4. **Prompt caching.** Order: tools + static system + *core* knowledge (breakpoint 1) → identity block
   (breakpoint 2); both are the stable prefix on `Agent:Llm:PrefixCacheTtl` ("1h" default, 2x write: idle gaps of 4-15 min
   no longer re-write ~35K tokens; "5m") → append-only history with a moving 5m breakpoint on the last message (longer TTLs
   first). 1h writes (`CacheCreation.Ephemeral1hInputTokens`) are billed at 2x base input, 5m at 1.25x. Per-turn data
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
   - **Author (Opus 5.5 only, `Agent/Authoring/AuthorAgent`)**: `script {name, goal}` (and the blueprint designer) hand a short
     spec to a SEPARATE Opus context: its own cached reference (Lua cheat-sheet / DSL), the brief + facts the caller pulls,
     up to `Agent:Llm:AuthorMaxTokens` (100K, clamped to 128K) output per call at `AuthorEffort` (medium), streamed above
     `StreamAboveTokens` (16K; same message/usage/stop_reason, prefix caching kept), and a write → lint → revise loop
     (`AuthorMaxVersions` 3) capped at `AuthorMaxUsd` ($3/job: a stream is aborted once its running cost passes it). The
     passing version is saved; the brain gets ONLY the final facts (name/version, purpose, args, lint status, size, $, or
     the blocker), never drafts, thinking or fixed errors (`script op=show` = a ≤600-char excerpt). Brain turns keep
     `MainMaxTokens`; the brain's context limit and compaction are unchanged.
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
- **Gestures** (`Bot/Brainstem/Gestures`, `Agent/Brainstem/GestureClassifier`, `Agent:Gestures`, zero tokens): the bot's own
  chat (`ChatRouter.Said`, before its lines go out) and `/me`/`*bows*` play body language from a static, config-extendable
  rule table: agree → nod, refuse/sorry → shake, hello → wave + look at them, thanks → bow, excitement → crouch-bob/hop;
  addressed player chat → look at the speaker; a yes/no to the bot's own question → a small nod. One per `MinIntervalMs`
  (3 s); per-login/clone switch. Client side (`MinecraftClient.Gestures`, vanilla 0x0C/0x0D, 0x12, 0x13): the head look is
  an overlay on the **sent** look only (physics keeps the driver's yaw, so a walk's heading is untouched), ≥ 20° swings
  (mc-dev EntityTrackerEntry: others see a look change only at ≥ 8/256 turn, every 2 ticks); any dig/place/use cancels the
  overlay and resends the real look first. Social priority: never in a safety reflex/fight, after a danger, near a
  hostile, at low hp, off the ground, or within 2 blocks of a deadly edge; sneak/hop only on an idle body (a job or script
  gets the head-only bow / an arm wave), hop needs ground under all four corners; sneak pulses always end unsneaked.
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
- **Two-phase replies** (`Brainstem:Reply`, `BotAgent.Reply.cs`): the brain's chat streams too (`LlmRequest.OnTextDelta`):
  each completed sentence/line goes out at once through every chat filter, never again at the end; a call that already sent
  a line is never cut for new chat. No brain line `AckAfterMs` (2 s) after addressed chat and no reflex → one Haiku call
  writes a ≤5-word non-committal ack (linted, else silence; memory tier; not when Paused); the brain's next round gets
  `(you already said: "…")` in its newest user message. Safe small talk (`QuickReply.Classify`: greetings, thanks, praise,
  emotes, how-are-you/what-are-you-doing) gets that same single cheap call first: `{needs_brain:false}` = the final reply, no
  Opus call (committed as a small episode); a correction right after ("no, I meant…") escalates and counts a miss.
- **Body lock, not brain lock**: chat never pauses a script; it wakes the brain while the script keeps the body. Body tools
  answer `body busy: script cobble_loop 40%` (pause/replace it, or `do{...,queue:true}` to run after it); chat, memory,
  look/see/status, hive (spawn/assign/recall clones, answer their needs_input), ask_player and script ops stay free. A
  `delegate` drives this body too, so it answers busy as well (a superseding worker would kill the script). `[body: script ...]` rides in each turn
  header. Only danger, a trusted stop/wait and the brain's own `script op=pause|replace|cancel` pause a script.
- **Pre-observation**: chat turns carry `[here]` (≤~250 tokens, uncached tail, built live each turn, deduped against the
  header): landmark, inventory, the speaker's place and facing, nearby builds, void edges — so the brain acts in round 0.
- **Effort**: simple chat turns (a reflex, one clear job, small talk) run at `Brainstem:SimpleTurnEffort` (low) for the
  whole turn; planning turns keep the tier's effort. Debounce: 1.2 s when the line looks complete, else 1.8 s.
  The message cache is per effort/thinking/model variant and found again only within 5 min and ~20 content blocks
  (`Context/CacheWarmth`): a simple turn whose low variant is cold stays on the warm variant once history passes ~6K tokens
  (live 10-27 15:55: a low turn after an 8-round medium turn re-wrote 2 × 38K), and a cold big-history call is never cut
  for a restart. Any call writing > 30K cache tokens logs a WARN with its likely cause (`Telemetry/CacheWriteGuard`).
  Compaction thresholds use estimate × `ContextManager.TokenScale`, calibrated from real prompt tokens (≈1.5× the estimate).
- **Telemetry** (`LatencyTracker`): per chat `reaction_ms`, `first_chat_ms` (ack or first line), `conclusive_ms` (final chat), `first_move_ms`, `first_action_ms`, `rounds_before_action`, cheap-only share and misses,
  speculation adopted/cancelled → `latency-YYYY-MM-DD.jsonl` and the TUI tokens panel's p50 line.
- **Addressing** (`ChatRouter.ReasonFor`, `Chat/Addressing.cs`): name/whisper > group word > obvious player-to-player (calls
  another online human by login or `Chat:PlayerNicknames` vocative: never wakes) > conversation continuity (`ReplyWindowSeconds`
  90 s after the bot talked to the player, its audience inferred from names/nicknames or the last partner, or they talked to it)
  > solo > imperatives about the bot's items/tasks/hive or "you/your" (not for clones). Still unsure, from a regular, near the bot
  or in a thread: ONE tiny Haiku yes/no (`AddressClassifier`, ≤30/h). `Addressing` counts continuity/imperative/classifier hits and
  misses (an ignored line the player then repeats by name). Outgoing guards in `SayAsync`: one food ask per player per 10 min,
  one greeting per player per 3 min, startup chatter ≤2 lines until someone talks to the bot.
- **Background failures always wake** (`GameEvent.MustWake`): a failed detached job/walk/script (limit included)/delegate, a
  failure ahead of a `do{queue}` job, a failing standing order, a clone's failed task and the **stall watchdog**
  (`Bot/Brainstem/StallWatchdog`: a job/script claiming to work with the body unchanged for 45 s → `stalled: <job> idle 45s; last:
  ...`) wake the brain even while a script sleeps, at normal priority (never interrupting the body). Hunger notes never
  interrupt a job either (High only at hp ≤ 6).
- **Other people's losses never interrupt**: a human's void fall or death is Normal + MustWake (one short condolence, the job
  keeps running); another bot's danger/death (`Chat:OtherBots`) is Background only. A quiet turn joined mid-turn by a
  'noticed' event becomes a warning turn (`BotAgent.AfterJoined`: one line public, the rest internal). Speech never folds
  another online account into a regular's nickname (`PlayerNames`: exact accounts win; "robin" for robinchan is unfolded).
- **Standing-order backoff**: a pass that found nothing to do yet ("still growing", "0 chopped") is quiet context, not a failure;
  C# waits 2 → 4 → 8 → 15 min and polls `WorkProbe` (a grown tree, a regenerated gen cell, a mature crop) to resume earlier.
- **Hand-off holds** (`Client/Survival/HandoffReservations`, `Bot/Perception/HandoffIntent`): a chat line either side ("I'll pass
  it to 67", "bread for 67") opens a 3-min intent; pickups during it are held for that target and auto-eat skips them unless
  hp ≤ 4; a hold ends when the item leaves the inventory (delivered) or after 10 min.
- **Hand-overs and spoken promises** (`Bot/Brainstem/HandoverWatch`, zero tokens): an item entity spawning at a player's eye height
  (a toss) or just ahead along their look is attributed to them; the bot's pickups of it, debounced 2 s, post one Normal wake
  "Fox gave you 21 wheat". The bot's own "I'll X when/once/the second Y" lines become pending promises (item from the condition,
  the partner's last line, or the recipe input; 5 min); when the item arrives (hand-over or inventory) the brain wakes with the
  intent: "…; you said you'd make bread when Fox handed it over (now 21 wheat)" (MustWake).
- **Shared chest memory is dated by the last open** (schema v6 `contents_at/contents_by`): `where_is` says "opened by maid_chan
  49s ago, may have changed", never the chest block's sighting. An open writes at once for every hive bot; when it shows items
  memory listed are gone (vs the window's first 0x68, so our own takes never count), `ChestChanges` posts "chest at …: bread x5
  gone since maid_chan opened it 49s ago; Fox may have taken it (holding bread)" and a failed `withdraw` appends it. An empty
  take names what the container held. Eating when full is ok ("already full"); reflex meals are booked "ate (reflex)".
- **Server moves**: a 0x0D more than 8 blocks from the body that doesn't answer a login/respawn raises `ServerMoved`; the brain
  gets "server moved me from A to B (teleport)" plus a visible cause (a recent tp line, the player standing there). A death
  respawn note says where it died. Portal trips keep their own wake.
- **Placement** (`World/Physics/PlaceRules`): plant soil rules (sapling/flowers: dirt/grass/farmland under; reeds; cactus) and
  "nothing solid on any side" are refused up front with the exact reason; a support face hidden from every reachable spot
  is clicked unseen (b1.8.1's server checks distance only, like `BlindUseAim`).
- **Workers stay on the worker model**: `delegate {profile}` may pick a cheaper profile, but an Opus-class profile runs the worker
  on `WorkerModel` unless that profile names `Worker` explicitly; a worker stops after 2 identical failures (numbers ignored).
  Worker-tier calls are booked as worker spend whatever model answered.

## Acquisition planner (Bot/GameTools/AcquisitionPlanner) — jobs get their own tools

Zero tokens, no model call per step. A need (tool kind ≥ tier, or N of an item) resolves cheapest-reliable first: inventory →
known chests (WorldIndexer snapshots) → a hive spare (`AcquisitionSources.RequestHandoff` seam, filled by the hive) → craft from
inventory + chest materials (`Crafter`) → bootstrap (chest logs / a tree the stewardship rule allows, a stored sapling makes the
last trees choppable → planks → sticks → table → wood pick → 3 cobble at the gen → stone pick; same for axe/shovel) → ask a
player (default: stop). Stone first whenever cobble is reachable; a gen loop holding a wood pick upgrades once its cobble allows.
Only trees, generator cells and chest contents are used; protected blocks and the last trees never. Bounded (14 steps, 4 min);
a non-trivial plan is logged and leads the result ("no pick, making one: logs→planks→sticks→table→wood pick→3 cobble→stone pick").
Callers: Lua `ensure` (hidden `acquire` tool, no prompt bytes), `Loadout.FetchAsync` (clone loadouts, gen loop re-tool), `mine`
(the block's tool first, so `do mine` too), skill preconditions (`SkillRegistry.AcquireMissing`). Lint warns on a tool job without `ensure`.
**One resolver** (`Obtainer` → `Crafter` over `ChestStock`): inventory → known chests nearest first (exact variant; never-touch or
"private" chests skipped; the last sapling/dirt stays) → the whole recipe tree planned over inventory + chests (`CraftPlan.Used`),
exactly its raw ingredients withdrawn one stop per chest in batches the inventory holds, intermediates crafted (2x2, a table only
for 3x3), an unnamed variant takes the possible recipe ("slab" = cobble slabs) → the exact shortfall ("need 1 more log for a chest;
none in inventory or the 3 known chests"). `craft`/`do craft`/the worker, `ensure`/`acquire`, builds and clone bodies (own sources per
body adapter) all use it; craft always makes new ones (a worn carried pick never counts); reserved items and carried food aren't ingredients.

## Blueprints: build from a description (Bot/Blueprints)

- **DSL** (`BlueprintParser`, written by the Opus brain tier only): `blueprint name`, `size WxHxD`, optional `origin`, `facing`
  (drawings put the front on the south/bottom row; `facing east` turns the build and every directional meta), `light`, a palette
  (`C=cobblestone D=door:south S=cobblestone_stairs:north W=wool:red H=slab:cobble`), `layer N` / `layer 1-3` rows (`.` air, `_` keep),
  and primitives in order (box, hollow, walls, wall, floor, clear, pillar, door, window, stairs, `roof flat|gable|slope:dir`).
  `BlueprintNormalizer` → `VoxelPlan` (world blocks + forced air). Blocks must be b1.8.1 building blocks (`BuildBlocks`); metas follow
  mc-dev: door lower = placer quarter 0..3 (+4 hinge), upper = lower|8, only on a top face; stairs 0 E/1 W/2 S/3 N; 6 slab types and a
  same-type slab on a slab merges (refused); ladder 2..5 = wall side; chest/furnace front 2..5.
- **Design loop** (`DesignLoop`, `design` brain tool ≈130 tok, policy: only `IsBrainModel` authors): brief (wish + site + constraints:
  materials inv+storage, ground extent to the void, protected builds nearby) → the author (`LlmBlueprintAuthor`: main model, its own
  small cached prompt, never the brain's history) → deterministic checks (`DesignChecks`: bill vs inventory+storage with the route
  (gen/trees/craft/ask), floating, falling sand, void columns, protected/never-break overlap, fluids, obstructions, reachable entrance,
  headroom ≥ 2, light if asked, door halves, torch/ladder walls) → preview (`BlueprintPreview`: ISO+TOP+SIDE contact sheet of the real
  site with the plan overlaid, role `Planned` = magenta `*`, roofs not cut away; cached by plan+site hash) → perception critique
  (`PerceptionCritic`, passing versions only, booked as perception). The author gets ≈100 tokens of evaluation text, the image only if
  it answers `SHOW`. `Bot:Blueprints:MaxIterations` (3) and `MaxUsdPerDesign` ($0.80); the best passing version is kept.
  `design-YYYY-MM-DD.jsonl` = versions, tokens, $ per design.
- **Review**: the summary goes to chat ("small cobble hut 5×5×4, door south, 2 windows: 44 cobblestone, 25 planks; say 'build it' or
  tell me what to change"). `DesignReview` (zero tokens): a trusted player's explicit "build it"/"looks good, build" accepts and starts
  construction; a change line from the requester or a trusted player revises; the consumed line reaches the brain as background only.
  Designs persist (`blueprints`, `blueprint_builds` tables); `design op=build id at` = "another one like the hut" (trusted). The
  supervisor serves `/api/blueprints`, `/api/blueprints/{id}` (bill/eval/DSL/progress) and `/view/blueprint/{id}` (PNG) + a Designs panel.
- **Construction** (`ConstructionExecutor` running the slicer's `BuildPlan`, as a body job via the hidden `build` tool; Lua `build{bp,
  part, of, layer}`, `build{bp, undo=true}` and `blueprint(id)`). The **slicer** (`Bot/Building/BuildPlanner`, zero tokens) turns the
  voxels into steps like a 3D printer: obstructions first, then layer by layer, support-first, nearest by real path cost. Each step has
  a stand, a clicked face and a technique: Normal, Edge (sneaking, bridging out), Hidden (a face the bot can't see, e.g. the floor's
  underside: mc-dev NetServerHandler checks only feet-to-clicked-block distance < 8, no raytrace; the client keeps its 4.5 eye reach)
  or Water. Stands come from `Pathfinder.Explore` (a Dijkstra flood over the real moves: doors, ladders, void rules) from where the
  previous step ends. A stand never intersects the cell and never walls the bot in (a flood after the placement must still reach
  outside the footprint). Facing metas are set by the look yaw (stairs/doors/furnaces read only the yaw); the planner prefers the
  side that faces right. A stuck block gets a scaffold chain, a floor walkway or a **water column**: a source poured beside the edge
  floor block (ItemBucket raytrace: leaning out), which only falls over open space. `SwimSim` (World/Physics, the same per-tick
  policies the client drives) verifies the descent, the hover at depth and the climb-out (0.3 boost) before it is planned; one bucket
  means one column at a time; the source is taken back at the end (an empty bucket takes only a meta-0 source, and the column dries).
  The client's `WaterHold` lets the void guard and the falling alarm stand aside only while inside a continuous column. Whatever
  still can't be planned is named before building (`Infeasible`, in the "build it" reply: "47 placements, 6 scaffold, 1 water
  descent, needs: …; est. 6 min").
  The plan is persisted (`build_plans`: steps with status, columns, gate, ledger). On a resume, done steps are re-verified and the
  rest re-planned from the current state. A deviation re-plans: someone else's block in a planned cell is kept and reported, never
  dug. A missing item doesn't stop the build: its blocks are held out of the plan, the rest is built, and the run pauses resumably.
  Materials come through the acquisition planner: the exact variant (cobblestone slab 44:3), every known chest in batches the
  inventory holds, then crafting, then the gen, and only then a question. **Confirmation gate**: the first 4–5 placements show the
  start edge and main axis, then the build pauses and asks once in landmark terms. There is no default: a timeout stays paused,
  and a restart asks again. A trusted yes continues. "wrong way" / "turn it" / "flip it" / "move 2 east" takes the gate blocks
  down, transforms the plan (`PlanTransform`, about the first gate block) and gates again. "undo" takes down everything the build
  placed. **Anchoring** (`SiteAnchor`) uses what the player referenced: "here" means the requester's feet and facing; "the 8x8
  floor" or "the end of the bridge" means the newest self-placed build near them (its floor, found by morphological opening). The
  footprint snaps to that extent, and a size mismatch is asked in the summary, which names the anchor rather than a nearby landmark.
  Supervisor: `/view/buildplan/{id}` (numbered steps coloured by technique, scaffold dashed, columns, progress, gate) and
  `/api/buildplans/{id}`.
  A finished build leaves the placed-block log and becomes a remembered `build` structure (soft-protected); scaffolding stays own-placed.
- **Bodies**: the executor acts through `IBuildBody` (resolved per call, never a captured client); `IBuildCrew.Bodies` lists them
  (today the bot's own). `VoxelPlan.Partition(n, byLayer)` = disjoint strips (built at once) or layer bands, each with its `ClaimKey.Area`.

## Protection policy (Bot/Protection + ProtectionPolicy/, Client hooks)

One place decides whether the bot may break a block, why not, and how a player unlocks it: `Protection` builds the client's
`IsProtected` (fast; the pathfinder asks), `ExplainProtection` (refusal text + telemetry) and `SoftOverride`.
- **Sources.** Config blocks/boxes (`Bot:ProtectedBlocks/ProtectedRegions`), memory structures (taught or `mark_structure`,
  e.g. the guessed "main house"), the world index (`WorldIndexer.ProtectionKind`: unknown builds, generator walls/fluids, farms,
  tree soil, portals, signs, stations, and placed **floors**: cobble/stone/planks/slabs, one-thick included), other hive bots'
  leases (claims), the client's tree stewardship (`MinTrees`), and players' marks (`ProtectionMarks`, db `protection_marks`).
  Generator dig cells (taught/confirmed) are never soft-protected.
- **Soft** (all of the above): stops only the bot's own choices (materials, clearing space). Yields to (1) blocks any hive bot
  placed itself (`WorldState.Placed`, recorded from the bot's own confirmed 0x0F placements, persisted + shared in db
  `placed_blocks` by `PlacedBlockStore`: pillars, bridges, scaffolding survive restarts and clones can take main's down) and (2)
  a trusted player's explicit permit (`ProtectionCommands`, zero tokens): "break that" with a punch, "you can break it" right
  after a refusal, "you can break the main house"; more than `Bot:Protection:ConfirmAboveBlocks` (32) asks one "say 'yes'"
  line first; permits last `PermitMinutes` (30). Containers open only for an exact permit (punch/coords). Untrusted players
  unlock nothing.
- **Hard**: a "never break X" mark (any player; "don't mine the middle one" marks the middle gen cell) never yields to a
  command; a trusted "you can break X" unmarks it. Hard safety lives in the client's dig vetoes and never yields: void support,
  lava release, and the block another player stands on (leaves included). Mining down the bot's own pillar is allowed one
  1-block step at a time onto solid, non-leaf footing.
- **Trees** (rule 2): a tree is logs with leaves on dirt/grass. Bare log pillars and the bot's own placed logs are never part of
  a tree (`TreeScan`), so stewardship never applies to them. Exception: a remnant next to cells a recent hive chop felled (or
  left standing when it stopped) is our own unfinished tree (`World.ChopLog`, shared by in-process bodies): chop_tree finishes it.
  Replanting uses the tree's own species only ("no birch sapling; plant oak?").
- **Autonomous `mine`** takes resource sources only: generator dig cells, natural tree logs, ores, sand, gravel, clay, leaves,
  natural nether blocks, and own/permitted blocks; never floors, walls, paths. `mine cobblestone/stone` runs the generator loop
  on the nearest known dig cell, else answers `needs_input` (which cells?). Protection filters dig candidates only: doors,
  chests, stations and portals stay findable (`SkillContext.Find`).
- **Refusals** use one format (`ProtectionText`): "X is protected (part of the main house build); say 'you can break it' if
  you want it gone"; a repeat within 10 min says "already asked". The worker stops at the first protected refusal and returns it.
  Every refusal is counted by reason (`ProtectionTelemetry`, log + `protection-YYYY-MM-DD.jsonl`).
- **Spawn protection**: with `Bot:Protection:ServerOp` (default true) a remembered spawn-protection region and its notes are
  dropped at startup; only real server reverts this session re-learn it. High falls onto ground are reported as `HighFall`, not
  "the void". Height work (chop_tree, build-mode goto, building skills) first ensures 4 clutch blocks (`ClutchBlocks`, never dirt).

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

## Vision v2 (Render/ContactSheet + Bot/Perception/Vision) — several angles, a separate perception brain

- **Contact sheet** (`ContactSheet`): one `see` = one image of the SAME focus: TOP (role layer), SIDE cut along the work axis
  (bot→focus line, else the way it faces: looking N or E), ISO, LAYERS (feet layer + the one below; a green dot = air with a
  side/top face to place against: holes, floor gaps, edges) and optionally EYE (the bot or a named player). Every panel draws the
  focus (cyan box/ring), the compass and ruler ticks with the focus coordinates in yellow. TOP and ISO cut away blocks above the
  feet layer that hang over air/fluid down to it (`CutAway`: a roof over a gen hid every gen cell and its water, live 10-27). ≈280-310 tokens for 4 panels (≈300
  for 5) vs ≈500 image + ≈230 legend tokens for the same views as separate pictures (and 4 tool rounds). Focus: x,y,z, a player,
  a saved place, `pointed`/`this`; default the current job's target, else the bot.
- **Role layer** (`RoleClassifier`, `BlockRole`): floor, wall, gen cell (G = MINE HERE), water, lava, void, door, station, player
  build (B), own placed (+), protected ($, outline only: floors under a remembered build still read as floors; never crops, farm
  soil or a hive lease), ripe crop (H = HARVEST) / growing crop (:) from the block meta (wheat 7/7), never-break (X), portal (P), from the protection policy's `Check` verdicts, the world
  index, dig cells (taught/confirmed/geometry; air over a gen cell is never one) and the placed-block log. Tinted cells + letters in
  every panel (every role glyph is drawable), letters in `look map`.
- **Deterministic first** (`VisionRouter`, rules, zero tokens): every `see`/auto-vision question is classified before any model
  call. Gen cells vs floor/walls (+ which fluids touch each cell), gen health ("water isn't touching"), placeable faces, void
  under / distance to the edge, the focus block + role, the portal sheet + what is beside it, and trees (TreeScan: logs, leaves,
  soil), saplings, crops, chests, doors, stations and ores are answered exactly from voxels + roles ("[vision] exact: ...").
  Only visual/relational questions (the middle one, what a build looks like, shapes, pointing, build completeness) reach the
  perception model. A question shape whose model answer had 0 checkable claims is answered from voxels next time.
- **Perception tier** (`Agent/Perception`, `Agent:Llm:Perception {Provider, Model, Endpoint, ApiKey, Deployment, ApiVersion, Mode}`):
  `IPerceptionModel` = anthropic (default `claude-sonnet-5-5`, the shared client and key: Claude to Claude) | azure-openai
  (chat completions, `image_url` data URI, `api-key`, deployment + api-version or the v1 API) | openai (any compatible endpoint,
  bearer) | ollama (`/api/chat` images, `format: json`). Swapping is config only. The model gets sheet + legend + question and must
  answer strict JSON `{look, answer, facts:[{claim, coords[], role?, confidence}]}` ("look" = its counting, inside the JSON),
  capped at `MaxTokens` 250 (was ≈880 tokens of prose + JSON, 7 s).
- **Verification** (`PerceptionVerifier`, zero tokens): every claim's coordinates, block names, roles (incl. "not a gen cell")
  and air/solid expectations are checked against WorldState voxels; a single-target claim one block off is snapped to the
  matching neighbour (floor vs feet misread), the rest dropped; the answer line survives only if every coordinate in it belongs
  to a kept fact and the model made no refuted claim at all. A partly refuted claim keeps only its confirmed coords under the
  voxels' own words ("gen cell @6,64,8"); claims without checked coords are marked "(uncertain)" and keep no coordinates: any x,y,z the voxels didn't
  confirm is scrubbed to "?" from every claim and answer (live 10-27: clone 69 dug 5,64,6, a cell from perception text). Perception never writes memory,
  dig cells or beliefs (a source-scan test guards it): it is advisory text for the next turn only. Deterministic role grounding (gen cells, portal, never-break near the
  focus) is appended. Hallucination rate = refuted / checked claims, logged per call. The brain gets the ≈100-token text, never
  the image. `Mode = direct` (or no model / no key) keeps the old image-to-caller path.
- **Cost/telemetry** (`PerceptionService`, `PerceptionTelemetry`): answers cached by (view hash, question); `PerHour` cap (60);
  booked in the budget ledger as its own `perception` tier (never "worker"); latency/tokens/$/hallucinations per call in
  `perception-YYYY-MM-DD.jsonl`. Non-Anthropic models are priced by `InputPerMTok/OutputPerMTok` (Ollama $0).
- **Auto-vision** (`AutoVision`, `Perception:Auto`): spatial job steps (gen mining, place/dig, build/repair skills, "this
  one"/"the middle one" after a punch-point) start ONE background look per step; the brain's next turn gets it through
  `TurnPerception` (never waits), the worker's next round through `WorkerAgent.RoundNote` (waits ≤ `WorkerWaitMs`). A spot whose
  view hash didn't change since the last answer is skipped. Rolling guard: when more than 40 % of the last 10 checked claims were
  refuted, auto-vision goes deterministic-only (WARN; explicit `see` still asks the model, and recovers it). Builds/repairs end with a before/after check ("[vision check] floor
  patched, 1 gap at x,y,z"); a build that changed nothing is not checked.
- **Hive vision** (main = the hive mind sees anywhere): `see focus=` coords, a landmark/saved place in any dimension ("mob
  chamber", "cobble gen", "main house"), a clone ("67", "at 67", "what 1337 sees": `view` IPC → the clone flushes its world
  memory to the shared db and answers with its pose; the sheet adds an EYE panel from that pose), or `hive` (deterministic
  overview: every bot's position, job, vitals, claims, dangers; the ≈150-300-token top-down picture with any `views` or in
  direct mode). Remote spots render from `RegionWorld`: main's live columns + the shared `world_chunks` (all dimensions, every
  hive bot writes them; `seen_by` says whose eyes; clones flush every 15 s), and the text/legend carry staleness per region
  ("mob chamber seen by 67 40 min ago; E side seen by 69 12 s ago"). Positions, jobs and claims are data (`IHiveVision`,
  Hive `HiveVisionSource`); the perception model only answers visual questions about the rendered spot.
- **Eval** (`Perception/Vision/Eval/VisionEval`, `tools/VisionAB sheets|perception|livegen`): today's failures as cases (floor vs gen cell,
  faces in a hole at 7,62,9, edge distance, the middle one, the portal on the void nether platform from
  `tests/fixtures/portal-trip`, the roofed dual gen from `tests/fixtures/vision-eval/dual-gen.txt`) × arms (Opus reading the
  sheet, Sonnet perception, Haiku perception, router+Sonnet). `livegen` rebuilds a live spot from a db snapshot (+ its WAL) and
  renders the sheet as the bot did. Reported: accuracy before/after
  verification, tokens, brain tokens, $ per question, hallucinations. Offline in the tests (recorded/fake answers); live is opt-in,
  ≤20 calls and ≤$0.50.

## Bodies (Bot/Bodies) — one brain, many bodies, steps 1-2

`BotHost.Create` = `CreateBrain` (runtime, shared stores, every tool registered once against two `BodyRoutingGameClient`s:
each body's claim-aware `Game` view and its plain `Adapter` view) → `BodyFactory.AttachJobs` (bridge + hunger latch, script
lane) → `FinishTools` (see, do) → `BodyFactory.AttachBehaviour` (here, brainstem, gestures, watchdog, standing orders,
hand-overs). A router acts on `BodyScope.Current` (AsyncLocal, pushed explicitly by each body's ActivityManager step, the
worker's tool calls and the script lane's thread/actions), else `BodyRegistry.Focus`; a body tool inside an activity with no
scope logs a WARN. Type checks unwrap it (`GameClients.Resolve/Claimed/Unclaimed`). `GameEvent.Body`, body-keyed EventQueue
dedupe and the telemetry `body` field are in place (null = brain-level); `BotAgent.ActivitiesFor(body)` interrupts only that
body. Design and later steps: `docs/design/one-brain-many-bodies.md`.

**Step 2, `Bodies:Mode=inproc`** (default `hive`: the clone processes below, unchanged). `App/Hosting/InProcBodies` replaces
MainHive: it hosts the claim board and is the registry's `IBodyHost`. `BodyRegistry.Spawn` checks the hive's SlotPolicy
against main's tab list, spaces logins by `LaunchGapSeconds`, refuses an account someone else is on (tab list, or a "logged
in from another location" kick → `Occupied` for `ExternalLoginCooldownMinutes`) and a saved void login spot until a trusted
player says they'll /tp it. Each clone runs under its own `BodyRunner` (connect with backoff, any exception = a crash that
restarts only that body, `MaxCrashes` in `CrashWindowMinutes` retire it). `YieldTick` (10 s) logs out the body furthest from
done for an offline regular; `DespawnAsync` pauses the job (order held, lane paused), runs DepositProcedure with main as
the in-process hand-off receiver, re-checks the inventory, logs out, strands leftovers in HiveStore; shutdown despawns all
in parallel before main stops. `BodyFactory.CreateClone`: own ActivityManager (events tagged `Body`), StandingOrders (+ an
idle resume timer), script lane, StallWatchdog, HandoverWatch, HungerLatch, a clone `GameBridge` (no state header, chat
dropped except whispers to it, which post to main's router tagged with the body; its adapter never sends chat), a
contributor `WorldIndexer` (containers, block changes, gen regen forwarded to main's indexer), a `WorldMemory` pumped by the
one `WorldMemoryFlusher`, and the shared Protection/DigCells/marks/PlacedBlockStore. The void hold (ported from CloneHost)
asks once on main's account, pauses the job and resumes it on landing. `BodyVisionSource` serves hive vision and
`BodyBotSource` the TUI/supervisor panels from the registry. The `hive` tool keeps its schema: spawn = spawn the body, then
its script on its lane or `delegate` on its slot. Thread safety: ToolCallGuard failures are keyed by body, PlacedBlockStore
flush/sync is locked, chunk rows are written 16 per transaction, A* runs through `Client.PathfindGate` (2 at once), workstation
concurrent GC with a 2.5 GB `GCHeapHardLimit` (runtimeconfig), and `MinecraftClient.TicksBehind` is logged per body.

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
- **Assignment fit** (`Clone/CloneFitness`, `Supervisor/AssignFailureMemory`): an unnamed spawn scores every free clone (offline
  idle or online without a pending task) on hp, food, tools/armor for the task's intent (fight/mine/chop/...) and distance to
  main; hp/food persist in `hive_inventory` across main restarts. A clone that can't safely do it is skipped, and when none
  fits the result says why and the fix ("67 hp 7, food 0 ... needs food"). A failed result is remembered by (clone, intent)
  until that clone's hp/food/inventory or day/night changes, or a player says try again / brings what was missing; meanwhile
  the same intent is refused for it (named or unnamed) with the reason.
- **Failure handling**: clone crash/kill → claims + grants released at once, last inventory snapshot marked *stranded*, quiet
  event to main, restart with exponential backoff (resuming its unfinished, uncancelled task with its loadout and re-granted
  leases), retired after `MaxCrashes` in `CrashWindowMinutes`. Spawns are serialized (parallel tool calls never share a slot), launches are spaced by `LaunchGapSeconds` (4.5 s, the server's login throttle), a config-error exit (2) retires the slot at once, and a task given to a clone being recalled (same turn, either order) relaunches it with that task
  and a superseded launch never takes over a slot's link. A task's loadout (pickaxe/axe/food from its text) is fetched from
  storage before the brain starts, then from the hive: main tosses a spare it carries (`lend`, keeping the one in hand;
  `CloneHost.RequestHandoffAsync`, also the acquisition planner's hive seam); what's still missing is reported to main with
  any live clone holding a spare. Main's task is the clone's standing order (`CloneTaskOrders`): a spawn with a task holds
  stored orders until it lands and replaces them; old orders resume only on a spawn without one. Main's chat to a clone
  ("69, ...") is relayed over IPC (`ChatRelayGuard`: reassignment → task, else note), since clones ignore hive chat. Main gone → each clone
  deposits only if quick and safe (`ParentLossDepositSeconds`), records leftovers as stranded, logs out. Next spawn of a
  stranded clone deposits first.
- **Logout gate**: every planned logout (task done, recall, budget, making room, main SIGTERM) deposits the ENTIRE inventory
  (WorldIndexer chests with free-slot estimates → hand-off to main by tossing at its feet (main stands still, job paused, from offer to confirmation), confirmed by its collect events →
  place a chest, labelled as a shared-memory waypoint) and logs out only when the inventory window is verified empty (or the
  deposit timeout hits → stranded). SIGTERM: main recalls clones first (`ShutdownRecallSeconds`, under systemd's 90 s).
- **Shared memory**: one SQLite file (WAL, busy_timeout, the per-process AgentDb gate = single writer, IMMEDIATE txs). Hive
  tables (`hive_inventory`, `hive_events`) and a `clone_id` column on docs/chat_log/notes/observations/waypoints/structures/
  routines filled by per-connection TEMP triggers. `PRAGMA data_version` polling reloads memory + vector cache (RAPTOR tree
  debounced); RAPTOR maintenance runs only in main.
- **Claim board** (main, `Claims/ClaimBoard.cs`): leases with TTLs on blocks, areas, chests, furnaces, tasks. Contract
  `MineClaw.World.Claims.IClaimBoard` (`IsFree` local/fast, `TryClaimAsync`, `ReleaseAsync`); clones use a pushed mirror.
  BotHost wires it into `IsProtected` (claimed blocks are skipped) and `ClaimAwareGameClient` (chest/furnace/dig/place leases).
  Leases coordinate the hive, they are not ownership: a "claimed" verdict reads "X is busy: another hive bot is working on that
  part (a hive claim, not protection); take another part" (no " is protected", no unlock phrase). One gen cell per bot
  (`ClaimAwareGameClient.GenCell`: a nested gen run digs the held cell; an unanswered claim is released, never orphaned); "no
  free gen cell: held by 1337 ×2" names the holders (`IClaimBoard.HolderOf`). Shared fields: `harvest_wheat` cuts the field
  (`FarmField`, connected crops+farmland) into 3-row strips and leases one at a time nearest first (or `part=i/n` bands).
- **Chat arbiter**: one deterministic owner per player message in every process — named clone > thread lease (the bot that asked the player something or is watching their demo owns their replies until resolved/timeout; `thread` IPC) > sticky partner > main; a bare clone number addresses it only at the start of a line/sentence or next to an address word ("69 bring me…", "hey 69", "maid 69", "69 maid", "also 67, ..."), never as a mention/quantity/coordinate ("get 67 cobble", "with 67?", "6,67,8"); a clone as the object of a request ("have clone 69 harvest") addresses main, and an address to an offline clone goes to main; a line naming several bots goes to each of them with main coordinating; ownership covers a player's *conversation*: an unnamed spawn of the request the clone talking with that player is on (or any spawn while it is asking them about it) is refused without `hive op=handover`, while additional clones for the same player's new or parallel request (a spar needs two) go ahead and clones already on that job get a `note` with the newcomer's task; a clone's task is *pending* in main's turn hint until its result event arrives, and main's speech guard drops claims about pending results;
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

## Hive supervisor viewer (App/Supervisor) — the TUI, graphical and read-only

`Supervisor:Enabled` makes the main process serve a GET-only HTTP viewer on `Supervisor:Bind:Port` (default 8765; reach it
with `tools/HiveSupervisor`, an SSH tunnel through alice). No game login, no commands, no chat, no memory writes. `/api/bots`
(tiles from `VisionHub.HiveBots` + claim leases), `/view/{bot}?mode=` (the exact `see` composition, `VisionHub.SheetRequest`,
rendered by the viewer's own renderer; clone poses over the existing `view` IPC, at most every 2.5 s per clone), `/view/hive`,
`/camera` (a free first-person camera over `RegionWorld`: main's live columns + the shared `world_chunks`, any dimension, with
staleness), `/api/status` (the TUI's `IBotTelemetrySource` snapshots: usage vs caps per tier, thoughts/tool calls, chat; the
supervisor's slots and leases) and `/api/events` (dangers, kicks, deaths, hive notices). CPU cap: one below-normal render
thread, ≥120 ms apart, a bounded queue (503 when full), single-threaded eye raycasts, ~1 s render cache. Optional shared-secret
token (header or `?token=`), never logged.

**Someone else on a clone's account** (a player's viewer). b1.8.1 kicks the older session with "You logged in from another
location", so the hive never fights a login: (a) an offline slot whose name is on main's tab list (0xC9) or spawned as a player
is skipped before launch ("slot 1337 is in use by a player's viewer", told once); (b) a clone kicked that way sends `occupied`,
stops its client's auto-reconnect and exits 76; main records its items as stranded, does not restart it, leaves the slot alone
for `Hive:ExternalLoginCooldownMinutes` (10) and tells the brain (MustWake when its task was unfinished).
