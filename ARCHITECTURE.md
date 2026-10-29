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
            └── MineClaw.Data  game data: b1.8.1 tables + 1.12.2 (Versioning/Release1122*), static facades over GameData.Current (no deps)
```

`tests/MineClaw.Tests` (xUnit) covers all layers.

**Multi-version** (`docs/design/multi-version.md`; target 1.12.2 / protocol 340 next to b1.8.1): one process speaks one version,
resolved once by the App before any client exists (`Bot:Server:Version` auto | b1.8.1 | 1.12.2, `--server-version`; auto =
`Protocol/Versioning/ServerPing`: 1.7+ status JSON, else the legacy 0xFE `motd§online§max`; server down → last detected in
`<data>/server-version.txt`, else b1.8.1; clones get main's id on their command line). It is injected as one
`Client/Versioning/GameVersionProfile` (`ClientOptions.Profile`): `IProtocolAdapter`/`IGameConnection` (the client connects and
logs in through it; the wire is translated to the canonical `Protocol.Packets` model), `IPhysicsRules`, `ICombatRules`,
`IHungerRules`, `IGameData`. P1: `GameVersionProfiles.Java1122` = `Protocol/Release1122` (a translating connection: VarInt
frames + zlib, login/keep-alive/teleport confirm inside, JSON chat → legacy text via the jar's `en_us.lang`, paletted chunk
sections → the neutral `ChunkSectionsMessage`; P2: windows with click types and the confirm flow, dig, place with hand +
cursor, use item/entity, signs, item entities and players; `Release1122ServerConnection` is the fake servers' side of the same
wire) + `Release1122PhysicsRules` (step 0.6) + `Release1122GameData` (minecraft-data names, ids, stack sizes, the 1.12.2 dig
rule) + `InventoryModel.Release1122` (off hand at 45, 1.12.2 `slotClick` prediction); its world is 256 high (`WorldState(height:)`, `ChunkColumn` stacks a second
128 half, so b1.8.1 indices are unchanged). P4: every entity kind and its typed metadata translated per class onto the canonical
indices (`Data/EntityMeta`: b1.8.1's 0/16/17 plus 32+ facts such as health, baby, hand states), exact positions, effects,
attributes, passengers; `Profile.Entities` (`World/Versioning/IEntityRules`: 1.12.2 mob profiles, stamped on each spawn as
`Entity.Profile`), `Release1122CombatRules` (the attack charge the `CombatEngine` waits for, crits, sweeps it never aims into a
bystander, the off-hand shield and the axe, attack-before-swing) and `Release1122HungerRules` + `FoodStats1122`. No `if (version)`
in game code, differences live behind the seams. P6: the world kind (skyblock vs a normal world) is `Data/Versioning/WorldMode`
(facts `HasVoidBelow`, `GeneratorStone`, `ScarceTerrain`, `Islands`; decided by the db's active knowledge packs, default skyblock),
`Blocks` is a facade over `GameData.Current` too, light/spawn block rules and chunk biomes come through `IPhysicsRules`/`WorldState`.

## Project ownership / contracts

| Project | Owns | Public contract (others code against this) |
|---|---|---|
| Protocol | `IPacket`, every packet class (both directions), `MinecraftReader/Writer` (big-endian, UTF-16 "string16"), `PacketRegistry`, `ItemStack` wire form, entity metadata, chunk payload (still compressed) | `PacketRegistry.Read(MinecraftReader, PacketDirection)` / `packet.Write(writer)` |
| Data | `BlockInfo`, `ItemInfo`, `Blocks`, `Items`, `Recipes`, `Smelting`, `Drops`, `Tools` (dig-time math); `IGameData` per version (`Beta181GameData`, `Release1122GameData`: 1.12.2 recipes, FurnaceRecipes + burn times, drop overrides, ItemFood, tool tiers) | `Blocks.Get(id)`, `Blocks.ByName(name)`, `Items.Get(id)`, `Items.ByName(name)`, `Recipes.*`, `Smelting.*`, `Drops.*`, `Tools.*`; all but `Blocks` answer from `GameData.Current` (the process's version, set by `VersionSetup`; `GameData.Override` in tests; each client action runs under its profile's data) |
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
   In direct mode the sheet (≈310 image tokens, 6 panels of 3D renders; the legacy `see {view}` = one 384×288 picture, ≈150 tokens;
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
     spec to a SEPARATE Opus context: its own cached reference (the generated JS SDK reference + examples / DSL), the brief + facts the caller pulls,
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
  speculation adopted/cancelled → `latency-YYYY-MM-DD.jsonl` and the TUI tokens panel's p50 line. Each record also has
  `category` (command|question|banter|greeting|help, `Brainstem/ChatIntent`), `reaction_by` (gaze|gesture|line|move|action),
  `full_ms`, `instant`; `LatencyReport` = p50/p95 per kind (also rebuilt from old text logs: `LatencyReport.FromLog`).
- **Reactive character** (docs/design/reactive-character.md; act first, think in parallel, correct if needed): (1) **instant
  lines**: `Agent/Character/LineBank` holds in-character lines a cheap batched call (`LinePrefetcher`, memory tier = wording,
  ≤12/h, idle/long jobs) wrote for the likely moments of the current context (greet/bye per player, thanks, praise, tease,
  status, hurt, come, follow, stop, onit, sorry), tagged with the context signature (activity, players, time of day, mood, hp)
  and expired when it changes; phrase memory (every line the bot says) blocks repeats for 30 min. The brainstem says one after
  a typing delay (`HumanTiming`, ≤0.9 s) on a reflex, a speculative start or small talk (`CharacterVoice.SayInstant`);
  small talk it answered is final with no model call (`BotAgent.InstantFinal`), otherwise the brain's turn gets `[already
  said] …` + `[already doing] … for 1.2s` and amends; the instant line counts as the ack (no quick-ack call) and a brain line
  repeating it is suppressed. (2) **Typed game signals** (`Bot/Perception/GameSignals`, `Scripting/Events/GameEventCatalog`):
  player.punchBlock (b1.8.1: swing + look raycast, debounced; 1.12.2: break animation) / swing / hurt / died / pickup / toss /
  equip / sneak / near / leave / join / quit / say{intent} / lookAt, block.changed{by}, item.dropped, mob.near, me.hurt,
  me.lowHp, time.dusk/dawn, weather; `EventFilter` (player, block, held class, item, pos/near/region, cooldown, once...);
  scripts bind with `on(event, filter, fn)` / `react(...)` / machine `on`. (3) **Standing bindings and learned reactions**
  (`script{op:bind,name,goal,params:{on,...filter,for,minutes,kind}}`, `op=bindings`, `op=unbind`; ops documented only in the
  cached `ScriptPrompts.BindLine`, no schema bytes): the trigger lives on the script's row (`RoutineScript.Binding`), the
  `Bot/Brainstem/BindingRunner` starts the script the instant the event fires (1-2 ms packet → first action) with the
  payload as args; never during a safety reflex or over a script/commanded job; world-acting bindings (dig/place/items) only
  from trusted players, reactions harmless only; reconcile verdicts (kept/amended/corrected) demote a reaction corrected
  more than kept. (4) The **`character` SDK module** (look, face, gesture, follow/stopFollow, line, typing, speak, mood,
  relation, recall, remember, heard, intent; `react`), via `Scripting/Character/ICharacterPort` (`Bot/GameTools/CharacterPort`).
- **Addressing** (`ChatRouter.ReasonFor`, `Chat/Addressing.cs`): name/whisper > group word > obvious player-to-player (calls
  another online human by login or `Chat:PlayerNicknames` vocative: never wakes) > conversation continuity (`ReplyWindowSeconds`
  90 s after the bot talked to the player, its audience inferred from names/nicknames or the last partner, or they talked to it)
  > solo > imperatives about the bot's items/tasks/hive or "you/your" (not for clones). **Foreign-bot threads**: a line naming a
  `Chat:OtherBots` bot, using its learned command prefix ("c " once 2 of a player's "c ..." lines got robinchan answers within 5 s;
  `CommandPrefixesOf`) or answered by it puts the player in that bot's thread for `ReplyWindowSeconds`; unnamed follow-ups stay
  there (`continues with robinchan`; no waiter takes them; Background note "kiwi is instructing robinchan: ...") unless the
  player addressed us since, looks at us, or it's a help call (`AddressingText.IsHelpCall`). Our plain talk doesn't take it back;
  an **open request** does (recency): our last line to them asked/told them something (`IsRequestLine`, `OpenRequestSeconds`
  120 s, closed by their reply) or an ask_player waiter is pending, newer than the bot's thread, and their last line wasn't to
  the bot → `AnswerToUs` (also beats "unclear, another bot around"). Known prefixes (learned, a trusted player's "when I say c,
  I'm talking to robinchan" in chat/rules/claims, or the bot's own "say c help") persist in db `command_prefixes`
  (`CommandPrefixStore`) and route even while that bot is offline. Still unsure, from a regular, near the bot
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
- **A turn says at most its in-persona lines**: after the `say` tool spoke, or once a turn no player typed in (a hand-over, a
  death notice) said its line, the rest of the brain's text is internal. `MetaSpeech` also drops recaps (its own speech acts
  "I thanked him in chat", "I picked the build back up since…", a fight log) and third-person reports about the addressee.
- **Standing rules** (`Agent/Rules`, db `directives`): a trusted player's instruction, preference or rule saved with
  `remember_note` is a directive (who, when, scope: design / activity / place / general), never a claim to verify. A newer
  rule on the same main activity and time-of-day condition, or one lifting it ("you can build at night now"), replaces it;
  `forget rule#N` ends it. Shown as text only where it applies: `[standing rules: …]` in the turn header (task, standing
  order, events, a paused build) and the design tool's resume/build results ("…; it's night now"). One coded check, generic:
  a rule that limits a build to a time of day (`DirectiveParser.OnlyWhen`: "the drop tower only in daytime, at night mine
  cobble" = day) is consulted between build steps (`DirectiveStore.TimeHold` → `ConstructionExecutor.Hold`, lanes too): from
  dusk (11500) the build stops resumably ("rule#6 (Fox): …; it's getting dark: stopped until day"), comes down off the build
  and walks back to where it started. Startup migrates old trusted instruction claims (idempotent).
- **Standing-order backoff**: a pass that found nothing to do yet ("still growing", "0 chopped") is quiet context, not a failure;
  C# waits 2 → 4 → 8 → 15 min and polls `WorkProbe` (a grown tree, a regenerated gen cell, a mature crop) to resume earlier.
  Every known chest full (`ChestStore`'s "all N known chests full (…); 141 cobblestone in bag; craft+place a chest (have 300
  logs) or keep carrying") backs off the same way (the probe can't cut it short), counts no failure, and wakes the brain once.
  A live script on the lane holds the order (`StandingOrders.BodyBusy`; "already running" is never a failed pass); a restart
  lifts a failure pause the old process persisted; `Prepare` (`ScriptBridge.CheckOrder`) re-resolves a script order to its
  newest version by name after the Lua→JS migration, and a call that can't run wakes the brain once (Normal) with why.
- **Safety first** (live 10-28 16:15–16:22: a restart woke main on the drop tower's platform at night, zombies, "retreating
  inward" had nowhere to go, dead; respawned with no food it walked west and died again; "Robin, a zombie is attacking you" was
  "not said"). (1) The **no-path danger reflex** (`Client/Survival/Shelter`, `MinecraftClient.Shelter`): when a retreat has no
  way home (no retreat cell, or no walk down to the spawn's level: `World/Pathing/WayHome`) and hp is low or ≥ 2 hostiles are
  near, the body boxes its own 1×2 column in from where it stands (4 sides at feet and head, the roof via a cap; a support beside
  its floor block over an edge; cobble, planks, dirt last, never saplings; never digs, never moves), eats, waits until hp ≥ 16 and
  the mobs are gone or it's day (10 min cap), then opens the side toward home. The combat engine asks it when a fight turns into
  a retreat (`CombatEngine.ShouldShelter`; no reflex fights while `Sheltering`), the survival tick when idle. One Critical line:
  "boxed myself in at …: 7 zombies, hp 4; waiting it out". No blocks: fights on as before. (2) **Spawn safety**: at login and
  respawn an exposed body (night or a hostile within 16, and above home level) walks down, else boxes in; `BotAgent.SafetyFirst`
  awaits it (≤ 45 s) before the standing order resumes and the startup wake, whose line it leads. (3) **Night + no food**
  (`BotAgent.BodySafety` → `GameBridge.BodySafety`): a new brain body job that leaves home ground (a goto beyond 12 of spawn,
  mining, chopping, following…) at night or with hostiles near and no safe food on hand is refused with one line ("night + no
  food: getting food first (…); not starting goto now") and the food reflex fetches food; eating, chests, the gen and walks home
  pass. (4) **Safety warnings** (`Chat/SafetyWarnings`): a warning about a threat to someone (a mob on them, a creeper, lava, the
  void, a death) overrides the startup-chatter cap and a quiet turn's "nobody is waiting", once per threat per 30 s.
- **Startup wake + idle watchdog** (`BotAgent.Startup`, main only; `ClonePolicy` turns both off): once per process start, one
  Normal wake "restarted: standing order X: resumed; <paused build line, its rules, it's night now>; rules: …" (the paused
  build rides in it via `AddPendingWork`, not a Background notice). Idle with nothing running or waking for
  `Agent:Idle:WatchdogMinutes` (10) while the order is stuck (not merely backing off/snoozed) or paused work waits → one
  Normal wake; each repeat without the bot getting busy doubles the wait (cap `WatchdogMaxMinutes`, 80).
- **Food reflex** (`GameBridge.FeedReflex` over `Bot/GameTools/FoodSupply`, zero tokens): a "hungry/starving, no food" danger
  first surveys the food resolver; when food can be had it fetches/crafts/cooks and eats with no brain turn (Background notes
  "…; 30 wheat in chest 11,68,-5 → can craft 10 bread: eating it now (reflex)", then "ate (reflex): …"). Only when nothing can be
  had, or the attempt fails, does the danger go through the hunger latch to the brain, carrying the shortfall line.
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

## Scripting (MineClaw.Scripting) — JavaScript jobs, the SDK, reactive machines

A saved script is a job that runs while the brain sleeps (zero tokens per step). The brain never writes code: its whole view
is `script {name|goal, params?, op}` (≈ 60 tokens of schema) and one cached line (`ScriptPrompts.AuthorLine`, ≤ 40 tokens; the
old 420-token Lua cheat-sheet is gone from its prefix). `goal` commissions the Opus author (above), whose cached system prompt is
the rules + the SDK reference generated from the code + six worked examples (≈ 5,200 tokens, `ScriptPrompts.AuthorSystem`).
`name` runs a saved script; `params` tune it for this run; `op=save name params` keeps the tunables without new code.

- **Engine: JavaScript on Jint 4.17** (pure managed, strict mode, ES2023; `Runtime/Js/JsRuntime`). **Execution model: host calls
  are synchronous on the script's own thread** — `mine("cobblestone", 16)` blocks that thread only and returns `{ok, msg, n}`;
  the call is the safe point where pauses, interrupts, handlers and machine transitions happen, exactly as in the Lua engine.
  The body runs inside an `async` function, so `await` is accepted (harmless: promise jobs drain synchronously) and top-level
  `return` works. *Why not await-only promises or generators:* Jint resumes promise continuations only when its event loop is
  pumped on the engine's thread (Task interop is still experimental), so "async" host calls would still be synchronous underneath
  while making every SDK helper and callback `async` and every call `await`ed — a forgotten `await` silently yields a pending
  Promise (`r.ok` undefined), the commonest LLM JS bug. Generators can't yield across a callback (`forEach`, handlers, `retry(fn)`),
  so every helper would need `function*`/`yield*`. Both cost output tokens on every call. Blocking calls keep the proven pause /
  resume / interrupt / checkpoint semantics with zero ceremony; parallelism lives in lanes (one thread + engine per body), not in
  one engine's event loop.
- **Sandbox** (`JsRuntime`, `JsBudget`): no CLR access, no `require`/`import`/`eval`/`Function`, recursion (`MaxCallDepth`),
  array size and regex-time limits, `PromiseTimeout` 2 s; the session's budget hook runs every 16 statements (stop requests,
  statement limit, busy loop without an action, memory allocated between host calls). Host signals (stop, limits, unwinds) are CLR
  exceptions: no `try/catch` in the script sees them. Globals are read-only (`def`), `me`/`inv`/`params()`/`args` frozen.
- **One core, two runtimes** (`ScriptSession` + `Runtime/IScriptRuntime`): the session owns control (thread, safe points,
  pause/resume, limits, events, checkpoints, timing, outcome) and a neutral API (JSON nodes in/out, `ScriptSession.Api.cs`);
  `ArgMapper` maps any call to tool JSON in one place (positional in schema order, one named object or trailing options, `n` =
  count, positions as `"x,y,z"`/`[x,y,z]`/`{x,y,z}`). Every game tool, alias (`go`, `gen`, `take`, `chop`, ...) and catalog skill is
  a global in snake_case and camelCase (`chop_tree`/`chopTree`); the whole Lua API is there with the same names (`waitfor` →
  `waitFor`, both work).
- **The SDK** (`Sdk/js/NN-module.js`, embedded, parsed once per process; each `def` has a doc comment and `SdkReference.Build()`
  turns them into the author's reference — a test pins that every global is documented): **core** (ok, expect, fail, log/console,
  prog, wait, waitFor (a kind, or a condition fn), say, emote, ask, wake, cp, on, skill, params, carry, args) · **world** (me, inv,
  block, near, find, item, hostile, where, night, time, players, pos, dist) · **flow** (retry, upto, until, quota, forEach, seq, sel,
  timeout, room, ensure) · **machine** (machine, after, every, when, signal) · **policies** (use, policy, keepFed, fleeVoidEdges,
  defendIfAttacked, depositWhenFull, replaceToolWhenBroken, pauseAtNight) · **jobs** = the composable primitives (gather, stash,
  handTo, makeRoom, patrol; below) · **crew** (body, bodies, spawn, parallel, crew.get/set/inc/send/recv/barrier/lock/unlock) · **character** (the persona: character.look/face/gesture/follow/line/speak/mood/relation/recall/remember/heard/intent, react(event|intent, filter, fn); docs/design/reactive-character.md). C# where it is hot or owns
  semantics (signals, machine runner, timeouts, crew lanes), JS where it reads clearer (helpers, policies, jobs).
- **Primitives, not strategies** (user feedback 10-28: "harvest farm, chop trees... too specific"; the game moves to a default 1.12.2
  world with an empty script library). Every primitive returns `{ok, n, msg}` like a game function and never fails the script
  (expect(r) if it must): `find(query, {r, filter, limit})` → targets nearest first (blocks with meta, mobs, players, drops, else a
  remembered place; host `ScriptSession.FindAll`) · `item(name)` → this version's name/id/craftable (`ScriptHost.Data`, the
  profile's `IGameData`, read-only) · `gather(what, n, {from, more, stash, chest, near, tool, r})`: inventory → chests (the
  named/nearest/every `where()` chest) → craft (only if the version has a recipe) → its source in this world (`Sdk/ItemSources`:
  the harvest skill's crop, a tree, the blocks that drop it — natural first, crafted blocks and farmland never — the mobs that
  drop it; names through `IGameData`, drop rules from `Drops` until a version brings its own) · 'gen' (a generator cell) only when
  named, never assumed; `stash` stores as the bag fills and at the end, every chest full → `{full, need}` · `quota(n, fn)` /
  `forEach(targets, fn, {order:'nearest', budget})` / `patrol(points | {at, r}, fn, {rounds, until})` · `stash(items |
  'all-but-tools', {to})`, `makeRoom(n, {to, drop})` (room() = makeRoom or fail), `handTo(player, item, n)`; `stash`/
  `depositWhenFull`/`makeRoom` all go through the `deposit` tool's one path (`Bot/GameTools/ChestStore`: the named chest, then known
  chests with room, never-touch/"private" skipped). Void edges stay the safety policies' and reflexes' job (any world). Version
  awareness: `ensure`'s tier lists keep only names `item()` knows; no primitive names a b1.8.1 id.
