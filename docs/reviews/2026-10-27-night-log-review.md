# MineClaw night of 2026-10-27: log review (remaining bugs)

Scope: everything recorded on 2026-10-27, i.e. the PC run `logs/live2/` (04:41–05:13 UTC, local log times 00:41–01:13), and
the alice server run `data/alice-logs/` (06:45–08:28 UTC, four systemd starts: 06:45, 07:09, 07:27, 07:52), including the
clone logs (67, 69, 420), `journal.txt`, and every `usage-*.jsonl`. This is analysis only; no code was changed.

The brief listed fixes that are done but not deployed. Those are excluded unless the logs show that a fix is incomplete;
such cases are marked **(fix incomplete?)**. Where an excluded fix probably explains a symptom, the bug list says so instead
of reporting the symptom again.

Times are UTC for alice and live2 telemetry. live2 *log* lines use local time (UTC−4).

---

## 1. Cost of the night

Sources: 1,205 telemetry records across 7 usage files. Prices inferred from the records by least squares: Opus $4 in,
$0.20 cache read, $5 cache write, $20 out per MTok; Sonnet $2 / $0.20 / $2.50 / $10; Haiku $0.10 / $0.01 / $0.125 / $0.50.

**Total: $5.18** (live2 $1.43, alice $3.75). The hive ledger agrees: "hive today $4.882 (clones $1.902)" at the 07:52 restart,
plus $0.30 spent after that.

### By tier

| tier (model) | calls | cost | cache hit* | notes |
|---|---:|---:|---:|---|
| opus (brain, main and clones) | 417 | **$3.91** (76 %) | 94.7 % | $1.56 of it is cache **writes** (40 %), $1.24 output, $1.12 reads |
| worker (Sonnet) | 366 | $1.19 (23 %) | 94.0 % | $0.36 writes |
| memory (Haiku) | 422 | $0.08 (1.5 %) | 6.0 % | 389 RAPTOR summaries ($0.073), 29 ask_memory ($0.0045) |

*cache hit = cache_read / (input + cache_read + cache_write)

### By bot

| bot | calls | cost | hit | opus | worker | memory |
|---|---:|---:|---:|---:|---:|---:|
| maid_chan (main) | 854 | $3.26 | 88.4 % | $2.69 | $0.50 | $0.077 |
| maid_chan_67 | 241 | $1.28 | 92.2 % | $0.81 | $0.47 | $0.001 |
| maid_chan_69 | 100 | $0.57 | 90.8 % | $0.35 | $0.22 | $0.001 |
| maid_chan_420 | 10 | $0.07 | 86.6 % | $0.07 | – | 0 |

Clones were 37 % of the night's spend. **Clone brains run on Opus**: $1.22, which is 31 % of all Opus spend.

### By purpose

| purpose | calls | cost |
|---|---:|---:|
| chat_turn (opus) | 417 | $3.914 |
| delegate_round (worker) | 366 | $1.190 |
| raptor_summary | 389 | $0.073 |
| ask_memory | 29 | $0.0045 |
| reflection / seed | 4 | $0.0003 |

By hour (UTC), main+clones: 04h $0.56, 05h $0.87, 06h $0.65, **07h $2.90**, 08h $0.20.

### Context and latency

- Peak prompt was 36.5K tokens (main at 07:52:23; history 28.7K). That is **37.7 % of the 100K ceiling**, so the ceiling
  was never close. There were **0 compactions** all night, because every restart (SIGTERM at 07:09, 07:27, 07:52) wiped the
  history first (see B11).
- Opus latency: median 2.6 s, p90 4.7 s, max 10.9 s. Worker: median 1.7 s, p90 2.5 s. ask_memory: median 1.1 s.

### Top 5 most expensive turns

| # | when / bot | cost | calls | why |
|---|---|---:|---:|---|
| 1 | 08:26:48 main (opus) | $0.084 | 3 | **Idle check-in nobody asked for**, after a 20-minute gap. The cache had expired (cr 0, **cw 13.5K**). It ran ask_memory and look, then sent unsolicited chat about melons and mobs. The whole turn was waste (B9). |
| 2 | 07:36:13 67 (worker) | $0.079 | 25 | Tree-chop delegate hit the 25-round cap and ended `failed`. Causes: a false "spawn protection" region (B10), an unreachable chest, and deposit Lua errors (B15). |
| 3 | 07:14:22 69 (worker) | $0.078 | 25 | Crafting a stone pickaxe failed after 24 rounds (10 crafts and 10 withdraws). Window-0 clicks were rejected and the inventory desynced: "+26 stick" that was never really there (B7). |
| 4 | 07:23:50 main (opus) | $0.076 | 8 | "Drop into the hole and mine your side to show me". The guard blocked the delegate with a 7-minute-old unrelated failure (B1), the script dig was refused (lava rule, already fixed), and the bot crouched twice as a gesture. **It never replied** (silent episode). |
| 5 | 08:05:10 main (opus) | $0.076 | 3 | Door test after a 10-minute idle gap. The cache was cold (cw 12.3K), so the full prefix was rewritten (B9). |

### Estimated waste: about $1.9 of $5.18 (≈ 37 %)

