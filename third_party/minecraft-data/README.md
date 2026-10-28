# PrismarineJS minecraft-data: Minecraft Java 1.12.2 (vendored)

- Source: https://github.com/PrismarineJS/minecraft-data, commit `9c97fe50a82feb2c0f2be1cff35ce19c891bfb35`
  (master on 2026-10-28), files `data/<path>/<name>.json` per `data/dataPaths.json` → `pc."1.12.2"`.
- License: MIT (as stated in the project's README "License" section; the repo has no separate LICENSE file).
- Approved by Fox on 2026-10-28 as the 1.12.2 game-data and protocol source (alongside the decompiled client, kept
  outside the repo, for mechanics). Downloaded only this version's files; nothing else from the project.
- Feeds: the 1.12.2 game-data tables (MineClaw.Data, generated), the 1.12.2 knowledge pack (`tools/ShardPacks`
  minecraft-data adapter) and the protocol 340 packet layouts (cross-checked against wiki.vg and the decompiled source).

Flattened into `pc/1.12.2/`; each file's origin path (dataPaths.json) and SHA-256:

| file | from | sha256 |
|---|---|---|
| attributes.json | data/pc/1.12/attributes.json | `cab4b127fe6df4e8b18ea86f4b44189f01feabdb155c624fd40bd5402e73e802` |
| biomes.json | data/pc/1.12/biomes.json | `4f2d115963d3157fd44638ae88d873b9133dcfa6ebb9994507ee53e9c5eae3d4` |
| blockCollisionShapes.json | data/pc/1.12/blockCollisionShapes.json | `dde7975ac91d071795bd82c557467b162ce1780167440a0f2a49c4861b872939` |
| blocks.json | data/pc/1.12/blocks.json | `9a23d082284b0243c96d1467cbe8cbca8dead11df1145f82148ae0580f82a659` |
| effects.json | data/pc/1.12/effects.json | `9c065a4bdfd1d1f3ac06b14a2cca7d4dcacf1af1e1e638de6f811b1e40ac2c94` |
| enchantments.json | data/pc/1.12/enchantments.json | `d544deab41630cf976d19fa18b8995efe3bcdd26f090682d379d0a1e746b0abc` |
| entities.json | data/pc/1.12/entities.json | `aaa21a6072a6a058aff18ccf7d288668a0abd86c3c206ab5e115370867af778c` |
| foods.json | data/pc/1.12/foods.json | `bf23e8201ea81aa8c61b3f336a0fbc9b1fa18e0a8c5ae772b5dd9f6fca3b6bf2` |
| instruments.json | data/pc/1.12/instruments.json | `9aaf3313ac5298d91d97973ffe9092d2e962b43513504e1cbb1cc122908f45bc` |
| items.json | data/pc/1.12/items.json | `848f590ba8d981b7e310d6b4aca3dd30787e73bce8ebb8ab57fa14971f559da6` |
| language.json | data/pc/1.12/language.json | `3264dec8fa88b2ef0be26ad9fb50b4a5ae83213386deb9e1aae2fdac37c7a80b` |
| mapIcons.json | data/pc/1.11/mapIcons.json | `09171dfb721e96bf4bf17bb2d8bc8d3aa6a5736e817ba2b97754178fa005af81` |
| materials.json | data/pc/1.12/materials.json | `2db7889998bf3f70f17dd1d2a34b0828cbb7a7f96b8b4c99683367a5d7c1b303` |
| particles.json | data/pc/1.12/particles.json | `df9d8e9aee699e350bada7ef6aebc3ce8395741da28a290670122a321d0076d8` |
| protocol.json | data/pc/1.12.2/protocol.json | `2dad4c918f6bd4492bfa8159b4db6ffbbf8f088b628258f03d437b0c0b760af9` |
| recipes.json | data/pc/1.12/recipes.json | `115466a7cf287c2b6326c55e7cc1f748c2afd587f3a99ee93b2eb96daa8f5950` |
| sounds.json | data/pc/1.12.2/sounds.json | `d59ebde9f482b9f4b32f247592793ac8d378ec50aa35892d9036e6f21c8a97d5` |
| tints.json | data/pc/1.16.2/tints.json | `492ff9017dae05a2b31110856663c8e1fd4cfb3f57ab97b2d9728f68bcdc1860` |
| version.json | data/pc/1.12.2/version.json | `a6e9bcb426134c7d044f6e59cb5ba6f1c188f877582ad231fb54a22e7ae4e0c3` |
| windows.json | data/pc/1.12/windows.json | `9b5eaad2c51089a2cefd42ac8f5342db9c64e7486fb1bc7ae99e65d29e3d5e14` |
| proto.yml | data/pc/1.12.2/proto.yml | `4d6303f5f6979af1b282ea8329b9c4fbabab918183529fd56cb922a414169180` |

Note: `tints.json` comes from pc/1.16.2 (dataPaths maps it there) and `mapIcons.json` from pc/1.11.
