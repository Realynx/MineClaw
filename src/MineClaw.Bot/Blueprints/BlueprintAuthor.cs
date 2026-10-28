using System.Text.RegularExpressions;
using MineClaw.Agent.Budget;
using MineClaw.Agent.Llm;

namespace MineClaw.Bot.Blueprints;

/// <summary>One answer of the blueprint author: its text, what it cost.</summary>
public sealed record AuthorTurn(string Text, LlmUsage Usage, string Model, decimal Usd);

/// <summary>Writes and revises blueprints (the Opus brain tier only; a scripted fake in tests).</summary>
public interface IBlueprintAuthor
{
    Task<AuthorTurn> WriteAsync(IReadOnlyList<LlmMessage> conversation, CancellationToken ct);
}

/// <summary>
/// The Opus main model with its own small private context (cached DSL cheat-sheet + the brief + evaluations): it never sees the
/// brain's history and the brain never sees its drafts. Calls are booked as main-tier ("opus") spend.
/// </summary>
public sealed class LlmBlueprintAuthor(ILlmClient llm, LlmOptions options, UsageTracker? usage, BlueprintOptions o) : IBlueprintAuthor
{
    public async Task<AuthorTurn> WriteAsync(IReadOnlyList<LlmMessage> conversation, CancellationToken ct)
    {
        var resp = await llm.CompleteAsync(new LlmRequest
        {
            Tier = ModelTier.Main, Model = options.MainModel, MaxTokens = o.AuthorMaxTokens, Purpose = "design",
            System = [new LlmSystemBlock(BlueprintPrompts.System, Cache: true)], Messages = conversation, CacheHistory = true,
            Thinking = string.IsNullOrWhiteSpace(o.AuthorThinking) ? null : o.AuthorThinking,
        }, ct).ConfigureAwait(false);
        decimal usd = 0;
        try { usd = usage?.Record(resp.Model, resp.Usage, ModelTier.Main) ?? 0; } catch (Exception) { }
        return new AuthorTurn(resp.Text, resp.Usage, resp.Model, usd);
    }
}

public static partial class BlueprintPrompts
{
    /// <summary>The author's cached system prompt: the DSL and the rules the checks enforce (≈650 tokens, cached).</summary>
    public const string System =
        "You design Minecraft Beta 1.8.1 builds for a skyblock bot as a compact blueprint. Reply with ONLY the blueprint in a ``` block " +
        "(or the single word SHOW to get the preview image once). Format, one statement per line, # comments:\n" +
        "blueprint <short name>\nsize WxHxD   (x width, y height, z depth; max 24x16x24)\n" +
        "facing south   (the front; DRAW the front on the bottom (south) row: facing east turns the whole build)\n" +
        "light          (only if the interior must be lit)\n" +
        "palette C=cobblestone P=planks L=log G=glass_pane D=door:south S=cobblestone_stairs:north T=torch W=wool:red H=slab:cobble\n" +
        "layer 0        (then D rows of W chars, north row first, west char first; layer 1-3 repeats rows)\n" +
        "'.' must be air (interior, doorway), '_' leave the site as it is. Layer 0 sits ON the ground (the ground is not part of it).\n" +
        "Primitives (local coords x,y,z from the west-north-bottom corner, later statements win): box X a b | hollow X a b | walls X a b " +
        "(4 sides, inside air) | floor X a b | clear a b | pillar X x,y,z h | door D x,y,z [dir] | window G a b | stairs S x,y,z dir " +
        "| roof X flat|gable|slope:dir x1,y,z1 x2,z2 [ridge-symbol] (gable/slope step up one layer per row; with stairs they face up-slope).\n" +
        "Blocks: b1.8.1 names only: stone cobblestone planks log(:oak|spruce|birch) sandstone bricks stone_bricks(:mossy|cracked) " +
        "mossy_cobblestone obsidian glass glass_pane wool(:colour) glowstone bookshelf clay netherrack fence fence_gate iron_bars ladder(:wall side) " +
        "torch trapdoor slab(:stone|sandstone|wood|cobble|brick|stonebrick) wooden_stairs cobblestone_stairs brick_stairs stone_brick_stairs " +
        "(:dir = the way UP) wooden_door iron_door(:dir = the wall side it opens to) chest furnace crafting_table pumpkin jack_o_lantern sand gravel. " +
        "No grass, leaves, liquids, beds, signs, redstone, double slabs; no upside-down stairs or top slabs (b1.8.1).\n" +
        "Rules the checker enforces: every block must touch something that stands (no floating parts; sand/gravel need a block under them); " +
        "a door is drawn on 2 layers (or once: its top is added) and stands on a full opaque block; a slab on the same slab merges, so don't; " +
        "torches/ladders need a wall; the interior needs 2 air of headroom and a reachable door or 2-high opening; never overlap protected " +
        "blocks or fluids; prefer cheap local materials (cobble from the gen, planks from logs) and the material list you are given; " +
        "keep it small unless asked. After each version you get a short evaluation (errors, warnings, bill, the perception model's look): " +
        "fix every error, answer with the WHOLE corrected blueprint, nothing else.";

    /// <summary>The design brief: wish, site, constraints (≈150-250 tokens).</summary>
    public static string Brief(string description, string site, string constraints, string? prior = null, string? change = null)
    {
        var s = $"Wish: {description.Trim()}\nSite: {site}\nConstraints: {constraints}";
        if (prior != null) s += $"\nCurrent blueprint:\n```\n{prior.Trim()}\n```\nChange requested: {change?.Trim()}\nKeep what wasn't asked to change.";
        return s;
    }

    [GeneratedRegex(@"```[a-zA-Z]*\s*\n(.*?)```", RegexOptions.Singleline)]
    private static partial Regex FenceRx();

    /// <summary>The DSL out of an answer (the fenced block, else the whole text).</summary>
    public static string ExtractDsl(string text)
    {
        var m = FenceRx().Match(text ?? "");
        return (m.Success ? m.Groups[1].Value : text ?? "").Trim();
    }

    /// <summary>The author asked for the preview image instead of answering.</summary>
    public static bool WantsImage(string text) =>
        (text ?? "").Trim().Trim('.', '!').Equals("SHOW", StringComparison.OrdinalIgnoreCase)
        || ((text ?? "").Length < 80 && (text ?? "").Contains("SHOW", StringComparison.Ordinal) && !(text ?? "").Contains("size", StringComparison.OrdinalIgnoreCase));
}
