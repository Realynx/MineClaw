# Reactive character: act first, think in parallel, correct if needed

maid-chan should feel instantly responsive and alive. The API's latency can't be avoided, so we change the order of things.
The body reacts at once with a gaze, a gesture, a walk or a prepared in-character line. The Opus brain still runs, in
parallel, and brings the depth: it amends, corrects or stays silent. Most social moments become programmatic reactions that
are **learned**. The brain writes them, and the runtime runs them with no model call. None of this should look canned: lines
vary, fit the context, carry the persona's mood and relationships, never repeat, and arrive with human timing.

## 1. Baseline (measured)

**Live logs.** The snapshots `data/alice-logs-s3`, `logs/live2`, `logs/live-hive` and `logs` (10-26/27) predate
`latency-*.jsonl`. The table below is rebuilt from the text logs by `Telemetry/LatencyReport.FromLog`, which the test
`LatencyBaselineTests.Baseline_from_live_logs` prints:

- A chat is a player line that a brain turn picked up within 15 s.
- The first visible reaction is the bot's first chat line or first game action, whichever comes first. Those builds had
  no head turn yet.
- The full answer is the later of the turn's last line and its first action.
- A tool's start is taken as the end of the model call that issued it.

| kind | n | first reaction p50 | p95 | full answer p50 | p95 |
|---|---|---|---|---|---|
| command | 63 | 7.54 s | 21.49 s | 9.80 s | 109.60 s |
| question | 17 | 7.25 s | 32.80 s | 7.25 s | 34.00 s |
| banter | 25 | 5.27 s | 12.72 s | 6.95 s | 35.70 s |
| greeting | 1 | 4.03 s | 4.03 s | 4.03 s | 4.03 s |
| help call | 0 | - | - | - | - |
| all | 106 | 6.35 s | 14.14 s | 9.00 s | 60.60 s |

The first reaction was a line in 48 chats and an action in 35. In 23 chats nothing visible happened in the turn at all.

**Benchmark: before vs after.** `ReactiveCharacterTests.Benchmark_first_visible_reaction_under_300ms_with_a_slow_llm`
runs the whole bot host with a fake brain that takes 2–4 s per round, in real time. "Before" is the brainstem off, which is
the old ordering. "After" is the current build with prefetched lines:

| line | first reaction before | after | first chat line before | after | model calls after |
|---|---|---|---|---|---|
| come here | 2.96 s | 0 ms (gaze), walk at once | 2.96 s | 0.40 s instant line | 1 brain turn, may stay silent |
| follow me | 2.82 s | 0 ms | 2.82 s | 0.50 s | 1, may stay silent |
| stop | 2.81 s | 0 ms | 2.81 s | 0.30 s | 1, may stay silent |
| thanks! | 2.81 s | 0 ms | 2.81 s | 0.52 s, final | **0** |
| hi | 2.80 s | 0 ms | 2.80 s | 0.61 s, final | **0** |
| a punch binding (packet → dig) | (needed a brain turn) | 1.2 ms (17 ms on the first fire, engine warm-up) | | | **0** |

**Permanent metrics.** Every `LatencyRecord` now carries:

- `category`: command, question, banter, greeting or help (`Brainstem/ChatIntent`)
- `reaction_by`: gaze, gesture, line, move or action
- `full_ms`
- `instant`: an instant line went out before any model

`LatencyTracker.ReportLine()` and `LatencySummary.ByKind` give p50/p95 of first reaction and full answer per kind. They flow
into `latency-YYYY-MM-DD.jsonl` and the supervisor JSON. `LatencyReport.Markdown` produces this table from any record set.

## 2. The ordering pipeline (per event)

```
packet / chat line
  │ 0 ms     gaze to the speaker (head-turn overlay) + nod; brainstem gestures
  │ 0-2 ms   typed game signals → running script handlers + standing bindings / learned reactions (no model)
  │ 0-5 ms   reflex (exact command) or speculative walk (clear job) starts
  │ 0.2-0.9 s  instant line: a prefetched in-character line, typed like a human
  │          (think 160 ms + 22 ms/char, ±20 %, capped at 0.9 s)
  │ in parallel: the brain turn (Opus), whose turn header carries
  │            [already said] "omw foxy~" to Fox (reflex, 0.4s ago): …
  │            [already doing] goto #4 for 1.2s (started for this chat): keep, amend or stop
  │            [reflex] walking to Fox
  ▼ reconcile: keep (silent), amend (adds what the instant line didn't cover), undo ("actually, can't…" + stop)
```

