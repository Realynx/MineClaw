using System.Globalization;
using MineClaw.Agent.Budget;
using MineClaw.Agent.Llm;
using MineClaw.Agent.Perception;
using MineClaw.Render;
using MineClaw.World;
using Microsoft.Extensions.Logging;

namespace MineClaw.Bot.Perception.Vision;

/// <summary>One perception look: the rendered sheet, the question and what the verifier needs.</summary>
public sealed record PerceptionLook(VisionImage Sheet, string Question, IBlockAccess World, RoleMap Roles, (int X, int Y, int Z) Focus, int Radius,
    string Source = "see");

/// <summary>
/// The perception tier at runtime: sheet + question → <see cref="IPerceptionModel"/> → strict JSON facts → voxel check →
/// ≈100 tokens of verified text for the brain (never the image). Answers are cached by (view hash, question); calls are booked
/// in the budget ledger under the "perception" tier, timed and counted (<see cref="PerceptionTelemetry"/>); a rolling hourly
/// cap bounds spend. Thread-safe.
/// </summary>
public sealed class PerceptionService
{
    private readonly IPerceptionModel? _model;
    private readonly PerceptionOptions _o;
    private readonly UsageTracker? _usage;
    private readonly PriceTable _prices;
    private readonly TimeProvider _time;
    private readonly ILogger? _log;
    private readonly object _lock = new();
    private readonly LinkedList<(ulong Key, VerifiedAnswer Answer)> _cache = new();
    private readonly Queue<DateTimeOffset> _calls = new(), _auto = new();
    private readonly HashSet<string> _lowValue = new();

    public PerceptionService(IPerceptionModel? model, PerceptionOptions options, UsageTracker? usage = null, PriceTable? prices = null,
        PerceptionTelemetry? telemetry = null, TimeProvider? time = null, ILogger? log = null)
    {
        _model = model;
        _o = options;
        _usage = usage;
        _prices = prices ?? usage?.Prices ?? new PriceTable();
        _time = time ?? TimeProvider.System;
        _log = log;
        Telemetry = telemetry ?? new PerceptionTelemetry(log, null, time);
        // Price a non-Anthropic perception model: configured price, else Ollama ($0 by default); Anthropic ids are in the table.
        if (model != null && !_prices.Models.ContainsKey(model.Model))
        {
            if (options.InputPerMTok > 0 || options.OutputPerMTok > 0)
                _prices.Models[model.Model] = ModelPrice.Standard(options.InputPerMTok, options.OutputPerMTok);
            else if (model.Provider == PerceptionProviders.Ollama) _prices.OllamaModels.Add(model.Model);
        }
    }

    public PerceptionOptions Options => _o;
    public PerceptionTelemetry Telemetry { get; }
    public IPerceptionModel? Model => _model;

    /// <summary>The perception path is on (a model is configured and the mode isn't "direct").</summary>
    public bool Available => _model != null && !_o.Direct;

    public static ulong Key(ulong viewKey, string question)
    {
        ulong h = viewKey * 1099511628211UL;
        unchecked { foreach (char c in Norm(question)) h = (h ^ c) * 1099511628211UL; }
        return h;
    }

    /// <summary>A question's shape (numbers masked): "is 4,64,6 free" and "is 7,62,9 free" are one shape.</summary>
    public static string Shape(string q) => Norm(System.Text.RegularExpressions.Regex.Replace(q, @"-?\d+", "#"));

    /// <summary>A question shape whose model answer had nothing checkable (0 claims verified): answer it from voxels next time.</summary>
    public bool LowValue(string question) { lock (_lock) return _lowValue.Contains(Shape(question)); }

    /// <summary>An exact answer from the voxels/roles (zero tokens): counted, never cached (it is cheaper to recompute).</summary>
    public VerifiedAnswer Exact(string question, string text, string source)
    {
        Telemetry.RecordExact(source, question);
        return new VerifiedAnswer(question, null, [], 0, 0, 0, 0, 0, text) { ModelText = text, Exact = true };
    }

    private static string Norm(string q) => string.Join(' ', q.ToLowerInvariant().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).TrimEnd('?', '.', '!');

