using System.Globalization;
using System.Text.Json.Nodes;

namespace MineClaw.Hive.Story;

/// <summary>
/// Recreation (docs/web/story-mode.md "Idle behaviour"): what a pawn does with nothing to do, in its free hours and while it rests after
/// failures. The director offers one leg at a time (kind <c>idle</c>): an activity, a safe cell of the home area reserved for the pawn
/// with room around it, where to look while it lingers. The pawn script runs it (<c>story.idle</c>: walk, look about, linger), cut the
/// moment real work comes up. Activities, never the same one twice in a row when another is possible:
/// <list type="bullet">
/// <item><c>wander</c>: a stroll (always possible).</item>
/// <item><c>socialise</c>: 2–3 blocks from another idle pawn, facing it; that pawn turns to it on its next leg.</item>
/// <item><c>stargaze</c>: at night, under open sky by home, looking up and slowly round.</item>
/// <item><c>sit</c>: beside a chair (a stairs block) or two blocks from a workbench or chest (never in front of it), facing it.</item>
/// <item><c>garden</c>: beside a growing zone or flowers, looking at them.</item>
/// <item><c>sunset</c>: at dusk (18:00), at the west edge of the home area, facing west.</item>
/// </list>
/// The colony option <see cref="ColonyMeta.Idle"/> picks recreation (default), wander (strolls only) or stay (hold where they are).
/// </summary>
public sealed partial class StoryColony
{
    public const string IdleRecreation = "recreation", IdleWander = "wander", IdleStay = "stay";
    public static readonly IReadOnlyList<string> IdleModes = [IdleRecreation, IdleWander, IdleStay];

    /// <summary>One leg on offer: the activity, its cell, where to look, its words for the label, the pawn it is with.</summary>
    private sealed record Leg(string Act, P3 At, IReadOnlyList<(double X, double Y, double Z)> Look, string Text, PawnState? With = null);

    private readonly Dictionary<string, string> _lastAct = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Who turned to whom: the pawn named in the key prefers to answer that pawn on its next leg (until the time).</summary>
    private readonly Dictionary<string, (string Pawn, DateTimeOffset Until)> _socialWith = new(StringComparer.OrdinalIgnoreCase);

    private static readonly HashSet<int> Chairs = [53, 67, 108, 109, 114, 128, 134, 135, 136, 156, 163, 164, 180, 203];
    private static readonly HashSet<int> Flowers = [37, 38, 175];

    /// <summary>The activity a pawn did last (tests, the state's job label carries it too).</summary>
    internal string? LastActivity(string pawn) { lock (_lock) return _lastAct.GetValueOrDefault(pawn); }

    /// <summary>
    /// The pawn's idle leg, or null when there is nowhere to go (no home, nothing loaded, another dimension) or the colony says stay.
    /// </summary>
    /// <param name="why">"work" (nothing to do in its work hours), "free" (its free hours) or "rest" (its back-off after failures).</param>
    private ActiveJob? IdleJob(PawnState p, PawnLive live, string why, int hour)
    {
        var mode = IdleModes.Contains(Meta.Idle) ? Meta.Idle : IdleRecreation;
        if (mode == IdleStay) return null;
        if (World is not { } w || live.Dim != w.Dimension || Home() is not { } home) return null;
        var from = live.Block;
        return IdlePick(p, mode, hour, w, home, from) is { } leg ? IdleLegJob(p, leg, why, from) : null;
    }