- **Small talk** (greeting, thanks, praise, status, emote) that an instant line answered needs no model call. It is
  committed as a tiny episode tagged `[instant reply…]`, so the brain sees it in the next turn. A correction right after
  ("no, I meant…") escalates to the brain and counts as a miss, like the cheap-only path.
- **Reflex and speculative turns** still wake the brain. The brain sees what the body already did and said, and amends.
  Speculative walks are adopted, retargeted or cancelled by the brain's next move (`BrainstemController.Judge`, as before).
- **No double speech.**
  - An instant line counts as the turn's ack, so no quick-ack call is made.
  - A brain line equal or near-equal to it (same letters, or a word Jaccard of at least 0.7) is suppressed in both the
    streamed path and the end-of-round path.
  - The turn note tells the brain to add only what was missing, or to stay silent.
- **Safety first.** Instant lines and bindings never run while `SafetyEngaged` is set (EdgeGuard, survival, combat,
  shelter). The safety layer of the priority stack still preempts everything.

## 3. Learned reactions and standing bindings ("learned, not coded")

Code adds only the primitives:

- the typed signal set
- the filters
- the `character` SDK module
- the binding runner
- the `script op=bind` path

Every behaviour lives in the script library as a script plus its trigger:

- **Standing bindings**, from a player's request: "when I punch a log, chop that tree", "if anyone hits me, guard me". The
  brain calls `script{op:bind, name, goal, params:{on:'player.punchBlock', player, held:'axe', for, minutes}}`. The Opus
  author writes the short JS (the event arrives in `args`) through the usual lint and dry run. The trigger is saved on the
  script's routine row (`RoutineScript.Binding`), so it is versioned, hive-shared and survives restarts. `op=bindings`
  lists the bindings. `op=unbind name` turns one off ("stop doing the punch thing"); the creator or a trusted player may do
  that.
- **Learned reactions**, from the brain's own reconcile ("next time do this instantly"). They use the same op with
  `kind:'reaction'` and a chat intent as the trigger (`on:'thanks'`, `'come'`, `'greeting'`…). Reactions must be
  harmless: they may move, look, gesture and talk.
- **Nothing is seeded.** The library starts empty. The brain writes the first version the first time it meets a situation,
  following the cached `ScriptPrompts.BindLine`: "offer one when a player repeats a request".

**The runner** (`Bot/Brainstem/BindingRunner`) checks each signal against the live bindings in this order: event or
intent, scope player, expiry, filter, cooldown. The first match starts that saved script as a normal run, with
`args = payload + {event}`. Its rules:

- Not while a safety reflex holds the body.
- Not over a running script or a job a player asked for. An idle standing order or a filler is fine.
- Main's body only.
- A world-acting binding (dig, place, attack, items, crafting…; `ScriptBindings.ActsOnWorld`) is created only for a
  trusted player and runs only if one made it. Regulars get the harmless kinds.
- Protected blocks, claims and the budget veto apply as for any script, because it runs through the same gated tools.

The JS engine is warmed when the runner starts, so the first fire is not cold.

**Feedback and demotion.** Each fire records ok, failed or skipped. After a reaction, the reconcile verdict is recorded:

- **kept**: the brain added nothing, or no model was needed.
- **amended**: the brain added lines.
- **corrected**: the brain's line opens with "actually / wait / sorry / no…", or the player corrects within 30 s.

A reaction corrected at least twice, and more often than it was kept, is demoted (turned off with a note). The brain hears
it as a background notice and answers that moment itself again. This follows the patch agent's demote-after-failures
policy for learned interrupt handlers, which live next to bindings on the same row (`RoutineScript.Handlers`).

## 4. Typed game signals (instant, no model)

`Bot/Perception/GameSignals` derives events from what the client actually observes. `Scripting/Events/GameEventCatalog`
documents each event and its source on both versions:

