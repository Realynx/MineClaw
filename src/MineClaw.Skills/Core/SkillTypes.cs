using System.Text.Json;

namespace MineClaw.Skills;

public enum SkillCategory { Movement, Doors, Redstone, Liquids, Farming, Building, Smelting, Storage, Items, Survival, Nether, Combat, Animals, Vehicles, Misc }

/// <summary>How risky a skill is; the registry gates edge/combat/destructive skills behind survival checks and the
/// caller's explicit consent flags.</summary>
public enum SafetyClass
{
    /// <summary>No fall, fight or irreversible change.</summary>
    Safe,
    /// <summary>Works at island edges or heights (EdgeGuard + sneaking; survival-gated).</summary>
    Edge,
    /// <summary>May start or continue a fight (survival-gated).</summary>
    Combat,
    /// <summary>Removes or converts blocks/items in ways that can't be undone (lava, TNT, obsidian, digging builds).</summary>
    Destructive,
}

[Flags]
public enum Dim
{
    Overworld = 1,
    Nether = 2,
    Any = Overworld | Nether,
}

public enum SkillStatus
{
    /// <summary>Runs on existing primitives.</summary>
    Ready,
    /// <summary>Implemented, but needs a client primitive the current backend may lack (checked at run time).</summary>
    NeedsPrimitive,
    /// <summary>Wave-2 stub: documented contract, returns "not implemented yet".</summary>
    Stub,
}

/// <summary>What must hold before a skill starts. Checked by <see cref="SkillRunner"/>; violations become a terse failure.</summary>
public sealed record Preconditions
{
    public static readonly Preconditions None = new();

    /// <summary>Each group needs at least one of its item ids (e.g. [[hoe ids...], [seeds]]).</summary>
    public IReadOnlyList<int[]> Items { get; init; } = [];
    /// <summary>Human-readable needs for the signature ("hoe+seeds"); defaults to "none".</summary>
    public string Needs { get; init; } = "none";
    public Dim Dimension { get; init; } = Dim.Any;
    public Primitive Primitives { get; init; } = Primitive.None;
    /// <summary>Apply the client's survival gate (hp/food minimums, food in inventory) before starting.</summary>
    public bool SurvivalGate { get; init; }
    /// <summary>Refuse at night (mob spawns) unless args say otherwise.</summary>
    public bool DaylightOnly { get; init; }
}

/// <summary>The static description of a skill: everything the catalog, search and prompt hints need.</summary>
public sealed record SkillSpec
{
    public required string Name { get; init; }
    public required SkillCategory Category { get; init; }
    /// <summary>One line, ≤ 60 chars: what it does.</summary>
    public required string Summary { get; init; }
    /// <summary>Compact arg list for the signature, e.g. "pos?,count?" (? = optional).</summary>
    public string Args { get; init; } = "";
    /// <summary>JSON schema of the args object (kept out of every prompt; returned only on demand).</summary>
    public JsonElement Schema { get; init; } = SkillJson.EmptySchema;
    public Preconditions Pre { get; init; } = Preconditions.None;
    public SafetyClass Safety { get; init; } = SafetyClass.Safe;
    /// <summary>Rough wall-clock estimate for planning (and the worker's minute budget).</summary>
    public TimeSpan Estimate { get; init; } = TimeSpan.FromSeconds(10);
    public SkillStatus Status { get; init; } = SkillStatus.Ready;
    /// <summary>Extra search words (synonyms, items, natural phrasings) — never shown in prompts.</summary>
    public string Keywords { get; init; } = "";
    /// <summary>"builtin", "routine", "structure".</summary>
    public string Source { get; init; } = "builtin";

    /// <summary>
    /// One-line compact signature, the only form a skill ever takes in a prompt, e.g.
    /// <c>harvest_cane(radius?) cut cane, leave bottoms | needs: none | safe</c> (≤ 120 chars).
    /// </summary>
    public string Signature
    {
        get
        {
            var flags = Safety.ToString().ToLowerInvariant();
            if (Pre.Dimension == Dim.Nether) flags += ",nether";
            if (Status == SkillStatus.Stub) flags += ",stub";
            var s = $"{Name}({Args}) {Summary} | needs: {Pre.Needs} | {flags}";
            return s.Length <= SkillCatalog.MaxSignatureChars ? s : s[..(SkillCatalog.MaxSignatureChars - 3)] + "...";
        }
    }
}

/// <summary>Terse outcome of a skill: one line, "ok: ..." / "failed: ...", safe to hand straight to the model.</summary>
public sealed record SkillResult(bool Ok, string Text)
{
    public static SkillResult Success(string text) => new(true, text);
    public static SkillResult Fail(string text) => new(false, text);
    public static SkillResult From(MineClaw.Client.ActionResult r) => new(r.Ok, r.Message);
    public override string ToString() => (Ok ? "ok: " : "failed: ") + Text;
}

/// <summary>Marks an <see cref="ISkill"/> with a parameterless constructor for discovery by <see cref="SkillRegistry.Discover"/>.</summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class SkillAttribute : Attribute;

/// <summary>A composable game skill. Implementations are deterministic C#; the model only picks the skill and args.</summary>
public interface ISkill
{
    SkillSpec Spec { get; }

    /// <summary>Runs the skill. Must honour <paramref name="ct"/>, never throw for game failures (return a failed
    /// result), and be idempotent where possible ("already open" is success).</summary>
    Task<SkillResult> ExecuteAsync(SkillContext ctx, JsonElement args, CancellationToken ct);
}
