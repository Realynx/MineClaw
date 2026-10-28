# Default knowledge packs

Clean, reproducible memory shards for setting up a bot's game knowledge without exporting a lived-in (mutated) db.
They are built from pristine sources only, never from a live db, and the same inputs always give byte-identical files.

| File | Kind | What it holds |
|---|---|---|
| `b1.8.1-gamedata.mcshard` | version pack | b1.8.1 game data (recipes, items, blocks, smelting, fuels, dig times; `MineClaw.Data` rendered by `SeedCorpus`) + `docs/knowledge/mechanics_b181.md` (hunger, fluids, light/spawning, farming, crafting quirks) |
| `skyblock.mcshard` | mode pack | `docs/knowledge/skyblock_b181.md`: the void, the starter island, unrenewables, challenges |
| `index.json` | | every pack's version, kind, description, counts, size, vectors, builder version and source hash |

A db holds **one version pack and any number of mode packs** (meta `knowledge_pack`). In a plain world, use the version pack
and no mode. In a skyblock, add the `skyblock` mode. The cached prompt's core lines come from the active packs only: a doc
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
dotnet run --project tools/ShardPacks -- build     # b1.8.1: MineClaw.Data + docs/knowledge, vectors from the seed
dotnet run --project tools/ShardPacks -- verify    # rebuild into a temp folder and compare byte for byte
dotnet run --project tools/ShardPacks -- list
```

Rebuild after editing `docs/knowledge/*.md` or `MineClaw.Data`, then commit the changed files. `.gitattributes` keeps the
bytes exact on every checkout.

## A new Minecraft version

1. Get a PrismarineJS [minecraft-data](https://github.com/PrismarineJS/minecraft-data) checkout yourself. The tool never
   downloads anything. It reads `data/dataPaths.json` → `data/pc/<dir>/{items,blocks,recipes,foods}.json`.
2. Add that version's docs to `docs/knowledge`, marked `<!-- pack version=1.8.9 -->` (general mechanics) or
   `<!-- pack mode=skyblock version=1.8.9 name=skyblock-1.8.9 -->` (a mode).
3. Build:
   ```
   dotnet run --project tools/ShardPacks -- build --version 1.8.9 --source minecraft-data --data <checkout> [--data-key 1.8.9]
   ```
   This writes `1.8.9-gamedata.mcshard` (+ that version's mode packs) and adds the entries to `index.json`.
   minecraft-data has no furnace or fuel tables, so put smelting facts in the version's knowledge doc. There is no seed for
   that version, so the pack carries no vectors and the target embeds it on import.

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