- **The strategies moved out of the SDK** into `ScriptPrompts.Examples` (the author's worked examples, also the sdk-js pack's
  example chunks): gen mining = ensure + `gather('cobblestone', n, {from:'gen', stash})`; chop + replant = `quota` over `forEach`
  of grown spots / `find('log')`, planting a carried sapling where the tool reports "not replanted"; farm rounds = `gather('wheat',
  64, {from:'harvest', stash})`; guarding = `patrol` + `keepFed`/`defendIfAttacked` + `on('mob.near')`; plus the machine and crew
  examples (`Js/PrimitiveTests` ExampleScriptTests: the gen example makes the same 8/8/4 calls and deposit as mineGen did on its
  fixture; the chop example skips ungrown spots and replants). **Compat** (`Sdk/js/99-compat.js`, header `module: compat
  (deprecated)`): store, restock, mineGen, harvestFarm, chopTrees, deliver, waitForDay, guardArea with their old behaviour, so
  saved b1.8.1 scripts keep running; left out of `SdkReference`, the author prompt and the sdk-js pack (`SdkModules.Current`);
  each doc comment says what replaces it (`SdkModules.Compat`). **Migration**: lint warns `compat` on every call ("mineGen() is a
  deprecated compat helper: use gather('cobblestone', ...)"; your own function of that name is exempt), the warning rides on run
  results; the author's check (`ScriptBridge.CheckDraft`) rejects a draft with a compat call and sends the warnings back, and
  improving a saved compat script tells the author what replaces each helper (`CompatNote` in `AuthorFacts`), so the next
  version is compat-free while the old one stays as history. A fresh db (reset-memory) starts with an empty library and a
  sdk-js pack that never mentions a compat helper. Once no saved active version calls one, delete `99-compat.js`.
- **Reactive core** (`Sdk/MachineRunner`, `Sdk/SignalSet`): `machine({initial, states:{s:{enter, tick, exit, on, final}}, on,
  guards, bb, policies, thresholds})` runs enter → tick… → exit per state. Events come from the bot's event stream (danger and its
  topics lowhp/void/died, chat and `'chat:<text>'`, join) and from **signals**, edge-triggered conditions polled by the session's
  watchdog and at safe points: built-ins (hungry, invFull, toolBroke[:kind], hurt, night, day), timers (`after(ms)`, `every(ticks)`)
  and `when(() => cond)`. An event the current state listens for cuts the running interruptible action and unwinds the tick at the
  next safe point (`ScriptUnwind`); a guard that says no leaves a danger to the default policy (pause + wake). Repeating timers
  never cut (they'd restart the action every period) and coalesce; a cut `wait()` resumes for the time left. Transitions asked for
  while a state exits are deferred. Deterministic: the same events in the same order give the same transitions. State, history
  (≤ 20) and the blackboard go into checkpoint `@id` on every transition (`m.state`, `m.history`, `m.bb`, `m.go()`), and the status
  line shows `state mine (4 moves)`. **Policies** are `signal + on` pairs attachable to any script (`use(...)`) or machine
  (`policies:[...]`): they handle the event between actions and the cut action carries on (fight-then-resume).
- **Params** (`params({minFood: 14})`, once at the top): defaults < saved (`RoutineScript.Params`, `op=save name params`) < this
  run's `params`/`args`; frozen; recorded per run with the timings in `ScriptVersion.ParamRuns` (≤ 20) for later self-tuning, and in
  the run log line / `ScriptRunRecord`. The catalog shows `tuned {...}`.
- **Checkpoints**: `cp(label)` and machine transitions save their snapshot in memory and in the saved script's row
  (`RoutineScript.Checkpoints`, "lane/label"), so `resume_from` after a restart or respawn (`CloneScriptRunner` writes the label
  into the standing order) restores `carry` — and a machine's state + blackboard — instead of starting over.
- **Crews** (`Crew/ICrewPort`, `ICrewBoard`): `parallel([[body, 'role', args], ...], {keepGoing})` / `spawn()` start *lanes*: the
  same script reloaded on another body, calling a top-level role function; `main(args)` is the coordinator. First failure cancels
  the siblings unless keepGoing; results come back in order. `body("67")` reads vitals and (in-process) runs one action on an
  idle body. In-process bodies: `Bot/Bodies/BodyCrewPort` starts the lane on that body's own ScriptBridge as its activity (its
  slot, its interrupts, "body busy" for brain tools); hive mode: `Hive/Crew/HiveCrewPort` starts it as a hive script spawn
  (`__role`/`__crew` in the args, `after: sleep`) and reads the hive result; the board is the shared db (`DbCrewBoard`: tables
  `crew_board`, `crew_inbox`, `crew_locks`, ≤ 4 KB per crew), which clone processes reach too (`LaneOnlyCrewPort`).
