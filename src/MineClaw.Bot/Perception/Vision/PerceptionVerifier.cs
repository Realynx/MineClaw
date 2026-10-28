using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using MineClaw.Agent.Perception;
using MineClaw.Data;
using MineClaw.Render;
using MineClaw.World;

namespace MineClaw.Bot.Perception.Vision;

public enum FactStatus { Verified, Corrected, Unverified, Dropped }

/// <summary>A perception claim after the voxel check.</summary>
public sealed record VerifiedFact(string Claim, IReadOnlyList<(int X, int Y, int Z)> Coords, BlockRole? Role, double Confidence, FactStatus Status, string? Note = null);

/// <summary>
/// What the brain gets back from a perception look: ≈100 tokens of checked text (<see cref="Text"/>), never the picture.
/// <see cref="Mismatched"/>/<see cref="Checked"/> is the hallucination rate (coordinate or block claims the voxels contradict).
/// </summary>
public sealed record VerifiedAnswer(string Question, string? Answer, IReadOnlyList<VerifiedFact> Facts, int Checked, int Mismatched,
    int Dropped, int Corrected, int Unverified, string Text, PerceptionResult? Raw = null, string? Error = null, bool Cached = false)
{
    public double HallucinationRate => Checked == 0 ? 0 : (double)Mismatched / Checked;
    /// <summary>The model's verified answer + facts without the deterministic role grounding (what the eval grades).</summary>
    public string ModelText { get; init; } = "";
    /// <summary>Answered exactly from voxels + roles (<see cref="VisionRouter"/>), no model call.</summary>
    public bool Exact { get; init; }
}

/// <summary>
/// Checks every coordinate and block claim of a perception answer against the WorldState voxels and the role layer:
/// a claim whose block/role contradicts the world is snapped to a matching neighbour (an off-by-one ruler read) or dropped;
/// a claim without coordinates is kept only when confident and marked unverified. The answer line survives only if every
/// coordinate in it belongs to a kept fact. Zero tokens, deterministic.
/// </summary>
public static partial class PerceptionVerifier
{
    public const int MaxChars = 420;

    [GeneratedRegex(@"(-?\d+)\s*,\s*(-?\d+)\s*,\s*(-?\d+)")]
    private static partial Regex TripleRx();

    [GeneratedRegex(@"\b(air|empty|hole|gap|gaps|missing|open|nothing)\b", RegexOptions.IgnoreCase)]
    private static partial Regex AirRx();

    [GeneratedRegex(@"\b(place against|face|faces|solid|stand on|support|neighbou?r(?:ing)? block)\b", RegexOptions.IgnoreCase)]
    private static partial Regex SolidRx();

    [GeneratedRegex(@"\b(?:not|isn't|aren't|no)\s+(?:a |an |the |part of (?:a |the )?)?(?:solid\s+)?([a-z]+(?:[ -][a-z]+)?)", RegexOptions.IgnoreCase)]
    private static partial Regex NegRx();

    /// <summary>The claim's subject ends at the first relation word: "glowstone floor WEST OF the portal" is about glowstone.</summary>
    [GeneratedRegex(@"\b(next to|west of|east of|north of|south of|beside|between|under|below|above|on top of|near|behind|left of|right of|from|toward|towards|of the|by the|around|against|in the|in front of|inside)\b", RegexOptions.IgnoreCase)]
    private static partial Regex RelationRx();

    private static string Subject(string claim)
    {
        var m = RelationRx().Match(claim);
        return m.Success ? claim[..m.Index] : claim;
    }

    private static BlockRole? Negated(string claim) => NegRx().Match(claim) is { Success: true } m ? BlockRoles.Parse(m.Groups[1].Value) : null;

