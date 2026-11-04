using MineClaw.World;

namespace MineClaw.Bot.Perception;

/// <summary>
/// One recognised piece of another player's body language (the <c>player.gesture</c> event): <paramref name="Kind"/> nod | shake |
/// bob | wave | jump | spin | stare | point, a 0..1 <paramref name="Strength"/>, <paramref name="At"/> = the body they aimed it at
/// ("me", another bot's login) or null, the pointed <paramref name="Target"/> (point only: a block or an entity) and where they stood.
/// </summary>
public sealed record SeenGesture(string Player, string Kind, double Strength, string? At, Vec3d Pos, DateTimeOffset When,
    BlockPos? Target = null, string? TargetName = null, string? TargetKind = null);

/// <summary>What a look ray rests on: <c>me</c> / <c>body</c> (another bot) / <c>block</c> / <c>entity</c>, its name, cell and distance.</summary>
public readonly record struct LookHit(string Kind, string Name, BlockPos? Pos, double Dist)
{
    public string Key => Kind + ":" + Name + (Pos is { } p ? $"@{p.X},{p.Y},{p.Z}" : "");
    public bool IsBody => Kind is "me" or "body";
}

/// <summary>
/// Deterministic gesture recognition over each nearby player's recent motion (zero tokens; Fox 10-29: "nodding my head yes or no,
/// or other things, for the AI to observe the players around it"). Fed at packet rate (<see cref="Observe"/>: every entity
/// look/move/head-look/metadata update), on arm swings and hits, and polled ~4×/s (<see cref="Poll"/>: stares, waves settling,
/// points), with explicit timestamps so tests replay traces. Tuned against false positives first:
/// <list type="bullet">
/// <item><b>nod / shake</b>: ≥ 4 alternating strokes (2 down-up / left-right cycles) of ≥ <see cref="MinStroke"/>° (zigzag with
/// that hysteresis, so jitter never counts) within <see cref="Window"/>, each stroke ≤ 0.7 s, median stroke 10-80°; the other axis
/// steady (range ≤ max(10°, half the stroke), ≤ 25°); standing (≤ 1.5 blocks moved); no dig/place/hit swing and ≤ 1 swing in the
/// window (mining looks up and down between cells). Shake reads the head yaw where the version sends it (1.12.2 Entity Head Look),
/// else the look yaw (b1.8.1 has no head packet; a player's look yaw is their head).</item>
/// <item><b>bob</b> (crouch-bob): ≥ 3 sneak toggles in 1.5 s (a single crouch is 2).</item>
/// <item><b>wave</b>: ≥ 3 clean swings (empty air: no block in reach, no use-item, no entity hurt within 0.5 s) in 1.5 s, decided
/// 0.3 s after the last one so a hit's hurt status can still disqualify it.</item>
/// <item><b>jump</b> (jump-spam): ≥ 3 hops (rise ≥ 0.5 from a floor and back within 1.1 s) in 3 s within 3 blocks.</item>
/// <item><b>spin</b>: ≥ 320° of one-way yaw in ≤ 1.3 s (≤ 45° back).</item>
/// <item><b>stare</b>: the look ray on a body (<see cref="LookHit.IsBody"/>) within 8 blocks for ≥ 1 s; once per gaze.</item>
/// <item><b>point</b>: a steady look ≥ 1.5 s at a block or entity out of reach (&gt; 4.5: a near one is a punch), then a clean swing
/// or a nod within 2 s; the target is exposed.</item>
/// </list>
/// Each gesture instance fires once: its evidence is consumed and a per player+kind <see cref="Cooldown"/> holds (stares longer).
/// <c>At</c>: the body the median look of the gesture's window rests on, else any sample's, else a stare within 3 s.
/// Thread-safe (one lock); listeners get the results, never the lock.
/// </summary>
public sealed class GestureDetector
{
    /// <summary>One motion sample: yaw unwrapped (continuous through ±180), pitch in −90..90, ground null = unknown.</summary>
    public readonly record struct Sample(DateTimeOffset T, Vec3d Pos, double Yaw, double Pitch, bool Sneak, bool? Ground, string? Aim);