    /// <summary>A cached answer for this view + question (counts a cache hit), else null.</summary>
    public VerifiedAnswer? Cached(ulong viewKey, string question)
    {
        ulong k = Key(viewKey, question);
        lock (_lock)
            for (var n = _cache.First; n != null; n = n.Next)
                if (n.Value.Key == k)
                {
                    _cache.Remove(n);
                    _cache.AddFirst(n);
                    var hit = n.Value.Answer with { Cached = true, Text = n.Value.Answer.Text.EndsWith(" (cached)", StringComparison.Ordinal) ? n.Value.Answer.Text : n.Value.Answer.Text + " (cached)" };
                    Telemetry.Record(hit, "cache", 0);
                    return hit;
                }
        return null;
    }

    /// <summary>Null = a call may run now; else why not (hourly cap).</summary>
    public string? Budget(bool auto)
    {
        var now = _time.GetUtcNow();
        lock (_lock)
        {
            Prune(_calls, now);
            Prune(_auto, now);
            if (_calls.Count >= Math.Max(0, _o.PerHour)) return $"perception budget used ({_o.PerHour}/h)";
            if (auto && _auto.Count >= Math.Max(0, _o.Auto.PerHour)) return $"auto-vision budget used ({_o.Auto.PerHour}/h)";
        }
        return null;
    }

    private static void Prune(Queue<DateTimeOffset> q, DateTimeOffset now)
    {
        while (q.Count > 0 && now - q.Peek() > TimeSpan.FromHours(1)) q.Dequeue();
    }

