using MineClaw.Agent.Memory;
using MineClaw.Agent.Rag.Beliefs;
using MineClaw.Agent.Self;

namespace MineClaw.Agent.Admin;

/// <summary>Fields per memory kind, for a generic edit form (names are the keys of <see cref="MemoryEdit.Fields"/>).</summary>
public static class MemoryEditSchema
{
    private static readonly IReadOnlyList<string> Dims = ["overworld", "nether"];

    private static readonly IReadOnlyDictionary<string, IReadOnlyList<EditField>> Schemas = new Dictionary<string, IReadOnlyList<EditField>>
    {
        [MemoryKinds.Fact] =
        [
            new("subject", "Subject", "text", true, Help: "who/what the fact is about: a player name, world, base, ..."),
            new("text", "Fact", "multiline", true),
        ],
        [MemoryKinds.Place] =
        [
            new("name", "Name", "text", true, Help: "the id; changing it renames the place"),
            new("x", "X", "int", true), new("y", "Y", "int", true), new("z", "Z", "int", true),
            new("dim", "Dimension", "enum", false, Dims, "overworld (0) or nether (-1); default overworld"),
            new("note", "Note", "text"),
        ],
        [MemoryKinds.Structure] =
        [
            new("name", "Name", "text", true, Help: "the id; changing it renames the structure"),
            new("kind", "Kind", "text", false, Help: "cobble_generator, chest_storage, furnace_bank, farm, tree_farm, mob_grinder, unknown or free text"),
            new("note", "Note", "multiline"),
            new("confidence", "Confidence", "number", Help: "0..1"),
            new("steps", "Steps", "multiline", Help: "how to use it, one step per line"),
            new("min", "Min corner", "text", Help: "x,y,z"),
            new("max", "Max corner", "text", Help: "x,y,z"),
            new("dim", "Dimension", "enum", false, Dims),
        ],
        [MemoryKinds.Claim] =
        [
            new("text", "Claim", "multiline", true, Help: "add only: what the player said"),
            new("claimant", "Claimant", "text", true, Help: "add only: the player who said it"),
            new("status", "Status", "enum", false, Enum.GetNames<ClaimStatus>().Select(s => s.ToLowerInvariant()).ToList(),
                "verified/refuted/disputed are recorded as admin ground-truth evidence; unverified/provisional are pinned"),
            new("confidence", "Confidence", "number", Help: "0..1, belief that the claim is true"),
            new("reason", "Reason", "text"),
        ],
        [MemoryKinds.Trait] =
        [
            new("section", "Section", "enum", true, Enum.GetNames<SelfSection>().Select(s => s.ToLowerInvariant()).ToList(), "fixed after add"),
            new("text", "Text", "multiline", true),
            new("player", "Player", "text", Help: "relationship entries need one"),
            new("reason", "Reason", "text"),
        ],
        [MemoryKinds.Lesson] =
        [
            new("title", "Title", "text", true),
            new("correction", "Correction", "multiline", true, Help: "the corrected lesson shown in place of superseded memories (<= 180 chars reads best)"),
            new("patterns", "Superseded text patterns", "multiline", true,
                Help: "one rule per line: regexes joined by && must all match one sentence; a line starting with * matches the whole text; && ... !! ... lists regexes that must not match. Update: empty = keep"),
            new("id", "Id", "text", false, Help: "add only: a slug; default from the title"),
        ],
        [MemoryKinds.Observation] =
        [
            new("text", "Text", "multiline", false, Help: "a text edit marks the observation taught (the world indexer then only annotates it)"),
            new("annotation", "Annotation", "text"),
        ],
    };

    /// <summary>The form fields of a kind (empty for an unknown kind).</summary>
    public static IReadOnlyList<EditField> For(string kind) =>
        MemoryKinds.Normalize(kind) is { } k && Schemas.TryGetValue(k, out var f) ? f : [];

    /// <summary>Ops a kind supports.</summary>
    public static IReadOnlyList<string> Ops(string kind) => MemoryKinds.Normalize(kind) switch
    {
        MemoryKinds.Observation => ["update", "delete"],
        null => [],
        _ => ["add", "update", "delete"],
    };

    internal static string DimName(int dim) => dim == -1 ? "nether" : "overworld";

    internal static int? ParseDim(string? s) => s?.Trim().ToLowerInvariant() switch
    {
        null or "" => null,
        "overworld" or "0" or "world" => 0,
        "nether" or "-1" => -1,
        _ => throw new ArgumentException($"dim must be overworld or nether, not '{s}'"),
    };

    internal static BlockPos? ParsePos(string? s, string field)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        var p = s.Split([',', ' ', ';'], StringSplitOptions.RemoveEmptyEntries);
        if (p.Length != 3 || !int.TryParse(p[0], out var x) || !int.TryParse(p[1], out var y) || !int.TryParse(p[2], out var z))
            throw new ArgumentException($"{field} must be x,y,z");
        return new BlockPos(x, y, z);
    }
}