    /// <summary>The offers (a wander; with recreation: company, the stars at night, a seat, a garden, the sunset at six) in a shuffled
    /// order, the last activity only when nothing else is possible.</summary>
    private Leg? IdlePick(PawnState p, string mode, int hour, IStoryWorld w, P3 home, P3 from)
    {
        bool night = StoryCharacter.Night(hour);
        var busy = Busy();
        var taken = Taken(p.Name);
        bool Free(P3 c) => c != from && !busy(c) && !Skipped("wander:" + c);
        var day = IdleCells(false);
        var cells = night ? IdleCells(true) : day;
        // After dark with nothing indoors or lit: potter about right by home (never off into the dark).
        if (night && cells.Count == 0) cells = day.Where(c => StoryWander.Gap(c, home) <= 5).ToList();
        var open = cells.Where(Free).ToList();

        var offers = new List<Func<Leg?>> { () => WanderLeg(open, taken, from) };
        if (mode == IdleRecreation)
        {
            offers.Add(() => SocialLeg(p, open, taken, w));
            if (night) offers.Add(() => StarLeg(day.Where(Free), taken, home, w));
            offers.Add(() => SitLeg(open, taken, w));
            offers.Add(() => GardenLeg(open, taken, w));
            if (!night && hour == 18) offers.Add(() => SunsetLeg(open, taken));
        }
        return VariedLeg(offers, _lastAct.GetValueOrDefault(p.Name));
    }

    /// <summary>Variety: the offers shuffled, the first that gives a leg; the last activity only when nothing else is possible.</summary>
    private Leg? VariedLeg(List<Func<Leg?>> offers, string? last)
    {
        Leg? repeat = null;
        foreach (var offer in offers.OrderBy(_ => Rng.Next()))
        {
            if (offer() is not { } l) continue;
            if (l.Act == last && offers.Count > 1) { repeat ??= l; continue; }
            return l;
        }
        return repeat;
    }

    private ActiveJob IdleLegJob(PawnState p, Leg leg, string why, P3 from)
    {
        int linger = leg.Act == "wander" ? Rng.Next(4, 13) : Rng.Next(8, 17);
        var job = Job("idle", "idle", IdleLabel(leg, why), leg.At, null, "wander", why, "idle", j =>
        {
            j["s"] = linger;
            j["dist"] = 0;
            j["wander"] = true;
            j["act"] = leg.Act;
            if (leg.Look.Count > 0)
                j["look"] = new JsonArray(leg.Look.Select(l => (JsonNode)string.Create(CultureInfo.InvariantCulture, $"{l.X:0.##},{l.Y:0.##},{l.Z:0.##}")).ToArray());
            if (leg.With is { } q) j["with"] = q.Name;
        }, xp: 0);
        Reserve(p.Name, leg.At, job, TimeSpan.FromSeconds(leg.At.Dist(from) / 3.0 + linger + 20));
        _lastAct[p.Name] = leg.Act;
        if (leg.With is { } partner) _socialWith[partner.Name] = (p.Name, Now + TimeSpan.FromSeconds(40));
        return job;
    }

    private static string IdleLabel(Leg leg, string why) => leg.Act == "wander"
        ? why switch { "free" => "Free time (wandering)", "rest" => "Resting (wandering)", _ => "Idle (wandering)" }
        : (why == "rest" ? "Resting: " : "Recreation: ") + leg.Text;

    private Leg? PickLeg(List<P3> cells, List<P3> taken, string act, Func<P3, IReadOnlyList<(double, double, double)>> look, string text,
        double[]? rooms = null)
    {
        var ok = WithRoom(cells, taken, out _, rooms);
        if (ok.Count == 0) return null;
        var at = ok[Rng.Next(ok.Count)];
        return new Leg(act, at, look(at), text);
    }

    /// <summary>A stroll: somewhere 3..12 blocks off when there is such a cell, else (far from home) one of the nearest.</summary>
    private Leg? WanderLeg(List<P3> open, List<P3> taken, P3 from)
    {
        var ok = WithRoom(open, taken, out _);
        if (ok.Count == 0) return null;
        var near = ok.Where(c => StoryWander.Gap(c, from) is >= 3 and <= 12).ToList();
        if (near.Count == 0) near = ok.OrderBy(c => c.Dist(from)).Take(8).ToList();
        var to = near[Rng.Next(near.Count)];
        // A glance either side on arrival.
        return new Leg("wander", to, [(to.X + 0.5 + Rng.Next(-6, 7), to.Y + 1.6, to.Z + 0.5 + Rng.Next(-6, 7))], "wandering");
    }

