# Hive hierarchy: kinds, ownership, dynamic accounts, the off-channel bus, one memory

Status: steps (a)–(h) below are implemented; "Remaining" lists what isn't.
Related: `ARCHITECTURE.md` (Hive, One brain many bodies), `docs/design/one-brain-many-bodies.md`.

## The model (Fox's words, condensed)

- There is **one main**: `maid_chan`. It is the **default hive** and always the **admin root**.
- **No fixed clone names.** The empty state is main alone, whitelisted and opped. Main whitelists the clones it brings in and
  de-whitelists them when they leave, so the hive can use as many clones as the server's player cap allows.
- A clone (a "subagent" in game) is one of three kinds, and nothing else:
  - **hive**: spawns another *brain* (Opus 5.5, or DeepSeek through our Ollama). It can do everything main can, except
    whitelisting: it asks main.
  - **subhive**: another *character the current hive's brain controls* (one brain, many bodies). A subhive belongs to its
    owning hive: main or a hive main spawned. "Subhive" is about who drives a body, not about the admin hierarchy.
  - **script-only**: a character that just runs one script. Whoever spawned it owns it.
- Hives talk to each other **off chat**. Game chat is for players and stays at low verbosity.
- **One memory.** Every hive (main, Opus hives, DeepSeek hives, every subhive and script body) uses the same memory at the same
  time. "Different hives" only means separate brain loops, each driving its own characters; externally they share one store.

## Terms

| term | meaning |
|---|---|
| main | the root hive, `maid_chan`; the default owner; the only process that whitelists, allocates accounts, owns the db's writes for other hives, and routes the bus |
| hive (kind) | a clone with its own brain loop (`BrainKind.Opus` or `BrainKind.DeepSeek`), today a clone process (`--clone`) |
| subhive (kind) | a body driven by its owner's brain, hosted in the owner's process (`InProcBodies` in subhive mode, `BodyRegistry`) |
| script (kind) | a body running one saved script with the brain asleep (the existing script spawn), released when the script ends |
| owner | the hive that spawned a clone: `main` or a hive-kind clone's account |
| admin root | main, always; ownership never grants whitelist rights |
| managed account | a `maid_chan_<id>` login main created (or adopted from the old roster), recorded in `hive-accounts.json` |

Code: `MineClaw.Hive.Hierarchy` (`HiveKinds`, `HiveTree`, `HiveHierarchy`, `AccountAllocator`, `ManagedAccounts`,
`WhitelistManager`, `Bus/*`, `CloneHiveTool`), `Hive/Chat/HiveSpeech`, `Supervisor/IHiveAccounts`.

## Ownership tree vs admin hierarchy

```
main (admin root, default hive, brain Opus 5.5)
├── 101 hive (opus)            owner main
│   ├── 102 script wheat_loop  owner 101
│   └── 103 hive (deepseek)    owner 101
│       └── 105 script cobble  owner 103
├── 106 subhive                owner main  (in main's process)
└── 107 hive (deepseek)        owner main
    └── 108, 109, 111 subhive    owner 107 (in 107's process)
```

- **Ownership** (`HiveTree`) decides who may assign, hand over, list and release a clone, and what a release cascades to. A hive
  sees and controls only its own subtree. Script and subhive bodies own nothing. An owner that disappears re-parents its
  children to main while they log out (never an orphan, never a cycle).
- **Administration** is flat: main does every account allocation and every `/whitelist add|remove`, whoever owns the clone.
  A hive asks over the bus (`spawn`, `release`, or `whitelist` for its own child); main checks the request against the tree.

## Lifecycle of a clone

1. **Request**: main's brain (`hive op=spawn kind=… brain=… script=… task=…`) or another hive over the bus (`spawn`).
2. **Allocate** (`AccountAllocator`): reuse a released managed account first (its saved login spot, join history and void
   status stay known; the most recently used one), else a fresh `maid_chan_<id>`: lowest free id from 101, skipping ids a player
   might read as a quantity at the start of a line (multiples of 8, 10, 25: "64 cobble", "110 more"). Rules: a legal name,
   ≤ 16 chars, starts with `Hive:Accounts:Prefix`, never main, never a name online, seen in chat, a regular or trusted player.
   The number is how players address the clone ("101, come here"), so the chat arbiter's number rules keep working.
