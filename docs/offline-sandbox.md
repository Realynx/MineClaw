# Offline sandbox

Exercise the real Minecraft client, tool loop and physics without a Minecraft server or an LLM API key.
Only loopback TCP is used. Requires .NET 10 and the repository's normal dependencies already restored.
No textures, game assets or extra packages are needed.

From the repository root, in two terminals:

```sh
dotnet run --project tools/SimServer -- --port 25565
dotnet run --project src/MineClaw.App -- --config docs/offline-config.json --no-tui
```

In the **SimServer** terminal, after the bot logs in:

```text
say come here
say hello
```

The simulated player `tester` stands at `(11.5, 64, 8.5)`; the bot starts at `(8.5, 64, 8.5)`.
The bot should say `On my way.` and walk east toward tester. Bot chat is printed immediately by SimServer.
`quit` or Ctrl+C stops SimServer; `/quit` in the App terminal stops the bot.
`--port 0` selects an ephemeral port and prints it; change `Minecraft:Port` in a copy of the example config to match.
The listener always binds **127.0.0.1**, never a public interface. It accepts one bot at a time; reconnecting resets the world.

## Scripted player

SimServer `--script <file>` reads commands after the first bot login. For example:

```text
# milliseconds; let the App finish starting its agent
wait 2000
say come here
wait 3000
say hello
```

```sh
dotnet run --project tools/SimServer -- --script tools/SimServer/example-chat.txt
```

Console input accepts the same `say`, `wait` and `quit` commands. EOF does not stop the server.
A script ending in `quit` shuts it down explicitly. Script commands target the current connection, not a queued future bot.

## Mock brain

`Agent:Llm:MainProvider`, `WorkerProvider` and `CheapProvider` each accept `mock` independently.
It is **never a default**. Startup logs a prominent `MOCK LLM provider enabled` warning.
Mock tiers neither require an API key nor run a network key check; mixed configurations still check any real providers.
For a completely offline run, use **all three** mock tiers as in `docs/offline-config.json`.
The example also disables embedding/RAG calls, Ollama endpoints, hive clones and vision downloads.
Do not enable downloads or switch a tier to a real provider for an offline run.

`Agent:Llm:MockScriptPath` names a JSON file (relative to the repository root in the App).
It is read once per mock client; restart after editing. Missing or malformed files fail configuration validation.
Omitting the path uses a built-in offline fallback and no rules. Example: `docs/offline-brain.json`:

```json
{
  "fallback": "Offline and ready.",
  "rules": [
    {
      "match": "come here",
      "reply": "On my way.",
      "tool_use": [
        { "name": "do", "input": { "action": "goto", "args": { "to": "tester", "dist": 1 } } }
      ]
    }
  ]
}
```

The **first case-insensitive substring match** on the **newest user message** wins. The agent's user message contains its
normal event/state wrapper, not just raw player chat. Assistant history and older user messages are never matched.
`reply` and `tool_use` are independently optional (a tool-only rule can omit `reply`). Tool results end the scripted turn
with the fallback rather than repeating the previous tool call. Unmatched events also get the fallback. Calls report zero
usage. This is deterministic test plumbing, not reasoning: summaries, classifiers and complex delegated jobs are not
meaningfully emulated. Scripts still go through the real tool registry, protection, body lock and safety checks.

The example disables the brainstem so `come here` is handled by the scripted model, not the built-in movement reflex.
The App's usual layered config/environment overrides still apply; use an isolated development configuration/environment,
not a production data directory. Example data and logs are isolated under `data/offline-sandbox` and `logs/offline-sandbox`.
No existing configuration needs to be edited, and the example contains no keys.

## Simulation limits

- Protocol 17 offline-mode handshake/login, time, chunks, inventory/vitals, position and one stationary named player.
- A small L-shaped, three-block-deep dirt/grass island over the void, an oak tree and a chest with lava, ice and a sapling.
- Shared scripted server behavior: basic dig/place updates, item drops/pickup, openable toggles, chest/furnace opening,
  transaction acknowledgements and eating. Existing client/skill tests reuse exactly this core with their own worlds/hooks.
- **Not a Minecraft implementation:** no authoritative movement validation, persistent worlds, complete fluid/redstone/
  plant simulation, hunger progression or multiplayer gameplay. Fixtures can opt into simplified crafting, inventory,
  drops, combat and falling-block behavior; these are test seams, not complete server implementations. Window
  acknowledgements alone do not prove inventory transfer. Physics/pathfinding use the real client, not an authoritative server.
- No rendering/assets are required. Asset-dependent vision is outside this sandbox's coverage.

The shared implementation lives in `tools/MineClaw.Sim`, a support library referencing Data, Protocol, World and Client.
Production client/agent layers do not depend on simulation tooling. `tools/SimServer` supplies the console transport/commands.

## Automated verification

```sh
DOTNET_CLI_TELEMETRY_OPTOUT=1 dotnet build MineClaw.sln
dotnet test MineClaw.sln -m:1
dotnet test tests/MineClaw.Bot.Tests --filter FullyQualifiedName~OfflineSandboxTests -m:1
```

`OfflineSandboxTests.ScriptedBrainRepliesAndWalksTowardSimulatedPlayerOverTcp` starts a loopback listener on port 0,
creates the real `MinecraftClient` and `BotHost` (in-memory agent persistence), and injects `come here` through the server.
It requires the scripted chat reply on the wire **and** movement packets showing over one block of progress toward tester,
with the bot still on the island. Reflexes, speculation and social idle are disabled, so they cannot mask a broken brain.
