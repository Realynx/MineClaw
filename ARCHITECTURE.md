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
     default). Its tool set is only cheap single-shot tools (status, inventory, look, where_is, goto/follow/stop, memory,
     identity, self, ask_player/ask_stand/watch_demo, run_routine) plus `delegate` and `ask_memory`. Its context never
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

## Hive (MineClaw.Hive) — clones as supervised processes

`App → Hive → Bot`. Up to `Hive:MaxClones` clones (`maid_chan_67/69/420/1337`), each a **separate OS process**: the same App
binary as `--clone <name> --parent-pipe <pipe>` (headless, own login, same three-tier brain, own personality shard via
`AgentOptions.Hive` scope, own `usage-<clone>.json` and `logs/hive/<clone>/`). The main process is the **supervisor**; a clone
connects its pipe before opening anything and exits 75 without one (never unsupervised).

- **IPC**: one named pipe per clone (`System.IO.Pipes`; Unix sockets on Linux), 4-byte LE length + snake_case JSON frames
  `{t,id,re,d,err}` with request/response (`Ipc/HiveMessages.cs` lists every type: hello/welcome, hb, task/progress/result,
  recall/bye, claim/release/claims, roster/chat_ok/inject, spend_auth/spend, snap/usage/cmd, inv/death/handoff).
  Heartbeats both ways; pipe closed or silent = that side is dead.
- **Failure handling**: clone crash/kill → claims + grants released at once, last inventory snapshot marked *stranded*, quiet
  event to main, restart with exponential backoff, retired after `MaxCrashes` in `CrashWindowMinutes`. Main gone → each clone
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
- **Chat arbiter**: one deterministic owner per player message in every process — named clone > thread lease (the bot that asked the player something or is watching their demo owns their replies until resolved/timeout; `thread` IPC) > sticky partner > main; a clone that owns a player's request keeps it (main hands it over only with `hive op=handover`); a clone's task is *pending* in main's turn hint until its result event arrives, and main's speech guard drops claims about pending results;
  hive bots' chat, /me, joins/leaves are internal. Main pushes live roster + sticky map.
- **Budget**: Agent:Budget caps are shared by the hive; main's ledger adds clone spend to main's tracker, grants clones spend in
  chunks (optional per-clone sub-caps; 0/unset = inherit main's hard caps), refuses at `RecallAtFraction` (default 1.0 = the cap), and recalls clones (furthest from finishing first) when the
  mode drops. Slots: a spawn must leave one free slot per offline regular; if the server fills up, the clone furthest from
  finishing logs out.
