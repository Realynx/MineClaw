using System.Reflection;
using System.Text.Json;

namespace MineClaw.Skills;

/// <summary>
/// All skills by name: built-ins discovered via <see cref="SkillAttribute"/>, plus skills added at run time (promoted
/// routines, taught structures). <see cref="RunAsync"/> is the single entry point: it checks dimension, items,
/// primitives and survival gating, honours cancellation, and turns exceptions into terse failures.
/// </summary>
public sealed class SkillRegistry
{
    private readonly Dictionary<string, ISkill> _byName = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<ISkill> _order = new();
    private readonly object _lock = new();

    public static SkillRegistry CreateDefault() => new SkillRegistry().Discover(typeof(SkillRegistry).Assembly);

    public IReadOnlyList<ISkill> All { get { lock (_lock) return _order.ToArray(); } }
    public IReadOnlyList<SkillSpec> Specs => All.Select(s => s.Spec).ToArray();
    public int Count { get { lock (_lock) return _order.Count; } }

    /// <summary>Raised when skills are added (the catalog re-indexes; tool definitions never change).</summary>
    public event Action<ISkill>? Added;

    /// <summary>
    /// Opened around every skill run (name, args) and disposed when it ends: the Bot's inventory ledger attributes every
    /// item the skill moves to it ("tossed to PoofimaFox" for give_items), so nothing leaves the inventory silently.
    /// </summary>
    public Func<string, JsonElement, IDisposable?>? RunScope { get; set; }

    /// <summary>
    /// Self-reliant preconditions: called for each item group a skill needs and the inventory lacks (any hoe, a bucket,
    /// torches...) before the precondition check, so a missing tool/material is acquired (chests, hive, crafting, bootstrap:
    /// the Bot layer's acquisition planner) instead of failing the skill. Returns a short note, or null when it got nothing.
    /// Null = no acquisition (the plain "needs X" failure).
    /// </summary>
    public Func<SkillContext, SkillSpec, int[], CancellationToken, Task<string?>>? AcquireMissing { get; set; }

    /// <summary>Registers every non-abstract <see cref="ISkill"/> marked <see cref="SkillAttribute"/> in <paramref name="assembly"/>.</summary>
    public SkillRegistry Discover(Assembly assembly)
    {
        foreach (var t in assembly.GetTypes().Where(t => !t.IsAbstract && typeof(ISkill).IsAssignableFrom(t)
                     && t.GetCustomAttribute<SkillAttribute>() != null).OrderBy(t => t.FullName, StringComparer.Ordinal))
        {
            if (t.GetConstructor(Type.EmptyTypes) is null) continue;
            Add((ISkill)Activator.CreateInstance(t)!);
        }
        return this;
    }

    public SkillRegistry Add(ISkill skill)
    {
        if (string.IsNullOrWhiteSpace(skill.Spec.Name)) throw new ArgumentException("skill needs a name");
        lock (_lock)
        {
            if (_byName.TryGetValue(skill.Spec.Name, out var old)) _order.Remove(old);
            _byName[skill.Spec.Name] = skill;
            _order.Add(skill);
        }
        Added?.Invoke(skill);
        return this;
    }

    public bool TryGet(string name, out ISkill skill)
    {
        lock (_lock) return _byName.TryGetValue(Normalize(name), out skill!);
    }

    private static string Normalize(string name) => name.Trim().Replace(' ', '_').Replace('-', '_');

