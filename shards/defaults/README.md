# Default knowledge packs

Clean, reproducible memory shards for setting up a bot's game knowledge without exporting a lived-in (mutated) db.
They are built from pristine sources only, never from a live db, and the same inputs always give byte-identical files.

| File | Kind | What it holds |
|---|---|---|
| `b1.8.1-gamedata.mcshard` | version pack | b1.8.1 game data (recipes, items, blocks, smelting, fuels, dig times; `MineClaw.Data` rendered by `SeedCorpus`) + `docs/knowledge/mechanics_b181.md` (hunger, fluids, light/spawning, farming, crafting quirks) |
| `skyblock.mcshard` | mode pack | `docs/knowledge/skyblock_b181.md`: the void, the starter island, unrenewables, challenges |
| `1.12.2-gamedata.mcshard` | version pack | 1.12.2 game data from the vendored minecraft-data (`third_party/minecraft-data/pc/1.12.2`: recipes with variant names, items, blocks with hardness/tool/drops, foods) + `shards/sources/1.12.2` facts (smelting, fuels, mobs with health/xp/loot, from the decompiled client) + `docs/knowledge/mechanics_1122.md` (hunger, combat cooldown, spawning, farming, fluids, leaves, tools, beds) |
| `sdk-js.mcshard` | mode pack, every version | the JavaScript scripting SDK, generated from the code: each module and helper (`def`) with its doc comment, every game function a script calls (per game version), the worked examples |
| `index.json` | | every pack's version, kind, description, counts, size, vectors, builder version and source hash |

A db holds **one version pack and any number of mode packs** (meta `knowledge_pack`). In a plain world, use the version pack
and no mode. In a skyblock, add the `skyblock` mode. `sdk-js` is a mode pack tagged version `any`: it applies in every world and
carries no cached-prompt lines (retrieval only: "how do I wait for day in a script" finds `waitFor`). The cached prompt's core lines come from the active packs only: a doc
whose `<!-- pack ... -->` marker isn't active adds no core lines and no chunks, even when the file is on disk.

## Using them

In the Memory Explorer, open the **Shards** page and go to **Default packs**. Choose **Preview swap** (version pack),
**Preview add mode**, or **Preview remove mode** (mode pack). Each opens the usual diff-first import against the edit target:
a local db (backed up first) or the live bot (admin writes). The page header shows the db's active version pack and modes.
Set the folder with `Explorer:DefaultPacksDir` (or env `MINECLAW_Explorer__DefaultPacksDir`). The default is `shards/defaults`
next to the exe, else this folder in the repo.

The importer's modes are `swap-pack` (version packs), `add-mode` and `remove-mode` (mode packs). Merge and replace-section
refuse packs, so versions never mix.

Vectors come from the seed (`data/seed/mineclaw-seed.db`, text-embedding-3-large @ 1024) by material hash, so importing
costs no embedding calls for those docs. Docs the seed has no vector for are counted in `needs_embedding` and embedded on
import. A pack over 20 MB is stored without vectors (`--max-mb`). Both current packs are well under that.

## Rebuilding

```
dotnet run --project tools/ShardPacks -- build                    # b1.8.1: MineClaw.Data + docs/knowledge, vectors from the seed (+ sdk-js)
dotnet run --project tools/ShardPacks -- build --version 1.12.2   # 1.12.2: vendored minecraft-data + shards/sources/1.12.2 + docs (+ sdk-js)
dotnet run --project tools/ShardPacks -- verify                   # rebuild every version in index.json + sdk-js into a temp folder and
                                                                  # compare byte for byte (exit 4 if anything is stale)
dotnet run --project tools/ShardPacks -- list
```

Rebuild after editing `docs/knowledge/*.md`, `MineClaw.Data`, `shards/sources/*`, the SDK modules
(`src/MineClaw.Scripting/Sdk/js/*.js`), `ScriptPrompts.Examples` or a scriptable tool, then commit the changed files.
`DefaultPackTests`, `Pack1122Tests` and `ScriptSdkPackTests` fail while a committed pack is stale. `.gitattributes` keeps the bytes
exact on every checkout.

The builder never calls an embedding API (there is no config key for it). Docs without a seed vector are marked `needs_embedding`
and the bot embeds them in the background on its next start: the 1.12.2 pack is ~1,100 docs of ~60-100 tokens, about 0.1 M
tokens, so roughly $0.01 at text-embedding-3-large's $0.13 per M tokens, once.

## A fresh world

`MineClaw.App --reset-memory --pack shards/defaults/1.12.2-gamedata.mcshard --modes none --yes` (service stopped) backs up the whole
data dir to `<data>-backup-<utc>`, makes a fresh db with only that pack + `sdk-js.mcshard` from the same folder (no mode), and writes
`server-version.txt`. On alice: `pwsh deploy/deploy-alice.ps1 -ResetMemory -Pack shards/defaults/1.12.2-gamedata.mcshard [-Start]`.

## A new Minecraft version

1. Get a PrismarineJS [minecraft-data](https://github.com/PrismarineJS/minecraft-data) checkout yourself (1.12.2 is vendored in
   `third_party/minecraft-data/pc/1.12.2`). The tool never downloads anything. It reads `data/dataPaths.json` →
   `data/pc/<dir>/{items,blocks,recipes,foods,entities}.json`, or a flat version folder.
2. Add that version's docs to `docs/knowledge`, marked `<!-- pack version=1.8.9 -->` (general mechanics) or
   `<!-- pack mode=skyblock version=1.8.9 name=skyblock-1.8.9 -->` (a mode).
3. Build:
   ```
   dotnet run --project tools/ShardPacks -- build --version 1.8.9 --source minecraft-data --data <checkout> [--data-key 1.8.9]
   ```
   This writes `1.8.9-gamedata.mcshard` (+ that version's mode packs) and adds the entries to `index.json`.
   minecraft-data has no furnace, fuel or mob loot tables: put them as facts in `shards/sources/<version>/smelting.json` and
   `mobs.json` (minecraft-data registry names; the 1.12.2 files are the format reference), picked up automatically (`--facts`).
   There is no seed for that version, so the pack carries (almost) no vectors and the target embeds it on import.

`tests/fixtures/minecraft-data` is a minimal hand-written folder in that layout. `DefaultPackTests` builds a 1.8.9 pack from
it and swaps it into a fresh db.

## Where b1.8.1's game data came from

`src/MineClaw.Data` (Blocks, Items, Recipes, Smelting, Tools, Drops) was transcribed by hand into C# tables from the b1.8.1
client jar: the Block static initializer and Material flags, the Item registry, CraftingManager and its recipe classes,
FurnaceRecipes, TileEntityFurnace.getItemBurnTime, and ItemTool/ItemPickaxe/ItemSpade/ItemAxe/ItemSword/ItemShears
(commit 24a4a5b). It was then audited against Bukkit mc-dev 218a2a71 (commit 669cf71), with later fixes such as slab kinds.
`SeedCorpus` renders those tables into ~100-token `gd:*` docs. `tools/SeedBuilder` put them in the seed together with the
docs/knowledge chunks, Azure embeddings and a Haiku-summarized RAPTOR tree. No extractor exists for b1.8.1: the tables are
the source.
