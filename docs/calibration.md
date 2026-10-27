# Live calibration session

About 20 minutes on the real server with Fox (`poofimafox`), Robin (`kiwi`) and maid-chan (`maid_chan`). The session is
recorded and then replayed offline. Afterwards `tools/CalibrationReport` scores the bot's detections against what we
noted by hand.

## Before

1. Start the bot with packet recording on. Either:
   - pass `--record` (once the App wires `ClientOptions.RecordDirectory`), or
   - set the env var `MINECLAW_RECORD=1`, which writes to `logs/recordings`, or set it to a directory.

   The log shows `recording packets to logs/recordings/<yyyyMMdd-HHmmss>.mcrec`.
2. The **note-taker** (Fox) starts a stopwatch when that line appears and opens `truth.txt`. Write one line per action:
   `mm:ss.f  player  kind  [block]  [xN]`. Comments start with `#`. Precision to the nearest second is fine.
3. Everyone stands on the main island. The bot should be idle (`status`: no `doing`).

## Script

| # | Who | Does | Bot should detect / do | Truth line |
|---|-----|------|------------------------|------------|
| 1 | Fox | Mines 5 cobblestone at the generator, one at a time | `poofimafox broke cobblestone` ×5 at the cobble gen. The gen refills are *not* attributed | `01:00 poofimafox broke cobblestone x5` |
| 2 | Robin | Places 3 blocks on the island edge, standing inside | `kiwi placed …` ×3. **No alarm or wake.** The edge is not a fall | `02:00 kiwi placed cobblestone x3` |
| 3 | Fox | Opens the starter chest and takes 1 item | `opened chest`, then `took <item>` from the chest snapshot (only if the bot sees the chest afterwards) | `03:00 poofimafox opened chest`<br>`03:05 poofimafox took <item>` |
| 4 | Robin | Smelts something (lights the furnace with fuel + input) | `kiwi lit_furnace` | `04:00 kiwi lit_furnace` |
| 5 | Fox | Places a sign with text, e.g. "fox was here" | `poofimafox wrote_sign "fox was here"`. The sign also becomes an index label | `05:00 poofimafox wrote_sign` |
| 6 | Anyone | `/me waves` | Logged as `chat` (emote) and answered if addressed | `06:00 kiwi chat` |
| 7 | Robin | One death: steps off the edge (void) or dies another way | `kiwi died` with the cause and a **high** wake ("kiwi fell…") | `07:00 kiwi died` |
| 8 | Fox | "maid-chan, spar with me", then says **"stop"** after a few hits | Spar starts; `stop` ends it at once with a result line | `08:00 poofimafox chat`<br>`08:20 poofimafox chat` |
| 9 | **Robin** | **"maid-chan, can you mine that tree?"** (the headline test) | Opus calls `chop_tree`, directly or via `delegate`. The bot walks to a safe spot (never the void side), fells every log of that tree only, never digs the dirt, breaks reachable leaves for saplings, collects drops (none near the void), **replants**, and answers with the result line, e.g. `ok: chopped oak tree (5 logs, 2 saplings), replanted at 3,64,-1`. Its own digs must **not** show up as Robin's | `09:00 maid_chan self_dug log x5` |
| 10 | Fox | "what did robin do?" | A short answer from the activity digest: placed blocks at the edge, smelted, died, asked for the tree. No invented actions | `10:00 poofimafox chat` |

Pass criteria for step 9:
- **Result:** the result line starts with `ok:` and says *replanted*.
- **World state:** the sapling is on the original dirt. Other trees, the dirt and every build are untouched.
- **Safety:** no clutch or void event.
- **Honest failures:** if no sapling dropped, the line says `no sapling dropped, NOT replanted` and the bot says so in chat.

## After

1. Stop the bot (`q`). Copy the `.mcrec` file and `truth.txt` somewhere safe.
2. Score the session:
   ```
   dotnet run --project tools/CalibrationReport -- logs/recordings/<file>.mcrec truth.txt --timeline
   ```
   - The report gives precision and recall per kind, with Δt = detected − noted.
   - Use `--offset <s>` if the stopwatch started late, and `--tolerance <s>` to widen the matching window (default 10).
   - Wakes are listed at the end. Step 2 must have none, and step 7 must have one.
3. Get the full timeline or a JSON diff:
   ```
   dotnet run --project tools/Replay -- <file>.mcrec --json run.json
   ```
4. Commit a trimmed recording and its truth file to `tests/fixtures/recordings/<name>.mcrec` plus `<name>.truth.txt`.
   `ReplayTests` replays every fixture and prints its score, so it then guards against regressions.

## Truth kinds

| Kind | Meaning |
|------|---------|
| `broke`, `placed`, `harvested`, `planted` | Block changes |
| `opened`, `took`, `stored` | Containers |
| `lit_furnace` | Furnace |
| `wrote_sign` | Sign text |
| `picked_up`, `dropped`, `equipped` | Items |
| `chat` | Chat, including `/me` |
| `died`, `hurt`, `fought`, `slept`, `rode` | Player state |
| `joined`, `left` | Presence |
| `self_dug`, `self_placed`, `self_said`, `self_died` | The bot's own actions, from its outbound packets |

- An optional block or item name narrows the match, e.g. `cobblestone` or `log`.
- `xN` counts repeats: the tracker merges identical actions within 3 s into one event with a count.