    private sealed class Track
    {
        public readonly List<Sample> S = new();
        public readonly List<(DateTimeOffset T, bool Clean)> Swings = new();
        public readonly List<DateTimeOffset> Toggles = new();
        public readonly Dictionary<string, DateTimeOffset> Consumed = new(StringComparer.Ordinal);
        public readonly Dictionary<string, DateTimeOffset> Fired = new(StringComparer.Ordinal);
        public bool? LastSneak;
        public double? LastRawYaw;
        public double YawU;
        public string? GazeKey;
        public DateTimeOffset GazeSince;
        public bool StareFired;
        public (string? Body, DateTimeOffset At) LastBodyGaze;
        public (LookHit Hit, DateTimeOffset Until)? PointReady;
        public DateTimeOffset LastSeen;
    }

    private readonly object _lock = new();
    private readonly Dictionary<string, Track> _tracks = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Nod/shake/wave/bob evidence window (the 1.5 s gesture plus a packet of latency).</summary>
    public TimeSpan Window { get; init; } = TimeSpan.FromSeconds(1.6);
    /// <summary>Smallest counted nod/shake stroke (b1.8.1 sends a look change only at ≥ 8/256 turn = 11.25°: a 15° nod reads ≈ 12°).</summary>
    public double MinStroke { get; init; } = 10;
    /// <summary>One event per player+kind per this long.</summary>
    public TimeSpan Cooldown { get; init; } = TimeSpan.FromSeconds(4);
    public TimeSpan StareCooldown { get; init; } = TimeSpan.FromSeconds(15);
    public TimeSpan StareHold { get; init; } = TimeSpan.FromSeconds(1);
    public double StareBlocks { get; init; } = 8;
    public TimeSpan PointHold { get; init; } = TimeSpan.FromSeconds(1.5);
    /// <summary>A pointed target must be further than this (closer is the player's reach: a swing there is a punch).</summary>
    public double PointMinBlocks { get; init; } = 4.5;

    private static readonly TimeSpan Keep = TimeSpan.FromSeconds(3.5);

    /// <summary>Normalises a wire pitch (the byte angle reads 0..360: −30° arrives as 330°) to −90..90.</summary>
    public static double Pitch(float p)
    {
        double v = p % 360;
        if (v > 180) v -= 360;
        else if (v < -180) v += 360;
        return Math.Clamp(v, -90, 90);
    }

    /// <summary>
    /// A motion sample of <paramref name="player"/> (a look/move/metadata update); returns the gestures it completed (nod, shake,
    /// spin, jump, bob, and a point a nod confirmed). <paramref name="aim"/> = the body their look ray hits now (me / a bot), or null.
    /// </summary>
    public IReadOnlyList<SeenGesture> Observe(string player, DateTimeOffset t, Vec3d pos, float yaw, float pitch, bool sneak, bool? ground = null, string? aim = null)
    {
        var fired = new List<SeenGesture>();
        lock (_lock)
        {
            var k = TrackOf(player, t);
            UnwrapYaw(k, yaw);
            var s = new Sample(t, pos, k.YawU, Pitch(pitch), sneak, ground, aim);
            // An update arriving with the one before it (look + move in one b1.8.1 packet, the 1.12.2 head look right after the
            // look) is one sample.
            if (k.S.Count > 0 && (t - k.S[^1].T).TotalMilliseconds < 15) k.S[^1] = s;
            else k.S.Add(s);
            if (aim != null) k.LastBodyGaze = (aim, t);
            Prune(k, t);
            SneakLocked(k, t, sneak);
            DetectMotion(k, player, t, fired);
        }
        return fired;
    }

    /// <summary>Continuous yaw (no wrap at 0/360), so a spin keeps counting past a full turn.</summary>
    private static void UnwrapYaw(Track k, float yaw)
    {
        double raw = ((yaw % 360) + 360) % 360;
        if (k.LastRawYaw is { } last)
        {
            double d = raw - last;
            if (d > 180) d -= 360; else if (d < -180) d += 360;
            k.YawU += d;
        }
        else k.YawU = raw;
        k.LastRawYaw = raw;
    }

    /// <summary>The sample-driven detectors (caller holds _lock): nod else shake, spin, jump, bob, and a point a nod confirmed.</summary>
    private void DetectMotion(Track k, string player, DateTimeOffset t, List<SeenGesture> fired)
    {
        if (Oscillation(k, player, t, pitchAxis: true) is { } nod) fired.Add(nod);
        else if (Oscillation(k, player, t, pitchAxis: false) is { } shake) fired.Add(shake);
        if (Spin(k, player, t) is { } spin) fired.Add(spin);
        if (Jumps(k, player, t) is { } jump) fired.Add(jump);
        if (Bob(k, player, t) is { } bob) fired.Add(bob);
        if (fired.Any(g => g.Kind == "nod") && PointFrom(k, player, t) is { } point) fired.Add(point);
    }