- **Lint and dry run before anything is saved or run** (`Lint/JsLint` on the Acornima AST; `ScriptLint` dispatches by language):
  errors = syntax, unknown global (with the closest name), forbidden construct (eval/Function/require/import/timers/host internals/
  debugger), constant-true loop that never acts or waits, a required tool arg a literal call can't fill, assignment to an SDK
  global, a machine target/initial that isn't a state, crew top level doing work, an unknown role, the same body twice; warnings =
  unchecked action results in loops, long script without cp/machine, tool job without ensure, say in a loop or a lane, acting loop
  that never ends, unknown machine event. The author's check adds `ScriptDryRun` (the bot's real action names, a fake world where
  every action succeeds, starter kit, time ×400, ≤ 2.5 s): runtime ReferenceErrors/TypeErrors/binding misuse go back to the author,
  never to a live run. Feedback is the same compact `lint errors (fix and resubmit): L3 …` text.
- **Telemetry**: per run: actions, active time, items gained, effective params, per-action timing (`ActionTiming`: calls, fails,
  total time — "gen 12x 31.2s"), language, body, machine state and transitions (log line, `ScriptRunRecord`, `ParamRuns`).
- **Live debugger** (`ScriptSession.Debug.cs`, `Runtime/Js/JsRuntime.Debug.cs`; the Explorer's Live Hive panel, read-only):
  `ScriptSession.DebugSnapshot(code)` (any thread, never blocks the script) → `ScriptDebugSnapshot`: current line/column (live
  while it steps), call stack, action in flight with args and timings per action kind, params/args, checkpoint, pause state and
  reason, machine state + history + blackboard, crew lanes, locals of the innermost script frame, logs. Jint's DebugMode stays
  off (it allocates per statement); instead the line is the engine's last-statement pointer read off-thread (`UnsafeAccessor`,
  no hook), the stack is the text Jint already builds at each host call (`CurrentLine`), locals come from the execution-context
  stack through Jint's public `DebugScopes` (reflection over its internal ctor; data properties only, no getters run) — on the
  script thread, or off-thread only while it is parked in a host call or a pause (`_blocked`, fenced by a lock on exit).
  Cost: nobody watching = one volatile read per 16 statements (the budget hook) and one small object per action (measured
  ≈ 0-1 % on a 4 M-statement benchmark, `ScriptDebugTests`; `Scripting:DebugProbe=false` removes even that); a snapshot request
  takes a 3 s lease (`WatchLease`) during which the script thread samples stack/locals/blackboard at ≤ 5 Hz (≈ 5 %). Clones answer
  over the `script_dbg` IPC request. Pause/resume from the panel is a TODO (an admin write; `ScriptDebugSnapshot.Controls`).