    /// <summary>Asks the perception model (or the cache), verifies the answer and books it. Never throws for model failures.</summary>
    public async Task<VerifiedAnswer> AskAsync(PerceptionLook look, CancellationToken ct)
    {
        if (Cached(look.Sheet.Key, look.Question) is { } hit) return hit;
        if (_model == null) return new VerifiedAnswer(look.Question, null, [], 0, 0, 0, 0, 0, "[vision] no perception model configured", Error: "no model");
        bool auto = look.Source.StartsWith("auto", StringComparison.Ordinal);
        if (Budget(auto) is { } over) { Telemetry.Skip(); return new VerifiedAnswer(look.Question, null, [], 0, 0, 0, 0, 0, "[vision] " + over, Error: over); }
        lock (_lock)
        {
            var now = _time.GetUtcNow();
            _calls.Enqueue(now);
            if (auto) _auto.Enqueue(now);
        }
        var q = new PerceptionQuery(look.Sheet.Png, look.Sheet.Width, look.Sheet.Height, look.Question, look.Sheet.Legend);
        PerceptionResult r;
        try { r = await _model.AskAsync(q, ct).ConfigureAwait(false); }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            r = new PerceptionResult([], null, "", LlmUsage.Zero, _model.Model, _model.Provider, 0, ex.GetType().Name + ": " + ex.Message);
        }
        decimal usd = 0;
        if (r.Usage != LlmUsage.Zero)
        {
            try { usd = _usage?.Record(r.Model, r.Usage, label: UsageTracker.PerceptionLabel) ?? _prices.Cost(r.Model, r.Usage); }
            catch (Exception ex) { _log?.LogDebug(ex, "perception usage not booked"); }
        }
        var verified = PerceptionVerifier.Verify(look.Question, r, look.World, look.Roles, look.Focus, look.Radius);
        if (verified.Error == null && verified.Checked == 0)
        {
            lock (_lock) _lowValue.Add(Shape(look.Question));
            _log?.LogInformation("perception: no checkable claim for \"{Q}\" (low value): that question shape is answered from voxels next time", look.Question);
        }
        if (verified.Error == null)
        {
            var grounding = Grounding(look.World, look.Roles, look.Focus, look.Radius, verified.Facts);
            verified = verified with
            {
                Text = PerceptionVerifier.Format(look.Question, verified.Answer, verified.Facts, verified.Dropped, false, grounding),
            };
            lock (_lock)
            {
                _cache.AddFirst((Key(look.Sheet.Key, look.Question), verified));
                while (_cache.Count > Math.Max(1, _o.CacheEntries)) _cache.RemoveLast();
            }
        }
        Telemetry.Record(verified, look.Source, usd);
        return verified;
    }

    /// <summary>
    /// Deterministic role grounding near the focus, never from the model: "roles: G 5,63,7 6,63,7; P 2,66,0..2,68,1" (≤3 cells per
    /// role, cells the facts already name are left out). Only roles that decide what to mine or where not to walk.
    /// </summary>
    public static string? Grounding(IBlockAccess w, RoleMap roles, (int X, int Y, int Z) f, int radius, IReadOnlyList<VerifiedFact> facts)
    {
        var named = facts.SelectMany(x => x.Coords).ToHashSet();
        var found = RoleCellsNear(w, roles, f, Math.Min(radius, 8));
        var parts = new List<string>();
        if (AirFocusFaces(w, f) is { } faces) parts.Add(faces);
        var roleParts = new List<string>();
        foreach (var (role, list) in found.OrderBy(kv => kv.Key))
        {
            var cells = list.Where(c => !named.Contains(c)).OrderBy(c => Math.Abs(c.X - f.X) + Math.Abs(c.Y - f.Y) + Math.Abs(c.Z - f.Z)).ToList();
            if (cells.Count == 0) continue;
            roleParts.Add($"{BlockRoles.Glyph(role)} {BlockRoles.Name(role)} {RoleCellsText(role, cells)}");
        }
        if (roleParts.Count > 0) parts.Add("roles: " + string.Join("; ", roleParts));
        return parts.Count == 0 ? null : string.Join(" | ", parts);
    }

    /// <summary>The gen cells, portal and never-break blocks within <paramref name="r"/> (±3 in y) of the focus; air only on the
    /// focus's and the floor's levels.</summary>
    private static Dictionary<BlockRole, List<(int X, int Y, int Z)>> RoleCellsNear(IBlockAccess w, RoleMap roles, (int X, int Y, int Z) f, int r)
    {
        var found = new Dictionary<BlockRole, List<(int X, int Y, int Z)>>();
        for (int y = f.Y - 3; y <= f.Y + 3; y++)
            for (int z = f.Z - r; z <= f.Z + r; z++)
                for (int x = f.X - r; x <= f.X + r; x++)
                {
                    if (!w.IsLoaded(x, z)) continue;
                    if (w.GetBlock(x, y, z) == 0 && y != f.Y && y != f.Y - 1) continue;
                    if (GroundingRole(roles, x, y, z) is not { } role) continue;
                    if (!found.TryGetValue(role, out var list)) found[role] = list = new();
                    list.Add((x, y, z));
                }
        return found;
    }

    /// <summary>The cell's role when grounding reports it (gen cell, portal, never-break), else null (a throwing map too).</summary>
    private static BlockRole? GroundingRole(RoleMap roles, int x, int y, int z)
    {
        BlockRole role;
        try { role = roles(x, y, z); } catch (Exception) { return null; }
        return role is BlockRole.GenCell or BlockRole.Portal or BlockRole.NeverBreak ? role : null;
    }

    private static readonly (string Name, int Dx, int Dy, int Dz)[] FaceDirs =
        [("E", 1, 0, 0), ("W", -1, 0, 0), ("S", 0, 0, 1), ("N", 0, 0, -1), ("below", 0, -1, 0), ("above", 0, 1, 0)];

    /// <summary>An air focus (a hole the bot stands in, a cell to fill): its solid faces, the thing models misread most (live
    /// 7,62,9). Null when the focus isn't loaded air.</summary>
    private static string? AirFocusFaces(IBlockAccess w, (int X, int Y, int Z) f)
    {
        if (!w.IsLoaded(f.X, f.Z) || w.GetBlock(f.X, f.Y, f.Z) != 0) return null;
        var faces = new List<string>();
        foreach (var (name, dx, dy, dz) in FaceDirs)
        {
            int id = w.GetBlock(f.X + dx, f.Y + dy, f.Z + dz);
            if (id != 0 && w.Info(id).Solid) faces.Add(string.Create(CultureInfo.InvariantCulture, $"{name} {f.X + dx},{f.Y + dy},{f.Z + dz}"));
        }
        return faces.Count == 0 ? "faces: none (nothing to place against)" : "faces: " + string.Join(", ", faces);
    }

    /// <summary>A role's cells, nearest first: a big portal as its box, else up to three cells and "+n" more.</summary>
    private static string RoleCellsText(BlockRole role, List<(int X, int Y, int Z)> cells) =>
        role == BlockRole.Portal && cells.Count > 3
            ? $"{cells.Min(c => c.X)},{cells.Min(c => c.Y)},{cells.Min(c => c.Z)}..{cells.Max(c => c.X)},{cells.Max(c => c.Y)},{cells.Max(c => c.Z)}"
            : string.Join(" ", cells.Take(3).Select(c => string.Create(CultureInfo.InvariantCulture, $"{c.X},{c.Y},{c.Z}"))) + (cells.Count > 3 ? $" +{cells.Count - 3}" : "");
}