    /// <summary>The sneak flag seen from another source (b1.8.1's crouch animation 104/105): a toggle when it differs.</summary>
    public IReadOnlyList<SeenGesture> SneakSeen(string player, DateTimeOffset t, bool on)
    {
        lock (_lock)
        {
            var k = TrackOf(player, t);
            SneakLocked(k, t, on);
            return Bob(k, player, t) is { } g ? [g] : [];
        }
    }

    /// <summary>An arm swing. <paramref name="clean"/> = into empty air (no block in reach, not a use-item): may be a wave or a point.</summary>
    public IReadOnlyList<SeenGesture> Swing(string player, DateTimeOffset t, bool clean)
    {
        lock (_lock)
        {
            var k = TrackOf(player, t);
            k.Swings.Add((t, clean));
            Prune(k, t);
            if (clean && PointFrom(k, player, t) is { } p)
            {
                k.Swings.RemoveAll(x => x.T >= t - TimeSpan.FromMilliseconds(1)); // the point's swing never counts toward a wave
                return [p];
            }
            return [];
        }
    }

    /// <summary>Something near this player got hurt (entity status 2): their swings of the last 0.5 s were hits, not waves.</summary>
    public void Struck(string player, DateTimeOffset t)
    {
        lock (_lock)
        {
            if (!_tracks.TryGetValue(player, out var k)) return;
            for (int i = 0; i < k.Swings.Count; i++)
                if (t - k.Swings[i].T <= TimeSpan.FromSeconds(0.5) && k.Swings[i].T <= t) k.Swings[i] = (k.Swings[i].T, false);
        }
    }

    /// <summary>
    /// The polled part (~4×/s): stares and points need time to pass with no packets (a still player sends none), and waves settle.
    /// <paramref name="look"/> = what the player's current look ray rests on (bodies, then blocks/entities), from their last sample.
    /// </summary>
    public IReadOnlyList<SeenGesture> Poll(DateTimeOffset now, Func<string, Sample, LookHit?>? look = null)
    {
        var fired = new List<SeenGesture>();
        lock (_lock)
        {
            foreach (var name in _tracks.Where(kv => now - kv.Value.LastSeen > TimeSpan.FromSeconds(30)).Select(kv => kv.Key).ToList())
                _tracks.Remove(name);
            foreach (var (player, k) in _tracks)
            {
                Prune(k, now);
                if (Wave(k, player, now) is { } w) fired.Add(w);
                if (look == null || k.S.Count == 0) continue;
                if (Gaze(k, player, now, look) is { } stare) fired.Add(stare);
            }
        }
        return fired;
    }

    /// <summary>Where the player's look rests now: a held gaze on a body is a stare (once per gaze); one on a far block or
    /// entity readies a point.</summary>
    private SeenGesture? Gaze(Track k, string player, DateTimeOffset now, Func<string, Sample, LookHit?> look)
    {
        var last = k.S[^1];
        LookHit? hit = null;
        try { hit = look(player, last); } catch (Exception) { }
        string key = hit?.Key ?? "";
        if (key != k.GazeKey) { k.GazeKey = key; k.GazeSince = now; k.StareFired = false; }
        if (hit is not { } h) return null;
        if (h.IsBody) return Stare(k, player, h, last, now);
        if (h.Dist > PointMinBlocks && now - k.GazeSince >= PointHold) k.PointReady = (h, now + TimeSpan.FromSeconds(2));
        return null;
    }

    private SeenGesture? Stare(Track k, string player, LookHit h, Sample last, DateTimeOffset now)
    {
        string body = h.Kind == "me" ? "me" : h.Name;
        k.LastBodyGaze = (body, now);
        if (k.StareFired || h.Dist > StareBlocks || now - k.GazeSince < StareHold || !Ready(k, "stare", now, StareCooldown)) return null;
        k.StareFired = true;
        double held = (now - k.GazeSince).TotalSeconds;
        return Fire(k, player, "stare", Math.Clamp(0.5 + 0.25 * (held - 1) + 0.03 * (StareBlocks - h.Dist), 0.3, 1), body, last.Pos, now);
    }