    private static readonly Dictionary<string, int> Aliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["cobble"] = Blocks.Cobblestone, ["cobblestone"] = Blocks.Cobblestone, ["stone"] = Blocks.Stone, ["glowstone"] = Blocks.Glowstone,
        ["obsidian"] = Blocks.Obsidian, ["dirt"] = Blocks.Dirt, ["grass"] = Blocks.Grass, ["planks"] = Blocks.Planks, ["plank"] = Blocks.Planks,
        ["log"] = Blocks.Log, ["wood"] = Blocks.Log, ["sand"] = Blocks.Sand, ["gravel"] = Blocks.Gravel, ["netherrack"] = Blocks.Netherrack,
        ["chest"] = Blocks.Chest, ["furnace"] = Blocks.Furnace, ["glass"] = Blocks.Glass, ["leaves"] = Blocks.Leaves, ["slab"] = Blocks.Slab,
        ["torch"] = Blocks.Torch, ["workbench"] = Blocks.Workbench, ["bench"] = Blocks.Workbench, ["soul sand"] = Blocks.SoulSand,
        ["water"] = Blocks.Water, ["lava"] = Blocks.Lava, ["door"] = Blocks.WoodenDoor,
        ["wheat"] = Blocks.Crops, ["crop"] = Blocks.Crops, ["crops"] = Blocks.Crops, ["farmland"] = Blocks.Farmland,
    };

    /// <summary>Verifies <paramref name="r"/> against the world. <paramref name="focus"/>/<paramref name="radius"/> bound plausible coordinates.</summary>
    public static VerifiedAnswer Verify(string question, PerceptionResult r, IBlockAccess world, RoleMap roles, (int X, int Y, int Z) focus, int radius)
    {
        if (!r.Ok) return new VerifiedAnswer(question, null, [], 0, 0, 0, 0, 0, $"[vision] perception failed ({r.Error}); use look map", r, r.Error);
        var facts = new List<VerifiedFact>();
        int checkedN = 0, mismatched = 0, dropped = 0, corrected = 0, unverified = 0;
        var replace = new Dictionary<(int, int, int), (int, int, int)>();
        var droppedSubjects = new List<string>();
        bool refuted = false;
        foreach (var f in r.Facts.Take(8))
        {
            var subject = Subject(f.Claim);
            var negRole = Negated(f.Claim);
            // "gap in the floor", "hole at": the cell itself is air, whatever surrounds it.
            bool airSubject = AirRx().IsMatch(subject);
            var role = BlockRoles.Parse(f.Role) ?? (airSubject ? null : RoleFromClaim(subject));
            if (role == negRole || (airSubject && role is BlockRole.Floor or BlockRole.Wall)) role = null;
            var block = airSubject ? null : BlockFromClaim(subject);
            bool expectAir = airSubject && role is null or BlockRole.Void or BlockRole.GenCell or BlockRole.None;
            bool expectSolid = SolidRx().IsMatch(f.Claim) && !expectAir && role is not (BlockRole.Void or BlockRole.None);
            bool checkable = role != null || negRole != null || block != null || expectAir || expectSolid;
            if (f.Coords.Count == 0)
            {
                if (f.Confidence >= 0.7) { facts.Add(new VerifiedFact(f.Claim, [], role, f.Confidence, FactStatus.Unverified)); unverified++; }
                else dropped++;
                continue;
            }
            if (!checkable)
            {
                // Coordinates with nothing to compare: the words may stay (uncertain), the coordinates never do (live 10-27: an
                // unchecked cell from perception became clone 69's dig target).
                var near = f.Coords.Where(c => Plausible(world, c, focus, radius)).ToList();
                if (near.Count == 0 || f.Confidence < 0.7) { dropped++; continue; }
                facts.Add(new VerifiedFact(f.Claim, [], role, f.Confidence, FactStatus.Unverified));
                unverified++;
                continue;
            }
            checkedN++;
            var kept = new List<(int, int, int)>();
            bool anyBad = false, anySnap = false, anyLost = false;
            foreach (var c in f.Coords.Take(6))
            {
                bool ok = Plausible(world, c, focus, radius) && Matches(world, roles, c, role, block, expectAir, expectSolid, negRole);
                if (ok) { kept.Add(c); continue; }
                anyBad = true;
                // Only a single-target claim is snapped (the classic floor-vs-feet misread); a list of faces is right or wrong per entry.
                if (f.Coords.Count == 1 && Snap(world, roles, c, role, block, expectAir, expectSolid, negRole, focus, radius, f.Coords) is { } s && !kept.Contains(s))
                {
                    kept.Add(s);
                    replace[c] = s;
                    anySnap = true;
                }
                else anyLost = true;
            }
            if (anyBad) mismatched++;
            if (kept.Count == 0) { dropped++; refuted = true; droppedSubjects.Add(subject.Trim().ToLowerInvariant()); continue; }
            var status = anyBad ? FactStatus.Corrected : FactStatus.Verified;
            if (anyBad) corrected++;
            // Some of its coords were refuted: the claim's words ("gen cells in rows z7-z8 near x4-8") are not to be trusted,
            // only what the voxels confirm at the kept coords.
            string claim = f.Claim;
            if (anyLost) { refuted = true; claim = Canonical(world, role, expectAir, expectSolid, kept[0]); }
            facts.Add(new VerifiedFact(claim, kept, role, f.Confidence, status,
                anySnap ? "coords corrected" : anyLost ? "some coords dropped" : null));
        }

        // The answer line: every coordinate it names must belong to a kept fact (corrected ones are rewritten).
        string? answer = r.Answer;
        if (!string.IsNullOrWhiteSpace(answer))
        {
            var keptCoords = facts.Where(x => x.Status is FactStatus.Verified or FactStatus.Corrected).SelectMany(x => x.Coords).ToHashSet();
            bool bad = false;
            answer = TripleRx().Replace(answer, m =>
            {
                var c = (int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value), int.Parse(m.Groups[3].Value));
                if (replace.TryGetValue(c, out var s)) return $"{s.Item1},{s.Item2},{s.Item3}";
                if (!keptCoords.Contains(c)) bad = true;
                return m.Value;
            });
            // ... and it must not repeat a claim the voxels just refuted ("glowstone floor east" when there's void).
            bad |= droppedSubjects.Any(d => d.Length >= 8 && answer.Contains(d, StringComparison.OrdinalIgnoreCase));
            if (bad) { answer = null; mismatched++; checkedN++; }
            // A model that just made refuted claims doesn't get its free-text answer passed on either (live 21:14: "the water
            // is about 8 blocks west, no water touching the lava" rode along with one true coordinate): verified facts only.
            else if (refuted) answer = null;
        }
        var ordered = facts.OrderByDescending(x => x.Status is FactStatus.Verified or FactStatus.Corrected).ThenByDescending(x => x.Confidence).ToList();
        string modelText = Format(question, answer, ordered, dropped, false);
        return new VerifiedAnswer(question, answer, ordered, checkedN, mismatched, dropped, corrected, unverified, modelText, r) { ModelText = modelText };
    }

    /// <summary>"[vision] answer | facts: claim @x,y,z (role); ... | 1 dropped" within <see cref="MaxChars"/>.</summary>
    public static string Format(string question, string? answer, IReadOnlyList<VerifiedFact> facts, int dropped, bool cached, string? grounding = null)
    {
        // Only voxel-verified coordinates ever leave the verifier: any other x,y,z in a claim or answer text becomes "?" (the
        // brain or a worker must never act on a perception coordinate the world didn't confirm).
        var verified = facts.Where(f => f.Status is FactStatus.Verified or FactStatus.Corrected).SelectMany(f => f.Coords).ToHashSet();
        string Scrub(string t) => TripleRx().Replace(t, m =>
            verified.Contains((int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value), int.Parse(m.Groups[3].Value))) ? m.Value : "?");
        var sb = new StringBuilder("[vision] ");
        if (!string.IsNullOrWhiteSpace(answer)) sb.Append(Trunc(Scrub(answer.Trim().TrimEnd('.')), 120)).Append(". ");
        var parts = new List<string>();
        foreach (var f in facts.Take(5))
        {
            var claim = Trunc(Scrub(f.Claim.Trim().TrimEnd('.')), 80);
            var coords = f.Coords.Where(c => !claim.Contains($"{c.X},{c.Y},{c.Z}", StringComparison.Ordinal)).Take(3).ToList();
            var s = claim + (coords.Count > 0 ? " @" + string.Join(" ", coords.Select(c => $"{c.X},{c.Y},{c.Z}")) : "");
            if (f.Status == FactStatus.Unverified) s += " (uncertain)";
            else if (f.Status == FactStatus.Corrected && f.Note != "some coords dropped") s += " (coords fixed)";
            parts.Add(s);
        }
        if (parts.Count > 0) sb.Append("seen: ").Append(string.Join("; ", parts));
        if (dropped > 0) sb.Append(CultureInfo.InvariantCulture, $" [{dropped} wrong claim{(dropped == 1 ? "" : "s")} dropped]");
        if (parts.Count == 0 && string.IsNullOrWhiteSpace(answer)) sb.Append("nothing verifiable seen; use look map");
        if (cached) sb.Append(" (cached)");
        var text = sb.ToString().Trim();
        if (!string.IsNullOrWhiteSpace(grounding)) text += " | " + grounding;
        return text.Length <= MaxChars ? text : text[..(MaxChars - 1)] + "…";
    }

    private static string Trunc(string s, int n) => s.Length <= n ? s : s[..(n - 1)] + "…";

    private static bool Plausible(IBlockAccess w, (int X, int Y, int Z) c, (int X, int Y, int Z) focus, int radius) =>
        (uint)c.Y < w.WorldHeight && w.IsLoaded(c.X, c.Z)
        && Math.Abs(c.X - focus.X) <= radius + 4 && Math.Abs(c.Z - focus.Z) <= radius + 4 && Math.Abs(c.Y - focus.Y) <= 12;

    private static bool Matches(IBlockAccess w, RoleMap roles, (int X, int Y, int Z) c, BlockRole? role, int? block, bool air, bool solid, BlockRole? negRole)
    {
        int id = w.GetBlock(c.X, c.Y, c.Z);
        var actual = roles(c.X, c.Y, c.Z);
        bool ok = true;
        if (block is { } b) ok &= SameFamily(b, id);
        if (air) ok &= id == 0;
        if (solid) ok &= id != 0 && w.Info(id).Solid;
        if (role is { } r) ok &= RoleMatches(w, r, actual, id, c);
        // "X is NOT a gen cell": true when the role differs.
        if (negRole is { } nr) ok &= !RoleMatches(w, nr, actual, id, c);
        return ok;
    }

    private static bool RoleMatches(IBlockAccess w, BlockRole expected, BlockRole actual, int id, (int X, int Y, int Z) c)
    {
        if (expected == actual) return true;
        bool solid = id != 0 && w.Info(id).Solid;
        return expected switch
        {
            // Geometry roles: the semantic role may be more specific (a protected floor, an own-placed wall).
            // Floor vs wall is a matter of words (the floor under a gen cell has a block on top): any solid non-gen, non-portal block.
            BlockRole.Floor or BlockRole.Wall => solid && actual is not (BlockRole.GenCell or BlockRole.Portal),
            BlockRole.Water => Blocks.IsWater(id),
            BlockRole.Lava => Blocks.IsLava(id),
            BlockRole.Station => MineClaw.Bot.WorldIndex.IndexKinds.StationKind(id) != null,
            BlockRole.SoftProtected => actual is BlockRole.NeverBreak or BlockRole.PlayerBuild or BlockRole.Portal,
            BlockRole.Void => id == 0 && actual == BlockRole.Void,
            BlockRole.None => id == 0,
            _ => false,
        };
    }

    private static bool SameFamily(int expected, int actual)
    {
        static int Fam(int id) => id switch
        {
            Blocks.Stone or Blocks.Cobblestone => Blocks.Cobblestone,
            Blocks.FlowingWater => Blocks.Water,
            Blocks.FlowingLava => Blocks.Lava,
            Blocks.LitFurnace => Blocks.Furnace,
            Blocks.IronDoor => Blocks.WoodenDoor,
            Blocks.Grass => Blocks.Dirt,
            _ => id,
        };
        return Fam(expected) == Fam(actual);
    }

    /// <summary>A 6-neighbour (y first: floor vs feet is the classic misread) that satisfies the claim, not already claimed.</summary>
    private static (int, int, int)? Snap(IBlockAccess w, RoleMap roles, (int X, int Y, int Z) c, BlockRole? role, int? block, bool air, bool solid,
        BlockRole? negRole, (int X, int Y, int Z) focus, int radius, IReadOnlyList<(int X, int Y, int Z)> claimed)
    {
        // Negative and shape-only claims are true where they point or wrong: nothing to snap to.
        if (negRole != null || (role == null && block == null)) return null;
        // Generic geometry (floor, wall, void, plain blocks) nearly always has a matching neighbour: snapping it would "fix" a
        // false claim into a different true one. Only distinctive targets are snapped.
        if (role is null or BlockRole.Floor or BlockRole.Wall or BlockRole.Void or BlockRole.None) return null;
        (int, int, int)? found = null;
        foreach (var (dx, dy, dz) in new[] { (0, -1, 0), (0, 1, 0), (1, 0, 0), (-1, 0, 0), (0, 0, 1), (0, 0, -1) })
        {
            var n = (c.X + dx, c.Y + dy, c.Z + dz);
            if (claimed.Contains(n) || !Plausible(w, n, focus, radius)) continue;
            if (!Matches(w, roles, n, role, block, air, solid, null)) continue;
            if (found != null && dy == 0) return null; // two horizontal candidates: ambiguous, don't guess
            found ??= n;
            if (dy != 0) return found;
        }
        return found;
    }

    /// <summary>What the voxels say is at <paramref name="c"/>, in the claim's terms ("gen cell", "water", "cobblestone").</summary>
    private static string Canonical(IBlockAccess w, BlockRole? role, bool air, bool solid, (int X, int Y, int Z) c)
    {
        // Crops in the voxels' words, stage from the block's metadata: "crop (ripe 7/7)", never a '$'/"protected" reading.
        if (role is BlockRole.CropRipe or BlockRole.CropGrowing)
            return RoleClassifier.CropText(w.GetBlock(c.X, c.Y, c.Z), w.GetMeta(c.X, c.Y, c.Z)) ?? BlockRoles.Name(role.Value);
        if (role is { } r && r is not (BlockRole.None or BlockRole.Floor or BlockRole.Wall)) return BlockRoles.Name(r);
        if (air) return "air";
        int id = w.GetBlock(c.X, c.Y, c.Z);
        var name = id == 0 ? "air" : Blocks.Get(id).Name.Replace('_', ' ');
        if (solid) return $"solid face ({name})";
        return role is BlockRole.Floor or BlockRole.Wall ? $"{BlockRoles.Name(role.Value)} ({name})" : name;
    }

    private static BlockRole? RoleFromClaim(string claim)
    {
        var c = claim.ToLowerInvariant();
        if (c.Contains("gen cell") || c.Contains("generator cell") || c.Contains("dig cell") || c.Contains("mine here")) return BlockRole.GenCell;
        if (c.Contains("portal")) return BlockRole.Portal;
        if (c.Contains("void")) return BlockRole.Void;
        if (c.Contains("never-break") || c.Contains("never break")) return BlockRole.NeverBreak;
        if (Regex.IsMatch(c, @"\b(unripe|not ripe|not fully grown|growing|immature|young)\b")) return BlockRole.CropGrowing;
        if (Regex.IsMatch(c, @"\b(ripe|fully grown|mature)\b")) return BlockRole.CropRipe;
        if (Regex.IsMatch(c, @"\bfloor\b")) return BlockRole.Floor;
        return null;
    }

    private static int? BlockFromClaim(string claim)
    {
        var c = claim.ToLowerInvariant();
        if (c.Contains("soul sand")) return Blocks.SoulSand;
        foreach (Match m in Regex.Matches(c, @"[a-z]+"))
            if (Aliases.TryGetValue(m.Value, out var id)) return id;
        return null;
    }
}