| bucket | est. $ | evidence |
|---|---:|---|
| Cache prefix lost after every multi-round turn (B3) | **0.75** | 157K tokens re-written at $5 instead of read at $0.20. Median loss is 4 tokens after a single-call turn but 344–633 after a tool-use turn. |
| Clone cold starts that achieved nothing | 0.40 | 13 clone Opus cold starts at about $0.039 each, plus worker cold starts at about $0.015 each. 420 spawned twice into the void, the 67/69 spar spawns were cancelled, the 06:55 duplicate 67 launch, the 07:54 "show you it lands" spawn of 67. |
| Failed or needs_input worker tasks | 0.39 | 9 failed ($0.33) and 3 needs_input ($0.06). At least 7 were avoidable (B1, B6, B7, B10, B12, B13). |
| Silent main episodes | 0.28 | 24 episodes with nothing said. 9 of them are back-to-back follow-ups to a turn that had just answered (B4), $0.08. |
| Cold cache after idle gaps (main) | 0.12 | 08:05 and 08:26: 25K tokens written. |
| Rounds spent on `not retried` guard refusals | 0.10 | 16 refusals at about $0.006 per extra Opus round. |
| Food-begging turns (B14) | 0.10 | About 8 turns after the player said "dw about food for now". |
| Clone Opus "scan" scripts that fail by design (B15) | 0.05 | 67: 4 scripts between 06:56:12 and 06:56:50. Main: 07:13:00. |

---

## 2. Bug list (most impactful first)