    // ------------------------------------------------------------------ detectors (caller holds _lock)

    private Track TrackOf(string player, DateTimeOffset t)
    {
        if (!_tracks.TryGetValue(player, out var k)) _tracks[player] = k = new Track();
        k.LastSeen = t;
        return k;
    }

    private static void Prune(Track k, DateTimeOffset t)
    {
        k.S.RemoveAll(s => t - s.T > Keep);
        if (k.S.Count > 128) k.S.RemoveRange(0, k.S.Count - 128);
        k.Swings.RemoveAll(s => t - s.T > Keep);
        k.Toggles.RemoveAll(s => t - s > Keep);
    }

    private static void SneakLocked(Track k, DateTimeOffset t, bool on)
    {
        if (k.LastSneak is { } was && was != on) k.Toggles.Add(t);
        k.LastSneak = on;
    }

    private bool Ready(Track k, string kind, DateTimeOffset t, TimeSpan? cooldown = null) =>
        !k.Fired.TryGetValue(kind, out var f) || t - f >= (cooldown ?? Cooldown);

    private DateTimeOffset From(Track k, string kind, DateTimeOffset t, TimeSpan window)
    {
        var from = t - window;
        return k.Consumed.TryGetValue(kind, out var c) && c > from ? c : from;
    }

    private SeenGesture Fire(Track k, string player, string kind, double strength, string? at, Vec3d pos, DateTimeOffset t,
        BlockPos? target = null, string? targetName = null, string? targetKind = null)
    {
        k.Fired[kind] = t;
        k.Consumed[kind] = t;
        return new SeenGesture(player, kind, Math.Round(Math.Clamp(strength, 0, 1), 2), at, pos, t, target, targetName, targetKind);
    }

    /// <summary>The body the gesture was aimed at: the median look of its samples, else any sample's aim, else a stare within 3 s.</summary>
    private static string? AtOf(Track k, List<Sample> w, DateTimeOffset t)
    {
        if (w.Count > 0)
        {
            var mid = w.OrderBy(s => s.Pitch).ElementAt(w.Count / 2);
            var midYaw = w.OrderBy(s => s.Yaw).ElementAt(w.Count / 2);
            // The sample nearest the median orientation stands for "where they looked while doing it".
            var c = w.MinBy(s => Math.Abs(s.Pitch - mid.Pitch) + Math.Abs(s.Yaw - midYaw.Yaw));
            if (c.Aim != null) return c.Aim;
            var any = w.Where(s => s.Aim != null).GroupBy(s => s.Aim).OrderByDescending(g => g.Count()).FirstOrDefault();
            if (any != null) return any.Key;
        }
        return k.LastBodyGaze.Body != null && t - k.LastBodyGaze.At <= TimeSpan.FromSeconds(3) ? k.LastBodyGaze.Body : null;
    }

    /// <summary>
    /// Zigzag turning points of <paramref name="v"/> with hysteresis <paramref name="h"/> (a reversal counts only once the value
    /// came back h from the extreme): the start, each confirmed extreme, and the last extreme.
    /// </summary>
    internal static List<int> Pivots(IReadOnlyList<double> v, double h)
    {
        var piv = new List<int>();
        if (v.Count == 0) return piv;
        piv.Add(0);
        int dir = 0, ext = 0;
        for (int i = 1; i < v.Count; i++)
        {
            if (dir == 0) FirstMove(v, i, h, ref dir, ref ext);
            else if (dir * (v[i] - v[ext]) > 0) ext = i;                        // further the same way: a new extreme
            else if (dir * (v[ext] - v[i]) >= h) { piv.Add(ext); dir = -dir; ext = i; } // came back h: the extreme was a turn
        }
        if (dir != 0) piv.Add(ext);
        return piv;
    }

    /// <summary>Before the first move of h: the direction once it comes, meanwhile the farthest sample from the start.</summary>
    private static void FirstMove(IReadOnlyList<double> v, int i, double h, ref int dir, ref int ext)
    {
        if (v[i] - v[0] >= h) { dir = 1; ext = i; }
        else if (v[0] - v[i] >= h) { dir = -1; ext = i; }
        else if (Math.Abs(v[i] - v[0]) > Math.Abs(v[ext] - v[0])) ext = i;
    }