3. **Cap check**: the server's max players (login packet; `MaxPlayersFallback`) minus online players minus launches not on the tab
   list yet must leave one slot per offline regular (`SlotPolicy.CanSpawn`; subhive bodies count as running, and as pending until
   they are on the tab list); the **resource ceiling** (`Hive:Resources`, `Slots/ResourceCeiling`: memory headroom from the cgroup
   limit, used = max(estimate, kernel's measurement)) must fit the new hive process or body; `Hive:MaxClones` > 0 is an extra ceiling (0 = only
   the player cap). Refusals say which ("player cap: only 1 slot free and 1 kept for offline regulars").
4. **Whitelist add**: right before the login (`IHiveAccounts.BeforeLaunch`), through the command gate's scoped exception.
5. **Spawn/connect**: the existing supervisor launch (login throttle spacing, current-user-only pipe, hello → welcome with the
   bus token).
6. **Work**: its task/script; hive-kind clones may spawn their own clones (bus).
7. **Deposit** its whole inventory before logout (the existing logout gate: chests → hand-off to main → placed chest; stranded
   on timeout).
8. **Disconnect**.
9. **Whitelist remove** once it is offline for good (`IHiveAccounts.Released`: not on a crash restart or a queued relaunch).
   The account stays in the managed list (inactive) for reuse.

**Release** (`hive op=release name|all`): the subtree is released deepest level first (siblings in parallel), each level waiting
for its bodies to deposit and log out before the next; whitelist removal follows each logout. A hive whose own outing ends takes
its clones with it the same way.

### First login of a fresh account

A never-joined account lands at the server's fresh-player spawn, which is over the void on this skyblock. The supervisor already
refuses that login without a trusted player's /tp consent ("never joined: … needs a /tp"), and that applies to fresh accounts.
Main never /tps on its own (op policy: only exact commands a trusted player names). Reuse-first keeps fresh accounts rare.

## Subhive bodies (one brain, many bodies inside a hive)

- **Where they live**: in their owner's process. Main hosts its own next to the clone processes (`InProcBodies.CreateSubhives` with
  MainHive's claim board); a hive-kind clone hosts its own (CloneMode: a local claim board layered over main's mirror, `BodyCrewPort`
  for its crews). Each body has its own connection, world view and wiring (`BodyFactory.CreateClone`), no brain of its own: the owning
  brain drives it through the body parameter (`Prompts.SubhivesHint`, one cached line), `do{body:"all"}` and crews.
- **Accounts**: always main's allocator. Main's own: allocate → tree + managed list → the host spawns → `BeforeConnect` waits for the
  whitelist add → login. A hive's: the hive asks main (`spawn{kind:subhive, bodies:N, reset:true}` once in game), main allocates each,
  whitelists it (confirmed, one at a time) and sends `host{account, task?}` to the owning hive, which logs it in (refusal = released).