- **Inline interrupt patches** (`ScriptPatching.cs`, `ScriptSession.Patch.cs`, `Agent/Authoring/PatchAgent`, `Bot/GameTools/ScriptPatcher`,
  `Scripting:Patches`, default on): an interrupt a few lines of JS can fix no longer costs a full brain turn. Baseline (10-27 logs, 36
  interrupts that woke the brain): first call back p50 4.9 s / p95 11.2 s, turn over p50 11.0 s / p95 46.8 s, job back p50 22.9 s / p95
  148 s, 2.6 calls and $0.029 per interrupt (mean in 7.6 / cache read 52K / write 2.2K / out 399 tokens). **Order**, on the script thread
  at the safe point the interrupt reaches: (1) safety reflexes stay first and deterministic (client tick; a patch's actions wait while
  `IGameClient.SafetyEngaged` and a different danger aborts the patch); (2) the script's own `onInterrupt(kind, fn)` (SDK core); (3) a
  **learned handler** (no LLM call; any tier may run one); (4) the **patch turn**: ONE Opus 5.5 call (`purpose=patch`, effort low,
  `Agent:Llm:PatchMaxTokens` 600, a ≈ 900-token cached system part = rules + one-screen SDK sheet + this bot's game functions, the one
  `patch{js, then, note?}` tool that exists only in this request; refused off-Opus like the author, so other tiers only run saved
  handlers); (5) escalate = the full brain turn, exactly the old path. **Interrupts**: a danger pause of the own body (lowhp/void/died/
  danger; someone else's danger never), `fail()` whose cause looks fixable (full, missing, no tool, broke, blocked/unreachable, occupied,
  a mob, food: `ScriptPatcher.FixableFailure`), `ask('brain', q)` (the patch's returned string is the answer), the stall watchdog on a
  running script (`stuck`), a trusted player's chat that tweaks the job (`LooksLikeTweak`: names a param, or a tweak word + a number).
  The bridge's gate holds the brain's wake while a patch may fix it (danger: `Wakes=false`, the escalation's "script paused" notice
  wakes it with the danger as context; stall/chat: the held event is re-posted on escalation). **The patch prompt** (≤ 6.5K chars,
  ≈ 1.9K tokens uncached): interrupt, script name/version, current line ±5 (marked), machine state + blackboard, params, checkpoint,
  carry, last failure, body (hp/food/pos/inventory/free/hostiles/night/safety), last logs, the previous patch. **Execution**
  (`RunGuarded`): the js is `async (ev, P, m, bb) => { js }` evaluated in the paused session's own Jint engine (globals, carry, SDK);
  `P` is the live params object (read-only to the script: an assignment is a TypeError as before; writable in a patch, `ParamsJson`
  follows), `m`/`bb` the running machine. Budget `PatchMaxActions` 8, `PatchMaxSeconds` 45 (actions cut at the deadline),
  `PatchMaxStatements` 50K, `PatchMaxPerRun` 8, linted first (syntax/forbidden/unknown names); a budget hit, a lint/runtime error (with
  its patch line) or `fail()` in the patch escalates with that line (`PatchNote`, shown in the state: "patch escalated: …"). `then`:
  resume (the cut action re-runs; after `fail()` the script goes on at the next line), restart_step (a machine re-enters its state via
  `ScriptUnwind`; else a fresh engine fast-forwards to the last `cp`, carry and patched params kept; ≤ 3 per run), stop. The debugger
  snapshot carries `Patching` ("planning: lowhp" / "running learned handler for failed"), `PatchNote`, `Patches`; the Live Hive panel
  shows "patching: …". **Learning** (learned, not coded): a resume/restart is verified — the same signature again within
  `PatchVerifySeconds` (90) = failed; progress past the window or the run finishing = worked. A fresh patch that worked is saved on its
  script (`RoutineScript.Handlers`, ≤ 16: kind, signature = kind | normalized text (numbers/positions/players stripped) | normalized
  source line, js, then, note, author, version, successes/failures); a learned handler counts successes, and `HandlerDemoteAfter` (2)
  failures in a row demote it, so the patch turn runs again and a working fix replaces it. The Explorer's Scripts page lists handlers
  under their script (↳ kind · situation · stats · then; demoted faded) and in a Handlers tab with the js. **Telemetry**
  (`Agent/Telemetry/InterruptTracker`, `interrupts-YYYY-MM-DD.jsonl`): per interrupt path declared|learned|patch|escalated|full,
  then, ms (interrupt → the script goes on; a full turn's resume closes at its turn's end), calls, tokens and $ of every LLM call while
  it was open (`UsageTracker.Recorded`); `Summary().Line()` gives p50/p95 per path. Benchmark (`PatchTurnTests`, fake Opus at 250 ms a
  call): full turn ≥ 2 calls vs the patch path 1 call and a shorter interrupt → resume; a learned handler 0 calls.
- **Mined from the saved library** (data/alice-snap, 16 unique scripts, 133 lines): bounded repeat wrapper 16/16; deposit to a
  chest + expect 14/16; hard-coded chest/cell coordinates 14/16 (→ params); gen mining loop 13/16 (9 near-duplicates differing
  only in the cell → `mineGen` + params); ensure a pickaxe 12/16; room(n) 12/16; inventory-full check 12/16; cp mine/store
  phases 8/16; retry(3, gen) 7/16; "mined nothing" guard 5/16; low-hp retreat 2/16; busy-cell fallback, fight at night, chop +
  replant with a spot list, deliver to a player, wake when done 1/16 each. Wheat harvest, wait-for-day and fight-then-resume
  weren't in the library yet but are the brain's common asks. (These first became SDK helpers; since 10-28 they are examples
  composed from the primitives, above.)
- **Speed** (hand-written equivalents of five representative jobs, before = Lua with the old prelude, after = JS + SDK;
  `Js/SpeedComparisonTests`; tokens = chars/3.5; after = the primitives since 10-28): gen mining loop 19 → 5 lines (238 → 81
  output tokens), wheat harvest 11 → 8 (150 → 102), deposit run 7 → 7 (81 → 70), guard the gen at night 20 → 12 (236 → 185), 2-body
  gen mining 25 → 11 (248 → 124): 82 → 43 lines (−48 %), 953 → 562 output tokens (−41 %; the strategy helpers gave −46 %, the
  general primitives cost ≈ 50 tokens more over the five). Every after-
  script passes the author loop in one call (lint + dry run 30-630 ms). The brain's prefix is ≈ 420 tokens lighter on every call,
  and a brain that used to inline-write the Lua now emits ≈ 30 tokens (`script{name, goal}`). Engine start per run ≈ 1 ms (SDK
  pre-parsed), the same as Lua.
- **Lua: migration and removal.** Saved versions carry their language (`ScriptVersion.Lang`, null = legacy Lua). At startup the
  main process runs `LuaMigration` once in the background (`Scripting:MigrateLua`, default true): every saved script whose active
  version is Lua, most used first, goes to the Opus author with the SDK reference (`ScriptPrompts.MigrationBrief`), must pass the
  same lint + dry run, and is saved as the next version with the Lua kept as history (rollback works; auto-revert compares their
  reliability); caps `MigrateMaxUsdPerScript` $0.25 and `MigrateMaxUsd` $5; each script gets a verdict
  (`RoutineScript.Migration`), so it never repeats. Until then `Scripting:LegacyLua` (default true) runs unmigrated Lua versions on
  the legacy runtime (`Runtime/Lua/*`, LuaCSharp 0.5.7); new code is JS only (inline Lua is refused with `ScriptPrompts.JsOnly`
  unless `AcceptLuaCode`). **Removal steps**: (1) after a week with every row's `Migration` = "ok…" (or the script archived), set
  `Scripting:LegacyLua=false` on alice and watch for "legacy Lua and not migrated" refusals; (2) delete `Runtime/Lua/`
  (`LuaRuntime`, `LuaPrelude`), `Lint/LuaLint.cs`, the Lua branch of `ScriptRuntimes`/`ScriptLint`, `ScriptLanguage.Lua`/
  `ScriptLanguages.Detect` and `ScriptOptions.LegacyLua/AcceptLuaCode`; (3) drop the `LuaCSharp` package reference; (4) port or
  delete the legacy engine tests that use `new Rig()` (EngineTests, LiveSession2Tests); (5) keep `ScriptVersion.Lang`, the old
  Lua versions stay readable history in the rows.

## Acquisition planner (Bot/GameTools/AcquisitionPlanner) — jobs get their own tools

Zero tokens, no model call per step. A need (tool kind ≥ tier, or N of an item) resolves cheapest-reliable first: inventory →
known chests (WorldIndexer snapshots) → a hive spare (`AcquisitionSources.RequestHandoff` seam, filled by the hive) → craft from
inventory + chest materials (`Crafter`) → bootstrap (chest logs / a tree the stewardship rule allows, a stored sapling makes the
last trees choppable → planks → sticks → table → wood pick → 3 cobble at the gen → stone pick; same for axe/shovel) → ask a
player (default: stop). Stone first whenever cobble is reachable; a gen loop holding a wood pick upgrades once its cobble allows.
Only trees, generator cells and chest contents are used; protected blocks and the last trees never. Bounded (14 steps, 4 min);
a non-trivial plan is logged and leads the result ("no pick, making one: logs→planks→sticks→table→wood pick→3 cobble→stone pick").
Callers: the script SDK's `ensure` (hidden `acquire` tool, no prompt bytes), `Loadout.FetchAsync` (clone loadouts, gen loop re-tool), `mine`
(the block's tool first, so `do mine` too), skill preconditions (`SkillRegistry.AcquireMissing`). Lint warns on a tool job without `ensure`.
**One resolver** (`Obtainer` → `Crafter` over `ChestStock`): inventory → known chests nearest first (exact variant; never-touch or
"private" chests skipped; the last sapling/dirt stays) → the whole recipe tree planned over inventory + chests (`CraftPlan.Used`),
exactly its raw ingredients withdrawn one stop per chest in batches the inventory holds, intermediates crafted (2x2, a table only
for 3x3), an unnamed variant takes the recipe possible without smelting ("slab" = cobble slabs), else it asks which (stone_slab 44:0 smelts only when named) → the exact shortfall ("need 1 more log for a chest;
none in inventory or the 3 known chests"). `craft`/`do craft`/the worker, `ensure`/`acquire`, builds and clone bodies (own sources per
body adapter) all use it; craft always makes new ones (a worn carried pick never counts); reserved items and carried food aren't ingredients.
**Food** (`FoodSupply`, over the same resolver): ready food carried (not reserved, not held for a hand-off) → chest food → bread from
3 wheat / mushroom stew (bowl + both mushrooms) from inventory + chests → raw meat cooked at a furnace in reach (chest fuel too),
else safe raw meat eaten raw → the shortfall ("no food anywhere: …; only 2 wheat in chest … (bread needs 3); 17 seeds to plant").
The `eat` tool (so scripts' eat and `keepFed`), `ensure('food')`, skills' eat, the gen loop and the food reflex all go through it.
**Versions:** the resolver, the planner and food read the static tables, so on 1.12.2 they plan with its recipes (wildcard plank/coal
keys, planks that keep the log's wood type), FurnaceRecipes, burn times, drops and food values (docs/design/multi-version.md 5.3).

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
  headroom ≥ 2, light if asked, door halves, torch/ladder walls) → preview (`BlueprintPreview`: ISO (SW) + ORBIT (NE) + TOP 3D sheet of the real
  site with the plan overlaid, role `Planned` = magenta `*`, roofs not cut away; cached by plan+site hash) → perception critique
  (`PerceptionCritic`, passing versions only, booked as perception). The author gets ≈100 tokens of evaluation text, the image only if
  it answers `SHOW`. `Bot:Blueprints:MaxIterations` (3) and `MaxUsdPerDesign` ($0.80); the best passing version is kept.
  `design-YYYY-MM-DD.jsonl` = versions, tokens, $ per design.
- **Review**: the summary goes to chat ("small cobble hut 5×5×4, door south, 2 windows: 44 cobblestone, 25 planks; say 'build it' or
  tell me what to change"). `DesignReview` (zero tokens): a trusted player's explicit "build it"/"looks good, build" accepts and starts
  construction; a change line from the requester or a trusted player revises; the consumed line reaches the brain as background only.
  Designs persist (`blueprints`, `blueprint_builds` tables); `design op=build id at` = "another one like the hut" (trusted). The
  supervisor serves `/api/blueprints`, `/api/blueprints/{id}` (bill/eval/DSL/progress) and `/view/blueprint/{id}` (PNG) + a Designs panel.
- **Capture** ("this is my house, memorize it": `design capture{name, at?|corners?}`, any speaker, read-only; `StructureCapture` +
  `DesignCapture`). Region: two corners ("x,y,z x,y,z"), a remembered structure's box (`at` = its name), or a flood fill from the
  speaker's feet / a pointed block: 26-connected over building blocks above the floor they stand on (the block under their feet;
  else the cell under the seed's run of building blocks), never terrain (grass, dirt, ores, fluids, plants, leaves, a living
  tree's logs, a generator's walls), then the floor layer under that footprint (a thick island's stone isn't a floor); capped
  at 32×32×32 per axis (the parser's `MaxSide` is 32; the author is still asked for ≤ 24) and `MaxBlocks`. Unknown (unloaded)
  cells over 5% of the box refuse; stale (remembered) ones are reported. The DSL is drawn as it stands (`facing south`, `origin`
  = the min corner, raw metas `wooden_door:3`, `oak_stairs:1`, `torch:2`; upper door halves derived; '.' = interior air between
  a column's lowest and highest captured block, '_' elsewhere; no 'L' symbol, so no row reads as `light`), stand-ins named (lit
  furnace → furnace, double slab → its full block, redstone torch → torch), unbuildables skipped and named (beds, signs, rails),
  chest contents never; a door on the captured floor gets its doorstep (the ground in front, as dirt) since a copy stands one
  above its site. Verified by parsing it back: every captured cell's id and meta, or the capture is refused. Saved with status
  `captured` (accepted by its capturer; the worker's `build{bp}` refuses it), the preview and the bill from nothing; one line:
  "captured 'Fox's house' as design #11: 7×6×5, 138 blocks (palette: …); bill to rebuild: …". The remembered structure (kind
  build, owner, "captured as design #11" in its note) soft-protects it as the owner's build; blocks a bot placed there leave
  the placed log. "my house"/"Fox's house" resolves to the design (`design show/build{id:"my house"}`). Rebuild = `Reuse` (a
  trusted go, the gate, crew lanes, climbing, the Obtainer as for any design), never overlapping the original's box unless
  `at:"in place"`. `BuildBlocks.Rotate` turns torches (1..4), trapdoors and pumpkins too, so "turn it" keeps them on their walls.
- **Construction** (`ConstructionExecutor` running the slicer's `BuildPlan`, as a body job via the hidden `build` tool; scripts call `build({bp,
  part, of, layer})`, `build({bp, undo: true})` and `blueprint(id)`). The **slicer** (`Bot/Building/BuildPlanner`, zero tokens) turns the
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
  descent, needs: …; est. 6 min"). Reasons point at the root cells: a cell resting on one that can't be placed is "waiting on supports;
  rests on x,y,z" (a final pass re-points "no support" cells whose neighbour leads to a real root). **Climbing**
  (`BuildPlanner.Climb.cs`), when a supported cell is above every stand's reach (stands are searched from 6 below: a y63 floor
  reaches the y70 layer), in order: the design's own placed blocks (their tops are flood stands: its stairs, step-ups); a
  **pillar** (`Technique.Pillar`: jump-place scaffold into the feet cell, `IBuildBody.PillarAsync` → the client's
  `PillarPlaceAsync`), the lowest that reaches it (or walks onto the design's floor from its top), every level void-free on all
  eight sides (the jump isn't sneak-guarded: never a 1×1 over the void); a **scaffold stair** (`Technique.Stair`: rises and
  landings, a support block where nothing holds the tread, never cutting an older flight's walk space, verified walkable both
  ways: the pathfinder's drop overshoot guard means a turn needs footing beyond the step below), the only climb in a crew plan.
  Stands before use: a stand on scaffold depends on the step that put it there, each tread on the one below. A body up on a
  pillar that no longer helps digs it back first; a stand on my own pillar counts as a way out (wall-in check). Scaffold comes
  down top-first keeping a way down: a pillar dug from on top (a 1-block drop onto the next block: the client's own-pillar rule),
  other scaffold from a stand that can still walk back to the start level. Scaffold blocks are counted in the plan ("13 scaffold
  (13 to climb: 11 pillar, 2 stair)") and fetched with the cobblestone. "A way down" always means back to the plan's
  **Home** (`BuildPlan.Home`: the first slicing's start, carried by every resume/re-plan; a plan without one sliced from up on the
  build uses the design's bottom layer): live 10-28 16:15 a resume / crew finale sliced on the drop tower's platform made the
  platform the start level, so the climb's stair came down from above and left the body up there. **Never strand a body**
  (`ExitRoute`, `ConstructionExecutor.LeaveSafelyAsync`): every run that ends (done, gate, materials, a rule's hold; not a cancel)
  and every finished lane with the body above Home and no walk down comes down first: its own pillar dug from on top, else a
  1-wide stair placed down (the next tread one down and one out against any solid face, a support under my own floor when none,
  two long where the follower's overshoot guard needs footing past it; never over or beside the void, never in a design block's
  cell, every step a verified walk); the stair is the plan's recorded `Exit` (scaffold again for a later resume). "came down off
  the build: placed 12 as a stair" or "stranded: no way down from …" rides in the build result. Over the void the scaffold-distance rule is unchanged.
  Only when no climb is possible is a cell "no stand: above my reach (I can't climb: every pillar or stair spot near it is
  beside the void | no room … | climbing is off)" (`Bot:Blueprints:Climb`). Design #4 (16×16×16 drop tower on Fox's 8×8 floor)
  replays complete: 689 placed, pillars inside the shaft. My own scaffold still up in a design air cell (the shaft's pillar) is
  the climb, never an obstruction to clear first (live 10-28 14:55 each re-plan dug it out from under the body: stranded in the
  shaft, "49 no stand", "won't dig the block I'm standing on"); an obstruction the body stands on is cleared after the placements.
  A pillar that walks onto the design's floor is scored by everything reachable up there (one climb up the shaft, then the
  platform grows ring by ring from its own cells), and a stand beside a drop onto solid ground is a sneak-edge stand
  (`Technique.Edge`: the client's EdgeGuard sneaks and cancels unverified jumps); beside the void the rules are unchanged. Every build/lane result, the `[task]` text, `design show` ("can't place N: …")
  and the restart notice carry them grouped in one line (`BuildSkips`): "placed 4, 685 missing — 673 waiting on supports e.g.
  -27,72,13; 12 no stand: above my reach (I can't climb) e.g. -27,71,13; 0 protected" (live 10-28: the drop tower said only
  "placed 0, 689 missing" for hours: its 16-high walls were out of reach from the floor and everything else rested on them).
  When most skips hang over the void (past the scaffold limit, or no climb because every pillar/stair spot is beside it), the
  line ends with the exact foundation from the plan's footprint (`BuildSkips.FloorHint`, `BuildPlan.Foundation`): "lay a floor:
  expand_island area=-33,7,-18,22 y=63 (192 cells, 192 cobble)" — a suggestion the brain may run, then resume. `design show`/`list`
  name the footprint in world coordinates ("footprint x -33..-18, z 7..22, y 64..79": live 10-28 the brain read it off the image 2
  off). **expand_island area=x0,z0,x1,z1 y?** (`Skills/AreaFill`) fills the rectangle in BFS order from the existing floor, each
  block against a solid neighbour (walk via the pathfinder, sneak/lean with the EdgeGuard, hostile/survival vetoes per block),
  stops with why when no cell is safely reachable, resumes on what is still empty, gets cobble via the registry's `Obtain` hook
  (the Obtainer) and flags a design footprint it covers only partly ("x -33..-32 of design #4 still uncovered").
  The plan is persisted (`build_plans`: steps with status, columns, gate, ledger). On a resume, done steps are re-verified and the
  rest re-planned from the current state. A stale plan (another design version, or `Format` older than `BuildPlan.CurrentFormat`,
  e.g. one saved before crew lanes) is planned again on load (`BuildPlan.Resume`) keeping its placed-block ledger, scaffold/water
  still up, gate (an approved build never asks again) and gate corrections. A deviation re-plans: someone else's block in a planned cell is kept and reported, never
  dug. A missing item doesn't stop the build: its blocks are held out of the plan, the rest is built, and the run pauses resumably.
  Materials come through the acquisition planner: the exact variant (cobblestone slab 44:3), every known chest in batches the
  inventory holds, then crafting, then the gen, and only then a question. **Confirmation gate**: the first 4–5 placements show the
  start edge and main axis, then the build pauses and asks once in landmark terms. There is no default: a timeout stays paused,
  and a restart asks again. A trusted yes continues. "wrong way" / "turn it" / "flip it" / "move 2 east" takes the gate blocks
  down, transforms the plan (`PlanTransform`, about the first gate block) and gates again. "undo" takes down everything the build
  placed (its own recorded placements only, never a protected block or a generator's walls/fluids, refunded). The brain's
  `design undo{id}` does the same (the plan reset), `design delete{id}` (a trusted player's word) also drops every plan and
  lane and marks the design deleted (hidden from lists, DSL kept); the worker's `build{bp, undo|delete:true}`. A gate "no"
  names the blocks standing ("gate blocks at …; undo with design undo{id=9}") and holds the build for that player
  (`Gate.HeldBy`): never resumed or offered as "no second go needed" until a trusted go; a copy at a new site cancels the
  original's unfinished build and names its blocks still standing. **Anchoring** (`SiteAnchor`) uses what the player referenced: "here" means the requester's feet and facing; "the 8x8
  floor" or "the end of the bridge" means the newest self-placed build near them (its floor, found by morphological opening). The
  footprint snaps to that extent, and a size mismatch is asked in the summary, which names the anchor rather than a nearby landmark.
  Supervisor: `/view/buildplan/{id}` (numbered steps coloured by technique, scaffold dashed, columns, progress, gate) and
  `/api/buildplans/{id}`.
  A finished build leaves the placed-block log and becomes a remembered `build` structure (soft-protected); scaffolding stays own-placed.
- **Bodies / crews** (`Bot/Building/CrewSlicer`, `Bot/Blueprints/CrewExecutor`): the slicer is parametric in 1..N builder bodies.
  The design is sliced once in dependency order from the first body's feet (N=1 = exactly the one-body plan), then the unfinished
  steps are cut into one `PlanLane` per body: the footprint's (x,z) columns swept along the long axis and cut into contiguous
  strips balanced by step count, lanes given to the bodies that finish soonest (travel + work), every water-column step in the
  bucket holder's lane, the shared scaffold stair in one lane (the strip it starts in; a crew climbs only by stairs), scaffold
  removal left to the finale. A lane waits only for the other lanes' steps it needs (the support it clicks, a cell cleared/placed
  first, its stand's floor, a stand another lane must leave first, the stair's treads below an elevated stand; a block in the
  stair's walk space waits for the earlier steps up there). The plan carries the one-body vs
  crew estimate ("with 3 bodies (main, 67, 69) ≈2 min instead of 6 alone": design summary, "build it" reply, `design show`).
  Run: one body places the gate blocks and asks (the build's single gate; the others wait); then the lanes run in parallel, each
  doing its first ready step, stands reserved (`StandBoard` + an `Area` lease on the claim board) so no two bodies share a cell,
  each fetching its own share of the bill (one body at the chests at a time); progress persists per lane (`build_lanes` rows +
  the plan) and a restart with the same crew resumes each lane. A body that dies/leaves/gets stuck: wait for its respawn when
  that finishes sooner, else re-slice the rest over the others; a body joining/leaving re-slices too. Finale: whoever finished last
  runs the one-body executor on the persisted plan (stragglers, scaffold down, water back, done). Crew = `design build{id,
  bodies:["67","69"]|crew:N}`, default every online idle body above `SplitAboveBlocks` (48). In-process: `RegistryCrew`; hive:
  `IBuildJobs` (`App/Hosting/HiveBuildJobs`): main slices for itself + idle clones, each clone lane is a hive job running the fixed
  JS lane script `build({bp, lane})` (`BuildJobs.LaneScript`), main runs its lane, reads theirs back, and does the finale. Per-lane status in `design show`
  ("build: lane 67 40/120, lane 69 38/118"). The `build{bp, lane?}` tool (worker tier; scripts also pass part/of/layer/undo/
  bodies/crew by name) is a thin wrapper over `BlueprintService.BuildAsync`/`BuildLaneAsync`; a delegated "continue building
  design #4" gets its `build{bp:"4"}` line, an unapproved one is refused back to the brain (`design build{id}` after a trusted go).
  The old `VoxelPlan.Partition(n, byLayer)` strips (`build{bp, part, of}`) remain for scripts.

## Light model and mob spawning (World/Light + Skills/Lighting) — task #29

Live 10-28 16:45: "we'll need to light up the floor of the build" (design #4's floor swarming, the main island's dark west
side the night before). Ground truth: mc-dev 218a2a71 World.b/d/e, Chunk.initLighting/g/i, Block.q/s, SpawnerCreature,
EntityMonster/EntityCreature/EntityLiving.
- **`WorldState.Light` (`LightEngine`)**: the 0x33 light nibbles are used as sent; the height map (Chunk.heightMap) is derived per
  column. Every block change runs Chunk.a's height-map update (`Chunk.g`: sky rewritten in the column, World.g with mc-dev's
  local-coordinate quirk kept, Chunk.d over the 4 neighbours + itself) then World.p = World.b(SKY) + World.b(BLOCK): the
  32768-entry queue BFS (darken within Manhattan 17, re-brighten), each step losing max(1, opacity). Tables (`LightTables`) are
  Block.q/s exactly: torch 14, lit furnace 13, portal 11, glowstone/lava/fire 15; leaves 1, water/ice 3, slabs/stairs/farmland
  255 (constructor f(255)), glass/chests/pistons 0. A region without light arrays (fixtures) is lit like initLighting + Chunk.i
  + emitters. One deviation: `areChunksLoaded(…,17)` is "the cell's column is loaded". Cost (pinned by `LightEngineTests`):
  ≈0.4 ms and ≈5k cell visits per torch change, ≈0.3 ms per floor block over the void; never a per-tick recompute.
- **Queries**: `LightAt(pos)` (sky, block), `EffectiveLight(pos, time?)` = getLightLevel with World.k (`SkylightSubtracted`: the
  MathHelper cos table, rain ×5/16; 0 by day, 11 at night; the slab/farmland/wood+cobble stairs neighbour rule), and
  `CanHostileSpawn(pos, time?)` (`SpawnRules`, null time = after dark): a normal-cube floor (material.j() && b(): never slab,
  glass, leaves, ice, TNT, stairs, farmland; chests and double slabs count), no normal cube in the cell or above, no liquid,
  light ≤ 7, ≥ 24 from the world spawn (0x06), and a zombie (0.6×1.8) or spider (1.4×0.9) box clear of collision boxes and
  liquid (World.c's negative-corner quirk kept). Thunder (World.k 10) isn't sent to the client: not modelled.
- **`spawn_map{area|design|around, y?, exclude?}`** (skill, zero tokens) and **`light_area{area|design|around, y?, exclude?,
  level?, max?, report?}`** (skill; the brain's `do light{area|design,report?}` = that skill, ≈+10 tokens, the short name and `eat`/`equip_armor`
  without `{}` pay for it: 2,948/2,950 in the strictest budget test, HiveScriptSpawnTests).
  `design=N` = its footprint ± `pad` 4 at feet levels base−2..base+2 (`Skills.DesignRegion` via `SkillRegistry.Design` ←
  `BlueprintService.RegionOf`, gate corrections applied); a design target never lights its own **decks** (cells on its blocks
  above the base layer: design #4's platform y77) unless `exclude=none`; `exclude` also takes "design N" (whole box), "N deck"
  or a box. The DSL has no role lines: its comments that name a spawning floor are quoted as the roles. The planner
  (`LightPlan`) is a set cover over each candidate torch's own light field (bucket BFS from 14, −max(1, opacity)): two greedy
  passes (max coverage; hardest cell first), pruned, the smaller wins. Candidates: empty cells a torch attaches to (floor
  first), never over the void, never excluded; spots the bot can stand near rank first. Torches through the Obtainer (craft
  from coal/charcoal + sticks; logs smelted to charcoal when needed); placed in a nearest-neighbour tour, then the real light
  is re-checked and re-planned (3 rounds). Result: "lit design #4 floor (x -37..-14, z 3..26, y 62..66): placed 11 torches; 0
  spawnable cells left (was 415); excluded design #4 decks y… (DSL: dark spawning platform with shaft hole; …)".
- **Overlay**: role `DarkSpawn` ('!', pink) = air a mob can spawn in even by day (the shade under a build, an unlit room): tinted
  in `see` sheets and lettered in `look map`; night darkens every open floor, so only the persistent dark spots are drawn. The
  Explorer freecam (hive memory: no light arrays) doesn't draw it yet.
- **Scripts**: `light(pos)` → {sky, block, now, night} and `canSpawn(pos, now?)` in the JS SDK world module.

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
  Anthropic tiers. A brain that isn't Opus 5.5 logs a warning: script authorship is keyed to the brain tier.
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

- **One renderer** (`Render/EyeRenderer` over `Geometry/BlockGeometry` + `EntityGeometry`): every picture the AI or a person
  sees (sheet panels, `see {view}`, "iso", "top"/"side"/"layers", hive overview, blueprint previews, the supervisor `/view` and
  `/camera`, the Live Hive tiles and freecam) is the same CPU raycaster: b1.8.1 per-face tiles + metadata (log tops, furnace
  fronts, chest halves facing away from walls, doors with mirrored faces, crops by stage, wool), fixed biome tints (grass
  0x91BD59, foliage 0x48B518; tile 73 sugar cane is already green: untinted), alpha-cut fancy leaves/glass/plants/torches/
  crops, every render type's shape (crossed squares, torches leaning off walls, # crops, slabs, stairs, fences, panes, ladders,
  rails, doors, cactus, signs, beds, levers), fluids with RenderBlocks corner heights (source 8/9, sources/falling weigh 11, the
  same fluid above = 1.0, flow-rotated procedural TextureWaterFX/LavaFX/FlamesFX textures; water blends front to back, lava
  opaque + emissive), vanilla face shade × block/sky light at the world time, distance fog. Entities are the b1.8.1 models
  (ModelBiped + char.png, held item, walk swing, sneak; zombie/skeleton/creeper/spider/pig/cow/sheep(+fleece)/chicken/
  enderman/slime/wolf/pigman with mob/*.png read from the local client jar or `data/assets/mob/`, procedural stand-ins
  otherwise), burning entities wear Render.renderEntityOnFire layers, drops spin/bob like RenderItem (0.25 cubes or items.png
  billboards, 1-4 copies), shadows. "top"/"side"/"layers" are orthographic cameras with clip boxes (no flat palette maps any
  more; `IsoRenderer`/`IsoPalette` only draw the TUI's character viewport and the no-texture fallback colours). The geometry
  is renderer-agnostic quads (block-local / feet-relative, UVs, shade, light face, pass) for the Live Hive GPU mesher too.
- **Contact sheet / composite** (`ContactSheet`, `PerspectivePanel`, `ReadingAids`): one `see` = ONE image of up to 6 panels
  in a fixed ≈228k-pixel budget (≈310 tokens whatever the count; was 522×419 ≈ 292): default EYE (the body's first person),
  SHOULDER, ORBIT from NW and from SE (40° down), HIGH from the south (65°, north up) + the orthographic TOP map; no body →
  more orbits. `views` can mix spots (`VisionHub.Composite`): `eye 67` (any body's eyes, a clone's via the view IPC), `cam
  <coord|landmark|structure|bot> <N..NW|above> [dist]` (a free camera on the merged world memory, staleness in its caption),
  `orbit <at> [n]`, `follow <name>`, plus the named views; more than 6 panels is refused with a hint to split. Each panel has
  a caption (viewpoint >facing; time of day, staleness) and reading aids: a 1-block floor grid with every 5th line labelled with
  its absolute X/Z, a Y ruler at the focus, crosshair + "x,y,z" on the focus/spots, 2/4/8-block rings around the body,
  an N/E/S/W gizmo, and ≤12 prioritised depth-aware labels (players, mobs + BURNING, drops × count, gen cells G1.., chests,
  furnace lit/off, structures, the build site) with leader lines; one shared legend strip. The legend text lists each
  panel's labels with coordinates (the same facts as text). `see {aids:false}` turns them off. ORBIT/HIGH/ISO/TOP cut away
  roofs over the feet layer (`CutAway`: a roof over a gen hid every gen cell, live 10-27). Render ≈50-180 ms with 8 threads,
  ≈180-250 ms single-threaded. Focus: x,y,z, a player, a saved place, `pointed`/`this`; default the current job's target, else the bot.
- **Camera adjustments** (`CamParams`, `PerspectivePanel.Params.cs`): every non-eye view takes optional `key=value` words or an
  object — `"iso yaw=200 pitch=50 zoom=1.3"`, `"orbit yaw=10 dist=20"`, `"high yaw=90"`, `"top zoom=2 dx=3 dz=-1"`, `"side axis=E
  zoom=2 dy=1"`, `"shoulder dist=6"`, `"cam tower NE pitch=60"`, `{"iso":{"yaw":200}}`. Yaw is the way the camera FACES (game/F3
  convention: 0 S, 90 W, 180 N, 270 E; the default iso faces NE = 225), pitch degrees down, zoom > 1 closer / a narrower ortho
  window, dx/dy/dz pan (TOP/SIDE move their clip box with it). No params = the old code path, byte-identical sheets; the same view
  twice at two angles goes the composite way. `SeeCall` formats a sheet back into its exact `see {...}` input. Not advertised in
  the tool description: the brain is at 2941/2950 tokens and the worker at its 2250 cap with `see` in both.
- **What the AI saw** (`VisionHistory`, `VisionHub.Capture`): every picture a model gets (direct sheet/composite/hive/legacy
  picture to the brain or worker, the sheet sent to the perception tier, auto-vision's looks) is kept per body in a ring
  (`Bot:Vision:HistorySize` 20; optional disk ring `HistoryDir` + `HistoryDiskKeep` 200, a folder per body): the request's own PNG
  array (no copy, no re-render; sha256 = the bytes the LLM request serializes), when, kind, source, mode, views, focus, radius,
  question, the tool input JSON, size/tokens, the legend, and the perception answer (model, tokens; "cached: the model was not
  called"). `VisionHub.RenderSeeAsync` renders any see input without a model, budget or record (the supervisor's compose route).
- **Role layer** (`RoleClassifier`, `BlockRole`): floor, wall, gen cell (G = MINE HERE), water, lava, void, door, station, player
  build (B), own placed (+), protected ($, outline only: floors under a remembered build still read as floors; never crops, farm
  soil or a hive lease), ripe crop (H = HARVEST) / growing crop (:) from the block meta (wheat 7/7), never-break (X), portal (P), from the protection policy's `Check` verdicts, the world
  index, dig cells (taught/confirmed/geometry; air over a gen cell is never one) and the placed-block log. Tinted cells + letters in
  every panel (every role glyph is drawable), letters in `look map`.
- **Deterministic first** (`VisionRouter`, rules, zero tokens): every `see`/auto-vision question is classified before any model
  call. Gen cells vs floor/walls (+ which fluids touch each cell), gen health ("water isn't touching"), placeable faces, void
  under / distance to the edge, the focus block + role, the portal sheet + what is beside it, and trees (TreeScan: logs, leaves,
  soil), saplings, crops, chests, doors, stations and ores are answered exactly from voxels + roles ("[vision] exact: ..."); drops
  and mobs from the entity tracker (0x15 pickups until 0x1D; flags from 0x18/0x28 index 0: 0x01 burning, 0x02 sneaking): "3
  drops: 2 sapling at 4,64,9 (2 m from the focus)", "mobs: 2 zombies burning (daylight: dying soon, no need to fight)".
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

**Step 3, the multi-body brain (inproc only; hive mode never enables it).** A clone body has the hive clone's parity
(3a): its client's survival/EdgeGuard/reflex defence, plus its own brainstem (chat reflexes for lines naming it, speculative
step, social idle; its stop reflex stops its own slot), its inventory ledger (`Body.Ledger`; inventory/where_is {item} and
the cause scopes read the ledger of the body in scope), and fight/chop/void-step reports tagged with the body. Its script lane
holds its own activity slot and joins the event gate: only its own body's dangers and chat naming it (or a plain trusted
"stop") interrupt it; main's lane skips lines naming only other bodies. `BodyDirectory.Enable` (Agent `IBodyDirectory`) turns
on: `body` on brain tool calls ("67", ["67","69"], "all", "any"; documented in the cached `Prompts.BodiesHint`, never in the
schemas, stripped before the tool runs; a list plans one call per body, jobs on each body's slot in parallel, "67: ...; 69:
..."; `do` then[] steps may set their own body; the script tool routes by BodyScope); the turn focus (`BodyFocus`: the body
the waking chat names via `BodyAddressing`, the arbiter's rules; else `bodies op=focus`) whose state header, task and [here]
head the turn; `[bodies]` (`BodyStatusBlock`, ≤120 tokens, ≤25 per body, offline collapsed, uncached tail, 2+ online);
`BodyEventDigest` at the queue intake (danger/MustWake/typed chat pass; a crew's results wait for its last busy member;
non-focus minor events never wake; other non-focus wakes ≤ 1 per 20 s per body; a batch reads body task results as one
"[bodies] 67 ok: ...; 69 failed: ..." line; events render "[67 task ok]"); chat naming a clone is addressed to the brain; the
`WorkerPool` (a `WorkerAgent.Sibling` per body on the shared cached prefix, `Agent:Worker:MaxConcurrent` runs at once,
"worker queued for 69, 1 ahead"); `bodies {op: list|spawn|despawn|focus, name, task?, script?}` replaces `hive` (no inline
code: scripts go through the script tool's Opus-only policy); `body:"any"` = App `BodySelector` (CloneFitness + failure memory).

## Hive (MineClaw.Hive) — clones as supervised processes

**Hierarchy** (`docs/design/hive-hierarchy.md`, `Hive/Hierarchy`): no fixed roster. Main (`maid_chan`) is the default hive and the
admin root; a clone is a **hive** (own brain: Opus 5.5 or DeepSeek via Ollama), a **subhive** (a body its owner's brain drives) or
**script-only**; whoever spawns it owns it (`HiveTree`). Main allocates every account (`AccountAllocator`: `maid_chan_<id>`,
released managed accounts first, never a player's name, within the player cap) and does every `/whitelist add|remove` through the
command gate's scoped exception, for managed names only (`hive-accounts.json`; startup reconcile removes stale ones; the old
`Hive:CloneUsernames` are adopted once and otherwise ignored). Hives talk over the off-channel bus (`bus` IPC frames, main routes,
per-endpoint tokens, current-user pipes), never chat; chat has a per-hive line budget. All hives share one memory: other hives'
doc writes go through main's groomer (`mem_write`, provenance in `clone_id`), `mem_changed` reloads at once, one self profile.

`App → Hive → Bot`. Clones are allocated per spawn (`Hive:MaxClones` > 0 = an optional ceiling), each a **separate OS process**: the same App
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

**Scripts** (`SupervisorScriptRoutes` + `LiveScriptData`; GET only, 403 without the token since they carry source and live
values): `/api/scripts` (the saved library from the shared routines rows: versions, language, author kind opus/migration/legacy,
params with defaults and saved tuning, last run, success rate, timings, lint with the bot's real game functions, the bodies whose
standing order runs it — `Agent/Routines/ScriptLibraryReport`, also what the Explorer's Scripts page builds offline from a db),
`/api/scripts/{name}[/{version}]` (code, lint findings, history, recent runs: live telemetry + saved param runs),
`/api/bodies/scripts` (who runs a script now: the Live Hive tiles' badge; clones from their telemetry brain state, no IPC) and
`/api/bodies/{name}/script?code=1` (the live debugger snapshot; main and in-process bodies directly, clones over `script_dbg`
IPC with a 1.5 s timeout; one probe per body per 150 ms however many viewers ask). The Explorer polls it at 4 Hz while its
debugger panel is open (HiveApi timeouts and back-off, "stale" after 2 s without a snapshot).

**Vision** (`SupervisorVisionRoutes` + `LiveVisionData`; GET only, 403 without the token): `/api/bodies/{name}/vision[?since=id]`
(the body's captured sheets, newest first, no bytes), `/api/bodies/{name}/vision/{id}` (one), `/api/bodies/{name}/vision/{id}.png`
(the exact PNG; `X-Sha256`, `X-Tokens`) and `/api/bodies/{name}/vision/render?see={json}[&meta=1]` (a fresh render of a see input
through the render gate: roles, marks and the world time included; a clone's spot rendered by main from its live pose and the
shared memory). Main and in-process bodies read the hub's history directly, clones answer over the `vision`/`vision_png` IPC
requests (1.5 s timeout). The Explorer's Live Hive: a tile mode "AI's last sheet" (polled every 2.5 s with `?since=`) and an "AI
vision" panel per tile — *What the AI saw* (the sheet, its request, cost and perception answer, a history strip, zoom/pan, Save
PNG with the exact bytes, Copy as see, Compose from this) and *Compose* (pick panels, every non-eye camera's sliders, centre = the
bot, a block or a build plan's footprint, radius, aids; a live preview from `LocalSheetComposer`: the bot's `ContactSheet` over the
synced world, debounced 90 ms, one panel well above 10 fps; without the role layer, memory labels and world time, which "Render
exact" / "server render" fetch; "Copy as see{...}" gives the call that renders it).

**Controller** (`SupervisorControllerRoutes` + `SupervisorControllerData`, `Agent/Telemetry/BrainContextCapture`; GET only, 403 without the
token): what drives a body. `TelemetryLlmClient` hands every call to the brain's `BrainContextCapture` (`Telemetry:Capture`: `Turns` 30,
`BackgroundTurns` 10, `CallsPerTurn` 48): the request object as built, a pointer copy of its message list and the parsed response, by
reference (measured ≈1 µs and ≈2.7 KB per call on a 301-message request; nothing is rendered, redacted or serialized until a viewer asks).
Calls group into turns by lane (brain / reply / compaction / worker / author / memory / perception / background) and the turn input's
first text block; sub-entries link to the brain turn whose `TurnContext` they inherited. `/api/bodies/{name}/controller` (kind main | hive |
subhive | script, owner from the `HiveTree`, brain id, tabs), `/api/brains/{id}/context?since=&body=` (turns changed after a version, the live
window = the newest brain call's system blocks with the cached prefix marked, messages with `[tag]` sections labelled, tool calls with inputs
and results, the response; evicted ids; `body` marks the turns/tool calls that targeted a subhive body) and `/api/brains/{id}/turn/{n}` (every
call: what it added, the response with Claude thinking / redacted thinking / Ollama reasoning (`LlmResponse.Reasoning`, never sent back)
labelled, usage, cost, latency, children). Everything passes `ShardRedactor` + the configured key values and the supervisor token. Clones
answer over the `brain_ctx` IPC request (1.5 s). Explorer: a tile's "Controller" button opens `ControllerPanelView` (≈1.4 Hz polls with
`since`, back-off, "stale"; a token bar against `Context:MaxTokens`, "now thinking…", search, collapsible turns, Copy JSON; a subhive shows its
owner's brain filtered to it with "Full hive view"; a script-only body the script debugger; a hive running a script both, Brain | Script).

**Someone else on a clone's account** (a player's viewer). b1.8.1 kicks the older session with "You logged in from another
location", so the hive never fights a login: (a) an offline slot whose name is on main's tab list (0xC9) or spawned as a player
is skipped before launch ("slot 1337 is in use by a player's viewer", told once); (b) a clone kicked that way sends `occupied`,
stops its client's auto-reconnect and exits 76; main records its items as stranded, does not restart it, leaves the slot alone
for `Hive:ExternalLoginCooldownMinutes` (10) and tells the brain (MustWake when its task was unfinished).

## Memory grooming (Agent/Memory/Grooming) — every write formatted to the memory

`RagIndex.Upsert`/`UpsertMany` pass each doc through `RagIndex.Groomer` (`MemoryGroomer`, wired in `AgentRuntime` and
`MemoryAdminContext.OpenLocal`; `UpsertGroomed` bypasses it). Deterministic, no LLM by default; the chain is per doc source
(`Agent:Memory:Groom:Sources`, unknown sources such as seed/knowledge pass through). Stages, each one class:
**normalize** (log prefixes stripped, journald times → "10-27 06:45: ", spans canonical "(10-28 00:31–02:10)", coords trimmed to
one decimal, aliases → canonical names and casing, whitespace), **denoise** (debug/telemetry lines: turn/call cost lines, cache
numbers, triage, raptor sync; inline costs/tokens stripped; an all-noise doc is dropped), **redact** (`ShardRedactor`), **phrase**
(natural verbs: never "broke"; "chat: <Fox> hi" → `Fox said "hi"`), **merge** (same fact of the same kind+source within
`MergeWindowMinutes` extends the existing doc: "… (10-27 06:48–06:49, 2×)"; a repeat inside the span is absorbed), **coalesce**
(activity, below), **size** (title/coords filled, chunked to the RAG chunk target: `ref`, `ref#2`…) and **rewrite** (optional,
off: the cheap 5.5 tier via `LlmGroomRewriter`, daily USD cap, only on the async path for log/paste sources).
- **Activity coalescing** (`ActivityCoalesceStage`, state in `activity_episodes`): a rollup (`act:` doc with player/start/end/place
  meta from `ActivityLog.AddRollup`, or parsed from its text) is routine when every part is counted work (mined/chopped/dug/
  harvested/planted/collected/stored/took/crafted/opened/lit, small placements); routine work extends the open episode of that
  actor+place within `Activity:RoutineWindowMinutes` (2 h, same day): counts summed, span extended, sessions counted, the first
  episode at a place says "first time there". Crew bots (main + clones) at the same place with overlapping times share one crew
  episode ("the crew (main, 67, 69) mined 900 cobblestone (main 300, 67 300, 69 300) at the cobble gen (…)"). Novel sessions
  (deaths, fights, signs, builds ≥ `BuildMinBlocks`, unknown structures, anything unparsed) stay individual. The rollup row's
  `doc_ref` points at the episode that holds it.
- **Compaction** (`Admin/ActivityCompaction`, idempotent): old per-minute `act:` docs (oldest first, rollup meta joined) go through
  the same chain; absorbed docs are deleted, the summaries above every removed/rewritten leaf get the RAPTOR dirty flag (refresh
  within the daily budget; untouched sections keep theirs). Only `source='activity'` `act:` docs are touched. Explorer: Edits &
  audit → Compact activity (preview, then a run with backup first; live via `POST /api/admin/activity/compact`); startup:
  `Agent:Memory:Groom:Activity:CompactOnStartup` (backup first, main only). Groom preview (same page): raw → groomed for a doc
  ref or pasted log text, dry run. The RAPTOR graph's topic labels drop action verbs/log words (`TopicLabeler.ActionWords`).

## Memory shards (Agent/Admin/Shards + tools/MemoryExplorer Shards page)

Portable slices of memory: a `.mcshard` zip = `manifest.json` (name, description, source db file name + schema version, embedding
model/dims, kind + selection, counts, sha256 content hash) + `records/{kind}.jsonl` (stable ids: doc refs for episodes/knowledge/
game data, natural keys for places/structures/lessons, a content hash for facts/claims/rules/traits; fields hashed per record) +
optional `embeddings.bin` (doc material hash → float32 vector) + optional `summaries.jsonl` (RAPTOR summaries keyed by their
cluster-text hash: a cache, never truth). `ShardFile.Read` refuses a newer format/schema, a bad record hash or content hash.
- **Select** (`ShardCollector`, plain SELECTs, works on a read-only snapshot): RAPTOR subtree (leaf refs → their records), filter
  (kinds, dimension, text or semantic query), person (login + nicknames: their facts/claims/relationship entries/episodes, rules
  only with "include rules"), knowledge pack (`gamedata` docs, optionally kb chunks or just the named doc files, tagged with the
  version; a mode pack is its doc files' chunks only). Never exported:
  observations/containers (world + inventory ledger), chunks, build plans, chat, routines; places/structures only with
  "include world". `ShardRedactor` drops records about keys/passwords and redacts token-shaped strings.
- **Import** (`ShardImporter`, through the stores: MemoryStore, BeliefStore, DirectiveStore (trusted players only), SelfProfile,
  KnowledgeCorrections, RagIndex): diff first (added / changed / conflicts / removed / unchanged / skipped, reusable vectors,
  scoped rebuild + estimate). Modes: merge (dedupe by stable id; conflicts keep-newer | prefer-shard | keep-mine),
  replace-section (re-applies the shard's selection to the target, removes what's not in the shard), swap-pack (replaces every
  `gamedata` doc, sets meta `knowledge_pack`). Vectors of the same model + dims go into `embed_cache` (no API call), the rest
  re-embed; the tree takes new leaves at zero tokens, then `RaptorSectionRebuilder` kind rebuilds run only for tree kinds that
  changed (cached summaries reused for identical clusters). One audit entry (`import`/`export`, kind `shard`).
- **Knowledge pack** (`KnowledgePack`): no meta = the built-in b1.8.1 seed. A non-default pack adds one cached-prompt line at
  startup (the default prefix is unchanged); retrieval only ever holds the active pack's game data.
- **Default packs** (`shards/defaults`, built by `tools/ShardPacks` via `Admin/Shards/Packs/DefaultPackBuilder`): reproducible
  packs from PRISTINE sources (never a live db; same inputs → byte-identical `.mcshard` + `index.json`: sorted records, the seed's
  fixed timestamp, `.gitattributes` keeps bytes exact). A db holds one **version pack** (`b1.8.1-gamedata`: an `IGameDataSource`'s
  `gd:*` docs + the docs marked `<!-- pack version=b1.8.1 -->`, i.e. `mechanics_b181.md`) and any number of **mode packs**
  (`skyblock`: docs marked `<!-- pack mode=skyblock … -->`). Meta `knowledge_pack` = version + name + core + files + `modes`
  (null = legacy: every mode doc on disk counts; [] = a plain world). Import modes: `swap-pack` (version packs; also replaces the
  outgoing/incoming version docs' chunks, keeps the modes), `add-mode` / `remove-mode` (mode packs; merge/replace refuse packs).
  `KnowledgeIndexer.Sync` leaves out docs whose marker isn't active (no core lines, chunks dropped) and appends the stored core of
  active packs whose files aren't on disk, so the cached prompt's core comes from the active packs only. Manifest `pack`
  (`ShardPackInfo`): kind, name, version, mode, core, files, source, source hash, builder, `needs_embedding`; vectors come from the
  seed's `vectors`/`embed_cache` by material hash for its model + dims (packs > 20 MB drop them). Sources: `MineClawDataSource`
  (b1.8.1: `MineClaw.Data`, hand-transcribed from the b1.8.1 client jar and audited against mc-dev 218a2a71, rendered by
  `SeedCorpus`) and `MinecraftDataSource` (a PrismarineJS minecraft-data checkout the user fetched: `dataPaths.json` → items/blocks/
  recipes/foods; no smelting tables). The Explorer's Shards page lists the folder (`Explorer:DefaultPacksDir`, else `shards/defaults`
  beside the exe or in the repo) with one-click "Preview swap / add mode / remove mode" into the diff-first import, and shows the
  db's version pack + modes. `shards/defaults/README.md` has the build command for a new version.
  **1.12.2**: `MinecraftDataSource` names variants (planks:1 = Spruce Wood Planks) and carries registry names; `shards/sources/1.12.2`
  holds the facts minecraft-data lacks (smelting + fuels from `FurnaceRecipes`/`TileEntityFurnace`, mobs with health/xp/loot from the
  entity classes and loot tables, written as facts from the decompiled client), and `mechanics_1122.md` the mechanics.
  **sdk-js** (`Bot/GameTools/ScriptSdkPack`, a `GeneratedPack`): the JS SDK as memory, generated at build time from the modules' doc
  comments (`SdkReference.Entries`), an offline bot's script globals per game version (`ScriptGlobals.List`) and
  `ScriptPrompts.Examples`; a mode pack of version `any` (`KnowledgePack.AnyVersion`), one chunk per module/helper/function/example.
  `ShardPacks verify` rebuilds every version in index.json + sdk-js (`PackVerifier`). A bot's game version (`AgentOptions.GameVersion`,
  set by the host) that isn't the db's pack version reads only its own version's docs and logs the mismatch.
- **Fresh-world reset** (`App/Hosting/MemoryReset`, `--reset-memory --pack <version pack> [--modes none] --yes`, service stopped):
  verified backup of the whole data dir to `<data>-backup-<utc>` (refuses on any failure), db files + world-state files
  (memory.json, hunger latches, a staged seed of another version) removed, fresh db (current schema, no lived-in rows) with the
  version pack + sdk-js from the same folder, no mode, `server-version.txt` := the pack's version; usage.json, hive-ledger.json and
  /etc untouched. `deploy-alice.ps1 -ResetMemory -Pack <file> [-Start]` stops the unit, installs `--no-start`, runs
  `mineclaw-release reset-memory` (as the service user) and leaves it stopped unless `-Start`; `-NoStart` never changes the enabled state.
- **Targets**: a local db file (Explorer, backup first, audit next to the db) or the live bot via `POST /api/admin/shard/import/
  preview` and `POST /api/admin/shard/import` (body `{shard: base64, mode, conflict, include_rules, include_world}`, ≤ 64 MB, read
  only after `Supervisor:AllowAdminWrites` + the token header check out; the bot backs up, applies with its own stores and runs
  the rebuild as a background job under `Rag:Raptor:RebuildMaxUsd` per section). The Explorer never writes the live db file.