    /// <summary>nod (pitch) or shake (yaw): see the class summary for every bound.</summary>
    private SeenGesture? Oscillation(Track k, string player, DateTimeOffset t, bool pitchAxis)
    {
        string kind = pitchAxis ? "nod" : "shake";
        if (!Ready(k, kind, t)) return null;
        var from = From(k, kind, t, Window);
        var w = k.S.Where(s => s.T >= from).ToList();
        if (w.Count < 5) return null;
        var main = w.Select(s => pitchAxis ? s.Pitch : s.Yaw).ToList();
        var piv = Pivots(main, MinStroke);
        int strokes = piv.Count - 1;
        if (strokes < 4) return null;
        // The strokes: the last up-to-6 (an ongoing gesture keeps its newest cycles).
        var use = piv.Skip(Math.Max(0, piv.Count - 7)).ToList();
        if (StrokeAmplitudes(w, main, use) is not { } amps) return null;
        amps.Sort();
        double a = amps[amps.Count / 2];
        if (a < MinStroke || a > 80) return null;
        var span = w.Skip(use[0]).Take(use[^1] - use[0] + 1).ToList();
        if (!HeadOnly(k, span, t, pitchAxis, a, out double r, out double maxR)) return null;
        double strength = 0.5 + 0.08 * (amps.Count - 4) + 0.25 * Math.Clamp((a - MinStroke) / 30, 0, 1) + 0.15 * (1 - r / maxR);
        var g = Fire(k, player, kind, strength, AtOf(k, span, t), span[^1].Pos, t);
        k.Consumed[pitchAxis ? "shake" : "nod"] = t; // one head gesture per motion
        return g;
    }

    /// <summary>Each stroke's amplitude between turning points; null when one took over 0.7 s (a slow look up then down).</summary>
    private static List<double>? StrokeAmplitudes(List<Sample> w, List<double> main, List<int> use)
    {
        var amps = new List<double>();
        for (int i = 1; i < use.Count; i++)
        {
            var from0 = i == 1 && use[0] == 0 ? w[RestEnd(main, use[1])].T : w[use[i - 1]].T;
            if ((w[use[i]].T - from0).TotalSeconds > 0.7) return null; // a slow look up then down is not a nod
            amps.Add(Math.Abs(main[use[i]] - main[use[i - 1]]));
        }
        return amps;
    }

    /// <summary>The window's first sample is no turning point: the stroke starts when the head left that rest (a player who
    /// stood still before nodding sent nothing in between).</summary>
    private static int RestEnd(List<double> main, int firstPivot)
    {
        int j = firstPivot - 1;
        while (j > 0 && Math.Abs(main[j] - main[0]) > 3) j--;
        return j;
    }

    /// <summary>Only the head moved: the other axis stayed (not looking around), the feet stayed (not walking while turning),
    /// and no work swings (digging / building: the head follows the work).</summary>
    private static bool HeadOnly(Track k, List<Sample> span, DateTimeOffset t, bool pitchAxis, double a, out double r, out double maxR)
    {
        var other = span.Select(s => pitchAxis ? s.Yaw : s.Pitch).ToList();
        r = other.Max() - other.Min();
        maxR = Math.Min(25, Math.Max(10, a * 0.5));
        if (r > maxR) return false; // looking around: both axes move
        if (Spread(span) > 1.5) return false; // walking while turning
        var sw = k.Swings.Where(x => x.T >= span[0].T - TimeSpan.FromSeconds(0.3) && x.T <= t).ToList();
        return sw.Count <= 1 && sw.All(x => x.Clean); // digging / building: the head follows the work
    }

    /// <summary>Horizontal extent (blocks) of the samples' positions.</summary>
    private static double Spread(List<Sample> w)
    {
        if (w.Count == 0) return 0;
        double minX = w.Min(s => s.Pos.X), maxX = w.Max(s => s.Pos.X), minZ = w.Min(s => s.Pos.Z), maxZ = w.Max(s => s.Pos.Z);
        return Math.Sqrt((maxX - minX) * (maxX - minX) + (maxZ - minZ) * (maxZ - minZ));
    }

