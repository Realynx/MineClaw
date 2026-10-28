using System.Diagnostics;
using MineClaw.Data;
using MineClaw.World;

namespace MineClaw.Scripting;

/// <summary>
/// A test run of a draft on a fake world before it is saved (author loop, Lua migration): the bot's real action and skill
/// names, every action succeeds at once, a starter kit (tools, food) in the inventory, time 400× faster, ≤ 2 s, ≤ 300 actions.
/// It catches what lint can't see — a ReferenceError, a TypeError, a binding misuse, a broken machine spec — and nothing else:
/// a script that fails on purpose, hits a limit or just keeps going passes. Null = passed; else the terse reason (with the line).
/// </summary>
public static class ScriptDryRun
{
    /// <summary>Reasons that mean the code itself is wrong (not the fake world).</summary>
    private static readonly string[] Bugs =
    [
        "error:", "syntax:", "is not defined", "unknown item", "machine '", "no role function", "isn't in states", "call depth limit",
        "takes a string", "read-only", "unknown lane", "needs a function", "needs a position", "needs an item", "needs a name",
        "needs a topic", "needs a label", "needs a question", "needs an object", "needs states", "needs seconds", "needs a body",
        "needs a role", "waitfor kind",
    ];

    public static string? Run(string code, ScriptHost real, ScriptLanguage language = ScriptLanguage.Js, string? paramsJson = null)
    {
        var world = new WorldState();
        world.ApplyChunkRegion(-16, 0, -16, 32, 128, 32, new byte[32 * 128 * 32], null, null, null);
        for (int x = -6; x <= 6; x++)
        for (int z = -6; z <= 6; z++)
            world.SetBlock(x, 64, z, Blocks.Stone, 0);
        world.Player.ServerTeleport(new Vec3d(0.5, 65, 0.5), 66.62, 0, 0, true);
        world.Player.SetHealth(20, 20, 5);
        world.Time = 1000;
        int slot = 9;
        foreach (var (item, n) in new[] { ("stone_pickaxe", 1), ("stone_axe", 1), ("stone_shovel", 1), ("stone_sword", 1), ("stone_hoe", 1), ("bread", 16), ("cobblestone", 16), ("log", 8) })
            if (Items.ByName(item) is { } it) world.Inventory.SetSlot(0, slot++, new ItemStack((short)it.Id, (byte)n, 0));
        var host = new ScriptHost
        {
            World = world,
            Username = real.Username,
            Actions = real.Actions,
            Act = (name, _, _) => Task.FromResult(ActResult.Success($"{name} ok")),
            Skills = real.Skills,
            Skill = (name, _, _) => Task.FromResult(ActResult.Success($"{name} ok")),
            SkillSchema = real.SkillSchema,
            SkillIsAtomic = real.SkillIsAtomic,
            Ask = (_, _, _, _) => Task.FromResult<string?>("yes"),
            NearestKnown = _ => new BlockPos(3, 65, 3),
            OnlinePlayers = () => ["player"],
            Body = real.Body,
        };
        var options = new ScriptOptions
        {
            MaxActions = 300, MaxMinutes = 0.035, MaxPauseMinutes = 0.02, MaxSayPerRun = 100, SayMinIntervalSeconds = 0, MaxSayPerMinute = 1000,
            MinWakeIntervalSeconds = 0,
        };
        using var controller = new ScriptController(host, options, new FastTime(400));
        var (s, err) = controller.Start(new ScriptRequest { Name = "dry_run", Code = code, Language = language, ParamsJson = paramsJson });
        if (s == null) return err;
        var sw = Stopwatch.StartNew();
        while (!s.Completion.IsCompleted && sw.Elapsed < TimeSpan.FromSeconds(2.5))
        {
            if (s.Status == ScriptStatus.Paused) s.Resume("yes"); // ask("brain") / a handler's pause: answered
            Thread.Sleep(20);
        }
        if (!s.Completion.IsCompleted) { s.Stop("dry run over"); s.Completion.Wait(TimeSpan.FromSeconds(3)); }
        if (s.Status != ScriptStatus.Failed || s.Reason is not { } why) return null;
        if (!Bugs.Any(b => why.Contains(b, StringComparison.Ordinal))) return null;
        return (s.Line > 0 ? $"L{s.Line} " : "") + why;
    }

    /// <summary>Time running <paramref name="factor"/>× faster (delays and timers scaled).</summary>
    private sealed class FastTime(double factor) : TimeProvider
    {
        private readonly DateTimeOffset _start = DateTimeOffset.UtcNow;
        private readonly Stopwatch _sw = Stopwatch.StartNew();

        public override DateTimeOffset GetUtcNow() => _start + TimeSpan.FromTicks((long)(_sw.Elapsed.Ticks * factor));

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) =>
            new Scaled(System.CreateTimer(callback, state, S(dueTime), S(period)), this);

        private TimeSpan S(TimeSpan t) => t == Timeout.InfiniteTimeSpan ? t : TimeSpan.FromTicks(Math.Max(1, (long)(t.Ticks / factor)));

        private sealed class Scaled(ITimer inner, FastTime f) : ITimer
        {
            public bool Change(TimeSpan dueTime, TimeSpan period) => inner.Change(f.S(dueTime), f.S(period));
            public void Dispose() => inner.Dispose();
            public ValueTask DisposeAsync() => inner.DisposeAsync();
        }
    }
}
