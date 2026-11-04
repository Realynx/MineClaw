using MineClaw.Bot.Game;
using MineClaw.Data;
using MineClaw.World;

namespace MineClaw.Bot.GameTools;

/// <summary>One obtain: <see cref="Text"/> is the whole chain in one line (or the exact shortfall).</summary>
public sealed record ObtainResult(bool Ok, string Text);

/// <summary>
/// The shared resolver every "I need N of X" path goes through (Lua <c>ensure</c>/<c>acquire</c> via the
/// <see cref="AcquisitionPlanner"/>, <c>craft</c>/<c>do craft</c>, the worker's craft, skill preconditions, builds), zero
/// tokens: (1) the inventory; (2) known chests, nearest first, the exact variant, as much as the inventory holds, skipping
/// chests <see cref="AcquisitionSources.SkipChest"/> refuses and never taking the last sapling/dirt; (3) the recipe tree
/// planned whole over inventory + chests and crafted (<see cref="Crafter"/>); (4) only then the real shortfall ("need 1 more
/// log for a chest; none in inventory or the 3 known chests"). Works on any body: the sources come from the client's
/// attached <see cref="AcquisitionSources"/> (main's or a clone body's).
/// </summary>
public sealed class Obtainer(IGameClient c, AcquisitionSources? sources = null)
{
    private readonly AcquisitionSources? _s = sources ?? Acquisition.Of(c);

    /// <summary>Budget hook (the acquisition planner counts each withdraw/craft as a step).</summary>
    public Action<string>? OnStep { get; init; }

    private WorldState W => c.World;

    /// <param name="damage">The exact variant (cobblestone slab = 44:3), or -1 = any.</param>
    public async Task<ObtainResult> ObtainAsync(int id, int damage, int count, CancellationToken ct)
    {
        count = Math.Max(1, count);
        int? dmg = damage >= 0 && Items.Exists(id) && Items.Get(id).MaxDamage <= 0 ? damage : null;
        string name = Names.Item(id, dmg ?? 0);
        if (Have(id, dmg) >= count) return new(true, $"had {count} {name}");

        var parts = new List<string>();
        var chests = ChestStock.Read(c, _s?.Observations, _s?.SkipChest);
        if (await FromChestsAsync(id, dmg, count, name, chests, parts, ct).ConfigureAwait(false) is { } done) return done;

        if (Recipes.ForOutput(id, dmg ?? -1).Count > 0) return await CraftRestAsync(id, dmg, count, parts, ct).ConfigureAwait(false);
        parts.Add($"need {count - Have(id, dmg)} more {name} (no recipe); {NoRecipeWhere(id, dmg, chests)}");
        return new(false, string.Join("; ", parts));
    }

    private async Task<ObtainResult> CraftRestAsync(int id, int? dmg, int count, List<string> parts, CancellationToken ct)
    {
        OnStep?.Invoke("craft");
        var r = await new Crafter(c) { Observations = _s?.Observations }.CraftAsync(id, dmg ?? -1, count - Have(id, dmg), ct).ConfigureAwait(false);
        parts.Add(r.Message);
        return new(Have(id, dmg) >= count, string.Join("; ", parts));
    }

    private int Have(int id, int? dmg) => W.Inventory.CountItem(id, dmg);

    /// <summary>Withdraws what storage holds; the result when that settles it (enough, or the inventory filled up), else null.</summary>
    private async Task<ObtainResult?> FromChestsAsync(int id, int? dmg, int count, string name, ChestStock chests, List<string> parts, CancellationToken ct)
    {
        if (chests.Count(id, dmg ?? -1) <= 0) return null;
        int start = Have(id, dmg);
        var took = await WithdrawAsync(id, dmg, count - start, chests, ct).ConfigureAwait(false);
        if (took.Count > 0) parts.Add("took " + string.Join("; ", took));
        int have = Have(id, dmg);
        if (have >= count) return new(true, string.Join("; ", parts));
        if (have > start && Room(id, dmg) == 0) return new(false, $"{string.Join("; ", parts)}; inventory full at {Have(id, dmg)}/{count} {name}");
        return null;
    }

    private string NoRecipeWhere(int id, int? dmg, ChestStock chests)
    {
        int stored = chests.Count(id, dmg ?? -1);
        int have = Have(id, dmg);
        return chests.Known == 0 ? "none in inventory and no known chests"
            : stored > 0 ? $"{stored} left in the {chests.Known} known chests"
            : have > 0 ? $"have {have}, none in the {chests.Known} known chests"
            : $"none in inventory or the {chests.Known} known chests";
    }

    /// <summary>Withdraws up to <paramref name="want"/> of the exact variant, chest by chest nearest first, as the inventory has room.</summary>
    private async Task<List<string>> WithdrawAsync(int id, int? dmg, int want, ChestStock chests, CancellationToken ct)
    {
        var lines = new List<string>();
        int start = W.Inventory.CountItem(id, dmg);
        foreach (var (chest, k) in chests.Allocate(id, dmg ?? -1, want))
        {
            int n = Math.Min(k, Math.Min(want - (W.Inventory.CountItem(id, dmg) - start), Room(id, dmg)));
            if (n <= 0) break;
            OnStep?.Invoke("withdraw");
            int before = W.Inventory.CountItem(id, dmg);
            var r = await c.WithdrawAsync(id, n, chest.Pos, dmg, ct).ConfigureAwait(false);
            int got = W.Inventory.CountItem(id, dmg) - before;
            if (got > 0) lines.Add($"{got} {Names.Item(id, dmg ?? 0)} from {chest.Pos.X},{chest.Pos.Y},{chest.Pos.Z}");
            if (got < n) chests.Forget(chest.Pos, id);
        }
        if (W.Inventory.OpenWindow != null) try { await c.CloseWindowAsync(ct).ConfigureAwait(false); } catch (Exception) { }
        return lines;
    }

    /// <summary>How many more of (id, damage) the inventory can take (free slots and room in partial stacks).</summary>
    public int Room(int id, int? damage)
    {
        int max = Math.Max(1, (int)Items.Get(id).MaxStack), room = 0;
        var slots = W.Inventory.PlayerSlots();
        for (int i = 9; i < 45 && i < slots.Length; i++)
        {
            var st = slots[i];
            if (st.IsEmpty) room += max;
            else if (st.Id == id && (damage is null || st.Damage == damage)) room += Math.Max(0, max - st.Count);
        }
        return room;
    }
}