    /// <summary>2–3 blocks from another pawn that is idle too (the one that turned to this pawn first), facing it.</summary>
    private Leg? SocialLeg(PawnState p, List<P3> open, List<P3> taken, IStoryWorld w)
    {
        var others = new List<(PawnState Q, P3 At)>();
        foreach (var q in Pawns.Values)
        {
            if (q == p || !_current.TryGetValue(q.Name, out var cur) || cur.Source != "wander") continue;
            var l = Host.Live(q.Name);
            if (!l.Online || l.Dim != w.Dimension) continue;
            var at = _spots.TryGetValue(q.Name, out var s) && SpotLive(q.Name, s) ? s.At : l.Block;
            others.Add((q, at));
        }
        if (others.Count == 0) return null;
        others = others.OrderBy(_ => Rng.Next()).ToList();
        // The one that turned to this pawn first gets an answer.
        if (_socialWith.TryGetValue(p.Name, out var asked) && asked.Until > Now)
            others = others.OrderBy(o => o.Q.Name.Equals(asked.Pawn, StringComparison.OrdinalIgnoreCase) ? 0 : 1).ToList();
        foreach (var (q, at) in others)
        {
            var near = open.Where(c => StoryWander.Gap(c, at) is >= 2 and <= 3.2).ToList();
            // Personal space holds: at least 2 from everyone (the partner included), never closer.
            var ok = WithRoom(near, taken, out _, [2.5, 2.0]);
            if (ok.Count == 0) continue;
            var to = ok[Rng.Next(ok.Count)];
            return new Leg("socialise", to, [(at.X + 0.5, at.Y + 1.6, at.Z + 0.5)], $"socialising with {q.Display}", q);
        }
        return null;
    }

    /// <summary>At night: under open sky by home (a home zone, or 8 blocks round the primary home), looking up and slowly round.</summary>
    private Leg? StarLeg(IEnumerable<P3> cells, List<P3> taken, P3 home, IStoryWorld w)
    {
        var sky = cells.Where(c => !StoryWander.Roofed(w, c) && OpenSky(w, c)
            && (StoryWander.Gap(c, home) <= 8 || Zones.Any(z => z.Kind == "home" && z.Area.ContainsXZ(c.X, c.Z)))).ToList();
        return PickLeg(sky, taken, "stargaze", c =>
        [
            (c.X + 8.5, c.Y + 30, c.Z + 0.5), (c.X + 0.5, c.Y + 30, c.Z + 8.5), (c.X - 7.5, c.Y + 30, c.Z + 0.5), (c.X + 0.5, c.Y + 30, c.Z - 7.5),
        ], "stargazing");
    }

    /// <summary>Nothing at all over the head up to the world's top (stars to see).</summary>
    private static bool OpenSky(IStoryWorld w, P3 c)
    {
        for (int y = c.Y + 2; y < w.Height; y++)
            if (w.Block(c.X, y, c.Z).Id != 0) return false;
        return true;
    }

