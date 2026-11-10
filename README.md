# MineClaw

MineClaw is a Minecraft bot platform written from scratch in C# (.NET 10). It plays on real servers alongside people,
talks to them in chat, and is directed by large language models.

- **Game client.** A headless client for Minecraft Java **Beta 1.8.1** (protocol 17) and **1.12.2** (protocol 340): protocol,
  world and inventory state, physics, pathfinding, digging, building, crafting, combat and survival. One process speaks
  one version, detected from the server.
- **Hive minds.** An LLM brain (Anthropic models by default, Ollama optional) plans and converses while C# does the playing,
  so the model is never on the per-tick path. A brain can drive several bodies, spawn clone hives and subhives, and write
  JavaScript jobs that bodies run without a model. Memory lives in SQLite with local retrieval (hybrid search and a RAPTOR
  tree) and versioned knowledge packs.
- **Story Mode.** A colony manager in the browser: a colony of script-driven pawns with work priorities, schedules, zones
  and direct orders, run by a colony director instead of an LLM.
- **Web Controller.** A Next.js + NestJS web app (`web/`) with a three.js world viewer, live bot views, chat as a signed-in
  player, memory and script explorers, and administration. A C# bridge (`tools/ViewerBridge`) serves the bot's data to it.
- **Design Studio.** A creative-mode canvas in the web app for designs of three kinds: **build** (structures),
  **mine** (shafts and strip mines over generated terrain) and **redstone** (circuits with a tick-accurate simulator and
  debugger). The AI can co-design, and pawns can build or dig the saved build and mine designs.

More detail: [ARCHITECTURE.md](ARCHITECTURE.md) (the C# side), [docs/web/architecture.md](docs/web/architecture.md) (the web
app), [docs/web/story-mode.md](docs/web/story-mode.md), [docs/design/](docs/design/) and
[docs/design/multi-version.md](docs/design/multi-version.md).

## Repository layout

| Path | Contents |
|---|---|
| `src/` | The C# libraries and the `MineClaw.App` host (Protocol, Data, World, Client, Skills, Bot, Agent, Hive, Scripting, Render, Raptor) |
| `tests/` | One xUnit project per area, plus fixtures |
| `tools/` | The offline simulator (`MineClaw.Sim`, `SimServer`), the web bridge (`ViewerBridge`), replay, knowledge-pack and diagnostic tools |
| `web/` | npm workspaces: `apps/web` (Next.js), `apps/api` (NestJS), `packages/contracts`, `packages/world-viewer`, `packages/redstone` |
| `shards/defaults/`, `data/seed/` | Default knowledge packs and the seed database |
| `third_party/` | Vendored PrismarineJS minecraft-data (MIT) |
| `deploy/`, `docs/` | Deployment scripts and notes, design documents |

## Build and run

Requirements: the .NET 10 SDK, and Node.js 22 with npm for the web app.

```sh
dotnet build MineClaw.sln
dotnet test MineClaw.sln -m:1
```

**Try it offline** (no Minecraft server, no API key): see [docs/offline-sandbox.md](docs/offline-sandbox.md).

```sh
dotnet run --project tools/SimServer -- --port 25565
dotnet run --project src/MineClaw.App -- --config docs/offline-config.json --no-tui
```

**Run against a server.** Copy `src/MineClaw.App/appsettings.example.json` to `src/MineClaw.App/appsettings.json` (ignored
by Git), set the server address, the bot's account name, the trusted players and your API keys, then:

```sh
dotnet run --project src/MineClaw.App
```

Settings can also come from `--config <file>` or environment variables such as `MINECLAW_Agent__Llm__ApiKey`.
`dotnet run --project src/MineClaw.App -- --help` lists the options.

**Web app** (from `web/`):

```sh
npm ci
npm run dev                          # Next.js on :3000, NestJS API on :4000 (dev mode)
npm run dev:mock -w @mineclaw/web    # the UI alone, with a mocked API
npm run check                        # build, typecheck, lint and test every workspace
```

The API talks to the bot through `tools/ViewerBridge`; its dev setup is in
`tools/ViewerBridge/appsettings.Development.example.json`. Deployment is described in [docs/web/deploy.md](docs/web/deploy.md)
and [deploy/README.md](deploy/README.md).

## Minecraft game assets

**No Minecraft textures, models, blockstates, fonts, sounds or language files are included in this repository.** The web
viewer imports them at runtime, inside the browser, from the user's own legally obtained game files (a client jar and, for
sounds, the launcher's `assets` folder); see [docs/web/local-game-assets.md](docs/web/local-game-assets.md). The C# renderer and
the 1.12.2 chat translations likewise read the user's installed client jar. Without these files the viewer draws plain
flat-colour geometry. The one exception is a fallback table of about 60 short en_us chat, join/leave, kick, command-reply
and death-message formats (in `src/MineClaw.Protocol/Release1122/ChatComponent.cs`), used to parse server chat when no
client jar is present.

## Interoperability

MineClaw contains no decompiled or Mojang source code. To interoperate with Minecraft servers, game rules such as movement
physics, lighting, combat, hunger and protocol handling are re-implemented in C# and TypeScript, using community
documentation (wiki.vg), Bukkit's mc-dev sources for Beta 1.8 and a locally decompiled 1.12.2 client (MCP names) as
references; the decompiled client is kept outside the repository and never committed. Game data tables for 1.12.2 come
from PrismarineJS minecraft-data (MIT).

## Security notes for operators

- Bots can be given operator rights, and trusted players can ask a bot to run server commands for them.
- Bots log in without Mojang authentication, so they need a server in offline mode (or behind a proxy that allows it). On
  an offline-mode server anyone can join under any player name. Names in the bot's config, such as
  `Agent:Self:TrustedPlayers`, are therefore only a safe basis for trust on servers that authenticate players or that are
  not reachable from the internet.
- The web app is built to be exposed (sign-in, sessions, CSRF protection, roles and an audit log), but the bot's Supervisor
  and the bridge are meant for loopback or a trusted network only. See the security model in
  [docs/web/architecture.md](docs/web/architecture.md).
- Keep API keys in `appsettings.json` or environment variables, never in committed files.

## Trademarks

MineClaw is not an official Minecraft product. It is not approved by or associated with Mojang or Microsoft. Minecraft is
a trademark of Mojang Synergies AB / Microsoft. Other names are trademarks of their respective owners and are used only to
describe compatibility or design comparisons.

## License

MineClaw is released under the [MIT License](LICENSE), Copyright (c) 2026 Realynx. The license covers MineClaw's own code
and documentation, not Minecraft's assets (which are not included). Third-party components and their licenses are listed
in [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md).
