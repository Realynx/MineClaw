using System.Text.Json;
using MineClaw.Agent.Tools;
using MineClaw.Bot.Game;
using MineClaw.Bot.Memory;
using MineClaw.Data;
using MineClaw.World;

namespace MineClaw.Bot.GameTools;

/// <summary>
/// The <c>harvest_generator</c> tool's handler (registered by <see cref="GameToolSet.RegisterAll"/>): picks the dig cell (auto,
/// a claimed one swapped for a free cell of the same generator, or a queue for one), the store chest and the stand spot, then
/// runs the <see cref="GeneratorLoop"/> and takes the next free cell when another bot won the lease.
/// </summary>
internal sealed class HarvestGeneratorTool(IGameClient c, IPlaceResolver places, BotOptions options,
    Func<BlockPos, IReadOnlyList<BlockPos>>? generatorCells, GeneratorSupport? generator)
{
    private static readonly HashSet<string> StoreYes = new(StringComparer.Ordinal) { "true", "yes", "1", "chest", "default" };
    private static readonly HashSet<string> StoreNo = new(StringComparer.Ordinal) { "false", "no", "0", "none" };

    public async Task<ToolResult> RunAsync(JsonElement i, CancellationToken ct)
    {
        var (g, at, autoNote, targetError) = ResolveTarget(i.ReqStr("at"));
        if (targetError != null) return targetError;
        // Hive: another bot already mines that cell -> take a free dig cell of the same generator instead.
        var (cell, note, standingBy) = await PickOrQueueAsync(g, ct).ConfigureAwait(false);
        if (standingBy != null) return standingBy;
        if (cell is not { } pick) return ToolResult.Error(note ?? $"no free dig cell at {Locations.Fmt(g)}");
        if (autoNote != null) note = note == null ? autoNote : autoNote + "; " + note;
        // Only a generator cell (lava + water next to it, or a taught/regen-confirmed one) is ever dug here.
        // (A named place is a taught key; the yield guard still stops on a wrong one.)
        if (Locations.ParseCoords(at) != null && !GameToolSet.IsGeneratorCell(c, pick, generatorCells))
            return ToolResult.Error($"{Locations.Fmt(pick)} isn't a generator cell (no lava and water next to it): need the right cell");
        var (store, storeError) = ResolveStore(i.Str("store"), at);
        if (storeError != null) return storeError;
        // Taught stand spot (B12/B21): dig the cell only from where the player stood in the demo, staying put there.
        var (stand, standError) = ParseStand(i.Str("stand"));
        if (standError != null) return standError;
        (stand, note) = FitStand(stand, pick, note);
        var loop = new GeneratorLoop(c, generator);
        int count = Math.Clamp(i.Int("count") ?? 16, 1, 20000);
        var r = GameToolSet.R(await loop.RunAsync(pick, count, store, ct, stand, Rotation(pick)).ConfigureAwait(false));
        (r, pick, note) = await RetryBusyAsync(loop, r, pick, count, store, note, ct).ConfigureAwait(false);
        r = GameToolSet.AskOnceIfEnclosed(r, pick);
        return note == null ? r : r with { Text = note + "; " + r.Text };
    }

    /// <summary>The asked generator cell, or for at=auto a distinct free taught/confirmed one through the claim board (several bots
    /// running one script, live 10-28: hive_acff51 hardcoded 5,64,7 for all three clones). <c>At</c> is the auto cell's coords.</summary>
    private (BlockPos Cell, string At, string? AutoNote, ToolResult? Error) ResolveTarget(string at)
    {
        if (GameToolSet.IsAuto(at))
        {
            var (p, autoNote) = GameToolSet.AutoGeneratorCell(c, generatorCells);
            if (p is not { } auto) return (default, at, autoNote, ToolResult.Error(autoNote ?? "needs_input: which blocks are the cobble generator's cells? Punch them"));
            return (auto, Locations.Fmt(auto), autoNote, null);
        }
        var q = Locations.ParseCoords(at) ?? places.Resolve(at, "mine") ?? places.Resolve(at, "output") ?? places.Resolve(at);
        return q is { } g ? (g, at, null, null) : (default, at, null, ToolResult.Error($"unknown generator '{at}'"));
    }

    /// <summary>A free dig cell of the generator; with more bots than cells (live 21:44: main, 67 and 1337 on one gen) a queue for
    /// one instead of failing, and a "standing by" result when none came free in time.</summary>
    private async Task<(BlockPos? Cell, string? Note, ToolResult? StandingBy)> PickOrQueueAsync(BlockPos g, CancellationToken ct)
    {
        var (cell, note) = GameToolSet.PickGeneratorCell(c, g, generatorCells);
        if (cell is not null || GameClients.Claimed(c) == null || options.GeneratorQueueSeconds <= 0) return (cell, note, null);
        var until = DateTime.UtcNow.AddSeconds(options.GeneratorQueueSeconds);
        while (cell is null && DateTime.UtcNow < until)
        {
            await Task.Delay(TimeSpan.FromSeconds(Math.Min(2, Math.Max(0.05, options.GeneratorQueueSeconds / 10.0))), ct).ConfigureAwait(false);
            (cell, note) = GameToolSet.PickGeneratorCell(c, g, generatorCells);
        }
        if (cell is null)
            return (null, note, ToolResult.Success($"standing by: {note}; waited {options.GeneratorQueueSeconds}s, dug nothing (help another way: a deposit run, or try again)"));
        return (cell, "waited for a free cell" + (note == null ? "" : "; " + note), null);
    }

    /// <summary>store:true (live 21:12 "unknown chest 'true'") = the gen's own store / nearest chest; store:false = keep it.</summary>
    private (BlockPos? Store, ToolResult? Error) ResolveStore(string? raw, string at)
    {
        var (arg, yes, no) = StoreChoice(raw);
        BlockPos? store = arg is { } st ? Locations.ParseCoords(st) ?? places.Resolve(st, "chest") ?? places.Resolve(st) : null;
        if (store == null && arg is { } bad) return (null, ToolResult.Error($"unknown chest '{bad}' (store takes true, x,y,z or a chest/waypoint name)"));
        store ??= DefaultStore(at, yes, no);
        return (store, null);
    }

    /// <summary>The store argument, unless it's a yes/no switch (then null, with which one).</summary>
    private static (string? Arg, bool Yes, bool No) StoreChoice(string? raw)
    {
        var arg = raw?.Trim();
        var lower = arg?.ToLowerInvariant();
        bool yes = lower != null && StoreYes.Contains(lower), no = lower != null && StoreNo.Contains(lower);
        return (yes || no ? null : arg, yes, no);
    }

    /// <summary>Default: the generator's own store key (only if that really is a chest; Resolve falls back to corners); for
    /// store:true then the nearest chest; nothing for store:false.</summary>
    private BlockPos? DefaultStore(string at, bool yes, bool no)
    {
        BlockPos? store = no ? null : IfChest(places.Resolve(at, "store")) ?? IfChest(places.Resolve(at, "chest"));
        return store ?? (yes ? GameToolSet.NearestChest(c) : null);
    }

    private BlockPos? IfChest(BlockPos? q) => q is { } x && c.World.GetBlock(x.X, x.Y, x.Z) == Blocks.Chest ? x : null;

    private static (BlockPos? Stand, ToolResult? Error) ParseStand(string? raw)
    {
        if (raw is not { Length: > 0 } sa) return (null, null);
        var stand = Locations.ParseCoords(sa);
        return stand == null ? (null, ToolResult.Error($"stand must be x,y,z (got '{sa}')")) : (stand, null);
    }

    /// <summary>The cell's taught stand when it reaches the cell (taught for another one = no stand for it), else null.</summary>
    private BlockPos? Taught(BlockPos cellPos)
    {
        try { return generator?.TaughtStand?.Invoke(cellPos) is { } t && GeneratorStand.Reaches(c.World, t, cellPos) ? t : null; }
        catch (Exception) { return null; } // memory busy: the client picks a spot
    }

    /// <summary>
    /// The given stand when it reaches the cell, else (live 21:12: one stand 3,64,4 passed for every cell; 6,64,8 was out of reach
    /// from it) the cell's own taught stand or a spot the client finds itself, saying so; never a refusal for a stand that can't
    /// work this cell. No stand given = the cell's taught one.
    /// </summary>
    private (BlockPos? Stand, string? Note) FitStand(BlockPos? stand, BlockPos pick, string? note)
    {
        if (stand is not { } given || GeneratorStand.Reaches(c.World, given, pick)) return (stand ?? Taught(pick), note);
        var own = Taught(pick);
        note = (note == null ? "" : note + "; ") + $"stand {Locations.Fmt(given)} can't reach {Locations.Fmt(pick)}: "
               + (own is { } o ? $"using its taught stand {Locations.Fmt(o)}" : "finding a spot that reaches it");
        return (own, note);
    }

    /// <summary>Every cell of this generator the stand reaches is dug in rotation (never hammering one cell).</summary>
    private List<BlockPos> Rotation(BlockPos pick) =>
        (generatorCells?.Invoke(pick) ?? Perception.BlockDescriber.GenCellsNear(c.World, pick, 4))
        .Where(x => x != pick && Math.Abs(x.X - pick.X) <= 4 && x.Y == pick.Y && Math.Abs(x.Z - pick.Z) <= 4)
        .Where(x => GameToolSet.IsGeneratorCell(c, x, generatorCells)).Distinct().ToList();

    /// <summary>Two bots picked the same free cell at once: the lease went to the other one. Take the next free cell (twice).</summary>
    private async Task<(ToolResult R, BlockPos Pick, string? Note)> RetryBusyAsync(GeneratorLoop loop, ToolResult r, BlockPos pick, int count,
        BlockPos? store, string? note, CancellationToken ct)
    {
        for (int retry = 0; retry < 2 && !r.Ok && r.Text.Contains("busy:", StringComparison.Ordinal); retry++)
        {
            var (next, why) = GameToolSet.PickGeneratorCell(c, pick, generatorCells);
            if (next is not { } n2 || n2 == pick) break;
            note = (note == null ? "" : note + "; ") + why;
            pick = n2;
            r = GameToolSet.R(await loop.RunAsync(pick, count, store, ct, Taught(pick)).ConfigureAwait(false));
        }
        return (r, pick, note);
    }
}
