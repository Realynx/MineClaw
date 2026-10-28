# One brain, many bodies

**Status:** design, 2026-10-28. When step 5 lands, this replaces the "Hive (MineClaw.Hive)" and "Hive supervisor viewer" sections of ARCHITECTURE.md.

**Decision (user):** there are no per-clone brains. The Opus main brain drives every body directly and writes Lua scripts that control several bodies at once. A shared cheap worker can run a long job on any body. All bodies run in main's process. Each body is isolated: it catches its own errors and restarts alone.

## 1. Target architecture

```
AppHost → BrainHost: one AgentRuntime (Opus brain, WorkerPool, ChatRouter on main's account, AgentDb/RagIndex/RAPTOR,
          ClaimBoard, Protection, DigCells, PlacedBlockStore)
        → BodyRegistry ─ Body maid_chan (main: default focus, the only chat sender, never yields)
                       ─ Body maid_chan_67 / 69 / 420 / 1337 (spawned on demand)
```

### Body (`src/MineClaw.Bot/Bodies/`)

`IBody`/`Body` members:

| Group | Members |
|---|---|
| Identity | `Name`, `Short` ("67"), `IsMain` |
| Connection | `Client` (MinecraftClient); `Game` (MinecraftGameClient → ClaimAwareGameClient with `Board.ViewFor(Name)`) |
| Jobs | `Activities` (its own ActivityManager over the shared ToolRegistry/EventQueue); `Orders` (StandingOrders, already keyed by name); `Scripts` (one ScriptController, i.e. one lane) |
| Per-body behaviour | `Bridge` (GameBridge), `Brainstem`, `Gestures`, `Watchdog` (StallWatchdog), `Handovers`, `Hunger` (HungerLatch per username) |
| Status | `State` (Offline\|Connecting\|Online\|Yielding\|Occupied\|Retired); `Progress` (0..1, used by slot yield) |

EdgeGuard, survival, combat defence and void-hold already live inside `MinecraftClient`, one per connection, so they stay as they are.

**Body routing:**
- `BodyScope` is an `AsyncLocal<IBody?>`, the same pattern as `ToolCaller`.
- `BodyRoutingGameClient : IGameClient` forwards to `BodyScope.Current ?? registry.Focus`.
- GameToolSet, SkillTools, VisionTools, AcquisitionPlanner and Loadout register once against the routing client.
- ActivityManager, WorkerPool and CrewSession push the scope explicitly around every step.
- A body tool called inside an activity with no scope logs a WARN.

**`BodyRunner`:** a per-body supervisor loop. It connects with backoff, runs the body, catches everything, and restarts only that body. It retires the body after `Bodies:MaxCrashes` crashes within `CrashWindowMinutes`.

### BodyRegistry
- Accounts come from `Bodies:Accounts`, which was `Hive:CloneUsernames`. Main is always online.
- **Spawn(name):**
  - `SlotPolicy.CanSpawn` checks main's tab list (`SlotFacts`) and keeps slots free for offline regulars.
  - Logins are spaced `LaunchGapSeconds` apart, 4.5 s by default.
  - An account already on the tab list, or kicked with "logged in from another location", becomes `Occupied` for `ExternalLoginCooldownMinutes`.
  - Void-hold on spawn is ported from `CloneHost.OnVoidHoldAsync`.
- **Yield:** the server limit is 7. Every 10 s, `SlotPolicy.MustYield` decides whether to yield; the body furthest from finishing its job goes first.
- **Despawn (logout gate):**
  1. Pause the job, keeping its order and lane checkpoint.
  2. Run `DepositProcedure` through `GameInventoryPort`, with main as the in-process `IHandoffTarget`.
  3. Check the inventory is empty, then disconnect.
  4. Record any leftovers as stranded in `HiveStore`.
- **Shutdown:** clones despawn in parallel within `ShutdownRecallSeconds`, then main stops.

### Shared world and memory
- **Stores:** one in-process instance each of AgentDb, MemoryStore, ObservationStore, RagIndex and RaptorMemory. `DataVersionWatcher` and the clone TEMP triggers go away. `seen_by` records the body name.
- **Shared state:** one ClaimBoard (owners are body names), one Protection, one DigCells, one ProtectionMarks and one PlacedBlockStore. The PlacedBlockStore subscribes to every body's `World.Placed`.
- **WorldIndexer:**
  - Main runs the full indexer.
  - Clone bodies use `WorldIndexOptions.Contributor`: container snapshots, block changes and gen regen only.
  - Step 5 merges these over `MultiBodyWorld`, the union of every body's live columns, newest wins.
  - One `WorldMemory` flusher serves all bodies. PlayerActivityTracker and MechanismWatcher run on main only.