    /// <summary>Why the skill can't start right now, or null.</summary>
    public static string? CheckPreconditions(SkillContext ctx, SkillSpec spec, JsonElement args)
    {
        var pre = spec.Pre;
        if ((pre.Dimension & ctx.Dimension) == 0)
            return $"only works in the {(pre.Dimension == Dim.Nether ? "nether" : "overworld")}";
        var missing = pre.Primitives & ~ctx.Game.Supported;
        if (missing != Primitive.None) return $"needs client primitive {missing}";
        foreach (var group in pre.Items)
            if (!group.Any(id => ctx.Has(id))) return $"needs {pre.Needs}";
        if (pre.DaylightOnly && ctx.World.IsNight && !args.Flag("night_ok")) return "it's night (mobs spawn); pass night_ok=true to do it anyway";
        bool gate = pre.SurvivalGate || spec.Safety is SafetyClass.Edge or SafetyClass.Combat;
        if (gate && ctx.SurvivalVeto(spec.Name, spec.Safety) is { } veto) return veto;
        if (spec.Safety is SafetyClass.Edge or SafetyClass.Combat && NetherHazards.EdgeVeto(ctx) is { } ghast) return ghast;
        return null;
    }

    /// <summary>Acquires each missing item group (dimension/primitive problems first: nothing to fetch for those). Null = nothing done.</summary>
    private async Task<string?> AcquireAsync(SkillContext ctx, SkillSpec spec, CancellationToken ct)
    {
        if (AcquireMissing is not { } acquire || spec.Pre.Items.Count == 0) return null;
        if ((spec.Pre.Dimension & ctx.Dimension) == 0 || (spec.Pre.Primitives & ~ctx.Game.Supported) != Primitive.None) return null;
        var notes = new List<string>();
        foreach (var group in spec.Pre.Items)
        {
            if (group.Any(id => ctx.Has(id))) continue;
            string? note;
            try { note = await acquire(ctx, spec, group, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) { note = null; ctx.Log?.Invoke($"acquire for {spec.Name} threw: {ex.Message}"); }
            if (note != null) notes.Add(note);
            if (!group.Any(id => ctx.Has(id))) break; // the precondition check reports it
        }
        return notes.Count == 0 ? null : string.Join("; ", notes);
    }

    /// <summary>Runs a skill by name. Never throws for game failures; "cancelled" when <paramref name="ct"/> fires.</summary>
    public async Task<SkillResult> RunAsync(SkillContext ctx, string name, JsonElement args, CancellationToken ct)
    {
        if (!TryGet(name, out var skill))
        {
            var near = SkillCatalog.Search(Specs, name, 3).Select(s => s.Name).ToArray();
            return SkillResult.Fail($"no skill '{name}'" + (near.Length > 0 ? $"; try {string.Join(", ", near)}" : "; use find_skill"));
        }
        var spec = skill.Spec;
        if (spec.Status == SkillStatus.Stub) return SkillResult.Fail($"{spec.Name} is not implemented yet (stub): {spec.Summary}");
        string? acquired;
        try { acquired = await AcquireAsync(ctx, spec, ct).ConfigureAwait(false); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { return SkillResult.Fail($"{spec.Name} cancelled"); }
        if (CheckPreconditions(ctx, spec, args) is { } why) return SkillResult.Fail($"{spec.Name}: {why}" + (acquired != null ? $" ({acquired})" : ""));
        SkillResult r;
        IDisposable? scope = null;
        try
        {
            ct.ThrowIfCancellationRequested();
            try { scope = RunScope?.Invoke(spec.Name, args); } catch (Exception) { scope = null; }
            r = await skill.ExecuteAsync(ctx, args, ct).ConfigureAwait(false) ?? SkillResult.Fail("no result");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            r = SkillResult.Fail($"{spec.Name} cancelled");
        }
        catch (Exception ex)
        {
            ctx.Log?.Invoke($"skill {spec.Name} threw: {ex}");
            r = SkillResult.Fail($"{spec.Name} error: {ex.GetType().Name}: {ex.Message.Replace('\n', ' ')}");
        }
        finally
        {
            scope?.Dispose();
        }
        if (acquired != null && r.Ok) r = r with { Text = acquired + "; " + r.Text };
        var text = r.Text.Replace('\r', ' ').Replace('\n', ' ').Trim();
        int cap = ctx.Options.MaxResultChars;
        if (text.Length > cap) text = text[..(cap - 3)].TrimEnd() + "...";
        return r with { Text = text };
    }
}
