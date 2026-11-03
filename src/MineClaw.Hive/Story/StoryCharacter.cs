using System.Security.Cryptography;
using System.Text;

namespace MineClaw.Hive.Story;

/// <summary>
/// A pawn's characteristics, generated once and seeded by its name (the same name always gives the same pawn): skills 0–20 per
/// work type with a passion flag, 1–3 traits, a display name, suggested priorities and a default day. Original content: no other
/// game's names or text.
/// </summary>
public static class StoryCharacter
{
    public static readonly IReadOnlyList<Trait> Traits =
    [
        new("night_owl", "Night owl", "works best at night: its day is shifted (sleeps 10:00-17:00) and it learns faster after dark"),
        new("brave", "Brave", "stays in a fight: flees only at 4 hp or less"),
        new("skittish", "Skittish", "flees early: at 12 hp or less, and eats sooner (food 8)"),
        new("industrious", "Industrious", "shorter idle between jobs and a little more experience from work"),
        new("green_thumb", "Green thumb", "farm skill grows twice as fast"),
        new("steady_hands", "Steady hands", "build skill grows twice as fast"),
        new("quick_study", "Quick study", "every skill grows half again as fast"),
    ];

    public static Trait? TraitOf(string id) => Traits.FirstOrDefault(t => t.Id == id);

    private static readonly string[] First =
    [
        "Ari", "Bram", "Cleo", "Dov", "Edda", "Fenn", "Gale", "Hollis", "Ivo", "Juno", "Kit", "Lark", "Mira", "Nils", "Odile", "Pell",
        "Quill", "Rhea", "Sol", "Tamsin", "Ula", "Vesper", "Wren", "Yara", "Zev",
    ];

    /// <summary>A deterministic generator for one name (SHA-256 of it: stable across runs and machines).</summary>
    private sealed class Seeded(string name)
    {
        private readonly byte[] _seed = SHA256.HashData(Encoding.UTF8.GetBytes("mineclaw-story:" + name.ToLowerInvariant()));
        private int _n;

        /// <summary>Counter mode: draw n is the first two bytes of SHA-256(seed ‖ n).</summary>
        public int Int(int max)
        {
            if (max <= 0) return 0;
            var h = SHA256.HashData([.. _seed, (byte)(_n & 0xff), (byte)(_n++ >> 8)]);
            return (h[0] << 8 | h[1]) % max;
        }
    }

    /// <summary>A new pawn for an account: characteristics, suggested priorities, the default day.</summary>
    public static PawnState Generate(string name)
    {
        var r = new Seeded(name);
        var p = new PawnState { Name = name };
        p.Display = First[r.Int(First.Length)] + " " + (char)('A' + r.Int(26)) + ".";
        foreach (var w in WorkTypes.All)
        {
            // Mostly middling, a few strong and a few weak: the sum of two dice-like draws.
            int level = Math.Clamp(r.Int(11) + r.Int(11) - 2, 0, 20);
            p.Skills[w] = new SkillState { Level = level, Passion = r.Int(5) == 0 };
        }
        int nTraits = 1 + r.Int(3);
        var pool = Traits.Select(t => t.Id).ToList();
        while (p.Traits.Count < nTraits && pool.Count > 0)
        {
            var t = pool[r.Int(pool.Count)];
            pool.Remove(t);
            if (t == "brave") pool.Remove("skittish");
            if (t == "skittish") pool.Remove("brave");
            p.Traits.Add(t);
        }
        foreach (var w in WorkTypes.All) p.Priorities[w] = SuggestedPriority(p.Skills[w]);
        p.Schedule = DefaultSchedule(p.Traits.Contains("night_owl"));
        return p;
    }

    /// <summary>Strong skills first: 12+ → 1, 8+ → 2, 4+ → 3, else 4; a passion lifts it one step. Never off by default.</summary>
    public static int SuggestedPriority(SkillState s)
    {
        int p = s.Level >= 12 ? 1 : s.Level >= 8 ? 2 : s.Level >= 4 ? 3 : 4;
        return s.Passion ? Math.Max(1, p - 1) : p;
    }

    /// <summary>The default day: sleep 22:00-05:59, anything else; a night owl sleeps 10:00-17:59.</summary>
    public static string[] DefaultSchedule(bool nightOwl)
    {
        var s = new string[24];
        for (int h = 0; h < 24; h++)
            s[h] = nightOwl ? (h is >= 10 and <= 17 ? ScheduleBlocks.Sleep : ScheduleBlocks.Anything)
                : (h >= 22 || h <= 5 ? ScheduleBlocks.Sleep : ScheduleBlocks.Anything);
        return s;
    }

    /// <summary>The in-game hour from the world time: 1000 ticks an hour, tick 0 = 06:00.</summary>
    public static int Hour(long timeOfDay) => (int)((((timeOfDay % 24000) + 24000) % 24000 / 1000 + 6) % 24);

    public static bool Night(int hour) => hour >= 19 || hour < 6;

    /// <summary>Experience to go from <paramref name="level"/> to the next.</summary>
    public static double XpFor(int level) => 100 + 50 * level;

    /// <summary>Adds experience in a work type (passion ×1.5, trait multipliers) and levels up (max 20). Returns levels gained.</summary>
    public static int Learn(PawnState p, string work, double xp, int hour)
    {
        if (!p.Skills.TryGetValue(work, out var s)) return 0;
        double k = s.Passion ? 1.5 : 1;
        if (p.Traits.Contains("quick_study")) k *= 1.5;
        if (p.Traits.Contains("industrious")) k *= 1.1;
        if (work == WorkTypes.Farm && p.Traits.Contains("green_thumb")) k *= 2;
        if (work == WorkTypes.Build && p.Traits.Contains("steady_hands")) k *= 2;
        if (p.Traits.Contains("night_owl") && Night(hour)) k *= 1.5;
        s.Xp += xp * k;
        int gained = 0;
        while (s.Level < 20 && s.Xp >= XpFor(s.Level)) { s.Xp -= XpFor(s.Level); s.Level++; gained++; }
        if (s.Level >= 20) s.Xp = Math.Min(s.Xp, XpFor(20));
        return gained;
    }

    /// <summary>hp at or below which a pawn flees or eats (survival thresholds shaped by traits).</summary>
    public static int FleeHp(PawnState p) => p.Traits.Contains("brave") ? 4 : p.Traits.Contains("skittish") ? 12 : 8;
    public static int EatFood(PawnState p) => p.Traits.Contains("skittish") ? 8 : 6;
    /// <summary>Seconds a pawn idles when it has nothing to do.</summary>
    public static int IdleSeconds(PawnState p) => p.Traits.Contains("industrious") ? 2 : 4;
}
