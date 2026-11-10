# Third-party notices

MineClaw is licensed under the MIT License (see `LICENSE`). It includes, or contains ports of, the third-party work listed
below. Each item keeps its own license; the full texts follow each entry.

Minecraft game assets (textures, models, blockstates, fonts, sounds and language files) are **not** included. The software
reads them at runtime from the user's own, legally obtained copy of the game (`docs/web/local-game-assets.md`). The only
game text in the tree is a fallback table of about 60 short en_us chat, join/leave, kick, command-reply and death-message
formats (`LangTable.Builtin` in `src/MineClaw.Protocol/Release1122/ChatComponent.cs`), used to parse server chat when no
client jar is present. Minecraft is a
trademark of Mojang Synergies AB / Microsoft; MineClaw is not affiliated with or endorsed by them.

## Included or ported components

### PrismarineJS minecraft-data (MIT)

- Upstream: https://github.com/PrismarineJS/minecraft-data, commit `9c97fe50a82feb2c0f2be1cff35ce19c891bfb35`.
- Where:
  - `third_party/minecraft-data/pc/1.12.2/*` (vendored game-data and protocol tables; the license is also in
    `third_party/minecraft-data/LICENSE`) and the small subset in `tests/fixtures/minecraft-data/`.
  - Copies embedded as resources in the `MineClaw.Data`, `MineClaw.Protocol` and `MineClaw.Render` assemblies.
  - Data generated from it: `src/MineClaw.World/Release1122/Shapes1122.Data.cs`,
    `web/packages/world-viewer/src/world/playerCollisionData.ts`, the `shards/defaults/1.12.2-gamedata.mcshard` knowledge pack
    and the game-data documents in `data/seed/mineclaw-seed.db`.

```
MIT License

Copyright (c) 2015 PrismarineJS

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
```

### RAPTOR reference implementation (MIT)

- Upstream: https://github.com/parthsarthi03/raptor (Sarthi et al., *RAPTOR: Recursive Abstractive Processing for
  Tree-Organized Retrieval*, ICLR 2024, arXiv 2401.18059).
- Where: `src/MineClaw.Raptor/**` is a C# port that follows the reference code (`raptor/*.py`: tree building, clustering,
  retrieval and the default summarization prompt).

```
MIT License

Copyright (c) 2024 Parth Sarthi

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
```

### umap-learn (BSD-3-Clause)

- Upstream: https://github.com/lmcinnes/umap
- Where: `src/MineClaw.Raptor/Umap/**` (fuzzy simplicial set, `a`/`b` curve fit, SGD layout, defaults) and the parts of
  `src/MineClaw.Raptor/Numerics/` that mirror it (spectral initialisation, the `tau_rand` seed state). These are C# ports of
  the algorithm; no Python source is included.

```
BSD 3-Clause License

Copyright (c) 2017, Leland McInnes
All rights reserved.

Redistribution and use in source and binary forms, with or without
modification, are permitted provided that the following conditions are met:

1. Redistributions of source code must retain the above copyright notice, this
   list of conditions and the following disclaimer.

2. Redistributions in binary form must reproduce the above copyright notice,
   this list of conditions and the following disclaimer in the documentation
   and/or other materials provided with the distribution.

3. Neither the name of the copyright holder nor the names of its
   contributors may be used to endorse or promote products derived from
   this software without specific prior written permission.

THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS "AS IS"
AND ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE
IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE
DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT HOLDER OR CONTRIBUTORS BE LIABLE
FOR ANY DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL
DAMAGES (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR
SERVICES; LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER
CAUSED AND ON ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY,
OR TORT (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE
OF THIS SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.
```

### scikit-learn (BSD-3-Clause)

- Upstream: https://github.com/scikit-learn/scikit-learn
- Where: `src/MineClaw.Raptor/Clustering/GaussianMixture.cs` and `KMeans.cs` (EM with full covariance, BIC, k-means++
  initialisation and their defaults) and the deterministic eigenvector sign flip in
  `src/MineClaw.Raptor/Numerics/SpectralSolver.cs`. These are C# ports of the algorithms; no Python source is included.

```
BSD 3-Clause License

Copyright (c) 2007-2024 The scikit-learn developers.
All rights reserved.

Redistribution and use in source and binary forms, with or without
modification, are permitted provided that the following conditions are met:

* Redistributions of source code must retain the above copyright notice, this
  list of conditions and the following disclaimer.

* Redistributions in binary form must reproduce the above copyright notice,
  this list of conditions and the following disclaimer in the documentation
  and/or other materials provided with the distribution.

* Neither the name of the copyright holder nor the names of its
  contributors may be used to endorse or promote products derived from
  this software without specific prior written permission.

THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS "AS IS"
AND ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE
IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE
DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT HOLDER OR CONTRIBUTORS BE LIABLE
FOR ANY DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL
DAMAGES (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR
SERVICES; LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER
CAUSED AND ON ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY,
OR TORT (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE
OF THIS SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.
```

### Mock Service Worker (MIT)

- Upstream: https://github.com/mswjs/msw
- Where: `web/apps/web/public/mockServiceWorker.js`, the worker script generated by `msw init` (used only by the web app's
  mock mode and tests).

```
MIT License

Copyright (c) 2018–present Artem Zakharchenko

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
```

## Public-domain material (no notice required)

- The mulberry32, xoshiro256\*\* and SplitMix64 pseudo-random generators (`src/MineClaw.Bot/Blueprints/Mine/MineTerrain.cs`,
  `web/apps/web/src/features/studio/mine/mineTerrain.ts`, `web/packages/world-viewer/src/atlas/betaFx.ts`,
  `src/MineClaw.Raptor/Numerics/Rng.cs`).
- Sections of the root `.gitignore` taken from the github/gitignore templates (CC0-1.0).

## References (consulted, no source included)

These sources informed MineClaw's behaviour or design. No code, prose or assets from them are included, apart from the
chat-format fallback table described above.

- Game behaviour (physics, lighting, combat, hunger, crafting and the network protocol) is re-implemented in C# and
  TypeScript for interoperability, using as references the wiki.vg protocol pages, the Bukkit mc-dev sources for Beta 1.8
  and a 1.12.2 client decompiled locally from a legally obtained client jar with MCP (stable_39) mappings
  (`docs/handoff/decompile-1.12.2.md`). Code comments cite the vanilla class and method names (and some numeric
  constants) they follow. The decompiled client is kept outside the repository; no decompiled or Mojang source code is
  included.
- The Minecraft Wiki (numeric ore-height tables in `web/apps/web/src/features/studio/mine/ore-heights.json`), Mojang release
  notes, the Minecraft Education workbook and github.com/samomezz/minecraftStripMiningSimulation (mine-layout design
  references in `docs/design/mine-layout-palette.md`).
- RimWorld (Ludeon Studios) is named in Story Mode documentation and code comments as a design comparison only. RimWorld is
  a trademark of Ludeon Studios; no names, text or assets from it are used.

## Dependencies

npm and NuGet dependencies are not vendored. They are downloaded at install or restore time (`web/package-lock.json`, the
`*.csproj` files) under their own licenses. Some optional or development dependencies use non-MIT licenses (for example
LGPL-3.0-or-later for sharp's prebuilt libvips binaries, MPL-2.0 for lightningcss and CC-BY-4.0 for caniuse-lite). Anyone
who redistributes a bundle that contains those packages, such as a Docker image or a packaged release, must include their
notices.