### B1. ToolCallGuard "not retried" blocks unrelated delegates and even `goto` for 10 minutes
- **Evidence** (16 refusals: main 9, 67 2, 69 3, live2 2):
  - 07:16:11 main: `delegate → error: not retried: delegate failed here 10s ago: failed: no spare stone_pickaxe…`. The new
    delegate was a *different* plan (craft from the chest's planks).
  - The same stale failure blocked delegates at 07:21:25 (5 min later), 07:23:59 (7 min later; "show me you can mine"), and
    the earlier one at 06:54:02 (4 min old expand_island failure, new edge, new task).
  - 07:41:49, 07:44:44, 07:45:26 main: three delegates (stash logs, collect saplings, tree loop) were refused because of
    the 07:41:39 "store 10 oak logs…" failure.
  - 67 07:41:45 `goto → error: not retried: delegate failed here 21s ago…` (**a goto blocked by a delegate failure**).
    69 07:50:00 `goto → error: not retried: delegate failed here 2s ago: needs_input…`.
  - 06:48:03 / 06:48:09 main: `goto` to the player was refused ("failed here 44s ago") right after PoofimaFox said "opened
    the door for you". The world had changed, but the guard still refused.
- **Root cause**: `MineClaw.Agent/Tools/ToolCallGuard.Check`. The tool check passes for any pair where either side is
  `delegate`: `toolMatch = … || tool == DelegateTool || f.Tool == DelegateTool`. Targets are *every* coordinate in the
  delegate's free text, and `Overlap` fires on any shared one. Almost every task mentions the house chest `14,64,-5`, so one
  failure poisons every later task and every goto. `needs_input` is recorded as a failure. Nothing clears the memory when
  the world changes (door opened, item obtained) or when the player re-asks. Only the literal words "try again" clear it.
- **Impact**: High. The bot repeatedly told the player it couldn't do things it could. It forced extra rounds, wrong
  apologies ("say try again"), and silent turns. The guard was a direct cause of top-5 turn #4.
- **Fix**: Match failures by (tool, *primary* target) only. For delegate, use the task signature (or Jaccard on the task
  words), not any coordinate. Never cross-match delegate to game tools except on the exact target. Don't record
  `needs_input`. Expire an entry on any new player chat that re-asks for the same thing, on block changes near the target,
  and on inventory changes. Cut `Memory` from 10 min to about 2 min.

### B2. A clone named mid-sentence is not addressed ("also 67, …", "hello 67?", "hey 69, …")
- **Evidence**:
  - 06:58:07 `<PoofimaFox> also 67, this block I'm punching is your cell to mine`. 67's log has no triage. Main took it and
    ran a silent turn (06:58:13, `said:` empty).
  - 06:59:21 `<PoofimaFox> hello 67? did you hear me about mining the right cell???`. Again 67 got nothing and main went
    silent (06:59:25).
  - 07:37:09 `hey 69, just deliver tools to any farming clone…`. 69 never answered.
  - By contrast, 06:56:38 `67 you need to…` (name first) was answered by 67 at 06:56:43.
- **Root cause**: `MineClaw.Hive/Chat/ChatArbiter.Rebuild`. A bare number only matches at the start of the line
  (`^[\s@#]*{num}`). Mid-sentence it only matches as `maid chan 67`, `clone 67` or `#67`.
- **Impact**: High. The player had to repeat themselves, was ignored twice, and got angry ("oh my god both of you stop").
  Main spent turns on messages that weren't for it.
- **Fix**: Treat a live clone's number as a vocative anywhere when it is (a) preceded by a greeting word or comma
  (`hey|hi|hello|yo|ok|also|and|so|oi`), or (b) followed by `,` `?` `!` `:`, or (c) at the end of the line. Keep "get 67
  cobble" (number followed by a noun) as a quantity.

### B3. The prompt cache prefix is lost after every tool-using turn (≈ $0.75, 19 % of Opus spend)
- **Evidence**: For consecutive main Opus turns, compare the first call's cache_read with the previous call's prompt length.
  After a single-call turn the loss is a median of **4 tokens**. After a multi-round (tool) turn it is **344–633 tokens**
  (segments 06:46–07:09: 516; 07:09–07:27: 633; 07:27–07:52: 344; after 07:52: 448). Example: turn ending 06:47:25 (prompt
  9.9K); the next turn at 06:48:03 reads only 8,989 (the *round-1* prefix of the earlier turn) and writes 1,143. In total,
  157K tokens were re-written that should have been read. Opus cache writes are 40 % of Opus spend.
- **Root cause**: `MineClaw.Agent/Context/ContextManager.Commit` runs `StripThinking` (removes thinking blocks from every
  assistant message of the turn) and `Seal` (replaces whitespace-only text with "(silent)", inserts cancelled tool results).
  Both mutate messages the API already cached, so the next request diverges at the turn's first assistant message. This is
  the same class of bug as the "per-turn data only in the newest user message" rule, but on the assistant side.
- **Impact**: High cost (≈ $0.75 tonight, more on longer sessions), and slightly higher latency.
- **Fix**: Commit the episode byte-identical to what was sent. Keep thinking blocks (or strip them only when the episode
  leaves the cached window, i.e. at compaction). Do the "(silent)" substitution at send time, not afterwards. Add a test
  that serializes request N+1 and asserts that it starts with request N's messages.

### B4. Chat injected mid-turn is not consumed and triggers a second, silent turn
- **Evidence**: 7 alice and 2 live2 follow-up turns start in the same millisecond the previous turn ended, and say nothing:
  07:02:13.724, 07:10:05.721, 07:12:58.217, 07:28:20.014, 07:31:34.096, 07:32:47.663, 07:41:30.608 (each 1 call, out 2–49
  tokens, cw 700–1,960). Example: `<PoofimaFox> also bring me 40 cobble…` (07:30:48) was answered inside the running turn
  ("Sure thing foxy~ I'll bring you 40 cobble…", 07:31:34.090). Then a new triage fired at 07:31:34.096 and produced
  `said:` (empty, $0.0115).
- **Root cause**: The events consumed by mid-turn injection stay in `EventQueue` (or are re-armed by `EndTurn`), so
  `WaitBatchAsync` returns them right after `HandleBatchAsync` (`MineClaw.Agent/BotAgent.RunAsync`). **(fix incomplete?)**:
  "mid-turn injection" is in the fixed list. Check that the undeployed fix marks injected events handled.
- **Impact**: Medium. $0.08 tonight; each follow-up also re-writes 1–2K of cache.
- **Fix**: When a chat event is folded into a running turn, remove it from the pending batch (mark it consumed by id).
  Skip a turn whose batch contains only events already seen by the previous turn.

### B5. Replies duplicated: paraphrase after a tool round, and "two voices" after the 07:52 deploy
- **Evidence**:
  - 07:32:14 `Okie foxy, I'll make you a door too~ and bring it with the rest uwu` (round 0, with remember_note), then
    07:32:17 `I'll make you a door too, foxy~ it'll come with the slabs and cobble uwu` (round 1).
  - 06:54:02/04 in one episode: `Got it foxy! Doing 5 rows out west…` then `Sorry foxy, my floor building keeps messing
    up… Say "try again"…`. The promise came before the tool result, then a contradiction.
  - Every reply after the 07:52 restart contains a plain sentence plus a lowercase persona re-statement of the same content,
    truncated at the 3-line cap with "...":
    - 07:53:37 `…I don't have a 420 helper, just 67 and 69 sitting idle… no 420 on island rn foxy, only you and me here...`
    - 07:55:09 `67 made it onto the island right next to me… 67 landed safe right next to me foxy, no falling this time!`
    - 08:05:37 `…Probably not, since I hit the top half of the door. i think so?? just clicked the door…`
    - 08:26:59 `…there are melon stems growing, but no melons yet… melon stems are growing up north but no melons yet...`
- **Root cause**: (a) The model emits text in round 0 next to tool calls and again in its final round. Nothing dedupes the
  second statement against what was already said this turn (`BotAgent` speech path / `ChatRouter.SayAsync`). (b) The
  07:52 build changed the prompt: the first turn after that deploy opened with "Since nothing is waking me up for a
  reason…". The likely trigger is the new `Prompts.BrainStyle` / `TerseStyle` "Internal style" section, which makes the
  model write a terse plain draft and then the persona chat line, and both are treated as chat.
- **Impact**: High visibility. It reads as spam, it contradicts itself, and replies hit the 3-line cap and get cut off.
- **Fix**: In the speech guard, drop a sentence whose content-word Jaccard against any sentence already sent this turn is
  ≥ 0.6. Forbid speech in rounds that also call tools unless it's a short acknowledgement, and only send the final round's
  text. Reword the BrainStyle section to say plainly that "plain text is game chat; write exactly one in-persona version".
  Add a regression test with the 07:55:09 text.

### B6. Hive "one owner per request" refuses legitimate second-clone spawns (spar, trees + cobble)
- **Evidence**:
  - 06:55:30 `hive → no: 67 already owns PoofimaFox's request; name 67, or op handover to move it to 69`. The tree clone
    never started (main: "The tree clone didn't start though").
  - 07:49:13 ×2 `no: 67 already owns PoofimaFox's request; … move it to 69` / `… to 420`, for a *spar* request while 67 was
    chopping trees. Main worked around it by **recalling 67 mid-job** (07:49:17 "I sent 67 home").
  - 07:50:20 `no: 69 already owns PoofimaFox's request; … move it to 420`. A spar needs two clones, so this rule can never
    be satisfied. Main then told the player a false reason: "I can't bring 420 back in while the spar is still running".
- **Root cause**: `MineClaw.Hive/Supervisor/HiveSupervisor.SpawnCore` / `OwnerSlot` / `SameRequest`. Ownership comes from
  the player's *conversation* (sticky/thread), and `SameRequest` is a Jaccard ≥ 0.5 over task words. Delegate texts are
  heavy on boilerplate (chest 14,64,-5, food, pickaxe, deposit), so unrelated jobs look like the same request. Multi-clone
  jobs are impossible by design.
- **Impact**: High. Useful work was killed, requested work never started, and the player was given wrong explanations.
- **Fix**: Key ownership on the originating player message id that main passes with the spawn, not on the conversation.
  An explicitly named spawn (`name: 69`) for a task main writes itself should always go ahead. Allow N clones per request
  (`group` id). Strip coordinates and stopwords before running `SameRequest`, and require ≥ 0.7.

### B7. Clone crafting desync: window-0 clicks rejected, phantom sticks, 24-round failure
- **Evidence**: 69 at 07:14:44, 07:14:51 and 07:15:22: `click 28/34/46 in window 0 rejected; resyncing`. The worker failed at
  07:15:27: "Inventory is 9 cobblestone and 26 sticks, no planks left. Craft calls kept failing 'ran out of ingredients' or
  'missing 1 Wood'…". The `inventory` tool at 07:15:38 shows **only `cobblestone x9`**. A script at 07:15:44 reports
  `+26 stick, +2 oak wood`, then `craft: ran out of ingredients for stone pickaxe` (07:15:47). 22 window-0 rejections in
  total, all on clones. Every clone logout has them (07:16:17, 07:49:17, 07:51:28, 07:55:04; 420 with an empty inventory).
- **Root cause**: `MineClaw.Client` window/inventory model (`MinecraftClient.Dispatch` transaction handling around line
  285, and the 2×2/3×3 crafting click sequence). After a rejected click the local model shows crafted output or stacks that
  the server never gave, and the "+N" delta reports phantom gains. This is probably the same family as the fixed
  "phantom slab inventory". **(fix incomplete?)**: it shows on crafting, not only on slabs.
- **Impact**: High for crafting duty (69's whole job). It cost $0.078 and 25 rounds. Main was told "no pickaxe".
- **Fix**: After a rejected transaction, wait for the server's window items (0x68) before the next click. Have craft
  verify the result slot against the server's slot set (0x67) before shift-clicking. Base "+N" deltas only on
  server-confirmed slots. Add a replay test from the 69 trace.

### B8. Background tools hold the brain for 20 s before returning "running in background"
- **Evidence**: Every `chop_tree`, `delegate` and `hive spawn` result arrives exactly 20.0 s after the action started:
  chop at 07:37:07.765, tool result at 07:37:27.763. Also 07:44:51.697 → 07:45:11.670, 07:46:03 → 07:46:23,
  06:55:05 → 06:55:25, 07:12:34 → 07:12:54, 07:51:54 → 07:52:14. Player chat → first bot line: **median 9.2 s, p90
  36 s, max 55.6 s** (61 player lines). Slow examples: 06:54:55 → 38.4 s, 07:12:09 → 40.1 s, 07:35:42 → 36.4 s,
  07:44:37 → 37.0 s.
- **Root cause**: `AgentOptions.InlineToolWaitSeconds = 20` (`BotAgent.Execution.RunChainAsync`). Only pure walks use
  `Brainstem.MoveInlineWaitMs` (1.5 s). All parallel tool calls in the round, and any chat the model planned to send after
  them, wait for it.
- **Impact**: High for perceived reactiveness. This overlaps the in-progress brainstem work, but jobs aren't covered by
  it yet.
- **Fix**: Use about 2 s for jobs too, or return immediately for activities known to be long (chop_tree, delegate, script,
  hive spawn, harvest_generator). Speak the round-0 acknowledgement before executing tools.

### B9. Idle check-ins run on Opus after the cache has expired (top-5 turns #1 and #5)
- **Evidence**: 08:26:42 triage with no player chat. Call at 08:26:48 `cr 0 cw 13513`, turn $0.084. Unsolicited chat:
  "I found the food situation…". 08:05:10 `cr 0 cw 11747` ($0.076) after a 10-minute gap.
- **Root cause**: The idle timer (`BotAgent.StartIdleTimer`) fires a Main-tier turn. `StaticTtl()` only uses the 1 h TTL
  when `StaticPrefixTtl` is "1h"/"auto" and the last activity is within `ActiveSessionMinutes`. History breakpoints are
  always 5 m, so any gap over 5 minutes rewrites the whole prompt.
- **Impact**: Medium. $0.16 tonight for 2 turns of little value.
- **Fix**: Route idle check-ins to the cheap tier, or skip them when nobody spoke in N minutes and no task is running. Use
  1 h TTL on the history breakpoint while players are online.

### B10. Spawn-protection inferred from one undone dig blocks replanting far from spawn (clone side)
- **Evidence**: 67 at 07:40:48: `learned protected_region center 0,66,-3 r16 (server undid dig at 13,70,2, 13 from spawn)`.
  At 07:41:20: `replant blocked: … skipping 13,65,3: inside the area where the server undid my edits (within 16 of spawn…)`.
  The worker ended `failed` (07:41:24, 25 rounds, $0.079). Main replanted fine at 11,65,0 (07:36:37), 17,65,3 and
  23,65,3. Main itself logs `edit at (16, 67, 3) worked inside the remembered spawn protection: retracted it` (07:46:12),
  which shows the region is wrong.
- **Root cause**: The protection learner (`MineClaw.Bot` world/claims protection code, the `learned protected_region`
  logger) accepts a single "server undid dig". At 13,70,2 that was a leaf in a crown being chopped by two bots at once (67
  and main shared trees), not protection. The retraction exists in main, but the clone keeps its own copy.
- **Impact**: High for tree stewardship. This is not covered by "doors sealed by protection" or "dig-cell retract".
- **Fix**: Require ≥ 2 independent undone edits at y ≤ spawn + 5 before learning a region. Ignore undone digs on blocks
  claimed by or adjacent to another hive bot's activity. Propagate retractions over IPC (shared-memory waypoint) so
  clones drop the region too.

### B11. A restart wipes the conversation history; the bot forgets what it was doing
- **Evidence**: Telemetry `sections.history` = **0** on the first call after each start: 07:09:36, 07:27:42, 07:52:46.
  Main at 07:53:37: "I don't have a 420 helper… is it someone new?" one minute after it spawned 420 twice. After the 07:09
  restart, the farm delegate from 07:10:02 ("no wheat at farm") was repeated at 07:28:11 with the same failure, and
  ask_memory at 07:27:48 still claimed "a wheat farm (5x3) is at -1,64,-22".
- **Root cause**: `ContextManager` episodes and `ToolCallGuard` failures live only in memory. On start, `BotAgent` begins
  with an empty history. A worker's negative finding ("only melon stems, no wheat") is not written as a fact that overrides
  the older "wheat farm" memory.
- **Impact**: Medium-high during redeploys (3 tonight). There were repeated failures and repeated questions to the player.
- **Fix**: Persist the last ~10 committed episodes (or the journal plus the last 3 episodes) to SQLite on commit, and
  reload them if the shutdown was < 30 min ago. Persist the guard's failure list with its TTL. When a delegate result
  contradicts a memory (wheat farm → melon stems), write a superseding note.

### B12. harvest_generator: walking into the cell loops "stuck, re-planning"; about half the cobble drops are written off as "void"
- **Evidence**:
  - Main logged 19 `goto: stuck, re-planning` (e.g. every ~8 s from 06:57:08 to 07:00:09, then 07:13:25 to 07:15:17); 67
    logged 18 (06:57:10 to 06:59:44).
  - PoofimaFox at 06:57:49: "you two seem to get stuck trying to walk into the block".
  - Yield: "dug 64, +33 cobblestone" (07:00:11); "harvested cell 6,64,8 24 times, +20" (07:15:24); 67 "Only 2 cobble".
  - 35 (main) and 29 (67) `collect: not chasing item 4:0 x1 at 6.3,64.1,7.7: over or next to the void`. Those drops lie
    in the generator's water channel; `look` at the same spot says "void E8 S6".
  - The same drop is logged up to 5× (07:46:25–07:46:37, `6:0 at 17.1,67.6,6`).
- **Root cause**: `MineClaw.Client/MinecraftClient.Gathering.HasSafePickupSpot` rejects drops whose neighbourhood has no
  `Safety.IsStandable` cell. Cells next to lava/flowing water aren't standable, so generator drops are "void". The log
  message is wrong, and `lostIds == null` means the line is logged on every pass. The generator harvester walks to each
  drop or cell instead of staying at the taught stand spot, and fights the water push.
- **Impact**: Medium-high. The core resource loop runs at about 50 % yield, gets stuck, and the player complained.
- **Fix**: In harvest mode, stand on the taught spot (it's in the demo: "stood at 7,63,8") and dig only the cell. Collect
  drops only from cells reachable without entering water or lava. Distinguish "hazard" from "void" in the message. Log
  each lost drop once.

### B13. Hive task partner changes aren't pushed to the clone (needs_input for a missing partner)
- **Evidence**: 420 failed at 07:49:40. Main spawned 67 as the replacement at 07:49:48 ("67 is stepping in as 69's
  sparring partner"). 69's worker at 07:49:58 returned `needs_input: player maid_chan_420 not found…`, and 69 kept
  "waiting at the base for 420 to log in" (07:50:26) until the spar was cancelled.
- **Root cause**: `HiveSupervisor` sends the task once. When the brain re-plans, the other clone's task text isn't updated
  (no `task` update message to 69).
- **Impact**: Medium. The spar never happened; 3 clone sessions were wasted.
- **Fix**: When main re-assigns a role in a multi-clone job, send an updated `task` (or `inject` note) to every clone in
  that group. Have the clone worker treat "partner offline" as "ask main", not as `needs_input` to the player.

### B14. Repeated food begging after the player acknowledged it
- **Evidence**: The player said "dw about food for now" (07:01:43) and "I'm working on the food situation <3" (07:14:46).
  The bot still asked for food or snacks at 07:10:04, 07:12:58, 07:14:08, 07:14:51, 07:28:18, 07:52:46, 07:53:10, 07:53:39,
  07:55:10 and 08:26:59. 48 hunger/starving log lines. Each restart re-fires `danger Hunger: starving` and a turn to beg.
- **Root cause**: The survival danger event (`Client survival` → `Bot` event → `danger Hunger`) has no "acknowledged /
  no food source" latch, and the latch doesn't survive restarts (B11).
- **Impact**: Medium (annoying, about $0.10).
- **Fix**: After one ask, latch hunger as "reported". Re-raise only on a threshold change (hp ≤ 6) or when food appears
  in a chest. Persist the latch.

### B15. Clone brains use `script` as a block-scanner; "say" muted makes scripts fail
- **Evidence**:
  - 67 at 06:56:12: `script … Failed: … (say: muted (hive clone speaks only when addressed))` after printing 5 lines of
    block data.
  - 67 at 06:56:14, 06:56:17, 06:56:20 and 06:56:50: four scripts that `error()` out with grid data
    (`3,5=stone_slab 4,5=air…`) to smuggle the result back.
  - 67 at 07:37:06: a chop script fails on `say` after 30 s of real work (`+4 oak wood`).
  - Main at 07:13:00 used the same trick (`Failed … (4,6=cobblestone 5,6=water…)`).
  - 67 at 07:41:34 / 07:41:36: Lua `deposit: unknown place 'all'`, `deposit: unknown place 'stone_axe'`. The script API
    signature was guessed wrong twice.
- **Root cause**: There is no script → brain return channel other than `say`, and the clone mute makes `say` throw instead
  of no-op (`MineClaw.Bot/GameTools/ScriptTools`). `look {map:true}` evidently doesn't give the brain the per-cell grid it
  wants. The `deposit` API docs in the tool description are ambiguous. **(fix incomplete?)**: "Lua say spam" is fixed for
  chat, but check that a muted `say` doesn't abort the script.
- **Impact**: Medium. 6+ wasted Opus rounds, and real work was aborted.
- **Fix**: A muted `say` should return to the brain as `report` output (not fail). Add `return value → tool result`. Add
  an `area` / `scan` read-only tool (bounded grid). Document `deposit(chest_or_waypoint, item?, count?)` with an example.

### B16. Clone task resume and recall race: a recall and a new task in the same turn drop the task
- **Evidence**: 07:19:20 main: `hive → ok: recalling 69` **and** `hive → ok: 69 got the new task (pending until its result
  arrives)` in parallel. 69 logged out at 07:19:21 ("recalled") and the task was lost. 07:19:52 `hive list: 69 idle`, so
  main had to spawn 69 again at 07:19:55 (another cold start, $0.04).
- **Root cause**: `HiveSupervisor.SpawnCore` assigns `slot.Task` to a Running slot that is already marked for recall, and
  the recall wins. Parallel hive ops aren't ordered per slot.
- **Impact**: Medium-low. One lost assignment, a false "got the new task" to the brain, and an extra spawn.
- **Fix**: Serialize hive ops per slot within a turn. A task for a slot in `Stopping`/recall should become "respawn with
  this task" (or return `no: 69 is logging out`).

### B17. Meta and process narration still reaches chat **(fix incomplete?)**
- **Evidence**: 07:52:46 `<maid_chan> Since nothing is waking me up for a reason, I'll keep it short and just ask for
  food.` At 07:54:53 the pending-claim guard dropped a fragment and left a stray clause: `dropped a pending-result claim
  from main's chat: , the only helpers I've got are 67 and 69.` (that isn't even a pending-result claim).
- **Root cause**: `MineClaw.Agent/Chat/MetaSpeech.MetaRx` matches only tool/delegate vocabulary, not "waking me up",
  "nothing is waking me", "for a reason". The pending-result guard splits on commas and drops clauses that don't
  mention a pending clone task.
- **Fix**: Add wake/turn/prompt vocabulary ("wak(e|ing) me", "this turn", "no reason to") to `MetaRx`. Restrict the
  pending-claim guard to sentences that name a clone *and* a result verb.

### B18. Inline "/me" inside a chat sentence is sent literally
- **Evidence**: 07:47:57 `<maid_chan> Hai, foxy sama~ /me curtsies. Your maid has chopped…`
- **Root cause**: `ChatRouter.SayAsync` treats `/me` as an emote only at the start of a line.
- **Fix**: Split `/me …` out of the sentence (up to the next sentence end) into its own emote line, or strip it.

### B19. Concurrent clone spawns hit the server login throttle
- **Evidence**: 67 at 07:35:54 / 07:35:57 and 69 at 07:49:24 / 07:49:27: `WRN App: connect failed: server closed the
  connection; retrying in 3s/6s`, whenever two clones launched in the same millisecond (07:35:52.127 ×2, 07:49:22.551 ×2).
  Each lost 6–10 s, and 67 joined 11 s after 69.
- **Root cause**: `HiveSupervisor` launches clones in parallel, and the b1.8.1 server's connection throttle rejects
  logins less than about 4 s apart from one IP.
- **Fix**: Stagger launches by ≥ 4.5 s (a per-server join gate in the supervisor). The existing backoff can stay.

### B20. Wrong or optimistic claims vs tool results (not covered by "honest corrections")
- 07:02:12 "I'm back on my block now and 67 is headed to the one by the lava". The delegate had only just started; at
  07:03:33 it failed: "goto 7,64,8 has no route".
- 07:21:54 "Got the new pickaxe, ty 69, and I'm working side B now". The script was mining the floor (07:21:58 "you're
  mining the flooor").
- 07:45:35 "Foxy just logged off…". He had re-joined at 07:45:35.04, before the line was sent (stale event).
- 07:50:23 "I can't bring 420 back in while the spar is still running". The real reason was B6.
- 07:52:17 "420 is spawning back in". 420 had failed at 07:52:17.014.
- **Root cause**: Speech in the same round as an async tool start is written before any result. The speech guard checks
  only *clone* pending results, not the brain's own background tasks or player join/leave state.
- **Fix**: Extend the pending-result guard to the bot's own `running in background` tasks ("I'm X-ing now" is fine,
  "done/working side B" isn't). Re-check join/leave right before sending.

### B21. Main's script "cobble_b" mined the floor instead of the taught cell
- **Evidence**: The demo at 07:20:07 was learned as `stood at 7,63,8 facing cobblestone; punched cobblestone at 6,64,8`.
  At 07:21:33 the bot saved script `cobble_b` v1 and ran it from 7,64,7 (it never dropped into the hole). It was cancelled
  at 07:22:03 after 15 acts and +2 cobble, and `look` then shows `void E2 S1 (EDGE)` (it had dug the floor). The player had
  to repair it (07:23:10).
- **Root cause**: The Opus-written script dug the "nearest cobblestone" rather than the absolute demo cell. There is no
  stand-position precondition. The protection against digging your own support / floor near the void didn't trigger.
- **Fix**: `harvest_generator` / the structure runner should take the demo's absolute stand and dig cells, and refuse if
  not standing on the taught spot. Guard digging any block that is the floor of a walkable path with void within 3.
  Prefer the built-in harvester over ad-hoc Lua for taught structures.

### B22. Budget summary line shows main-only spend against the hive cap
- **Evidence**: At 07:52:36 main: `today $2.980/50.00`. Ledger: `hive today $4.882/50.00 (clones $1.902)`. "calls/h"
  counts RAPTOR summaries (245 calls/h at 07:09 while Opus did about 20).
- **Root cause**: `UsageTracker.Summary()` prints `_state` totals and ignores `ExternalSpend`, which is used only for the
  mode. Calls/h includes every tier.
- **Impact**: Low (display only), but it misled the "today" figures in logs and the TUI.
- **Fix**: Print `today $main+clones/cap (main $x)`, and report calls/h per tier.

### B23. RAPTOR summaries are truncated (255 of 389 stop at max_tokens = 150)
- **Evidence**: telemetry `purpose: raptor_summary`, `stop_reason: max_tokens` 255×. A full rebuild runs after every
  restart (06:46:50 "changed fraction 26 % > 25 %", 112 summaries, 54.5 s).
- **Root cause**: The summary max_tokens (150) is below what the prompt asks for.
- **Impact**: Low cost ($0.07), but the retrieval tree holds cut-off sentences (which may explain the stale "wheat farm"
  answers).
- **Fix**: max_tokens 250, or ask for ≤ 80 words. Don't count restart-induced churn toward the rebuild threshold.

### B24. Drops handed to main and logout deposits: small losses and unreachable chests
- **Evidence**: 67 at 07:49:25 `handoff to main: tossed 29 item(s); main collected 28`. `deposit into chest (14, 64, -5):
  can't reach chest` (67 07:49:17, 67 Lua 07:41:41, main worker 07:31:07 `withdraw! can't reach chest at 14,64,-5`).
- **Root cause**: The chest-reach failures are probably the fixed "doors sealed by protection" bug. The 1 lost item came
  from a handoff while main was moving (chopping).
- **Fix**: Re-verify after deploy. For handoff, ask main to stand still (brainstem pause) during the toss.

---

## 3. Smaller observations

- Simultaneous parallel `hive spawn` calls put both jobs on slot 67 at 06:55:05 (two pids, 12880/12881), then "67 lost
  (pipe closed)". This is covered by the "false main lost from duplicate spawns" fix. Re-test with two unnamed spawns in
  one round.
- `Rag: query embedding timed out; FTS-only` once in main (07:13:44, 4 min after start) and once in 67 (06:55:10). The
  embedding warmup fix should cover both.
- Memory peak grew between runs: 466 MB → 631 MB → 702 MB (journal), then 395 MB on the last, short run. Worth a look if
  sessions get longer.
- Main asked "do you want them or should I stash them?" at 07:37:43, then idled 3 minutes ("why is main idle?", 07:40:56).
  This is covered by "questions with defaults".
- watch_demo at 07:00:57 recorded the player's two *punches* as `right-clicked cobblestone on cobblestone`. The second demo
  (07:18:17) recorded `punched` correctly. Check the arm-swing vs block-place classification when the player holds
  cobblestone.

---

## 4. Live2 (PC run, 00:41–01:13 local = 04:41–05:13 UTC)

live2 ran **before** commit 1369a44. Every finding below was re-checked against the current working tree, uncommitted
changes included. Two problems in the logs are already fixed and are left out:

- The `ClusterIndex.Recluster` KeyNotFound loop (212 WRN lines with stack traces, 1/s from 01:09:39, in main and 67).
- The clone start failure "DailyCapUsd must be > 0" (67 retired at 01:00:06).

Findings that repeat alice bugs:

- ToolCallGuard: 67 at 00:52:27 `not retried: … needs_input: which cell is minable` right after Fox demonstrated the
  cell; main at 00:43:59 goto refused after "come to me" was repeated. This is **B1**.
- 20 s inline wait: every delegate/chop result arrives about 20 s late (00:41:58→00:42:18 … 01:11:56→01:12:16). At
  01:00:20 `67 spawning` was delivered 14 s after the hive had retired 67, so main said "my clone's got the west side"
  and then corrected itself. This is **B8**. In live2 the first action came after 3–8 s, but replies took ~29 s whenever
  a delegate ran in the round (00:59:53, 01:02:29, 01:03:20, 01:11:48).
- 25× `collect: not chasing … 6.4,64.8,8.8: over or next to the void` inside gen cell 6,64,8, and `goto: stuck` at the
  gen (01:06:51–01:09:54). This is **B12**.
- Empty Opus wakes at 00:41:42, 00:47:55, 00:56:58, 00:57:03, 01:01:03 and 01:10:57 (≈ $0.035). This is **B4**/**B9**.
  Every triage logs `(config)`, so the wake source can't be seen (L7).

### L1. A second watch_demo wipes the first demo's steps and stores relative offsets with no anchor
- **Evidence**: At 00:52:23 main logs `learned dual cobble generator (4 steps): stood at -1,+1,-1 …; broke cobblestone at
  +0,+1,+0`. The first demo's absolute east-cell steps (00:46:52, `stood at 8,64,8 … 6,64,8`) are gone from the 00:59:06
  recall. Main misread the offsets at 00:52:35: "is the clone's brick the one at 3,64,5?" (that is the stand spot).
  67 at 01:02:46: "doesn't say which cell is the second one". On alice the same confusion led to the wrong cell, the
  floor being mined (B21), and repeated questions.
- **Root cause**: `MineClaw.Bot/Demo/HumanGameTools` watch_demo → `DemoSummary.Summarize(actions, known?.Min)` writes
  offsets from the structure's min corner. `MineClaw.Bot/Memory/MemoryAdapters.SaveSteps` runs `s.Steps.Clear()` before
  adding the new steps.
- **Fix**: Store absolute coordinates, or put the anchor in the text ("relative to 4,63,6"). Append a second demo as a
  variant ("cell 2") instead of replacing.

### L2. Two bots handle the same player line before any thread lease exists
- **Evidence**: At 00:51:47 Fox says "watch me punch the correct brick to mine for your clone". 67 answers at 00:51:49.5;
  main answers at 00:51:51 and again at 00:51:52. Both run watch_demo. At 01:11:16, 67 was mid-conversation about
  "2 rows outwards" when main spawned 69 for the same job; 67 then delegated a conflicting job at 01:12:13.
- **Root cause**: `ChatArbiter` leases open only when the tool starts. Main triages within ~1.5 s, before the clone's
  lease arrives over IPC. `SameRequest` treats follow-ups as different jobs. This is the opposite failure of B6.
- **Fix**: While a clone is the player's sticky partner, main yields that player's lines unless it is named. Count
  follow-ups to a clone's open request as the same request.

### L3. Raw coordinates in the model's own sentences
- **Evidence**: main 00:49:15 "doing at 10,64,8? uwu"; 00:52:35 "the one at 3,64,5?"; 00:59:17 "over by -1,64,10?";
  67 00:51:15 "the one at 4,64,6 or 4,63,6?". (Alice: only the Lua `say` dump at 07:12:49, already fixed.)
- **Root cause**: `ChatRouter.Landmark` is used only for tool-generated prompts. `MetaSpeech.IsData` drops only whole
  data lines.
- **Fix**: Rewrite `x,y,z` in outgoing model text to the Landmark description when one resolves. Add a persona rule.

### L4. More meta narration gets past MetaSpeech (adds to B17)
- **Evidence**: 67 01:07:29 "I've started the job in the background: grab a stone axe…"; 69 01:11:52 "The floor job is
  running in the background, I'll wait for its result."
- **Fix**: Add `i'?ve\s+`, `(?:the|my) \w+ (?:job|task) is running`, `in the background`, and `wait for (?:its|the)
  result` to `MetaRx`.

### L5. Recall deposit from inside the gen hole fails; the tool goes to main; the respawned clone does an unannounced prerequisite
- **Evidence**: 67 at 00:52:46: `deposit into chest (14, 64, -5): can't reach chest`, then the same for 15,68,-8, then
  `handoff to main: tossed 1 item(s)` (Fox's wooden pickaxe). The respawned 67 had no pickaxe and chopped a tree
  (01:03:08), which Fox questioned ("67 you're mining a tree?"). 67 replied with an invented excuse: "got distracted by
  a shiny tree".
- **Root cause**: `MineClaw.Hive/Inventory/DepositProcedure.RunAsync` treats "can't reach" like "full" and hands off on
  the first failure, with no re-path from inside the hole.
- **Fix**: Step out to an open standable spot and retry before handing off. Send tools to the storage chest, not main.
  The worker should say why it is doing a prerequisite ("no pickaxe, crafting one").

### L6. Smaller live2 items
- `ask_stand` timeout fallback accepts chatter as the answer. At 01:12:31: `PoofimaFox stands at -1,64,1 (said
  "…that is wood not cobble 67")`. Cause: `HumanGameTools.RegisterAskStand` has `reply ??= said.LastOrDefault()`.
  Return an ambiguous result on timeout instead.
- The chop report says "sapling kept" with 0 saplings: `MinecraftClient.Trees.cs:768` always appends ", sapling kept".
  The worker also chopped 10 trees and replanted none without stopping. Stop after 2 consecutive replant failures.
- Main claimed "I told 69 to match the width…" (01:12:18) with no hive call that turn; 69 had already logged out. Add a
  post-turn check for "told/sent <clone>" without a hive call.

### L7. Observability gaps (live2 and alice)
- Worker tool calls and results are never logged (`WorkerAgent` logs only "worker call N" and the summary). Neither the
  69 crafting failure (B7) nor 67's "unprotect the generator" needs_input can be traced.
- Every triage logs `(config)`. Log the waking event kinds.
- Hive task texts aren't logged, so B6 can't be traced.
- A config-error exit code 2 is retried 3× as a "crash" (`HiveSupervisor`). Fail fast instead.
- `WorldIndexer.cs:127` logs a full stack every second. Rate-limit it.
- A RAPTOR refresh is a burst of up to 37 Haiku calls in ~13 s. Make sure no call-count limit still counts the memory tier.
