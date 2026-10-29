using System.Text.Json;
using MineClaw.Client;
using MineClaw.Data;
using MineClaw.World;

namespace MineClaw.Skills;

/// <summary>Convenience base: a <see cref="SkillSpec"/> plus terse result helpers.</summary>
public abstract class Skill : ISkill
{
    public abstract SkillSpec Spec { get; }

    public abstract Task<SkillResult> ExecuteAsync(SkillContext ctx, JsonElement args, CancellationToken ct);

    protected static SkillResult Ok(string text) => SkillResult.Success(text);
    protected static SkillResult Fail(string text) => SkillResult.Fail(text);
    protected static string P(BlockPos p) => SkillJson.Fmt(p);
    protected static string Item(int id, int damage = 0) => Items.DisplayName(id, damage).ToLowerInvariant();

    /// <summary>Position arg, else <paramref name="fallback"/>.</summary>
    protected static BlockPos? PosArg(JsonElement args, Func<BlockPos?> fallback, string name = "pos") => args.Pos(name) ?? fallback();

    protected static int Clamp(int? v, int def, int min, int max) => Math.Clamp(v ?? def, min, max);
}

/// <summary>A documented wave-2 skill: discoverable, honest about not being implemented.</summary>
public abstract class StubSkill : Skill
{
    public override Task<SkillResult> ExecuteAsync(SkillContext ctx, JsonElement args, CancellationToken ct) =>
        Task.FromResult(Fail($"{Spec.Name} is not implemented yet: {Spec.Summary}"));
}

/// <summary>Small block helpers shared by skills.</summary>
public static class SkillWorld
{
    public static readonly BlockPos[] Horizontal = [new(1, 0, 0), new(-1, 0, 0), new(0, 0, 1), new(0, 0, -1)];

    public static bool Solid(IBlockAccess w, BlockPos p) => BlockShapes.IsFullCube(w, p.X, p.Y, p.Z);
    public static bool Air(IBlockAccess w, BlockPos p) => w.GetBlock(p.X, p.Y, p.Z) == 0;
    public static bool Replaceable(IBlockAccess w, BlockPos p) => Aiming.IsReplaceable(w, w.GetBlock(p.X, p.Y, p.Z));
    public static bool OverVoid(IBlockAccess w, BlockPos p) => Safety.IsOverVoid(w, p.X, p.Y, p.Z);

    /// <summary>A cell a player can stand in (support, clear body, no hazard, not over the void).</summary>
    public static bool Standable(IBlockAccess w, BlockPos p) => Safety.IsStandable(w, p.X, p.Y, p.Z) && !OverVoid(w, p);

    public static string Name(int id) => Blocks.Get(id).Name;

    /// <summary>Best scaffold/building block in the inventory (never reserved items; never falling sand unless asked for by
    /// name; dirt only in a normal world, where it isn't scarce): the client's <see cref="MineClaw.Client.MinecraftClient.ScaffoldBlocks"/> order.</summary>
    public static int? BuildingBlock(SkillContext ctx, string? preferred = null)
    {
        if (SkillJson.Item(preferred) is { } want && want.Id < 256 && ctx.Has(want.Id)) return want.Id;
        foreach (int id in MineClaw.Client.MinecraftClient.ScaffoldBlocks)
        {
            // Dirt is everywhere in a normal world: a fine building block, before the logs (skyblock never spends it).
            if (id == Blocks.Log && !MineClaw.Data.Versioning.WorldMode.Current.ScarceTerrain
                && !ctx.Game.ReservedItems.Contains(Blocks.Dirt) && ctx.Has(Blocks.Dirt)) return Blocks.Dirt;
            if (!ctx.Game.ReservedItems.Contains(id) && ctx.Has(id)) return id;
        }
        if (!MineClaw.Data.Versioning.WorldMode.Current.ScarceTerrain && !ctx.Game.ReservedItems.Contains(Blocks.Dirt) && ctx.Has(Blocks.Dirt)) return Blocks.Dirt;
        return null;
    }

    public static ActionResult Ensure(bool ok, string fail) => ok ? ActionResult.Success("ok") : ActionResult.Fail(fail);
}
