# MineClaw Memory Explorer: feature and data-source spec (for the web rebuild)

Source: `tools/MemoryExplorer` (Avalonia 12, .NET 10, CommunityToolkit.Mvvm). Tests: `tests/MineClaw.MemoryExplorer.Tests`
(incl. `LiveHive/`). References `src/MineClaw.Agent`, `MineClaw.Raptor`, `MineClaw.World`, `MineClaw.Render`, `MineClaw.Scripting`.
Everything below comes from the code. Secret values are never given here, only key names.

---------------------------------------------------------------------------------------------------------------------------------

## 0. Architecture in one screen

There are two separate data planes, and every page uses one or both:

| Plane | What | How | Write? |
|---|---|---|---|
| **A. Local SQLite file** | the bot's db: dev db, snapshot copy, or seed | `AgentDb.OpenReadOnly(path)` (Microsoft.Data.Sqlite, `Mode=ReadOnly`, `Pooling=false`) inside `MemoryBank` | read-only for browsing; writes only through `LocalAdminTarget` (a second connection, read-write, via the bot's own stores) while edit mode is on |
| **B. Live Supervisor HTTP** | the HTTP server inside the running main bot (`src/MineClaw.App/Supervisor/*`) | `HttpClient` + header `X-Supervisor-Token` | GET only, except the `/api/admin/*` POSTs (server flag `Supervisor:AllowAdminWrites`) |
| C. ssh side channel | snapshot fetch, port tunnel | spawns `ssh` processes | snapshot writes local files only |

Shell (`MainWindowViewModel`) has a left nav with 10 pages (key → label). Order:
`overview` Overview · `tree` RAPTOR tree · `search` Search · `memory` Memory · `personality` Personality · `shards` Shards ·
`scripts` Scripts · `designs` Designs · `audit` Edits & audit · `live` Live Hive.

Each `PageViewModel.LoadAsync(bank)` runs again whenever a db is opened or reloaded (reload follows every local write). Live Hive
does not depend on the db. It has its own `Activate`/`Deactivate`: its polling stops while the tab is hidden.

---------------------------------------------------------------------------------------------------------------------------------

## 1. Config, discovery, auth