    private SeenGesture? Spin(Track k, string player, DateTimeOffset t)
    {
        if (!Ready(k, "spin", t)) return null;
        var w = k.S.Where(s => s.T >= From(k, "spin", t, TimeSpan.FromSeconds(1.3))).ToList();
        if (w.Count < 3) return null;
        for (int i = 0; i < w.Count - 2; i++)
        {
            double net = w[^1].Yaw - w[i].Yaw;
            if (Math.Abs(net) < 320) continue;
            double back = 0;
            for (int j = i + 1; j < w.Count; j++)
            {
                double d = w[j].Yaw - w[j - 1].Yaw;
                if (Math.Sign(d) != Math.Sign(net)) back += Math.Abs(d);
            }
            if (back > 45) continue;
            double secs = Math.Max(0.05, (w[^1].T - w[i].T).TotalSeconds);
            return Fire(k, player, "spin", 0.6 + 0.2 * Math.Clamp((Math.Abs(net) - 320) / 360, 0, 1) + 0.2 * Math.Clamp(1.3 - secs, 0, 1),
                AtOf(k, [], t), w[^1].Pos, t);
        }
        return null;
    }

    private SeenGesture? Jumps(Track k, string player, DateTimeOffset t)
    {
        if (!Ready(k, "jump", t)) return null;
        var w = k.S.Where(s => s.T >= From(k, "jump", t, TimeSpan.FromSeconds(3))).ToList();
        if (w.Count < 6 || Spread(w) > 3) return null;
        int hops = 0;
        int i = 0;
        while (i < w.Count)
        {
            // A rise must start from a floor (when the floor is known).
            int land = w[i].Ground == false ? -1 : HopLanding(w, i);
            if (land >= 0) { hops++; i = land; }
            else i++;
        }
        if (hops < 3) return null;
        return Fire(k, player, "jump", 0.5 + 0.15 * (hops - 3), AtOf(k, w, t), w[^1].Pos, t);
    }

    /// <summary>The sample where a hop from sample <paramref name="i"/> landed back (rose ≥ 0.5 and came back within 0.25 inside
    /// 1.1 s, without first stepping down), or -1.</summary>
    private static int HopLanding(List<Sample> w, int i)
    {
        var b = w[i];
        bool rose = false;
        for (int j = i + 1; j < w.Count && (w[j].T - b.T).TotalSeconds <= 1.1; j++)
        {
            double y = w[j].Pos.Y;
            if (!rose)
            {
                if (y < b.Pos.Y - 0.25) return -1;   // stepped down
                if (y >= b.Pos.Y + 0.5) rose = true;
            }
            else if (y <= b.Pos.Y + 0.25 && y >= b.Pos.Y - 0.25) return j;
        }
        return -1;
    }

    private SeenGesture? Bob(Track k, string player, DateTimeOffset t)
    {
        if (!Ready(k, "bob", t)) return null;
        var from = From(k, "bob", t, TimeSpan.FromSeconds(1.5));
        int n = k.Toggles.Count(x => x >= from && x <= t);
        if (n < 3) return null;
        var w = k.S.Where(s => s.T >= from).ToList();
        return Fire(k, player, "bob", 0.5 + 0.12 * (n - 3), AtOf(k, w, t), k.S.Count > 0 ? k.S[^1].Pos : default, t);
    }

    private SeenGesture? Wave(Track k, string player, DateTimeOffset now)
    {
        if (!Ready(k, "wave", now)) return null;
        var from = From(k, "wave", now, TimeSpan.FromSeconds(1.5) + TimeSpan.FromSeconds(0.3));
        var sw = k.Swings.Where(x => x.T >= from).ToList();
        if (sw.Count < 3 || sw.Any(x => !x.Clean)) return null;   // any dig/hit swing in the burst: work, not a wave
        if ((now - sw[^1].T).TotalSeconds < 0.3) return null;       // wait for a hit's hurt status
        if ((sw[^1].T - sw[^3].T).TotalSeconds > 1.5) return null;
        var w = k.S.Where(s => s.T >= sw[0].T - TimeSpan.FromSeconds(0.5)).ToList();
        if (w.Count == 0 && k.S.Count > 0) w.Add(k.S[^1]);
        return Fire(k, player, "wave", 0.5 + 0.1 * (sw.Count - 3), AtOf(k, w, now), k.S.Count > 0 ? k.S[^1].Pos : default, now);
    }

    private SeenGesture? PointFrom(Track k, string player, DateTimeOffset t)
    {
        if (k.PointReady is not { } p || t > p.Until || !Ready(k, "point", t)) return null;
        k.PointReady = null;
        var pos = k.S.Count > 0 ? k.S[^1].Pos : default;
        return Fire(k, player, "point", 0.7, null, pos, t, p.Hit.Pos, p.Hit.Name, p.Hit.Kind);
    }
}