- **Gone**: a body that logged out for good (despawn, retire, someone else's login) is `InProcBodies.Gone`: main releases it directly,
  a hive sends `subhive_gone` (accepted only from the hosting hive), then main removes it from the whitelist. A hive process that ends
  takes its bodies with it (its logout gate despawns them first, deposit included); a crash restart reuses them (`reset`).
- **Release**: `hive release 107` cascades bodies first: main asks 107 over the bus (`release{id}`), each body deposits, logs out,
  is reported gone and de-whitelisted; only then is 107 itself recalled.
- **Memory per body**: a small view distance (`Hive:Subhives:ViewDistance`, default 4, 1.12.2 Client Settings), one chunk store per
  process (`World/Chunks/SharedChunkStore`, overlapping views held once), lazily allocated 16³ sections (256-high columns ≈ 59 KB
  instead of 256 KB on normal terrain). Measured (`Client.Tests/BodyMemoryBenchmarkTests`, managed MB per extra body): r8 eager 72.9,
  r4 eager 20.6, r8 lazy 16.6, r4 lazy 4.8, r4 lazy + shared 0.25, r3 lazy + shared 0.24 (the shared store itself ≈ 4.6 MiB at r4).

## Crews of many bodies (live 10-29: a DeepSeek hive with 20 bodies deforesting)

- **Seeing the work**: the exact `see` scan answers the area a question names ("trees within 64 of 152,63,288", ≤ 64, a taller
  band, log2 woods; `VisionRouter.AskedArea`), over the crew's combined view: `WorldState.PlanningView` reads columns any body of
  the process holds in the shared chunk store (`SharedChunkStore.Peek`) before remembered ones. Before, a 12-block scan round the
  brain's own body said "1 grown tree" and the hive reported the forest gone.
- **One body per target**: `MinecraftClient.TargetClaims` (the body's claim view) leases the tree chop_tree picks (`tree:x,y,z@dim`,
  task key, 6 min TTL, released when the chop ends), skips trees siblings hold (a named `at=` tree held by another → the next free
  one near it) and prefers trees with nobody standing at the trunk. The SDK's `forEach` leases each positioned target the same way
  on the hive board (`claimTarget`/`releaseTarget`; a done target stays marked 10 min so stale lists skip it), for any job.
- **The frontier, typed**: chop_tree with nothing free in its radius fails `exhausted: …; nearest beyond: oak 200,64,300 (40 m E)`;
  `find()` with nothing in range returns an empty list with `.exhausted` and `.beyond`; `forEach` over nothing returns
  `{exhausted, beyond}`. Widening the area is one call (`chop_tree at=`, `find(q, {r})`).
- **Idle bodies** (`Bot/Bodies/BodyIdleWatch`): a body with no job, lane or reflex for 30 s is idle; `[bodies]` collapses them
  ("idle 7 (101,105,…): chop_tree: exhausted: …") and the brain gets ONE wake per idle stretch (≥ 2 min apart) with the count and why.
- **Chests**: the walk to a container happens before its lease; deposits take one of 4 shared slot leases per chest (vanilla
  lets several players use one chest; the server arbitrates the slots), queue ≤ 20 s (≤ 3 s behind an exclusive user), and when the
  drop-off chest is full or still crowded `ChestStore` places a chest two blocks beside it (carried, else crafted) and spreads onto
  it: "chest 152,63,288 busy: placed a new chest at 154,63,288; stored …".
- **Addressing**: a line naming a hive is that hive's (a bot is always live to itself), a line naming a subhive body is its host
  hive's (`ChatArbiter.SetHost`, main follows the ownership tree, a hive registers its own bodies); main takes it only when the host
  is unreachable. A brain call with a `body` it doesn't drive is refused ("not done: 420 isn't a body I drive …; hive op=handover"),
  and main's speech states a clone's whereabouts only when main sees it beside a player (`HiveSupervisor.GuardWhereabouts`).
- **Pre-emption**: a logout that follows a clone's own finished task yields to a new order (main's task, or a player naming it):
  the deposit stops, the clone stays online and takes the order. Recall, budget and shutdown logouts are never pre-empted.

## Whitelisting: the only path

- `/whitelist` stays in `Agent:Commands:Blocked`: the model can never send it, even with a trusted player's exact line.
- `CommandGate.ArmManagedWhitelist(line)` arms exactly one `/whitelist add|remove <name>` when `CommandGate.ManagedAccount` (main
  only: `WhitelistManager.Accepts` = in the managed list, managed shape, not main) accepts the name. `AllowOutgoing` lets that one
  line through once and audits it as "hive account manager". Any other subcommand, name or a second send is refused.
- Clone processes have no `ManagedAccount` hook at all; they ask main.

## Crash cleanup

`hive-accounts.json` (data directory, atomic replace) lists every managed account with `whitelisted`, `active`, kind, brain and
owner. It lives outside the db on purpose: a memory reset must never orphan a whitelisted account. At startup, once main is in
game, `WhitelistManager.ReconcileAsync` releases every managed account that isn't running and sends `/whitelist remove` for every
one main left whitelisted. The old fixed roster (`Hive:CloneUsernames`) is **adopted** once (marked whitelisted, since it was
whitelisted by hand), so the first reconcile clears it too and the allocator can reuse those names; the config entries are
otherwise ignored.