### 1.1 Config layering (`Services/ExplorerConfig.cs`), lowest priority first
1. `<exe dir>/appsettings.json`
2. `<repo>/src/MineClaw.App/appsettings.json` (the bot's gitignored config, `ExplorerPaths.BotConfig`)
3. the optional `--config FILE` or "Open config file…" (it must exist)
4. plain environment variables, then variables with the `MINECLAW_` prefix (`__` = `:`)

Only the `Agent` section is bound to `AgentOptions`. The rest is read raw. If `Agent:Llm:ApiKey` is empty or ends in `...`, the
code falls back to env `ANTHROPIC_API_KEY`.

**Keys the explorer reads**

| Key | Used for |
|---|---|
| `Explorer:SupervisorUrl` (env `MINECLAW_Explorer__SupervisorUrl`) | Supervisor base URL for the Scripts "Live" source and the Live edit target. Default `http://10.1.2.73:8765` |
| `Explorer:DefaultPacksDir` | default knowledge packs folder (else `<exe>/shards/defaults`, else `<repo>/shards/defaults`) |
| `Agent:Embeddings:*`: `Provider` (AzureOpenAI \| ollama), `Endpoint`, `ApiKey`, `Deployment`, `ApiVersion`, `Model`, `Dimensions`, `MaxBatch`, `TimeoutSeconds`, `MaxRetries`, `MaxInputChars`, `PricePerMTok` | embeds the query for hybrid search, semantic shard filters and re-embedding on edits |
| `Agent:Llm:*`: `ApiKey`, `CheapModel` (default `claude-haiku-5-5`), `CheapProvider`, `Ollama:*`, plus the provider validation (`ValidateProviders`, 5.5-generation rule) | RAPTOR summary rebuilds, groom LLM rewrite |
| `Agent:Rag:*` (incl. `Rag:Raptor:Mode` collapsed\|traversal\|hybrid, `CandidateTopK` 24, `CandidateMaxTokens`, `TraversalTopK` 5, `RrfK`) | the search pipeline (same as the bot) |
| `Agent:Memory:Groom:*` incl. `Rewrite:Enabled` | groom preview |
| `Agent:Self:TrustedPlayers`, `Agent:Self:RegularPlayers`, `Agent:Chat:PlayerNicknames` | person picker for shards |
| `Agent:Prices` | groom rewriter cost |
| env `MINECLAW_SUPERVISOR_TOKEN` | Supervisor token (overrides the file) |

The UI only shows status strings such as "embeddings: configured (deployment X)", "token: from env" or "token: missing". It never
renders a key or an endpoint.

### 1.2 Finding the db (`Services/ExplorerPaths.cs`)
- Repo root = the first parent of the exe dir or the CWD that holds `MineClaw.sln`.
- `data/mineclaw.db` (LocalDev), `data/alice-snap/memory-YYYYMMDD-HHmmss.db` (Snapshot, newest first), `data/seed/mineclaw-seed.db` (Seed).
- On start: `--db PATH`, else the first known db. The "recent" dropdown lists up to 12.
- `data/server-version.txt` contains "1.12.2" or not: this selects block names and textures (b1.8.1 by default).
- `data/assets/`: texture cache (terrain.png/items.png or client jar, see the Render section).
- `data/shards/`: exported `.mcshard` files.

### 1.3 Finding the Supervisor and authenticating (`Services/SupervisorEndpoint.cs`)
- Base URL: config `Explorer:SupervisorUrl` (Scripts and Edit). **Live Hive ignores the config.** Its URL text box starts at the
  hard-coded `SupervisorEndpoint.DefaultUrl` = `http://10.1.2.73:8765` and can be edited at runtime.
- Token: env `MINECLAW_SUPERVISOR_TOKEN`, else the file `~/.mineclaw-supervisor-token` (trimmed). It is sent only as the request
  header `X-Supervisor-Token`. The server also accepts `?token=`, but the explorer never uses it.
- Server auth behaviour: world, bots and view routes answer **401** without a token. Script, vision and controller routes answer
  **403**. Admin routes answer 403 unless `Supervisor:AllowAdminWrites=true` AND a token is set AND it matches. Every method other
  than GET/HEAD is 405, except admin.
- "Tunnel" button: runs `ssh -N -o ExitOnForwardFailure=yes -o ServerAliveInterval=30 -L 8765:10.1.2.73:8765 alice`, then switches
  the URL to `http://localhost:8765` and reconnects. A second click kills the process.

### 1.4 CLI flags (`App.axaml.cs` StartupOptions)
`--db`, `--config`, `--page <key>`, `--theme light|dark`, `--screenshot OUT.png` (headless render, then exit), `--query`, `--node N`,
`--graph`, `--graph-layout icicle|radial|nodelink`, `--color-by kind|time|cluster`, `--focus N`, `--tip N`,
`--freecam-profile OUT.json`, `--livehive-watch OUT.json`, `--profile-tab feeds|freecam`, `--profile-seconds`, `--renderer cpu|gl`,
`--profile-size WxH`. The last group is diagnostics only (FreecamProfiler, LiveHiveWatch) and does not need a web equivalent.

---------------------------------------------------------------------------------------------------------------------------------

## 2. Shell / header (`Views/MainWindow.axaml`, `ViewModels/MainWindowViewModel.cs`)

**User sees:**
- Source title: "Local dev db" / "Snapshot from alice" / "Clean-state seed" / file name.
- Source detail: path · size (db + -wal + -shm) · `schema v{AgentDb.SchemaVersion}`.
- Snapshot age ("snapshot 2026-10-28 14:30 (3 h ago)").
- Knowledge pack chip "knowledge b1.8.1" with a tooltip (`KnowledgePack.Active(db)`: `meta('knowledge_pack')`).
- Embeddings status.
- Mode badge `READ-ONLY` / `EDIT MODE`, with an edit-mode toggle.
- Theme toggle (System → Light → Dark).
- Global search box (Ctrl+K), status bar.

**Actions:**
- Open db (Ctrl+O, file picker).
- Recent dbs dropdown.
- "Open config file…": layers the file in and reloads.
- **Fetch snapshot from alice** (cancellable). See below.
- Reload (F5).
- Global search: Enter opens the Search page and runs the query.
- Drag-and-drop a `.mcshard` onto the window: starts an import (Shards page).
- Copy to clipboard: available on most pages, with a status line.

**Snapshot fetch (`Services/SnapshotService.cs`):** host `alice`, LXC container 114, remote db `/var/lib/mineclaw/mineclaw.db`.
1. `ssh -o BatchMode=yes -o ConnectTimeout=10 alice "pct exec 114 -- sh -c 'command -v sqlite3 || true'"`.
2. If sqlite3 exists: `sqlite3 -readonly DB ".timeout 10000" ".backup /tmp/mineclaw-explorer-<ms>.db" && cat tmp; rm tmp`. Stdout
   streams to `<target>.part`, which is then renamed.
3. Otherwise: tar `db + -wal + -shm` → extract locally → drop `-shm` → `VACUUM INTO target`.
4. Verify: `PRAGMA quick_check` = ok, and a `docs` table exists. Saved as `data/alice-snap/memory-<utc>.db` (a numeric suffix on clash).
   Progress strings go to the status bar.
   *Web note: this is a backend job (ssh from the NestJS host). It needs ssh keys and a writable data dir.*

---------------------------------------------------------------------------------------------------------------------------------

## 3. Overview page (`OverviewViewModel`, `MemoryQueries.Counts/VectorModel`, `RaptorStats`)

**Shows:**
- **Count cards** (label, count, detail, group). The SQL behind each card:
  - Facts `count(*) notes`
  - Recipes: `docs where kind='gamedata' and tag in ('recipe','smelt')`
  - Claims: total plus "N status · …" from `GROUP BY status`
  - Beliefs: `claims where status in ('verified','provisional')`
  - Episodes: `docs kind='episode'`
  - Activity `activity_log`, Chat lines `chat_log`, Structures `structures`, Places `waypoints`
  - World observations: `observations where removed is null`, top 4 by kind
  - Remembered chunks `world_chunks`
  - Personality traits: `self_profile`, grouped by section
  - Lessons `knowledge_corrections`, Routines `routines`
  - Docs indexed `docs`, Vectors `vectors`
  - A card is skipped when its table is missing (older dbs).
- **DB lines:**
  - File, Size
  - Schema `v{n} · FTS5 yes/no · R*Tree yes/no`
  - Vectors `N × model @ dims` (top `GROUP BY model,dims`)
  - Search mode note (hybrid vs lexical fallback and why)
- **RAPTOR stats** (only if a tree is loaded):
  - Per-layer table: layer, name, nodes, avg children, avg/max tokens, dirty+pending count, bar share.
  - Lines: embedding model + dims, built at, leaves at build, inserted/removed since, leaf→parent cosine at build (mean ± std),
    inserted leaf→parent cosine, insert temperature τ, dirty/pending summaries, stored layers.
  - Headline "N nodes · L leaves · K summary layers". When no tree, a note: "no RAPTOR tree stored in this db", "the stored tree
    could not be loaded", …

**Data:** read-only SELECTs, plus the tree object `RaptorTree` from `MemoryBank.Raptor.Index`.

---------------------------------------------------------------------------------------------------------------------------------

## 4. RAPTOR tree / graph page (`RaptorTreeViewModel`, `Controls/LayeredGraph.cs`, `Controls/Graph/*`)

### 4.1 Tree mode
- Lazy tree view of the roots, ordered by index. Each child list is sorted by children count desc, then index. Node row: title
  (leaf: `docKind · title`; summary: the first 90 chars), kind (`L{layer}` or doc kind), meta `#idx · N children · T tok`, dirty flag.
- Right-click on a node: "Export this subtree as a shard…".
- **Detail pane** for the selected node:
  - title (leaf: the doc title; summary: "Layer L summary #i"), full text
  - meta: tokens, parents, pinned, children, leaves below, dirty, placeholder summary
  - doc line `ref · kind/tag · source · updated`
  - breadcrumb root→node (clickable)
  - children list (sorted by tokens) and parents list
- **Actions:**
  - Go to `#node`, Collapse all, Copy node text
  - "Show leaves under": every leaf below the node, deduplicated, with token sum; Copy leaves (TSV); Hide
  - Export subtree as a shard (Shards export dialog, mode `subtree`, leaf refs = `ShardCollector.LeafRefsUnder(tree, node)`)
  - **Rebuild subtree** (edit mode; see §11)

### 4.2 Graph mode (toggle)
- `GraphModel.Build(tree, docOf)` turns the tree into a partition tree. Each node sits under its *primary* parent; other
  soft-cluster parents are kept separately. Weight = leaf count. Per-node data: category (Episode/Activity/Fact/Recipe/Chat/World/
  Self, majority of the leaves), newest leaf time + time-quantile rank, root cluster, TF-IDF topic terms (`TopicLabeler`, with
  per-layer document frequency and stop/action-word lists; deterministic, no model calls, cached per tree state key).
- **Layouts:** Icicle (default; band width = leaf count, roots on top), Radial/sunburst, Node-link (best for a focused subtree).
- **Colour by:** kind | time | cluster. Opacity follows tokens.
- **Interaction:** wheel zooms around the pointer; drag pans; click selects (syncs the detail pane); double-click focuses a subtree;
  Esc zooms out; arrows walk parent/child/sibling; Enter selects; F fits; Home shows the whole tree. Hover tooltip (can be pinned).
  Minimap. Level of detail: individual leaves are hidden until each one is a few pixels wide.
- Focus breadcrumb "all memory › L3 term › …". **Search hits** from the Search page are lit up ("N hits for q", with a clear button).
- The server side doesn't need to do the layout. It is all client geometry (`PartitionLayout.cs`, ~300 lines), and d3-hierarchy
  partition can stand in for it.

**Data:**
- `meta('raptor_meta')` (JSON `RaptorTreeMeta`)
- `docs` rows with `raptor_idx` not null: `raptor_idx,id,ref,kind,tag,title,source,updated,dimension`
- summary docs: `kind='summary'`, `ref='raptor:{idx}'`, level = layer
- `doc_edges(parent,child,weight)` for parent→child links (multi-parent)
- `vectors(doc_id,model,dims,hash,vec)` (float32 LE blob)
- Loaded by `RaptorMemory.LoadAsync` with `LoadWritesBack=false` and an embedder (`StoredModelEmbedder`) that reports the stored
  model/dims and refuses to embed unless the configured provider matches.

---------------------------------------------------------------------------------------------------------------------------------

## 5. Search page (`SearchViewModel`, `Services/SearchService.cs`)

**User:**
- Query box (Enter).
- Kind filter: all kinds | episode | knowledge | gamedata | structure | note | player | world | claim | skill | routine | summary | selfnote.
- Dimension: any | overworld | nether.
- Changing a filter re-runs the search. Limit 30.

**Shows:**
- Mode note: hybrid (FTS/BM25 + vectors + RAPTOR) / hybrid without tree / lexical fallback, with the reason (no embedding config,
  vectors from another model, embed call failed).
- `N hits in X ms · query embedded`.
- **Hit rows:** rank, kind/tag, title, snippet (260 chars), score (4 dp), signals (`bm25 x · cos y · d m`), updated, bounds/where
  (nether flag), RAPTOR breadcrumb (root→leaf crumbs, clickable to open in tree).
- **Tree hits list:** the tree's own candidate lists before fusion. List name (`collapsed` / `traversal`), `leaf #i` or `L{layer} #i`,
  score, preview.
- **Actions:** Copy text; "Show in graph" (opens the graph focused on the node's grandparent, all hits lit); open the node in tree.

**Data / logic (hard to port, flag):**
- `RagIndex.Search(RagQuery{Text,Kinds,Dimension,Limit}, queryVector, augment)`: FTS5 BM25 over `docs_fts` (porter unicode61),
  flat cosine over `vectors`, spatial via `docs_rtree`, and RAPTOR collapsed/traversal lists from
  `RaptorMemory.Augment` / `RaptorIndex.Retrieve`, fused by RRF (`1/(RrfK+rank+1)`), plus the bot's own weighting/boost rules.
- Query embedding: `RagIndex.EmbedQueryAsync` → configured provider (Azure OpenAI / Ollama). Vectors are used only when stored
  `model`+`dims` match the provider (`MemoryBank.OpenAsync` checks `vectors` counts).
- The result must match the bot exactly. That is the whole point of the page ("the bot's recall pipeline").

---------------------------------------------------------------------------------------------------------------------------------

## 6. Memory browser page (`MemoryBrowserViewModel`, `Services/MemoryQueries.cs`, `EditPrefill.cs`)

**Left:** 16 kinds in groups, each with a row count. **Grid columns:** Name, Type, State, Dim (overworld/nether/""), Conf.,
Updated (sortable), Text preview. **Filters:**
- free text (every word must be in `id name type state text`, lower-cased)
- dimension: any | overworld | nether | no position
- state: values present in the kind
- Clear

**Detail pane:** a props table (per kind) + "RAPTOR leaf #n" when the doc is in the tree. Extra list:
- claims: evidence
- containers: contents

**Row actions:**
- Copy row
- Show in tree
- Edit / Delete / Add (edit mode; only kinds with an edit kind)
- Rebuild kind (edit mode)
- "Export this kind as a shard (current filter)…"
- "Export everything about this person…" (prefilled from row props `player` / `claimant` / `subject`)

| Kind key | Label | Edit kind | SQL (read-only) | Notable props / text |
|---|---|---|---|---|
| facts | Facts | fact | `notes(id,subject,text,created)` desc | DocRef `note:{id}` |
| claims | Claims | claim | `claims(id,claimant,text,category,stakes,confidence,status,reason,plan,risk,directive,attempts,checked,created,updated,clone_id,dimension,x,y,z)` by updated | evidence: `claim_evidence(at,kind,polarity,weight,ref,note) where claim_id` |
| beliefs | Beliefs | claim | same, `status in ('verified','provisional')` | |
| personality | Self-profile | trait | `self_profile(id,section,player,text,strength,created,reinforced,reason,scope,observations)` | |
| structures | Structures | structure | `structures(name,kind,taught_by,confidence,data,updated,dimension)`; `data` = camelCase JSON `Structure` (min,max,note,steps,keyBlocks,history) | DocRef `st:{lower name}` |
| places | Places | place | `waypoints(name,x,y,z,note,updated,dimension)` | `wp:{lower}` |
| chests | Chests | observation | `observations where kind in (chest,trapped_chest,furnace,dispenser)` | contents: `container_items(name,count,damage) where obs_id` |
| gencells | Gen cells | observation | `observations kind='cobble_generator'` | |
| observed | Observed places | observation | the other observation kinds | cols `id,kind,key,min_*,max_*,text,source,confidence,annotation,first_seen,last_seen,seen_count,removed,dimension,contents_at,contents_by` |
| lessons | Lessons | lesson | `knowledge_corrections(id,title,correction,source,updated)` | |
| episodes | Episodes | – | `docs kind='episode'` | doc cols `id,ref,kind,tag,title,body,source,updated,dimension,min_x,raptor_idx` |
| activity | Activity log | – | `activity_log(id,at,player,kind,category,text,place_name,x,y,z,confidence,dimension)` LIMIT 50000 | |
| chat | Chat log | – | `chat_log(id,at,kind,player,text)` LIMIT 50000 | |
| routines | Routines & scripts | – | `routines(name,data,updated)`; `data` JSON `Routine` (script.code → type "script") | |
| recipes | Recipes & game data | – | `docs kind='gamedata'` | |
| knowledge | Knowledge & skills | – | `docs kind in ('knowledge','skill')` | |

All timestamps are unix ms (`AgentDb.FromUnixMs`). Every query copes with missing tables and columns (`sqlite_master` and
`HasColumn` probes). *Note: each kind loads every row into memory (up to 50k for chat and activity). A web API should page.*

---------------------------------------------------------------------------------------------------------------------------------

## 7. Personality page (`PersonalityViewModel`, `MemoryQueries.Self*`)

- **Scope picker:** `SELECT DISTINCT scope FROM self_profile UNION … self_profile_history` (default `main`). Hive clones have
  their own scopes.
- **Trait groups** in section order trait, like, dislike, opinion, joke, nickname, relationship, milestone. Each entry: text,
  "about {player}", strength bar (0..1 → %), meta `#id · seen N× · reinforced MM-dd HH:mm`, reason.
- **History:** `self_profile_history(version,at,op,section,text,reason,source[,scope])`, newest first, limit 500 (ops: add,
  reinforce, adjust, remove).
- **Mood (hours-scale aggregates):** `self_acc(scope,family='mood',key,dim,sum,n)`, only rows where key `''` or `'_'`. Value
  shown as `+0.00`, bar maps −1..1 to 0..100.
- **Self-tree abstractions:** `self_abstractions(scope,layer,idx,text,leaves)`, ordered layer desc.
- **Summary:** `N entries · M versions · last change … (source)`. Read-only (edits go through the Memory browser, kind `trait`).

---------------------------------------------------------------------------------------------------------------------------------

## 8. Shards page (+ export and import dialogs)

**Page (`ShardsViewModel`, `Services/ShardService.cs`):**
- **Shard files** in `data/shards/*.mcshard`, read from the manifest only (`ShardFile.ReadManifest`). Per file: name, kind label
  (about a person / knowledge pack V / mode pack M (V) / RAPTOR subtree / filter), record count, counts by kind, created, size,
  description, embedding model, or the error text when the file is broken. Actions: "Import this shard…", Open folder, Refresh.
- **Default packs** (`ShardService.DefaultPacksDir`, `PackIndex.Load(dir)` → `index.json`):
  - Version packs and mode packs, each with `KindLabel`, description, counts, `bytes · N vectors model@dims · K to embed · builder ·
    source hash[..12]`, docs files.
  - Error text when the file is missing or the manifest `ContentHash` doesn't match index.json.
  - `.mcshard` files in the folder that are not in the index are listed from their manifest.
  - Buttons: **Preview swap** (version pack → import mode `swap-pack`), **Preview add mode / remove mode** (mode pack), Open folder.
- **This db:** active knowledge pack label (`KnowledgePack.Active(db).Label`, e.g. "b1.8.1-gamedata (b1.8.1) + skyblock").
- **History:** audit entries with `kind=='shard' && op in (import,export)`, newest first, max 200. Sources: the local audit JSONL
  and, when edit mode is on with the Live target, `GET api/admin/audit`.
- **Export buttons:** By filter…, About a person…, Knowledge pack…. Export is also reachable from the tree and the browser.
- **Import:** Import shard… (file picker), drag-drop, or Import selected. Progress bar with stage text; Cancel.

**Export dialog (`Views/ShardExportDialog.cs`). Always runs locally on the opened read-only bank. There is no live export.**
- Name, description.
- Mode radio (each mode has its own panel):
  - **subtree**: only offered from the tree; shows the node label.
  - **filter**: kind checkboxes (fact, claim, rule, trait, lesson, place, structure, episode, knowledge, gamedata); dimension;
    query text; "Semantic" checkbox. Semantic uses the bot's hybrid search (`RagIndex.SearchAsync`, limit 200 → ref set).
  - **person**: player picker (`ShardService.People`: login + nicknames) and an editable comma list of names.
  - **pack**: Minecraft version (defaults to the active one); "also carry docs/knowledge chunks".
- Flags: include standing rules, include world memory (places, structures), include embeddings, include RAPTOR summaries as a cache.
- **Preview:** `ShardExporter.Preview(req, refs)` → `ShardPreview{Counts, Total, Vectors, EmbeddingModel, Dims, Summaries, Bytes,
  SecretsRedacted, Samples, Notes, PackVersion}`.
- **Export…:** save-path picker (suggests `data/shards/{slug}-{yyyyMMdd-HHmm}.mcshard`) → `ShardExporter.Export`. Appends an
  `export` audit line to `<db>.admin-audit.jsonl` (file, kind, records, content_hash, pack).

**Import dialog (`Views/ShardImportDialog.cs`):**
- Shows the manifest summary and the target ("local: path · a backup is taken first" or "live: url · the bot applies it…").
- Mode radio: Merge | Replace section | Swap version pack | Add mode | Remove mode. Which ones are enabled depends on the shard
  kind and pack/mode. A hint text explains each mode.
- Conflict: Keep the newer copy | Prefer the shard | Keep mine. Checkboxes: include rules, include world.
- **Preview diff** → `ShardDiff`:
  - Counts: added, changed, conflicts, removed, unchanged, skipped, writes.
  - Rebuild list + estimate; reusable vectors / to embed; pack from→to; modes from→to; notes; refused reason.
  - Item list (chip `added|changed|conflict|removed|unchanged|skipped`, "(kept)" when not applied, `kind · label`, note), sorted
    removed > conflict > changed > added > skipped. "Show unchanged" toggle.
- The button reads "Import" or "Import and rebuild". It is disabled when the diff is refused or empty.
- **Flow:**
  1. Needs edit mode (offers to turn it on).
  2. Session backup.
  3. `ImportShardAsync`.
  4. Then the scoped RAPTOR rebuilds. Local: runs here, reusing cached summaries. Live: follows the bot's `job_id`.
  5. Then reload.

**`.mcshard` format (`src/MineClaw.Agent/Admin/Shards/ShardFile.cs`, FormatVersion 1). A zip containing:**
- `manifest.json`: `ShardManifest`: format, name, description, created, createdBy, source{db,schemaVersion,bot}, kind,
  selection, packVersion, counts, total, contentHash, embeddings{model,dims,count,sha256}, summaries{count,sha256},
  secretsRedacted, pack{kind,name,version,mode,core,files,source,sourceHash,builder,needsEmbedding}.
- `records/{kind}.jsonl`: canonical `ShardRecord` lines {id, kind, dimension, created, updated, ref, docHash, source, cloneId,
  fields (sorted string map, hashed), info, hash = sha256(kind+fields)}, sorted by id.
- `embeddings.bin`: docHash → float32 LE vector.
- `summaries.jsonl`: {contextHash, layer, text}.
- Reading checks every hash and rejects mismatches with `ShardFormatException`. **Flag:** record canonicalisation and hashing,
  secret redaction (`ShardCollector`), the diff/merge policy (`ShardImporter`) and pack swaps (`KnowledgePack`) are all C#
  domain logic. Port them byte-for-byte or keep them in a C# service.

---------------------------------------------------------------------------------------------------------------------------------

## 9. Scripts page (`ScriptsViewModel`, `Services/ScriptLibrarySource.cs`, `ScriptDiff.cs`, `Controls/CodeHighlighter.cs`)

**Source picker:**
- **This database** (`DbScriptLibrarySource`): reads `routines.data` (JSON `Routine` whose `script.code` is non-empty) and
  `standing_orders.data` (JSON `StandingOrder`), then `ScriptLibraryReport.List/Detail` with **OfflineLint**. Offline lint
  counts every name the script calls as a known game function. Syntax, forbidden constructs and machine/loop checks still apply.
  Note "offline lint: game function names not checked".
- **Live (Supervisor)** (`LiveScriptLibrarySource`, URL from `Explorer:SupervisorUrl`, 8 s timeout):
  `GET api/scripts` → `{at, scripts:[ScriptLibraryEntry]}`, `GET api/scripts/{name}[/{v}]` → `ScriptVersionDetail` (404 → null).
  Lint uses the bot's real game functions.

**List:**
- Filter text over name, purpose, author, used-by and handler kinds.
- Summary "N saved scripts · J JS, L Lua · K in use · source".
- Row: name, `vX of N`, lang (JS / JS·Lua history / Lua), author (Opus (who) / Lua migration / legacy), params `n=64 → 80`,
  last run, success rate of runs, avg seconds + timings, used by, lint chip (errors / warnings / ok), flags (archived, skill X,
  duplicate of Y).
- Learned interrupt **handlers** appear as child rows `↳ kind · situation · 5 ok / 1 failed · then` (faded when demoted).

**Detail tabs:**
- **Source:** syntax-highlighted lines with lint marks; Copy code.
- **History:** versions `vN · lang · author · active`, `saved · ok/runs · avg s · lines · top failures`.
- **Diff:** "Compare with" picker → LCS line diff in hunks with 3 lines of context (`ScriptDiff`); `+a −d` summary; Copy unified diff.
- **Params & lint:** declared `params({...})` default → saved tuning; lint findings `L{n} message` (error/warn).
- **Runs:** at, ok/status · seconds · actions · fail line, outcome or params/timings/yield/body.
- **Handlers:** label, detail (note · author · learned on vN · learned at · last used), where (`at \`…\``), JS body.
- Header meta: language, author, saved, purpose, used by. Revert note, migration note.
- Read-only. There is no write/revert from the UI.

**Types** (`src/MineClaw.Agent/Routines/ScriptLibraryReport.cs`, snake_case on the wire):
- `ScriptLibraryEntry{name, version, versions, lang, author, author_kind, purpose, params[{name,default,tuned}], last_run, runs,
  success_rate, avg_seconds, timings, used_by[], lint{status,errors,warnings,findings[{severity,line,rule,message}],note},
  migration, archived, promoted_as, last_outcome, duplicate_of, updated, has_lua_history, handlers[ScriptHandlerInfo]}`
- `ScriptVersionDetail{script, version{v,lang,author,author_kind,saved_at,active,runs,successes,failures,avg_seconds,last_run,
  top_failures,lines}, code, lint, params, history[], runs[{at,ok,seconds,source,script,outcome,actions,fail_line,version,params,
  timings,body,yield,status}], revert_note, tuned_params}`
- `ScriptHandlerInfo{kind, signature ("kind|situation|where"), js, then, note, author, version, learned_at, last_used, successes,
  failures, demoted}`

**Flag:** `ScriptLint.Check` / `CalledNames` / `Params` (MineClaw.Scripting: JS and Lua parsing and rules) and
`ScriptLibraryReport` (versions, stats, used-by from standing orders) are C#. The simplest port path is to always read the Live
API, or to run a small C# "library report" service for offline dbs.

---------------------------------------------------------------------------------------------------------------------------------

## 10. Designs page (`DesignsViewModel`, `Services/DesignHistorySource.cs`)

- **List:**
  - `blueprints b` where status ≠ 'deleted', newest id first, with `count(*)` and `max(rev)` of `blueprint_revisions`.
  - Row `#id name`, meta `status · rN · K revisions · for requester · updated`.
  - Filter over id, name, status, requester. Summary "N designs · M revisions".
- **Detail:**
  - Preview picture: `blueprints.png` BLOB (the bot's rendered sheet).
  - Revisions: `blueprint_revisions(blueprint_id, rev, author, author_kind, message, tag, created, n)` desc. Label
    `rN · author (kind) · when · [tag]`, message `'msg' · n blocks`.
  - World note from `blueprint_world(blueprint_id, rev, seen, seen_by, diff, causes, blocks, air)`: "last seen by X at rN: diff (causes)".
  - **Diff:** From = a revision; To = another revision or "the world (last seen)".
    - Default: HEAD → world when the bot has looked, else HEAD~1 → HEAD.
    - Summary `+12 stone, −3 planks, ~2 oak stairs facing (a added, r removed, c changed, u not seen)`.
    - Cell lines `+ x,y,z name[:meta]`, `− …`, `~ … from → to`, max 500 shown. Copy diff.
- Block-set encoding (`MineClaw.World/Designs/DesignSnapshot`): `blocks` text = lines `id:meta=<rows>`, rows `y,z:x0/x1,x2;…`
  (x-runs); `air` = the same row format. **Flag:** `DesignDiff.Between/Against` (ignores state-only changes such as open
  doors, lit furnaces, grass on dirt) and block names per version (`Blocks.Get`, `GameData.Override(Release1122GameData)` when
  `server-version.txt` says 1.12.2) are C# data and logic.
- Read-only. Commits and reverts happen in the bot. The Supervisor also has `GET /api/blueprints`, `/api/blueprints/{id}` and
  `/view/blueprint/{id}` (PNG), but the explorer doesn't use them.

---------------------------------------------------------------------------------------------------------------------------------

## 11. Edits & audit page (`EditSessionViewModel`, `Services/AdminTargets.cs`, `Views/EditDialog.cs`, `ConfirmDialog.cs`)

**Edit mode:**
- Off by default. Toggled here or in the header.
- Turning it on for the local dev db asks for confirmation ("stop a locally running bot first").
- Opening a different db ends the session.

**Target picker:**
- **Local file**: `LocalAdminTarget`. `MemoryAdminContext.OpenLocal(dbPath, AgentOptions, embeddings, llm)` opens the bot's
  stores read-write. Audit goes to `<db without ext>.admin-audit.jsonl`. Backups go to `<db dir>/backups/memory-backup-{utc
  yyyyMMdd-HHmmss-fff}.db`.
- **Live alice**: `LiveAdminTarget` over the Supervisor (60 s timeout). The bot applies the change itself. Browsing still shows the
  snapshot, so you must fetch a new one to see it.
- Target info text. Backup info: a backup is taken automatically before the session's first write; "Backup now" also exists.

**Edits** (from the Memory browser):
- The generic form comes from `MemoryEditSchema.For(kind)`. Fields have type text|multiline|int|number|enum|bool, required, options
  and help, plus a hidden `__reason`.

| Kind | Ops | Fields |
|---|---|---|
| fact | add/update/delete | subject*, text* |
| place | add/update/delete | name* (renames), x*, y*, z*, dim (overworld/nether), note |
| structure | add/update/delete | name*, kind, note, confidence 0..1, steps (lines), min/max "x,y,z", dim |
| claim | add/update/delete | text* (add only), claimant* (add only), status enum (ClaimStatus), confidence, reason |
| trait | add/update/delete | section* enum (SelfSection, fixed after add), text*, player, reason |
| lesson | add/update/delete | title*, correction*, patterns* (regex rules `a && b`, `*` whole, `!!` negatives), id (add) |
| observation | update/delete | text (marks it taught), annotation |

- Update sends only the changed fields (`EditPrefill.Changed`). "" clears a field.
- Delete asks for confirmation (preview text). The store removes index, vectors and the RAPTOR leaf.
- `MemoryEdit{op, kind, id, fields, who:"admin:<OS user>", reason}` → `MemoryEditResult{ok, message, id, before, after, reembedded}`.
- The last result line is shown. Local success → reload.

**Rebuild** (RAPTOR):
- From the tree: `RebuildRequest{scope:"node", node}`. From the browser: `{scope:"kind", kind}` (episodes→episode,
  recipes→gamedata).
- Local is refused when there are LLM config problems.
- Flow: estimate → `RebuildEstimate{summaries, input_tokens, output_tokens, embedding_tokens, usd, model, note}`.
  - "Nothing to rebuild" when summaries = 0.
  - Otherwise a confirm dialog with the cost and a hard cap (`max(0.05, ceil(usd*1.5 cents))`).
  - Then backup → run with `max_usd = cap`.
- Progress bar + stage `stage done/total · msg`; Cancel.
- Result: summary diff table (node `L# #idx`, before/after text) + `Done/Failed: msg · $spent · s`.

**Compact activity** (one-off):
- Preview (dry run) / Run (confirm; local backup first) → `ActivityCompactionReport{ok, dry_run, docs_before, docs_after,
  activity_before, activity_after, processed, removed, rewritten, episodes, dirty_summaries, message, samples[], backup}`.
- Needs edit mode, even for the preview.

**Groom preview** (read-only, no edit mode):
- Input: pasted text or a doc ref (`act:…`, `ep:…`).
- Source: paste|log|activity|episode|journal|memory|structure|rule|claim|hive.
- → `GroomPreviewService.PreviewAsync` runs `MemoryGroomer` (dry run, reads `docs` and `activity_rollups(player,start,end,
  place_kind,place_key,place_name) where doc_ref`) and `LogIngest.Parse/GroomAsync`.
- Optional LLM rewrite (`LlmGroomRewriter`) when `Agent:Memory:Groom:Rewrite:Enabled`.
- Shows the groomed docs `[kind · source · ref] title\nbody`, `in → out`, and the stage chain or per-stage trace.
- **Flag:** this is the whole C# grooming pipeline.

**Audit trail:**
- Local: read the JSONL (300).
- Live: `GET api/admin/audit?max=300`. Needs edit mode on.
- Rows: when, who, op (add|update|delete|rebuild|backup|compact|import|export), kind, id, ok, message, target, `before → after`.

**Write logic that must stay in C# (flag):** `MemoryAdmin.ApplyAsync` (per-kind stores: MemoryStore, BeliefStore, SelfProfile,
KnowledgeCorrections, ObservationStore, with re-embedding and RAPTOR leaf updates), `RaptorSectionRebuilder` (cluster →
summarize with the cheap model → embed, budget cap), `AdminBackup.Create`, `ActivityCompaction`, `ShardImporter`. For a web
rebuild, the cleanest route is to call the Live admin API for live writes. For offline files, you need a C# worker (or ship the
Supervisor admin routes over a local db).

---------------------------------------------------------------------------------------------------------------------------------

## 12. Live Hive page (`LiveHive/*`)

### 12.1 Header / connection (`LiveHiveViewModel`)
- URL box (default `http://10.1.2.73:8765`), Connect/Disconnect, Tunnel.
- Status text: connected | connecting… | unauthorized: check token | reconnecting… (err) | paused (tab hidden).
- Latency (EWMA of tracked routes), token status, texture status ("textures: …" from `VersionedAtlas`), tunnel status,
  "bots updated N s ago".
- The first time the tab is shown, it auto-connects once. Hiding the tab stops every loop. The "not connected" card has help text
  and its own Connect/Tunnel buttons.
- Sub-tabs: **Bot feeds** (0), **Free camera** (1), **Hive tree** (2).

### 12.2 Polling loops (all GET, all off the UI thread, `HiveApi` for timeouts and back-off)

| Loop | Route | Cadence | Notes |
|---|---|---|---|
| HiveSync fast | `api/world/entities?since={rev}` | every 200 ms (5 Hz) | delta stream; drives connection state; 401/403 → Unauthorized, retry 3 s |
| HiveSync world | `api/world/index` then `api/world/chunks` | loop 150 ms; each interest re-indexed every 900 ms (freecam) / 1500 ms (eye tiles, compose) | hash-delta fetch, nearest first, 48 cols per request, at most 8 batches per pass; keeps ≤ 1600 cols/dim |
| HiveSync overlays | `api/world/overlays?dim` | 4 s (or when the dim changes) | only when the freecam tab is shown with an overlay on, or vision compose is open |
| HiveSync views | `view/{bot}?mode={iso\|shoulder\|orbit\|top\|side}[&xray=on\|off]` | per (bot, mode) every 1.5 s | PNG, for tiles in server-picture mode |
| ScriptBadgePoller | `api/bodies/scripts` | 1.5 s | the tiles' "script" badge |
| VisionTilePoller | `api/bodies/{key}/vision?since=` + `…/vision/{id}.png` | 2.5 s | tiles in "AI's last sheet" mode |
| Hive tree | `api/hive/tree` | 3 s (from the 500 ms stats tick) | |
| Script debugger | `api/bodies/{body}/script[?code=1]` | 250 ms (4 Hz) while open | |
| Controller panel | `api/bodies/{body}/controller` (every 5 s), `api/brains/{id}/context?since=&body=` (700 ms), `api/brains/{id}/turn/{n}?body=` | 1.4 Hz | on demand plus ≤ 2 expanded-turn refreshes per poll |
| Vision panel | `api/bodies/{body}/vision?since=`, `…/{id}.png`, `…/vision/render?see=` | 2 s list | render on demand |
| Locate | `api/locate?q=` | on "Go" | 404 → "no place called …" |

- **Streams:** no SSE or websocket. Everything is polling. The entity delta (`since=rev`) is the only incremental feed.
- **Per-route timeouts:** entities 3 s, chunks 10 s, view 6 s, script(s) 2.5 s, vision 4 s, vision_png 6 s, vision_render 12 s,
  others 5 s.
- **Back-off:** per route, exponential with ±25 % jitter (0.5→15 s; 404s 2→30 s), or `Retry-After` on 429/503.
- **Server side:** token bucket per route (index, chunks, entities, overlays). Index radius clamped 16..256, ≤ 64 cols per chunks
  request. Reply caches: index 400 ms, entities 100 ms, overlays 3 s, views `Supervisor:CacheMs` 1000. At most 2 in flight.

### 12.3 Bot feeds tab, tiles (`BotTileViewModel`, `TileRenderer`)
- **Layout:** 1 | 2 | 4 | 6 | Focus + strip (double-click a tile to focus). "N more online" when they don't fit. Default mode
  for all tiles. X-ray: auto|on|off (sent as `xray=` on server pictures).
- **One tile per bot**, ordered main first then by key, added and removed as bots appear and go away. Tile shows:
  - title `name · main|short`, subtitle = brain id or "in-process body"/"clone"
  - job (or idle), `♥ hp ◆ food` (low-hp style when ≤ 6), "holding X", "claims a, b, c +n", where `[nether ]x,y,z`
  - pose note (no pose yet / pose N s old)
  - stale badge ("stale N s", "reconnecting… · N s old")
  - **script badge** `script NAME · L{line}` or `· paused`, which opens the script debugger
- **Tile modes:**
  - **Eye**: rendered *locally* from the synced world at the bot's pose, textured, 12 fps max, 48-block draw distance, other bots
    drawn as overlay.
  - **Iso (3D), Shoulder, Orbit, Top + roles, Side**: server PNGs from `/view/{bot}?mode=…`.
  - **AI's last sheet**: the last vision sheet PNG + caption `kind → perception|brain · views · ~tok` + age.
- **Tile buttons:** Controller, AI vision, Fly (free camera at the bot's eyes), mode picker.
- **Side panels** (one of each can be open, with Close): AI vision, Controller, Script debugger.

### 12.4 Free camera tab (GPU default, CPU fallback)
- **Controls:**
  - Click to engage, Esc to release.
  - WASD/arrows move, E/Space/PgUp up, Q/PgDn down, Shift fast.
  - Drag or mouse to look; wheel sets speed (1..200, default 12). F3 toggles the stats HUD.
- **Panel:**
  - "Go to" box: `x,y,z` | a bot key/name | a place name via `api/locate`. "Snap to a bot" buttons.
  - Dimension (Overworld / Nether), speed.
  - **Overlays** checkboxes: bots and players; claims and gen cells; structures and places; chests/gens/farms/portals; build
    plans (ghost blocks); dangers; drops (name and count); tint for areas not seen recently (staleness); X-ray toward bots
    (on = a cut cone toward the bodies in front of the camera).
  - **Drops** list: items near the camera, nearest 60, `name xN · d m · x,y,z`, click to fly there.
  - Render stats: GPU `fps · WxH · sections · queued · GL info` or CPU `fps · raster ms · WxH · threads`; cache stats
    `N columns (M here) · MB received`; overlay counts and notes.
- **The camera** is placed first near the main bot (−6, +6, −6, yaw 315, pitch 30). It feeds the world sync an interest of radius
  `MaxDistance + 24` (GPU 32..192, CPU 64).
- **Rendering:**
  - GPU: `Gpu/SectionStore` meshes 16³ sections off-thread from the cache. It diffs against the neighbours and relights on time
    of day. `GpuMesh` turns `MineClaw.Render` `GeoQuad`s into 28-byte vertices with baked vanilla flat lighting. The atlas
    combines terrain.png, items.png, extras, liquids and entity skins. `GlFreecamRenderer` runs on GLES 3.0 / GL 3.3 with passes
    sky → solid → entities → overlay lines → translucent + shadows. Frustum culling, staleness and "remembered" shading, rain and
    snow streaks.
  - CPU: `FreecamEngine` raycasts via `EyeRenderer` on every core, at reduced resolution while moving.
  - Overlay boxes and labels are depth-tested (`OverlayPainter`). Remembered (not live) columns are dimmed: live = seen within 2 min.
  - Textures: `TextureLocator.Load({AssetsDir: data/assets, AllowDownload:false, SearchLauncherDirs:true, Version})`. The version
    is the hive's word from the index reply, else 1.12.2 when columns are 256 high, else `server-version.txt`. 1.12.2 uses
    client-jar block models.
- **Weather and time:** the index reply's `time` (ticks, advanced locally at 20/s) and `rain`/`thunder` drive day/night light, sky
  and rain.

### 12.5 Hive tree tab
- `GET api/hive/tree` → `HiveTreeNodeDto{id, name, kind (main|hive|subhive|script), brain, owner, state, task, children[]}`.
- Rendered as indented rows `● / └  id · name · kind · brain · state · task`. Summary: "Main alone…" or "N clone(s): h hive,
  s subhive, c script".

### 12.6 Controller panel (`ControllerPanelViewModel`, wire types `src/MineClaw.Agent/Telemetry/BrainContextModels.cs`)
- **Header:**
  - kind text: "main hive · its own brain" / "hive · … owned by X" / "subhive · driven by X's brain" / "script-only · runs S"
  - brain text `brainKind · model · provider`
  - note, stale badge, error
  - "Full hive view" (subhive → opens the owner's panel)
- **Tabs:** Brain | Script, when both exist. Script-only bodies show only the script debugger.
- **Brain pane:**
  - "now thinking… 3.2 s · lane (purpose) · model · for body" chip, from `in_flight`.
  - **Token bar:** used / limit (red when > 85 %), cached-prefix bar, compaction mark; text
    `used (est.) / limit tok (%) · cached prefix ~ · compacts at ~`.
  - Subhive filter line + "only these turns" toggle (default on for a subhive).
  - Search box filtering context blocks and turns: "N of M blocks match".
  - **Turns tab:** newest first. Each brain turn is followed by its sub-entries, indented (worker, author, memory, reply,
    compaction, perception, background). Row: `#id lane-label · preview`, meta `tier · model · N calls · in/out/cache
    read/write · $cost · latency · tools … · thinking · for body · error · in flight`, highlighted when it targets the body.
    Expanding fetches `turn/{n}`: per call a header `call #seq · round · tier · model · stop · usage · effort · for · +N msgs of
    history`, then the added messages' blocks and the response blocks; child sub-entries. Each turn has "Copy JSON" (the indented
    detail).
  - **Context window tab:** the "System prompt · N blocks · ~tok · T tools ~tok · cached prefix ~tok (this call: read/wrote)"
    expander → per block `#i · ~tok · cache breakpoint (ttl) · cached prefix|uncached` + text. Then `call #seq · turn #id · model ·
    N messages · in flight` and the full message list.
- **Block row kinds:** text (labels: memory → "retrieved memory", journal → "compaction summary", turn input), tool_use (pretty
  JSON input), tool_result / tool error, thinking ("signed (N bytes)"), redacted_thinking ("(encrypted …)"), reasoning, image (label).
- **Polling:**
  - `context?since={version}`: the server returns only the turns changed since then, plus `evicted[]` and the window when it
    changed.
  - A full reset happens when version = 0, note = "full", or the server version goes backwards.
  - Expanded in-flight turns are refetched when their `version` changes, at most 2 per poll.
- **Wire shapes (snake_case):**
  - `BodyControllerInfo{body, kind, owner, brain, brain_kind, model, provider, has_script, script, tabs[], filter,
    links{context, script, owner}, note}`
  - `BrainContextPage{brain, version, at, capacity, enabled, in_flight{seq,turn,lane,purpose,model,since,body},
    turns[CtxTurnSummary], window CtxWindow?, evicted[], filter, note}`
  - `CtxTurnSummary{id, lane, purpose, start, end, in_flight, calls, model, tier, provider, in, out, cache_read, cache_write,
    cost, latency_ms, preview, tools[], has_thinking, parent, body, targets, version, error, dropped_calls}`
  - `CtxWindow{call_seq, turn_id, at, model, tier, system[{index,text,cache,ttl,tokens,in_cached_prefix}], tool_count,
    tool_tokens, tool_names[], messages[{index, role, blocks[CtxBlock], tag}], prompt_tokens, estimated_tokens, limit,
    compact_at, cached_prefix_tokens, usage{in,out,cache_read,cache_write,cache_write1h,cost,latency_ms,prompt_tokens}, in_flight}`
  - `CtxBlock{kind, text, label, name, id, input, is_error, bytes, target}`
  - `BrainTurnDetail{brain, turn, system[], tool_names[], calls[CtxCall{seq, at, purpose, tier, model, provider, body, round,
    in_flight, stop_reason, usage, error, context, added[CtxMessage], response[CtxBlock], max_tokens, effort, thinking}],
    children[CtxTurnSummary], filter}`
- **Server capture limits** (`Telemetry:Capture`): Turns 30, BackgroundTurns 10, CallsPerTurn 48. Everything is redacted.

### 12.7 AI vision panel (`VisionPanelViewModel`, `VisionSource.cs`)
**Mode "What the AI saw":**
- History strip: newest first, thumbnails, label `HH:mm:ss`, detail `kind · → perception|brain · tok`. Capacity is the server's
  ring (default 20).
- Following mode jumps to the newest sheet.
- Selected sheet:
  - title `date · kind for the perception tier|brain · source`
  - request `views [...] · focus x,y,z rN | hive · Q: … \n see {call}`
  - cost `WxHpx · ~tok image tokens · KB · sha256[..12] · model in/out tok · note`
  - answer: the perception answer, "(waiting…)" or "(direct mode…)"
  - legend: the text that rides with the picture
- Zoom − / 1 / + (0.25..6) with pan; Save PNG (the exact bytes); "Copy as see{…}"; "Compose from this".
- Sheets in perception mode are re-polled from the oldest unanswered id until the answer arrives (gives up after 2 min).

**Mode "Compose":**
- Centre: the bot | a block `x,y,z` | a build plan footprint (from the overlays' `plans`; radius computed from the footprint).
- Radius 3..12; reading aids on/off; "server render" checkbox.
- Panels (≤ `ContactSheet.MaxPanels`): eye, shoulder (dist 3.4), iso (yaw 225, pitch 35, zoom 1), orbit (yaw 315, pitch 40,
  dist), high (yaw 180, pitch 65), top (zoom, dx, dz), side (zoom, dx, dy, axis auto|N|E), layers. Each has sliders; "Reset cameras".
- **Live preview:** rendered *locally*. `LocalSheetComposer` runs the bot's `ContactSheet` + `VisionRenderer` over the synced
  world, debounced 90 ms. It has no role layer and no memory labels.
- **"Render exact":** `GET api/bodies/{body}/vision/render?see={urlencoded JSON}` renders on the server with roles and marks.
- Stats `local|server · WxH ~tok · ms (fps)`; legend; `see {...}` text.
- **`see` JSON:** `{"views":["iso yaw=200 pitch=30","top zoom=1.5 dx=2",...],"focus":"x,y,z","radius":6[,"aids":false][,"question":"…"]}`.
  View strings are `kind[ at][ k=v …]` with keys yaw, pitch, zoom, dist, dx, dy, dz, axis.

**Wire shapes:**
- `VisionListDto{at, body, capacity, note, sheets[VisionSheetDto]}`
- `VisionSheetDto{id, at, body, kind, source, mode (perception|brain…), views[], focus[3]?, radius, question, call, width,
  height, tokens, bytes, sha256, legend, answer, note, model, model_input_tokens, model_output_tokens}`
- `GET …/vision/{id}.png`: raw PNG (headers `X-Sha256`, `X-Tokens`). Width and height are read from the IHDR.

### 12.8 Live script debugger (`ScriptDebugViewModel`, `src/MineClaw.Scripting/ScriptDebugSnapshot.cs`)
- Opened from a tile's script badge, or the Controller's Script tab. Polls `GET api/bodies/{body}/script` at 4 Hz.
- `?code=1` is sent once per new run, keyed by `session_id:name:version`. While the panel is open, the server keeps ~5 Hz
  sampling on through a lease. A hidden panel stops polling so the lease runs out.
- Stale after 2 s ("stale N s · error"). Back-off 0.5…5 s.
- **Reply:** `BodyScriptDto{body, running, snapshot ScriptDebugSnapshot?, note}`. Badge feed: `LiveScriptsDto{at,
  bodies[{body, short, script, status, line, paused, version, role}]}`.
- **Header:**
  - `name vN · lane ROLE · crew of COORD`
  - status: running / paused / pausing at the next safe point / done / failed / cancelled, `· line N`, `patching · …`
  - pause text `paused (kind): reason · duration · patch note`
  - where `line L:C \`snippet\`` (live) or `line L (last host call) · now in sdk/…`
  - action line `helper → label args · 1.2 s · atomic` / `done in … (failed)`
  - actions count, elapsed, checkpoint `cp 'x' (p/t passed)`, progress `%` + note, last failure / outcome
  - a **Pause** button exists but is disabled (`controls` is always empty for now)
- **Source pane:** highlighted code with the current line marked (follows `line`).
- **Tabs:**
  - **State:** state machine `id: state · N transitions · blackboard N s old`; history `from → to (why)  t s` newest first;
    BLACKBOARD key/value (JSON previews ≤ 120 chars); PARAMS (current values, plus args marked "arg"); LOCALS (name, value,
    scope) with note "sampled N s ago".
  - **Activity:** TIME PER ACTION KIND `action · Nx (f failed) · total s · avg ms`; LOG lines.
  - **Stack:** frames `function · file:line`, script frames vs host/SDK frames.
  - **Crew** (shown when lanes exist): crew, coordinator, `lane id · body → status`.
- **`ScriptDebugSnapshot` fields (snake_case JSON):**
  - Identity: `session_id, name, version, lang (js|lua), status (starting|running|paused|done|failed|cancelled), live, body,
    author, role, crew, coordinator, at, started, seq`
  - Position: `line, column, line_live, in_sdk, snippet, stack[{function, file, line, column, script}], stack_age_seconds`
  - Action: `action{label, args, seconds, atomic, running, ok}, actions, elapsed_seconds, timings[{action, calls, fails, seconds,
    avg_ms}]`
  - Params and checkpoints: `params (JSON|null), args (JSON), checkpoint, checkpoints_passed, checkpoint_total`
  - Pause and patching: `paused, pause_pending, pause_kind (chat|script|brain|danger|stop), pause_reason, paused_seconds,
    patching, patch_note, patches`
  - Machine: `machine{id, state, transitions, history[{from, to, why, at}], blackboard (JSON), blackboard_age_seconds}`
  - Locals: `locals[{name, value, scope}]|null, locals_note, locals_age_seconds`
  - Rest: `lanes[{id, body, status}], progress_pct, progress_note, last_failure, outcome, logs[], code (only with ?code=1),
    controls[] (always empty)`

---------------------------------------------------------------------------------------------------------------------------------

## 13. Live world wire formats (how blocks and entities reach the viewer)

### 13.1 `GET api/world/index?dim={0|-1}&x={blockX}&z={blockZ}&r={16..256}&light=1` (JSON, snake_case)
`IndexDto{dim, at, columns:[{cx, cz, hash (i64), seen (unix ms), by (body), live (bool)}], version ("b1.8.1"|"1.12.2"), time
(world ticks), rain (0..1), thunder (0..1)}`.
- The client keeps a cache keyed `(dim,cx,cz)`.
- Reconcile: a listed column with the same hash only gets seen/by/live updated. A new or changed hash goes on the fetch list. A
  cached column inside the rectangle that the index no longer lists is dropped.
- With `light=1`, live hashes = `LightSnapshot.Combine(blockHash, lightHash) = blockHash * -7046029254386353131 ^ lightHash`
  (wrapping i64).

### 13.2 `GET api/world/chunks?dim={d}&cols=cx,cz;cx,cz;…&light=1` → `application/octet-stream` (`MineClaw.World/Chunks/ColumnPayload.cs`)
Little-endian:
```
"MCWC"  u16 version(1|2)  u16 flags(0)  i32 dim  i32 count(<=1024; server caps a request at 64)
count × { i32 cx, i32 cz, i64 hash, i64 seen_unix_ms, u8 live(0|1), u8 by_len, by_len×utf8,
          i32 data_len, data_len×byte            // data_len 0 = column unknown → drop it
          [v2 only: i32 light_len, light_len×byte] }   // light 0 = none (remembered column)
```
Trailing bytes or truncation are errors. Max 256 KB per column blob.

**`data` = `ChunkSnapshot.Encode()`.** *Raw* DEFLATE (no zlib header; Node `zlib.inflateRawSync`). It inflates to `V + V/2`
bytes, where V = 16·H·16 (H = 128 for b1.8.1, 256 for 1.12.2): the first V bytes are block ids (u8), then V/2 bytes of metadata
nibbles (`meta[i] = b & 0x0F`, `meta[i+1] = b >> 4`). The height follows from the inflated length (49 152 or 98 304). Cell
index: `idx(lx,y,lz) = (y>>7)<<15 | (lx*16 + lz)*128 + (y & 127)` (the upper 128 follow the lower half). The hash is 64-bit
FNV-1a over the ids, then over `meta & 0x0F`. These are the same bytes as `world_chunks.data` in the db.

**`light` = `LightSnapshot.Encode()`.** Also raw DEFLATE: `u8 version(1) u8 sections u8 flags(1=biomes)`, then per 16³ section
`u8 kind`: 0 = uniform + `u8 value`, or 1 = full + 4096 bytes. Each byte is `sky<<4 | block`, in local order
`lx<<8 | lz<<4 | (y&15)`. Then 256 biome bytes when flagged (index `lz*16+lx`). A uniform section is typically 0xF0 (open sky)
or 0x00 (buried). Remembered columns carry no light: sky is estimated above the opaque top and unknown below.

**Block ids and meta are version-specific** (b1.8.1 ids vs 1.12.2 ids). Turning them into shapes or textures needs
`MineClaw.Render` (BlockTextures, IBlockModels, the 1.12.2 jar models) and `MineClaw.Data` (Blocks, GameData). **Flag**:
porting this to WebGL means reimplementing block geometry, texture mapping, fluids and lighting.

### 13.3 `GET api/world/entities[?since={rev}]` (JSON)
`EntitiesDto{at, rev, full (bool), bots:[BotDto], entities:[EntityDto], removed:[[dim,id],…]}`.
- `since=0` or no `since` returns everything. `full=true` = you were too far behind, so replace the whole set. Otherwise
  `entities` = only changed ones, `removed` = gone.
- Bots are always sent in full, for every dimension.
- `BotDto{name, short ("main"/"67", key for /view), main, dim, x, y, z, yaw?, pitch?, pose_age?, hp, food, job, brain, held,
  claims[], live, age_seconds}`
- `EntityDto{kind (player|mob|item|…), name, dim, x, y, z, id, type, item, damage, count, vx, vy, vz (blocks/tick), age
  (s since first seen), yaw, by (body whose tracker saw it), flags (0x01 burning, 0x02 sneaking, 0x08 sprinting), variant}`
- Client (`EntityStore`): per (dim,id) it glides from the drawn position to the new one over the measured update interval
  (EWMA, 50..1000 ms). A jump of more than 8 blocks snaps. Age advances locally (bobbing drops). Server-side the delta log
  has a 100 ms minimum interval.

### 13.4 `GET api/world/overlays?dim` (JSON)
`OverlaysDto{at, dim, claims[Mark], places[Mark], points[Mark] (chests, gens, farms, portals…), plans[{id, name, status, part,
blocks:[[x,y,z,id,meta,done],…]}], dangers[Mark], notes[]}`, `Mark{kind, text, min[x,y,z], max[x,y,z], owner}`.

### 13.5 Pictures
- `GET view/{botKey}?mode=sheet|brain|eye|iso|shoulder|orbit|high|top|side|layers[&radius=3..12][&xray=on|off][&meta=1]` → PNG.
  Server cache ~1 s. 404 when the bot is not online. The explorer requests only iso, shoulder, orbit, top, side.
- `GET api/bodies/{name}/vision[?since=id]` → JSON, `…/vision/{id}` → one sheet JSON, `…/vision/{id}.png` → PNG,
  `…/vision/render?see=JSON[&meta=1]` → PNG (CPU-capped render gate on the server).
- `GET api/locate?q=` → `{name, dim, x, y, z}` or 404.
- The supervisor also has `/camera?x&y&z&yaw&pitch&dim&w&h` (a server-rendered free-camera PNG), `/view/hive`, `/api/bots`,
  `/api/status?bot=`, `/api/events?since=`. The explorer doesn't use them, but a web app could use `/camera` instead of a WebGL
  renderer, and `/api/events` for a feed.

---------------------------------------------------------------------------------------------------------------------------------

## 14. Supervisor endpoint catalogue used by the explorer

All are relative to the base URL. Header `X-Supervisor-Token: <token>`, `Accept: application/json`. JSON is snake_case, numbers
may come as strings, and names are case-insensitive.

| Method | Path | Request | Response | Used by |
|---|---|---|---|---|
| GET | `api/world/index?dim&x&z&r&light=1` | – | IndexDto | world sync |
| GET | `api/world/chunks?dim&cols=…&light=1` | – | binary MCWC | world sync |
| GET | `api/world/entities?since=` | – | EntitiesDto | fast loop |
| GET | `api/world/overlays?dim` | – | OverlaysDto | freecam, compose |
| GET | `view/{bot}?mode&xray` | – | PNG | tiles |
| GET | `api/locate?q` | – | LocateDto / 404 | freecam Go |
| GET | `api/hive/tree` | – | HiveTreeNodeDto | Hive tree tab |
| GET | `api/bodies/scripts` | – | LiveScriptsDto | tile badges |
| GET | `api/bodies/{b}/script[?code=1]` | – | BodyScriptDto | debugger |
| GET | `api/bodies/{b}/controller` | – | BodyControllerInfo | controller |
| GET | `api/brains/{id}/context?since&body` | – | BrainContextPage | controller |
| GET | `api/brains/{id}/turn/{n}?body` | – | BrainTurnDetail | controller |
| GET | `api/bodies/{b}/vision?since` | – | VisionListDto | vision panel, tiles |
| GET | `api/bodies/{b}/vision/{id}.png` | – | PNG | vision |
| GET | `api/bodies/{b}/vision/render?see=` | – | PNG | compose "Render exact" |
| GET | `api/scripts` | – | `{at, scripts[]}` | Scripts (live) |
| GET | `api/scripts/{name}[/{v}]` | – | ScriptVersionDetail / 404 | Scripts (live) |
| POST | `api/admin/backup` | `{}` | `{path}` | Edit (live) |
| POST | `api/admin/memory` | MemoryEdit `{op,kind,id,fields{},who,reason}` | MemoryEditResult (400 + same body on refusal) | Edit |
| POST | `api/admin/rebuild/estimate` | RebuildRequest `{scope,node,kind,max_usd,who}` | RebuildEstimate | Edit |
| POST | `api/admin/rebuild` | RebuildRequest | `{job_id}` | Edit |
| GET | `api/admin/rebuild/{job}` | – | `{state (running|done|failed|cancelled), progress{stage,done,total,message}, result{ok,message,diffs[{index,layer,old,new}],spent_usd,elapsed}, error}` polled every 1.5 s | Edit, shard import |
| POST | `api/admin/rebuild/{job}/cancel` | `{}` | – | Edit cancel |
| GET | `api/admin/audit?max=N` | – | `{entries[AuditEntry{at,who,target,op,kind,id,before,after,reason,ok,message}]}` | Edit, Shards history |
| POST | `api/admin/shard/import/preview` | `{shard: base64 .mcshard, mode: merge\|replace-section\|swap-pack\|add-mode\|remove-mode, conflict: keep-newer\|prefer-shard\|keep-mine, include_rules, include_world, who, max_usd}` | ShardDiff | Shards (live) |
| POST | `api/admin/shard/import` | same | ShardImportResult `{ok,message,diff,written,removed,reused_vectors,embedded,rebuild[],job_id,active_pack}` | Shards (live) |
| POST | `api/admin/activity/compact` | `{dry_run, who}` | ActivityCompactionReport | Edit (live) |

- Errors: 401 means the token is missing, 403 means admin writes are off or the token is wrong, and either way the body is `{error}`.
  429/503 carry `Retry-After`. Shard bodies above ~60 MB of base64 are refused on the client before sending. The client validates
  the shard locally first (`ShardFile.Read`).
- The Supervisor's own config keys (server side): `Supervisor:Enabled, Bind ("*"), Port (8765), Token, AllowAdminWrites,
  CacheMs, HiveCacheMs, MinRenderIntervalMs, MaxQueuedRenders, MaxConnections, Radius, EyeWidth/Height, CameraMax*,
  CameraDistance, PoseCacheMs`.

---------------------------------------------------------------------------------------------------------------------------------

## 15. Domain logic in shared C# libraries that a Node backend can't trivially reimplement

| Area | C# entry point | What it does | Port difficulty / suggestion |
|---|---|---|---|
| Hybrid recall | `MineClaw.Agent.Rag.RagIndex.Search/SearchAsync/EmbedQueryAsync`, `RaptorMemory.Augment`, `RaptorIndex.Retrieve` | FTS5 BM25, cosine over float32 blobs, R*Tree spatial, RAPTOR collapsed and traversal lists, RRF fusion, bot-specific weights | **High**: results must match the bot. Keep as a C# sidecar, or call the bot. |
| Embedding providers | `EmbeddingProviders.Create` (Azure OpenAI / Ollama), `VectorMath.Normalize` | query vectors that match the stored model/dims | Medium (plain HTTP), but must reproduce normalisation and model ids |
| RAPTOR tree load/model | `MineClaw.Raptor` (`RaptorTree`, `RaptorNode`), `AgentDbRaptorStore` (`docs.raptor_idx`, `doc_edges`, `meta.raptor_meta`) | tree stats, paths, leaves | Medium: the tables are plain, so read the SQL directly |
| RAPTOR rebuild | `RaptorSectionRebuilder.Estimate/RunAsync` | cluster, summarize (LLM), embed, cost cap | **High**: use the live admin API or a C# worker |
| Memory writes | `MemoryAdmin.ApplyAsync`, `MemoryAdminContext.OpenLocal`, `MemoryEditSchema`, `AdminBackup`, `AdminAudit` | per-kind store semantics, re-embed, leaf updates | **High**: same |
| Activity compaction | `ActivityCompaction.For(ctx).RunAsync` | rewrites episodes | High |
| Grooming | `MemoryGroomer`, `GroomEnvironment`, `LogIngest`, `LlmGroomRewriter` | the write-path normaliser | High |
| Shards | `ShardFile` (zip, canonical JSON, hashes), `ShardExporter`/`ShardCollector` (selection, secret redaction), `ShardImporter.Preview/ApplyAsync`, `KnowledgePack`, `PackIndex` | export, diff, import, pack swap | **High** for import and export. Reading the manifest is easy (zip + JSON). |
| Script library and lint | `ScriptLibraryReport.List/Detail`, `ScriptLint.Check/CalledNames/Params`, `ScriptSession.Aliases` (MineClaw.Scripting) | version stats, used-by, JS and Lua lint | High for lint. The library is easy via the live `/api/scripts`. |
| Script diff / highlight | `ScriptDiff` (LCS), `CodeHighlighter` | UI only | Low (use the `diff` package and Prism/Shiki) |
| Design diff | `DesignSnapshot.Decode`, `DesignDiff.Between/Against`, `Blocks.Get` + `GameData.Override(Release1122GameData)` | revision diffs, state-only equivalences, block names per version | Medium-high (needs the block tables) |
| Graph labels/layout | `GraphModel.Build`, `TopicLabeler.Compute`, `PartitionLayout` | partition, TF-IDF labels | Low-medium (pure algorithms; reimplement or precompute in an API) |
| Chunk decode | `ColumnPayload.Decode`, `ChunkSnapshot.Decode`, `LightSnapshot.Decode` | binary world format | **Low**: documented above (raw deflate + fixed layout) |
| World meshing/rendering | `MineClaw.Render` (`EyeRenderer`, `IBlockModels`, `BlockTextures`, `GeoQuad`, `FluidTextures`, `EntityTextures`, `TextureLocator`, `TextureAtlas`), explorer `Gpu/SectionStore`, `GpuMesh`, `FrameScene` | textured voxel rendering, b1.8.1 and 1.12.2 models, lighting, entities | **Very high** in WebGL. Options: server `/camera` PNGs, or a C# mesh service that ships vertex buffers. |
| Vision compose | `ContactSheet`, `VisionRenderer`, `SeeCall`, `CamParams`, `PerspectivePanel` | the AI's contact sheet | **Very high**: use server `vision/render` only (drop the local preview, or accept the latency) |
| Snapshot | `SnapshotService` (ssh + sqlite3 .backup / tar + VACUUM INTO) | db copy | Medium (shell out from NestJS) |
| Pack labels | `KnowledgePack.Active(db)` → meta `knowledge_pack` | header chip | Low |

---------------------------------------------------------------------------------------------------------------------------------

## 16. SQLite tables and columns the explorer reads (browsing is read-only)

`meta(key,value)`: keys `raptor_meta`, `knowledge_pack` (`raptor_spend` exists but isn't read).

- `docs(id, ref UNIQUE, kind, tag, title, body, hash, source, embed, weight, created, updated, min_x..max_z, dimension, raptor_idx, level)`
- `docs_fts` (fts5 over title, body), `docs_rtree`
- `vectors(doc_id, model, dims, hash, vec BLOB float32 LE)`, `doc_edges(parent, child, weight)`
- `notes`, `claims` (+ `claim_evidence`), `self_profile`, `self_profile_history`, `self_acc(scope, family, key, dim, sum, n)`, `self_abstractions`
- `structures`, `waypoints`, `observations` (+ `container_items`), `knowledge_corrections`, `activity_log`, `chat_log`,
  `routines(name, data JSON, updated)`, `standing_orders(data JSON)`, `world_chunks` (count only), `activity_rollups`
- `blueprints(id, name, status, requester, updated, png BLOB)`, `blueprint_revisions(blueprint_id, rev, author, author_kind,
  message, tag, created, n, blocks, air)`, `blueprint_world(blueprint_id, rev, seen, seen_by, diff, causes, blocks, air)`
- Times are unix ms. Schema version: `AgentDb.SchemaVersion`. Tables are probed with `sqlite_master` because of older dbs.
- The db must be opened **read-only** for browsing. A local edit opens a separate read-write connection, and only in edit mode.
  Never write a db a running bot uses (the dev-db warning).

Local files written: `data/alice-snap/*.db`, `<db>.admin-audit.jsonl`, `<db dir>/backups/memory-backup-*.db`, `data/shards/*.mcshard`.

---------------------------------------------------------------------------------------------------------------------------------

## 17. Pain points (what the live views render that is noisy)

1. **Controller panel context window = the whole prompt.** The Context window tab lists *every* message of the newest brain
   call: the full history, which can be dozens of prior turns with tool results and retrieved-memory blocks. It also lists every
   system block. The Turns tab shows up to 30 brain turns plus a second ring of sub-entries (worker, author, memory, reply,
   compaction, perception, background), all expanded inline. There's no "since last turn", no collapse per message, and no
   paging. Prior contexts drown the current turn, and the search box is the only way to filter.
2. **Turn rows are dense meta strings** (`tier · model · calls · in/out/cache read/write · $ · latency · tools · thinking · for ·
   error`). The token/cost data could be columns or a chart.
3. **Sub-entry noise:** background lanes (reflection, RAPTOR summaries, grooming, line prefetch) are interleaved with the turns
   that matter. There's no lane filter except the subhive "only these turns".
4. **Script debugger shows everything at once:** a source listing, four tabs of state (machine history, blackboard, params,
   locals), timings, logs and stack, all refreshed at 4 Hz. Lists are diffed in place to avoid flicker. The Pause button is always
   disabled (`controls` is always empty), which is confusing.
5. **Vision history:** up to 20 full sheets in a strip, each with long request/cost/legend text. Perception answers arrive later,
   so rows mutate after they first appear.
6. **Tiles:** each tile packs title, subtitle, job, vitals, held, claims, coords, pose note, stale badge and script badge. With
   6 tiles plus a side panel, the screen is busy. Stale and "reconnecting" badges flicker on brief hiccups (3–5 s thresholds).
7. **Many independent pollers** (entities 5 Hz, world 150 ms loop, views 1.5 s/tile, badges 1.5 s, vision tiles 2.5 s, tree 3 s,
   debugger 4 Hz, controller 1.4 Hz, vision 2 s). Each reports errors on its own, so one link problem shows up as several
   different messages ("stale", "reconnecting…", "world stale N s · error", panel error text). A web rebuild should use a single
   backend-for-frontend with one SSE/WebSocket stream per view.
8. **URL inconsistency:** Live Hive uses a hard-coded default URL plus its own text box, while Scripts (Live) and the Live edit
   target use `Explorer:SupervisorUrl`. They can point at different servers.
9. **Memory browser loads whole tables** (chat and activity up to 50 000 rows) and filters on the client. It needs server-side
   paging and search in the web version.
10. **Free camera HUD stats** (fps, sections, queued meshes, GL info, cache MB, overlay counts) are developer telemetry shown to
    every user.
11. **Live writes aren't reflected:** after a live edit the browser still shows the old snapshot, and the user has to fetch a new
    one by hand.