    /// <summary>Beside a chair (stairs), else two blocks from a workbench or chest (never in front of it), facing it.</summary>
    private Leg? SitLeg(List<P3> open, List<P3> taken, IStoryWorld w)
    {
        var chairs = new List<(P3 Cell, P3 Chair)>();
        foreach (var c in open)
            foreach (var (dx, dz) in new[] { (1, 0), (-1, 0), (0, 1), (0, -1) })
                if (Chairs.Contains(w.Block(c.X + dx, c.Y, c.Z + dz).Id)) { chairs.Add((c, c.Add(dx, 0, dz))); break; }
        if (chairs.Count > 0)
        {
            var ok = WithRoom(chairs.Select(x => x.Cell), taken, out _);
            if (ok.Count > 0)
            {
                var to = ok[Rng.Next(ok.Count)];
                var chair = chairs.First(x => x.Cell == to).Chair;
                return new Leg("sit", to, [(chair.X + 0.5, chair.Y + 0.5, chair.Z + 0.5)], "resting by a chair");
            }
        }
        var tables = Benches.Select(b => (b.Pos, b.Kind == "furnace" ? "the furnace" : "the crafting table"))
            .Concat(Storages.Select(s => (s.Chest, "the chests"))).ToList();
        if (tables.Count == 0) return null;
        var by = new List<(P3 Cell, P3 Thing, string Name)>();
        foreach (var c in open)
            foreach (var (pos, name) in tables)
                if (Math.Max(Math.Abs(c.X - pos.X), Math.Abs(c.Z - pos.Z)) == 2 && Math.Abs(c.Y - pos.Y) <= 1) { by.Add((c, pos, name)); break; }
        var fit = WithRoom(by.Select(x => x.Cell), taken, out _);
        if (fit.Count == 0) return null;
        var at = fit[Rng.Next(fit.Count)];
        var t = by.First(x => x.Cell == at);
        return new Leg("sit", at, [(t.Thing.X + 0.5, t.Thing.Y + 0.5, t.Thing.Z + 0.5)], $"sitting by {t.Name}");
    }

    /// <summary>Beside a growing zone (just outside it) or flowers, looking at them.</summary>
    private Leg? GardenLeg(List<P3> open, List<P3> taken, IStoryWorld w)
    {
        var farms = Zones.Where(z => z.Kind == "farm").Select(z => z.Area).ToList();
        var by = new List<(P3 Cell, (double, double, double) Look)>();
        foreach (var c in open)
        {
            foreach (var a in farms)
            {
                int dx = c.X < a.X1 ? a.X1 - c.X : c.X > a.X2 ? c.X - a.X2 : 0, dz = c.Z < a.Z1 ? a.Z1 - c.Z : c.Z > a.Z2 ? c.Z - a.Z2 : 0;
                if (Math.Max(dx, dz) is >= 1 and <= 2) { by.Add((c, ((a.X1 + a.X2) / 2.0 + 0.5, a.Y1 + 1, (a.Z1 + a.Z2) / 2.0 + 0.5))); goto next; }
            }
            foreach (var (dx, dz) in new[] { (1, 0), (-1, 0), (0, 1), (0, -1) })
                if (Flowers.Contains(w.Block(c.X + dx, c.Y, c.Z + dz).Id)) { by.Add((c, (c.X + dx + 0.5, c.Y + 0.3, c.Z + dz + 0.5))); break; }
            next:;
        }
        var ok = WithRoom(by.Select(x => x.Cell), taken, out _);
        if (ok.Count == 0) return null;
        var at = ok[Rng.Next(ok.Count)];
        return new Leg("garden", at, [by.First(x => x.Cell == at).Look], "visiting the garden");
    }

    /// <summary>At dusk: the west edge of the home area, facing west.</summary>
    private Leg? SunsetLeg(List<P3> open, List<P3> taken)
    {
        if (open.Count == 0) return null;
        int edge = open.Min(c => c.X);
        var west = open.Where(c => c.X <= edge + 2).ToList();
        return PickLeg(west, taken, "sunset", c => [(c.X - 40.5, c.Y + 4, c.Z + 0.5)], "watching the sunset");
    }

    /// <summary>The colony's idle option (recreation | wander | stay); null keeps it.</summary>
    public string? SetIdle(string? mode)
    {
        if (mode == null) return null;
        if (!IdleModes.Contains(mode)) return $"idle must be {string.Join("|", IdleModes)}";
        lock (_lock)
        {
            Meta.Idle = mode;
            Touch();
        }
        return null;
    }
}