| event | payload | b1.8.1 source | 1.12.2 |
|---|---|---|---|
| player.punchBlock | player, pos, block, held | No dig packet for other players: an arm swing (0x12 anim 1) plus a raycast along their look to the first solid block in 4.5. Use-items are skipped, and the same block is debounced for 1.5 s. | Block Break Animation (0x08), plus the same swing path |
| player.swing | player, held | 0x12 anim 1 | 0x06 |
| player.hurt / died | player, by?, cause | 0x26 status 2/3. `by` is the player who swung within 0.8 s and 4 blocks, else a hostile mob in 4. | 0x1B |
| player.pickup / toss / equip | item, count / slot | 0x16 collect; an item spawn at their eye height; 0x05 | 0x4B / 0x00 / 0x3F |
| player.sneak / unsneak | player | anim 104/105, or metadata flag 0x02 | 0x3C |
| player.near / leave | d | positions (6 blocks) | same |
| player.join / quit, player.say | text, intent | player list, chat + `ChatIntent` | 0x2E, 0x0F |
| player.lookAt | target me / block | look ray within 0.6 of my head, or resting on a block, for 1 s | same |
| player.gesture | kind, strength, at?, answer?, target?, block?, pos | Body language (`Bot/Perception/GestureDetector`, below): look packets 0x20/0x21 (sent only at a turn of 8/256 or more, every 2 ticks), moves, metadata flag 0x02 / anim 104-105, anim 1 into empty air | 0x28/0x27 look (1/256), 0x36 Entity Head Look for the head yaw, 0x3C, 0x06 |
| block.changed | pos, from, to, by? | 0x35 / 0x34; `by` is the punch that hit it within 2 s | 0x0B / 0x10 |
| item.dropped, mob.near | item / mob, d | 0x15; a hostile within 8 | same |
| me.hurt / me.lowHp | by?, hp | 0x08 (low below 8, re-armed above 12) | 0x41 |
| time.dusk / dawn, weather | time / rain | 0x04 crossing 12500 / 23500; 0x46 reason 1/2 | 0x47; 0x1E |

**Filters** (`EventFilter`, all optional, all of them must match):