## The off-channel bus

Inter-hive messages never go through game chat.

- **Transport**: the existing IPC (one named pipe per clone process, length-prefixed snake_case JSON) gets one more frame type,
  `t = "bus"`, carrying a `BusEnvelope {type, from, to, body, token, seq}`. In-process endpoints call `HiveBus.RouteAsync`.
- **Local and authenticated**: pipes are created `PipeOptions.CurrentUserOnly`; main registers each endpoint and hands it a random
  128-bit token in the welcome; an envelope is accepted only with its sender's token (constant-time compare) **and**, over IPC,
  only from the link that endpoint owns (no hive can speak as another or as main). Tokens never travel onward. Main-only types
  (`mem_changed`) are refused from hives.
- **Main routes**: `to = "main"` → main's handler (the hierarchy); `to = <account>` → that hive; `to = "*"` → every hive except
  the sender (and main).
- **Types** (`BusTypes`): requests `whitelist`, `spawn`, `assign`, `handover`, `release`, `list`; notifications `status`,
  `result`, `observation` (shared), `need_help`; memory `mem_write`, `mem_changed`. Status/results/help reach main's brain as quiet
  `[hive bus]` notices (`need_help` wakes it), never chat.
- **DeepSeek hives** are clone processes with a DeepSeek brain profile, so they get the same bus through the same pipe.
- A hive-kind clone gets its own small `hive` tool (`CloneHiveTool`: spawn/release/list, all bus requests).

## Chat: low verbosity

- `ChatVerbosityBudget` per hive (`Hive:Accounts:ChatLinesPerMinute`, default 4, sliding minute): lines a player asked for always
  pass but count; unasked lines (narration, "done!", idle chatter) pass only while there is room. Muted text from a clone goes to
  main as progress (`BotAgent.Muted`).
- `HiveSpeech.Scrub` drops sentences about plumbing (whitelisting, the bus, spawning/releasing clones/accounts, subhive/script-only,
  `[hive` tags, raw `maid_chan_<n>` logins) on top of the existing guards. Coverage check of the existing narration-leak guards:
  `ClonePolicy.Speech` (clones speak only when addressed or for a one-line task ack; strips hive/clone/hive_report sentences),
  `HiveSupervisor.GuardSpeech` (main never reports a pending clone result) and `ChatRelayGuard` (main's instructions to a clone go
  over IPC). They covered clone narration and main's result claims, but not main mentioning whitelisting/spawning plumbing; the
  scrub covers that for main and clones alike.

## One memory for every hive

There is one memory store for main, Opus hives, DeepSeek hives and all their bodies: the same RAG/RAPTOR db, beliefs, directives,
players, world index, structures, script library, blueprints and self profile. No per-hive shards or copies.

**Decision: one SQLite file; main owns the groomed memory writes over the bus; reads are direct WAL reads.**

- Writes: in a hive process that isn't main, `RagIndex.WriteThrough` sends raw docs to main (`mem_write`). Main runs them through
  **its** groomer chain once (normalize/merge/phrase/redact, the activity-episode state, the optional LLM stage all stay
  single-writer), stores them, stamps provenance (`docs.clone_id = "<account>/<brain>"`, the column every process already fills)
  and broadcasts `mem_changed` to every other hive. RAPTOR maintenance stays main-only (unchanged).
- Reads: direct reads of the shared WAL file. Why not reads over RPC too: recall runs every turn, often several queries; an RPC per
  read adds latency and main CPU for nothing, while WAL readers never block the writer and see every committed write. Freshness:
  `mem_changed` reloads a hive's caches (memory, vector cache) at once; the 2 s `PRAGMA data_version` poll stays as the backstop.
- Contention: hives don't write docs themselves, so the writer queue is main's gate. A hive writing **inside its own transaction**
  never waits on main (that would hold its SQLite write lock while main tries to write); it writes locally, still into the one
  store, and counts a fallback. Main unreachable = the same fallback.
