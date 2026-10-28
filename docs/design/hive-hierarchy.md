# Hive hierarchy: kinds, ownership, dynamic accounts, the off-channel bus, one memory

Status: steps (a)–(f) below are implemented (see "What exists now"); "Remaining" lists what isn't.
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
| subhive (kind) | a body driven by its owner's brain (`Bodies:Mode=inproc`, `BodyRegistry`) |
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
└── 106 subhive                owner main  (inproc only)
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
   list yet must leave one slot per offline regular (`SlotPolicy.CanSpawn`); `Hive:MaxClones` > 0 is an extra ceiling (0 = only
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

`hive {op: spawn|release|list|handover, task, name, script, code, args, after, kind, brain}`:

- `kind`: `hive` (default; `script` when a script is given) | `subhive` | `script`.
- `brain`: `opus` (default) | `deepseek` | a model profile name (the old `profile`, still read). Shown only when a profile is usable.
- `release name|all` replaces `recall` (still accepted).
- One cached prompt line (`HiveTools.KindHint`). Measured: the hive tool ≈114 tokens (≤ `ToolBudgets.HiveTool` 115), the plain one
  ≈77; the description got shorter to pay for `kind`. A hive clone's own tool ≈95, below main's, so a hive clone's brain set stays
  under main's: one cap (`ToolBudgets.Brain` 2,950).
- DeepSeek: available only when the `Hive:Accounts:DeepSeekProfile` model profile (default `deepseek`, Ollama) is usable; otherwise
  `no: brain deepseek not available (…); use brain opus`.

## How today's code maps

| before | now |
|---|---|
| `Hive:CloneUsernames` fixed roster (67/69/420/1337), `Hive:MaxClones` 4 | ignored at startup (`HiveConfig.IgnoreLegacyRoster`, `HiveOptions.IgnoreLegacyRoster`), adopted into the managed list once; accounts allocated per spawn; `MaxClones` an optional ceiling (0 = player cap only) |
| `MainHive.Create` needed configured clones | runs whenever `Hive:Enabled`; `HiveHierarchyWiring` builds accounts, whitelist, tree, bus, memory host, chat budget |
| `HiveSupervisor` slots from the config | copy-on-write slot map; `AddSlot` (allocated or restored managed accounts); `IHiveAccounts` hooks: `Allocate` (no free slot), `Assigned`, `BeforeLaunch` (whitelist add), `Released` (whitelist remove) |
| `ChatArbiter` clone names fixed | `AddClone`; the roster message carries every known clone so clone processes learn new names |
| `CloneMode` (one process per clone) | the **hive** kind (Opus or DeepSeek profile); gets the bus token, memory write-through, inbox, its own `hive` tool when kind=hive, the chat budget |
| script spawns (`CloneScriptRunner`, `after: logout`) | the **script** kind, owned by the spawner, released (and de-whitelisted) when it logs out |
| `InProcBodies` / `BodyRegistry` (one brain, many bodies) | the **subhive** kind host (`ISubhiveHost`); not wired yet, so `kind=subhive` answers "not available here" |
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

## Tests

`tests/MineClaw.Hive.Tests/HiveHierarchyTests.cs`: allocator rules, collisions, reuse, caps; whitelist only through main and only
for managed names (model path refused, gate exception exact and one-shot); bus whitelist only for the asker's own clones; startup
reconcile after a crash + legacy adoption; the empty state; spawn of each kind (opus/deepseek/script/subhive, unavailable cases);
release cascade order (deposit → logout → whitelist remove, children before owners); owner logout takes its clones; bus routing,
authentication, never chat; speech scrub; chat budget; the hive tool budgets; the supervisor allocating, whitelisting before login
and removing after release.
`tests/MineClaw.Hive.Tests/SharedMemoryTests.cs`: two hives read each other's writes instantly; a DeepSeek hive's write goes through
main's groomer with provenance (and the in-transaction fallback); placed blocks shared across hives in protection; one budget.

## Remaining

- **Subhive host**: wire `ISubhiveHost` to `InProcBodies`/`BodyRegistry` so main (and later a hive process) can add bodies its brain
  drives with an allocated account (InProcBodies still takes its accounts from `Bodies:Accounts`).
- **TUI and supervisor status panels** are built from a roster at startup (main alone now); dynamic clones show in `/api/bots`, the
  Live Hive tiles and the tree, but the TUI tabs and `/api/status?bot=` for clones need a dynamic source list.
- **Structured stores behind main's writer** (beliefs, directives, notes over `mem_write`-style messages) if the shared-WAL
  writes ever contend.
- A clone process's own hive tool only appears for `kind=hive` tasks at login; a later reassignment doesn't add or remove it.