- **Chat goes out only from main's account,** through one ChatRouter.
  - Each clone client's `ChatReceived` is dropped, except whispers to that account, which post as a Whisper with `Body=67`.
  - Lua `say`, reflex acknowledgements and quick replies all use main's account.
  - Gestures play on the addressed body, otherwise on main.

### Events
- `GameEvent` gains `string? Body`; null means the event concerns the brain, not a body.
- GameBridge, StallWatchdog, HandoverWatch and ActivityManager stamp it.
- There is one EventQueue, and its dedupe keys include the body.
- `Format()` prefixes events from non-main bodies, e.g. `[67 task ok] …` or `[69 DANGER] hp 5/20 …`.
- `BotAgent.OnEventPosted` cancels only `ActivitiesFor(e.Body)`.

## 2. Brain interface

### Tool schema (`ToolBudgets.Brain` stays at 2,800)

| Tool | Change |
|---|---|
| `do` | + `body`: "67", ["67","69"], "all" or "any" (default: the turn's focus body). Each step in `then[]` can set its own `body`. A list or "all" runs one activity per body, and the result reads `67: ok …; 69: failed …`. |
| `delegate` | + `body` (the worker run is bound to that body) |
| `stop` | + `body` (default focus, or "all") |
| `status`, `inventory`, `see` | + `body` (`see` keeps `focus=`) |
| `script` | + `bodies[]` starts a crew run; `body` on pause/resume/cancel targets one lane |
| `hive` → `bodies` | `op` list\|spawn\|despawn\|focus, `name`, `script?`, `args?` |

The worker's tool set doesn't change, because a worker run is bound to one body.

**Turn focus:** if the waking chat names a body, that body is the focus. `BodyAddressing` holds ChatArbiter's number and explicit-name regex rules. Otherwise the focus is whatever `bodies op=focus` last set, and failing that, main.

**"any":** `BodySelector.Pick(intent)` uses `CloneFitness.Score` over live hp, food, tools and distance, plus a failure memory keyed by (body, intent). Any live change to that body's hp, food or inventory clears its entries.

### `[bodies]` status block
It goes in the uncached tail on every turn while 2 or more bodies are online, and is at most 120 tokens:

```
[bodies] 67 gen@cobble_gen 42/64 hp18 f12 stone_pick free8 | lane gen_crew
69 idle @main_house hp20 f20 | 420 DANGER hp6 retreating | 1337 off
```

Each non-focus body gets one line of at most about 25 tokens, and offline bodies collapse. The focus body keeps today's state header and `[here]`, at most 250 tokens. The worst case is about 400 tokens, and a budget test pins that.

### Wake and triage rules
- **Danger on any body:** High or Critical priority. It wakes the brain at once and interrupts only that body; the body's reflexes have already acted.
- **Body task ok/failed:** Normal priority, coalesced across bodies within the batch window into one line: `[bodies] 67 ok: 64 cobble; 69 failed: no free cell`.
- **MustWake:** stall, failed detached job, kept promise. Per body, as today.
- **Minor events from non-focus bodies:** Background priority, folded into `[bodies]`. Outside danger and MustWake, a non-focus body can wake the brain at most once per 20 s.
- **Lane events:** they go to the crew script first (`HasHandler`), and the brain stays asleep.
- **Idle:** one check-in for the whole brain, e.g. `[idle] 69, 420 idle 5m`.

## 3. Lua multi-body API

**Status (2026-10-28):** built in JavaScript instead (`body()`, `parallel()`, `spawn()`, `crew.*`, `main()` + role functions, per-lane checkpoints, lint `crew-*`), for both `inproc` lanes and hive jobs: see ARCHITECTURE.md, "Scripting → Crews". The Lua below is the original design.

Each body gets its own `ScriptSession` (own thread, own LuaState), coordinated by `CrewSession` in `MineClaw.Scripting/Crew/`. A LuaState can't be shared across threads, and host calls block, so parallel work means separate lanes. The `coroutine` library stays removed.

```
body"67"                 handle: .name .hp .food .pos .busy  :inv"log"  :free()  every action/skill as a method: b:gen{"auto",8}
me                       this lane's body; inside a role, bare actions act on it
parallel{ {b,"role",args}, ... , keep_going=true? }   runs roles in lanes, waits for all, returns results in order
spawn(b,"role",args)     → lane  :wait(s?) :cancel() .status .result
crew.get/set/inc(k,v)    shared JSON board (≤4 KB)     crew.send(body,msg) / crew.recv(s)
crew.barrier(name,n,s)   crew.lock(key,ttl?) / crew.unlock(key)   (ClaimBoard "task:" leases)
```

- **Script structure:**
  - A crew script's top level holds only function definitions and constant locals.
  - `main(args)` runs in the coordinator lane.
  - Each lane reloads the chunk and calls its role function.
  - Single-body scripts don't change.
- **Body leases:**
  - A lane may act on a body only while it holds that body's lease.
  - A busy body answers `body 69 busy: do mine #12`.
  - Brain tools on a lane's body answer `body busy: crew gen_crew lane 69 40%`. `queue=true` still works.
- **Interrupts:**
  - Danger on body X pauses only lane X, and also reaches `on("danger", fn)` in main with `ev.body` set.
  - A trusted "stop" pauses every lane; "67 stop" pauses lane 67 only.
  - `script op=pause|cancel body=69` acts on one lane; without `body` it acts on all lanes.
  - Unless `keep_going` is set, the first lane failure cancels its siblings.
- **Checkpoints:** `cp` and `carry` are stored per lane (key: script/body). `resume_from` and a crash restart resume each lane from its last checkpoint.
- **Brain sleep:** the brain makes zero calls until one of these happens:
  - a lane fails or hits a limit (MustWake);
  - a body stalls;
  - a lane calls `wake"…"` or `ask("brain",…)` (rate-limited);
  - a danger no handler took;
  - addressed chat, which wakes the brain but never pauses lanes.
  
  When the crew ends it posts one line: `[task ok] crew gen_crew: 67 64, 69 64, 420 failed (no free cell)`.
- **Authorship:** only the Opus brain writes crew code. A worker is bound to one body and can't start a crew.
- **Lint:**
  - Errors: `crew-toplevel`, `crew-unknown-role`, `crew-dup-body`.
  - Warnings: `crew-say`, `crew-me-in-main`, `crew-unbounded`.
  - The cheat sheet grows by 2 lines, about 60 tokens.

**Gen crew (3 bodies, auto cells):**
```lua
-- purpose: n cobble per body at the gen, auto cells, stored
function miner(a)
  ensure"pickaxe" room(2)
  local got = 0
  cp"mine"
  upto(a.n, function()
    local ok, m, n = gen{"auto", 8}
    if not ok then
      if m:find("no free cell") then wait(3) return true end
      fail(m)
    end
    got = got + (n or 0); crew.inc("cobble", n or 0)
    prog(crew.get("cobble") / a.total)
    if inv.free() < 2 then local ok2, m2 = deposit{} expect(ok2, m2) end
    return got < a.n
  end)
  local ok, m = deposit{} expect(ok, m)
  return me.name .. " " .. got
end
function main(a)
  local names, n, jobs = a.bodies or {"67", "69", "420"}, a.n or 64, {}
  for _, b in ipairs(names) do jobs[#jobs + 1] = {body(b), "miner", {n = n, total = n * #names}} end
  local r = parallel(jobs)
  return "gen crew: " .. crew.get("cobble") .. " cobble (" .. table.concat(r, ", ") .. ")"
end
```

**Build crew (2 builders, 1 hauler), using the blueprint builder's `blueprint()`/`build{}`:**
```lua
-- purpose: build blueprint a.bp split in 2 parts, 420 hauls materials
function hauler(a)
  upto(60, function()
    local need = crew.recv(120)
    if not need or need == "done" then return false end
    local ok, m = take{need.item, need.n} expect(ok, m)
    ok, m = give{need.to, need.item, need.n} expect(ok, m)
    return true
  end)
end
function builder(a)
  ensure"pickaxe"
  for layer = 1, a.layers do
    cp("layer" .. layer)
    upto(6, function()
      local ok, m = build{a.bp, part = a.part, of = a.of, layer = layer}
      if ok then return false end
      local item = m:match("missing (%w+)") or fail(m)
      crew.send("420", {item = item, n = 32, to = me.name})
      waitfor("item", item, 90)
      return true
    end)
    crew.barrier("layer" .. layer, a.of, 300)
  end
  return me.name .. " part " .. a.part
end
function main(a)
  local info = blueprint(a.bp)
  local h = spawn(body"420", "hauler", {})
  local r = parallel{ {body"67", "builder", {bp = a.bp, part = 1, of = 2, layers = info.layers}},
                      {body"69", "builder", {bp = a.bp, part = 2, of = 2, layers = info.layers}} }
  crew.send("420", "done") h:wait(30)
  return "built " .. a.bp .. ": " .. table.concat(r, ", ")
end
```

## 4. Worker fallback
- **WorkerPool:** one `WorkerAgent` per body, created lazily, all sharing the cached worker prefix.
- **Concurrency:** capped by `Agent:Worker:MaxConcurrent` (default 2). When the pool is full, `delegate{task, body}` queues and answers "worker queued for 69, 1 ahead".
- **Runs:** each run pushes `BodyScope`, owns that body's ActivityManager, keeps its own caps, and returns one line tagged with the body.
- **Budget:** one UsageTracker, with each call's body recorded in telemetry. There are no grants, sub-caps or recall. When the budget is Paused, C# work keeps going and new worker runs are refused.

## 5. Removed or simplified

| Removed | Replacement |
|---|---|
| CloneMode, CloneHost, HiveLink, HiveMessages, ProcessLauncher, HiveSupervisor, `--clone/--parent-pipe`, exit codes 75/76 | BodyRegistry and BodyRunner |
| ChatArbiter, ChatRelayGuard, ClonePolicy, GuardSpeech pending-result claims | one ChatRouter; the regex rules move to BodyAddressing |
| Per-clone personality shards (`AgentOptions.Hive`, SelfGrowth.Hive, CloneMode persona) | one self-profile; one cached line: "bodies 67/69/420/1337 are you; chat only as maid_chan" |
| BudgetLedger, GatedLlmClient, `HiveBudgetConfig` sub-caps, `usage-<clone>.json`, recall | one UsageTracker, body-tagged telemetry |
| CloneTaskOrders, CloneScriptRunner, semi-sleep spawns | per-body StandingOrders and crew lanes |
| RemoteBotSource, `view` IPC, `IHiveVision.ViewAsync` | InProcessBotSource per body |
| CloneFitness/AssignFailureMemory as spawn gates | `BodySelector` for "any" |

**Kept:** ClaimBoard, SlotPolicy, DepositProcedure, HiveLending (body to body), HiveStore (stranded items), CloneFitness (renamed BodyFitness).

**Tests:**
- **Obsolete:**
  - Hive.Tests: HiveProcessTests (with CloneStub), HiveReliabilityTests, CloneBudgetLedgerTests, LiveHiveChatTests, LiveSession2OwnershipTests, NightReviewSpeechGuardTests.
  - App.Tests: CloneBudgetTests, and the HiveLink part of ShutdownProcessTests.
  - Agent.Tests: the hive-scope parts of SelfGrowthTests.
- **Port:**
  - LiveSession5AddressingTests (number addressing)
  - ExternalLoginTests, LiveSession5VoidSpawnTests, LiveSession5HiveLifecycleTests
  - LiveSession4HiveTests (fitness)
  - HiveScriptSpawnTests
  - HiveVisionTests and VisionHiveTests
  - HiveToolTests (the budget pin)
  - the SlotPolicy and ClaimBoard parts of HiveUnitTests, LiveSession2FixesTests and NightReviewHiveTests
  - the CloneTaskOrders part of LiveSession3HiveTests
- **Unchanged:** DimensionClaimTests, the Bot.Tests ClaimBoard users and the Scripting.Tests BotScriptTests.

## 6. Migration (each step green and deployable)

1. **Step 0: prep.**
   - Add `GameEvent.Body` (nullable, unused), `BodyScope`, a `body` field in PromptTelemetry, and the body in EventQueue dedupe keys.
   - Precondition: the in-flight fix batch has landed.
   - Risk: none.
2. **Step 1: Body abstraction, main only.**
   - Add `Bot/Bodies/{IBody,Body,BodyRegistry,BodyScope,BodyRoutingGameClient}.cs`.
   - Split `BotHost.Create` into `CreateBrain` and `BodyFactory.Attach`.
   - `AgentRuntime.Activities` becomes an alias for main's ActivityManager, and `BotAgent` gains `ActivitiesFor(body)`.
   - The registry holds one body.
   - Risk: medium (wiring order).
   - Tests: the full suite plus BodyRoutingGameClientTests.
   - Live: a normal session, checking that latency, `[here]`, reflexes, scripts and `delegate` are unchanged.
3. **Step 2: clone bodies in-process, behind `Bodies:Mode=inproc`** (the default stays `hive`).
   - Add BodyRunner, registry spawn/yield/despawn, the Occupied state, the void-hold port, the contributor indexer, the shared WorldMemory flusher, and InProcessBotSource.
   - Risk: high (N tick loops, memory, login throttle).
   - Tests: BodyRegistryTests and BodyRunnerTests, plus the ported lifecycle, void-spawn and external-login tests.
   - Live: a watched window with 67 and 69 spawned. Watch RSS/CPU, "tick behind" logs and `/kick` isolation. Fall back to the `hive` flag if needed.
4. **Step 3: multi-body brain interface and worker pool.**
   - Add the `body` parameters, the `bodies` tool, BodyStatusBlock, BodyEventDigest, per-body interrupts, BodyAddressing and turn focus, WorkerPool and BodySelector.
   - Risk: medium (one cold cache write on deploy).
   - Tests: the budget pins, the status block cap, digest coalescing and interrupt isolation.
   - Live: "67 and 69 mine the gen" should take one Opus turn, with the bodies on different cells.
5. **Step 4: Lua crew API.**
   - Add CrewSession and CrewBoard, the `ScriptHost.Bodies` port, handles, per-lane checkpoints, body leases, lint rules, the cheat sheet and `script{bodies}`.
   - Risk: medium-high.
   - Tests: a fake 3-body host.
   - Live: gen-crew for 15 minutes; build-crew after the blueprint builder lands.
6. **Step 5: remove the old hive.**
   - Delete everything in the section 5 table, move the survivors to `Bot/Bodies`, and delete MineClaw.Hive.
   - Build the merged indexer, flip the default to `inproc`, then remove the flag.
   - Rewrite ARCHITECTURE.md.

## 7. Risks
- **Thread safety.** Bodies' state stays separate, but:
  - `ToolCallGuard` failure memory must be keyed by body;
  - `LatencyTracker.Bot` must be per body;
  - handlers that N reader threads now call at once need locks (BotHost's `punched` dict, ChestChanges, PlacedBlockStore);
  - a missing `BodyScope` silently acts on the focus body, hence the WARN and the explicit scope pushes.
- **Database.** One AgentDb write gate serialises N writers. Batch the flushes, keep write transactions to 50 ms or less, and use the single WorldMemory timer. Sharing one vector cache saves memory.
- **CT 114 (4 cores, 4 GB RAM, 512 MB swap).**
  - Memory: about 60–120 MB per in-process body, vs about 250–350 MB per clone process today.
  - CPU: add a `PathfindGate` (`SemaphoreSlim(2)`) for A* bursts.
  - GC: one heap means a gen2 pause stalls every body's tick at once, EdgeGuard included. Use workstation concurrent GC and `GCHeapHardLimit` ≈ 2.5 GB, and track "tick behind" counts.
  - Blast radius: an OOM or a stack overflow now kills every body. Mitigations: Lua depth limits, systemd restart, and resuming orders and checkpoints on start.
- **Token budget.** Tool definitions grow by about 40 tokens net, inside 2,800. `[bodies]` stays at most 120 tokens in the uncached tail, and only the static list of body names goes in the cached prompt.
- **In-flight work.**
  - The blueprint builder must code against `IGameClient` through `BodyScope` (or take an `IBody`), never capture `MinecraftClient` or `rt.Activities`, and expose `build{bp, part, of, layer}` and `VoxelPlan.Partition(n)`.
  - The fix batch must land before step 2.
- **Players.** Clones no longer speak, so main answers for them ("67's on it"). "67, come here" still works through BodyAddressing.

## 8. Effort

| Step | Effort (working days) |
|---|---|
| 0 Prep | 0.5–1 |
| 1 Body abstraction (main only) | 2–3 |
| 2 In-process bodies behind a flag | 4–5 |
| 3 Multi-body tools, status, events, worker pool | 3–4 |
| 4 Lua crew API, lint, examples | 4–5 |
| 5 Remove the hive, merged indexer, docs | 2–3 |
| **Total** | **≈ 16–21** (+2 for live soak and fixes) |