- Structured tables (beliefs, directives, notes, the world index's chunks, `placed_blocks`, `hive_inventory`, crew boards) are
  already shared rows in the one file with `clone_id` provenance; they stay direct writes (WAL + busy timeout + the per-process
  gate). Moving them behind main's queue too is a later step if contention shows up in the latency tracker.
- Self profile: `HiveOptions.SharedSelf` (default true) puts every hive account on main's self scope; the per-clone personality
  shards remain only behind `SharedSelf=false` (the old shard tests and old dbs).
- **Placed blocks / protection**: one `placed_blocks` table, so "self-placed is always minable" means placed by *any* of our hives
  (`Protection.OverridesSoft` reads the shared log).
- **Budget**: one daily/monthly cap across all hives (`BudgetLedger`: main's spend + every clone's, DeepSeek priced the same way).

## The brain's interface (token budget)

`hive {op: spawn|release|list|handover, task, name, script, code, args, after, kind, bodies, brain}`:

- `kind`: default `script` when a script is given, `hive` when a brain is named, a named clone keeps its kind, else **`subhive`**
  wherever subhive bodies exist (the old one-process-per-helper path, "picked maid_chan_67 for Chop", is only taken when asked for).
- `bodies`: kind=subhive: N bodies the asking hive's own brain drives ("use 2 agents" = `bodies=2`); kind=hive: ONE hive process
  whose brain drives N subhive bodies ("spawn a deepseek hive with 15 agents to do X" = `kind=hive brain=deepseek bodies=15 task=X`).
  Main caps N by memory (`BodiesThatFit`) and the player cap up front and says so ("only 7 of 15 bodies fit (memory: room for 7)");
  every answer says what exists ("none in game yet"), and the cached hint says "Report only what spawned".
- `brain`: `opus` (default) | `deepseek` | a model profile name (the old `profile`, still read). Shown only when a profile is usable.
- `release name|all` replaces `recall` (still accepted).
- One cached prompt line (`HiveTools.KindHint`, 208 chars). Measured: the hive tool with `bodies` ≈115 tokens (≤ `ToolBudgets.HiveTool`
  115; the description shrank to "Spawn/reassign (not chat)."), brain tools + hive ≈2,940 (≤ 2,950); before: ≈114, the plain one
  ≈77; the description got shorter to pay for `kind`. A hive clone's own tool ≈95, below main's, so a hive clone's brain set stays
  under main's: one cap (`ToolBudgets.Brain` 2,950).
- DeepSeek: available only when the `Hive:Accounts:DeepSeekProfile` model profile (default `deepseek`, Ollama) is usable; otherwise
  `no: brain deepseek not available (…); use brain opus; spawned nothing`. Its brain has no prompt cache, so its tool set is compact
  (`App/Hosting/CompactBrainTools`: design, reflect_self, identity, mark/describe_structure, watch_demo, ask_stand leave the brain
  tier, ≈990 tokens per call); it runs saved scripts (crews included), never writes them.

## How today's code maps

| before | now |
|---|---|
| `Hive:CloneUsernames` fixed roster (67/69/420/1337), `Hive:MaxClones` 4 | ignored at startup (`HiveConfig.IgnoreLegacyRoster`, `HiveOptions.IgnoreLegacyRoster`), adopted into the managed list once; accounts allocated per spawn; `MaxClones` an optional ceiling (0 = player cap only) |
| `MainHive.Create` needed configured clones | runs whenever `Hive:Enabled`; `HiveHierarchyWiring` builds accounts, whitelist, tree, bus, memory host, chat budget |
| `HiveSupervisor` slots from the config | copy-on-write slot map; `AddSlot` (allocated or restored managed accounts); `IHiveAccounts` hooks: `Allocate` (no free slot), `Assigned`, `BeforeLaunch` (whitelist add), `Released` (whitelist remove) |
| `ChatArbiter` clone names fixed | `AddClone`; the roster message carries every known clone so clone processes learn new names |
| `CloneMode` (one process per clone) | the **hive** kind (Opus or DeepSeek profile); gets the bus token, memory write-through, inbox, its own `hive` tool when kind=hive, the chat budget |
| script spawns (`CloneScriptRunner`, `after: logout`) | the **script** kind, owned by the spawner, released (and de-whitelisted) when it logs out |
| `InProcBodies` / `BodyRegistry` (one brain, many bodies) | the **subhive** kind host (`ISubhiveHost`, subhive mode) in main and in every hive-kind clone; dynamic accounts (`BodyRegistry.AddAccount`) |
| `Hive:MaxClones` 4 | `Hive:Resources` (memory ceiling) + `Hive:MaxClones` as an optional hard cap (default 0 = none) |
| the sub-hive plan (DeepSeek v4 flash on `maid_*` accounts) | a hive-kind clone with `brain=deepseek` on a managed `maid_chan_<id>` account |
| per-clone personality shards | one shared self profile (`SharedSelf`) |
| Explorer Live Hive: tiles from live bots | plus a **Hive tree** tab (`/api/hive/tree`: owner → children, kind, brain, state, task) |

## Implementation steps (shipped in order, each green)

- (a) Fixed names out of config and code paths; old config still reads.
- (b) Allocator + managed list + reconcile + the gate's scoped `/whitelist` exception.
- (c) Kind model, ownership tree, spawn API on the `hive` tool (kind/brain/release), budget pinned.
- (d) The bus with typed messages; main routes; local + authenticated.
- (e) Script-only bodies: the script kind, owned by the spawner, released at the end.
- (f) Explorer Live Hive tree.
- (g) One memory: write-through to main's groomer with provenance, instant invalidation, shared self profile.
- (h) Subhive bodies wired (main's and a hive's, `host`/`subhive_gone`), `bodies=N`, default kind subhive, the resource ceiling,
  small view distance + shared chunk store + lazy sections, spawn protection as an account-aware world fact.

## Tests

`tests/MineClaw.Hive.Tests/HiveHierarchyTests.cs`: allocator rules, collisions, reuse, caps; whitelist only through main and only
for managed names (model path refused, gate exception exact and one-shot); bus whitelist only for the asker's own clones; startup
reconcile after a crash + legacy adoption; the empty state; spawn of each kind (opus/deepseek/script/subhive, unavailable cases);
release cascade order (deposit → logout → whitelist remove, children before owners); owner logout takes its clones; bus routing,
authentication, never chat; speech scrub; chat budget; the hive tool budgets; the supervisor allocating, whitelisting before login
and removing after release.
`tests/MineClaw.Hive.Tests/SubhiveHierarchyTests.cs`: main's bodies whitelisted before login and released after a deposit; a DeepSeek
hive with 15 bodies is one process (or an honest no); the memory ceiling and the player cap; cgroup v2/v1 parsing; a hive's bodies over
the bus (host, reuse on restart, only the host reports gone) and its release cascade bodies first. `tests/MineClaw.App.Tests/SubhiveHostTests.cs`:
a DeepSeek hive (fake model) hosting 3 bodies on fake servers, accounts through main, a saved crew chopping in parallel lanes, the
release cascade with deposits; main in hive mode keeps its hive tool within the brain budget. `tests/MineClaw.Bot.Tests/SpawnProtectionFactTests.cs`.
`tests/MineClaw.Hive.Tests/SharedMemoryTests.cs`: two hives read each other's writes instantly; a DeepSeek hive's write goes through
main's groomer with provenance (and the in-transaction fallback); placed blocks shared across hives in protection; one budget.

## Remaining

- **TUI and supervisor status panels** are built from a roster at startup (main alone now); dynamic clones show in `/api/bots`, the
  Live Hive tiles and the tree, but the TUI tabs and `/api/status?bot=` for clones need a dynamic source list.
- **Structured stores behind main's writer** (beliefs, directives, notes over `mem_write`-style messages) if the shared-WAL
  writes ever contend.
- A clone process's own hive tool only appears for `kind=hive` tasks at login; a later reassignment doesn't add or remove it.
