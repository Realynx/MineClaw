using System.Globalization;
using System.Text;
using System.Text.Json;
using MineClaw.Agent.Rag;
using MineClaw.Agent.Rag.Raptor;

namespace MineClaw.Agent.Admin.Shards.Packs;

/// <summary>
/// Where a Minecraft version's game data comes from. A source turns its tables into short, human-readable RAPTOR leaves (kind
/// <c>gamedata</c>, refs <c>gd:{sub-kind}:...</c>, the fixed seed timestamp, deterministic order), which a version pack carries.
/// </summary>
public interface IGameDataSource
{
    /// <summary>The version tag ("b1.8.1", "1.8.9").</summary>
    string Version { get; }
    /// <summary>One line for the manifest and index.json ("MineClaw.Data b1.8.1 tables", "minecraft-data pc/1.8").</summary>
    string Description { get; }
    /// <summary>The game-data docs, deterministic (same input, same docs in the same order).</summary>
    IReadOnlyList<RagDoc> Docs();
}

/// <summary>
/// b1.8.1: the static tables in <c>MineClaw.Data</c> (Blocks/Items/Recipes/Smelting/Tools/Drops, transcribed from the b1.8.1 client
/// jar's Block/Item static initializers, CraftingManager/FurnaceRecipes, ItemTool tables, cross-checked against Bukkit mc-dev
/// 218a2a71) rendered by <see cref="SeedCorpus"/>: exactly the game data the seed db holds.
/// </summary>
public sealed class MineClawDataSource : IGameDataSource
{
    public string Version => KnowledgePack.DefaultVersion;
    public string Description => "MineClaw.Data b1.8.1 tables (client jar / mc-dev), rendered by SeedCorpus";
    public IReadOnlyList<RagDoc> Docs() => SeedCorpus.Build();
}
