# MineClaw — architecture

MineClaw is a headless Minecraft **Beta 1.8.1** client (protocol version **17**) written in C# (.NET 10),
driven by a Claude agent over the Anthropic API. Target: a Skyblock world on a b1.8.1 server, playing
alongside humans and talking to them through in-game chat.

## Layering (lower layers never reference higher ones)

```
MineClaw.App       console host: config, logging, wires everything, runs the bot
   │
MineClaw.Agent     Claude tool-use loop, game tools, chat routing, persistent memory, Skyblock knowledge
   │
MineClaw.Client    TCP connection + packet dispatch, 20 TPS tick, physics, player actions
   │        (dig, place, use, window clicks, crafting, smelting, chest ops, pathfind/goto, chat)
   ├── MineClaw.Protocol   packets + big-endian codec for protocol 17 (no deps)
   └── MineClaw.World      world/entity/inventory state, collision, A* pathfinding
            └── MineClaw.Data  static b1.8.1 data: blocks, items, recipes, smelting, tool rules (no deps)
```

`tests/MineClaw.Tests` (xUnit) covers all layers.

## Project ownership / contracts

| Project | Owns | Public contract (others code against this) |
|---|---|---|
| Protocol | `IPacket`, every packet class (both directions), `MinecraftReader/Writer` (big-endian, UTF-16 "string16"), `PacketRegistry`, `ItemStack` wire form, entity metadata, chunk payload (still compressed) | `PacketRegistry.Read(MinecraftReader, PacketDirection)` / `packet.Write(writer)` |
| Data | `BlockInfo`, `ItemInfo`, `Blocks`, `Items`, `Recipes`, `Smelting`, `Tools` (dig-time math) | `Blocks.Get(id)`, `Blocks.ByName(name)`, `Items.Get(id)`, `Items.ByName(name)`, `Recipes.*`, `Smelting.*`, `Tools.*` |
| World | chunk columns (16x128x16: ids, metadata, block light, sky light), `WorldState`, entity tracker, inventory/window model, AABB collision, A* pathfinder | `WorldState.GetBlock(x,y,z)`, `SetBlock`, `LoadChunk(...)`, `Pathfinder.FindPath(...)` |
| Client | `MinecraftClient`, packet → world/event wiring, physics tick, action primitives | async action API used by Agent tools |
| Agent | Anthropic client, tool loop, tools, `MemoryStore` (JSON on disk), prompts | `BotAgent.RunAsync(ct)` |
| App | `Program.cs`, `appsettings.json` | — |

## Conventions

- File-scoped namespaces matching the project (`MineClaw.World`, ...). One public type per file unless tiny.
- Coordinates: block coords are `int`, entity positions are `double`. Beta's wire position packets send
  **X, Y(feet), Stance(eyes = Y+1.62), Z** client→server; server→client 0x0D sends X, **Stance, Y**, Z (order differs!).
- Async APIs take a `CancellationToken`. No blocking `.Result`.
- Build only your own project while other workstreams are active (`dotnet build src/MineClaw.X`), to avoid
  file locks; tests live in `tests/MineClaw.Tests/<Area>/`.