- `player` (a name or a list)
- `block`, `item`, `mob`, `kind` (a gesture's kind, else the mob), `cause`, `intent`, `target`, `at`, `answer`, `entity`, `by` (a word matches a name ending in it: `log` takes `oak_log`)
- `held`: pickaxe, axe, shovel, sword, hoe, tool, empty or any, or an item name. A pickaxe is never an axe.
- `text`, `dist`, `minCount`
- `pos`, `near {x,y,z,r}`, `region {min,max}`
- `cooldown` (seconds), `once`, `cut`

Scripts bind with `on('player.punchBlock', {player:'PoofImaFox', held:'pickaxe'}, e => dig(e.pos))`, with `react(...)`, or
with a machine's `on` key. Noisy events never cut the running action unless the script asks with `cut:true`.

### Gestures (`player.gesture`)

Fox 10-29: "gesture detection, like nodding my head yes or no ... a signal for the state machine scripting". `GestureDetector`
keeps a short per-player ring buffer (3.5 s, at most 128 samples) of yaw (the head yaw where 1.12.2 sends it, unwrapped through
±180), pitch (the byte angle's 330° read as −30°), position, sneak and a ground guess, fed at packet rate by
`EntityTracker.Updated`; swings and hits come from the animation and status packets, and a 4 Hz poll covers what needs time to
pass with no packets (a still player sends nothing). Deterministic, tuned against false positives first:

| kind | fires when | never when |
|---|---|---|
| nod / shake | 4+ alternating strokes of 10° or more (zigzag with that hysteresis) in 1.6 s, each stroke at most 0.7 s, median stroke 10-80°; the other axis steady (range at most max(10°, half the stroke), and at most 25°) | moved more than 1.5 blocks (walking while turning); a dig/place/hit swing or 2+ swings in the window (mining looks between cells); a single nod; a slow look down and up |
| bob | 3+ sneak toggles in 1.5 s | a single crouch (2 toggles) |
| wave | 3+ swings into empty air in 1.5 s, decided 0.3 s after the last | a block in reach, a use-item, anything hurt within 0.5 s of the swing (a fight, punching the bot) |
| jump | 3+ hops (a rise of 0.5 from a floor and back within 1.1 s) in 3 s within 3 blocks | sprint-jumping across |
| spin | 320°+ of one-way yaw in at most 1.3 s (at most 45° back) | turning around |
| stare | the look ray on a body (me or another bot) within 8 blocks for 1 s, once per gaze | further than 8 |
| point | a look resting 1.5 s on a block or entity out of reach (more than 4.5), then an air swing or a nod within 2 s; target exposed | a near block (that's a punch) or a glance |

One event per gesture instance (its evidence is consumed) and a 4 s cooldown per player and kind (stares 15 s). `at` is the body
the gesture's median look rests on ("me", or another bot's login), else any sample's, else a stare within 3 s; a nod carries
`answer:'yes'`, a shake `answer:'no'`. b1.8.1's 11.25° look deadband makes a 15° nod read as about 14°, so the stroke minimum is
10°; 1.12.2 resolves 1.4°. Meanings beyond nod = yes, shake = no are learned, never coded: "when I crouch twice, come here" is
a binding (`script{op:bind, params:{on:'player.gesture', kind:'bob', player:'Fox'}}`; `kind` other than reaction/binding is
the filter).

The brain (`Bot/Brainstem/GestureFeed`): one short line, `[gesture] PoofimaFox nods yes at you`. A nod or shake answers what is
pending first (`ChatRouter.AnswerGesture`): an ask_player waiting on that player (or an "anyone" ask, when aimed at us) completes
with "yes (nod)" at once; else our chat question to them within `Chat:GestureAnswerSeconds` (30) wakes the brain at Normal with
"(answering your question)", and the body nods back first. A gesture aimed at us wakes at Low, once per player per 30 s; the
rest is Background (one per player and kind per 20 s; stares per 60 s, never waking). A body handles what is aimed at it, main
the unaimed ones; a gesture aimed at another bot is never our answer.

## 5. Non-canned output

- **Prefetched lines** (`Agent/Character/LineBank` and `LinePrefetcher`). During idle time or long jobs (checked every
  30 s), one cheap batched call on the memory tier writes 3 lines for each likely moment in the current context. Wording is
  allowed on that tier; it never authors scripts. The moments are: greet and bye per online player, thanks, praise, tease,
  status, hurt, come, follow, stop, onit, sorry.
  - The prompt carries the persona, the `[self]` block (mood, relationships, nicknames, inside jokes) and the lines said
    recently.
  - Every line is linted: one line, ASCII, at most 90 chars, no commands, no coordinates, no numbers the situation lacks,
    no new promises.
  - Lines are tagged with the context signature: activity, players online, time of day, mood words and hp band. They
    expire when the context changes, or after 20 min.
  - At most 12 calls an hour, one in flight. No call when the budget is paused or nobody is online.
- **Phrase memory.** Every outgoing line goes into the bank's said-list (`ChatRouter.Said`): prefetched lines, the brain's
  and scripts'. A line close to one said within 30 minutes is never picked. A player's own greeting beats a generic one.
- **Timing.** Think time plus a per-character delay, jittered and capped low. The gaze comes first, on the next tick.
- **Gaze and gestures.** Brainstem gaze happens on addressed chat, and the gesture rule table plays body language on the
  bot's own lines (nod, shake, wave, bow, bob). Scripts get them as `character.look/face/gesture`.

## 6. The `character` SDK module (Sdk/js/80-character.js)

The module is read-mostly and budgeted like the other host actions. Its doc comments flow into the generated SDK reference,
and from there into the sdk-js memory pack.

- **Body:** `character.look(target)`, `face(player, s)`, `gesture(name, target)` (a counted action),
  `follow(player, {dist, s})` with `stopFollow()` (5 s re-planned slices of the `follow` tool).
- **Voice:** `line(kind, player)` returns a fresh prefetched line, never a recent repeat. `typing(text|ms)` and
  `speak(kind, fallback, player)` add human timing; speak then goes through the usual `say`.
- **Reads:** `mood()`, `relation(player)` (read-only self-profile), `recall(query, k)` (20 per run), `heard(player,
  sinceSec)`, `intent(text)`.
- **Writes:** `remember(text)`, a groomed note, 3 per run.
- `react(trigger, filter, fn)`: a game event, or a chat intent (= `player.say` with that intent).

Without a port, as in tests and the author's dry run, every call is a harmless no-op.

## 7. Reconciling the body with the brain's later answer

The brain never contradicts what the body did without saying so. Its turn header shows:

- `[already said] …`: what the body said, with the source and age
- `[already doing] …`: speculative or reflex, with the elapsed time
- `[reflex] …`
- `[busy] …`

Its choices:

- **Keep.** Stay silent. A matching move adopts the speculative walk.
- **Amend.** Add what the instant line didn't cover: an answer, a detail.
- **Undo.** "actually, can't, I'm holding lava" plus `stop`. The streamed line goes out at once, and the stop cancels the
  reflex walk.

For learned reactions, the same verdicts feed demotion (section 3).

## 8. What's next

- A reconcile helper for the brain: one tool flag on the next turn that saves "next time do this" from what the turn just
  did, instead of the brain writing `op=bind` itself.
- Forwarding 1.12.2's Block Break Animation (0x08) through the `Release1122` adapter into `GameSignals.OnBreakAnimation`
  (P2 of multi-version; the swing path already works there).
- Mood-aware timing (sleepy = slower typing), and gestures chosen from the line's tone by the prefetch call.
- Feeding the latency per kind back into the TUI line (the data is already in `LatencySummary.ByKind`).
