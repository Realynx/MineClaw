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
        var run = new VerifyRun(world, roles, focus, radius);
        foreach (var f in r.Facts.Take(8)) run.Check(f);
        string? answer = run.CheckAnswer(r.Answer);
        var ordered = run.Facts.OrderByDescending(x => x.Status is FactStatus.Verified or FactStatus.Corrected).ThenByDescending(x => x.Confidence).ToList();
        string modelText = Format(question, answer, ordered, run.Dropped, false);
        return new VerifiedAnswer(question, answer, ordered, run.Checked, run.Mismatched, run.Dropped, run.Corrected, run.Unverified, modelText, r)
            { ModelText = modelText };
    }

    /// <summary>What a claim says it is: the role/block it names (a negated role excluded), and whether it expects air or a solid
    /// block. Unchecked when it names nothing the voxels can confirm.</summary>
    private readonly record struct ClaimShape(string Subject, BlockRole? NegRole, BlockRole? Role, int? Block, bool ExpectAir, bool ExpectSolid)
    {
        public bool Checkable => Role != null || NegRole != null || Block != null || ExpectAir || ExpectSolid;

        public static ClaimShape Of(PerceptionFact f)
        {
            var subject = PerceptionVerifier.Subject(f.Claim);
            var negRole = Negated(f.Claim);
            // "gap in the floor", "hole at": the cell itself is air, whatever surrounds it.
            bool airSubject = AirRx().IsMatch(subject);
            var role = ClaimedRole(f, subject, airSubject, negRole);
            var block = airSubject ? null : BlockFromClaim(subject);
            bool expectAir = airSubject && role is null or BlockRole.Void or BlockRole.GenCell or BlockRole.None;
            bool expectSolid = SolidRx().IsMatch(f.Claim) && !expectAir && role is not (BlockRole.Void or BlockRole.None);
            return new ClaimShape(subject, negRole, role, block, expectAir, expectSolid);
        }

        private static BlockRole? ClaimedRole(PerceptionFact f, string subject, bool airSubject, BlockRole? negRole)
        {
            var role = BlockRoles.Parse(f.Role) ?? (airSubject ? null : RoleFromClaim(subject));
            return role == negRole || (airSubject && role is BlockRole.Floor or BlockRole.Wall) ? null : role;
        }
    }

    /// <summary>One <see cref="Verify"/>: the facts kept, the coordinates snapped, and the counts.</summary>
    private sealed class VerifyRun(IBlockAccess world, RoleMap roles, (int X, int Y, int Z) focus, int radius)
    {
        public readonly List<VerifiedFact> Facts = new();
        public int Checked, Mismatched, Dropped, Corrected, Unverified;
        private readonly Dictionary<(int, int, int), (int, int, int)> _replace = new();
        private readonly List<string> _droppedSubjects = new();
        private bool _refuted;

        public void Check(PerceptionFact f)
        {
            var shape = ClaimShape.Of(f);
            if (f.Coords.Count == 0) KeepWords(f, shape.Role);
            else if (!shape.Checkable) KeepWordsIfNear(f, shape.Role);
            else CheckCoords(f, shape);
        }

        /// <summary>No coordinates: a confident claim stays as unverified words, a vague one is dropped.</summary>
        private void KeepWords(PerceptionFact f, BlockRole? role)
        {
            if (f.Confidence >= 0.7) { Facts.Add(new VerifiedFact(f.Claim, [], role, f.Confidence, FactStatus.Unverified)); Unverified++; }
            else Dropped++;
        }

        /// <summary>Coordinates with nothing to compare: the words may stay (uncertain), the coordinates never do (live 10-27: an
        /// unchecked cell from perception became clone 69's dig target).</summary>
        private void KeepWordsIfNear(PerceptionFact f, BlockRole? role)
        {
            var near = f.Coords.Where(c => Plausible(world, c, focus, radius)).ToList();
            if (near.Count == 0 || f.Confidence < 0.7) { Dropped++; return; }
            Facts.Add(new VerifiedFact(f.Claim, [], role, f.Confidence, FactStatus.Unverified));
            Unverified++;
        }

        private void CheckCoords(PerceptionFact f, ClaimShape shape)
        {
            Checked++;
            var kept = KeepCoords(f, shape, out bool anySnap, out bool anyLost);
            bool anyBad = anySnap || anyLost;
            if (anyBad) Mismatched++;
            if (kept.Count == 0) { Dropped++; _refuted = true; _droppedSubjects.Add(shape.Subject.Trim().ToLowerInvariant()); return; }
            var status = anyBad ? FactStatus.Corrected : FactStatus.Verified;
            if (anyBad) Corrected++;
            // Some of its coords were refuted: the claim's words ("gen cells in rows z7-z8 near x4-8") are not to be trusted,
            // only what the voxels confirm at the kept coords.
            string claim = f.Claim;
            if (anyLost) { _refuted = true; claim = Canonical(world, shape.Role, shape.ExpectAir, shape.ExpectSolid, kept[0]); }
            Facts.Add(new VerifiedFact(claim, kept, shape.Role, f.Confidence, status,
                anySnap ? "coords corrected" : anyLost ? "some coords dropped" : null));
        }

        /// <summary>The coordinates the voxels confirm; a wrong single target is snapped to the right cell nearby.</summary>
        private List<(int, int, int)> KeepCoords(PerceptionFact f, ClaimShape shape, out bool anySnap, out bool anyLost)
        {
            var kept = new List<(int, int, int)>();
            anySnap = anyLost = false;
            foreach (var c in f.Coords.Take(6))
            {
                if (Plausible(world, c, focus, radius) && Matches(world, roles, c, shape.Role, shape.Block, shape.ExpectAir, shape.ExpectSolid, shape.NegRole))
                {
                    kept.Add(c);
                    continue;
                }
                if (TrySnap(f, c, shape, kept)) anySnap = true;
                else anyLost = true;
            }
            return kept;
        }

        /// <summary>Only a single-target claim is snapped (the classic floor-vs-feet misread); a list of faces is right or wrong per entry.</summary>
        private bool TrySnap(PerceptionFact f, (int X, int Y, int Z) c, ClaimShape shape, List<(int, int, int)> kept)
        {
            if (f.Coords.Count != 1
                || Snap(world, roles, c, shape.Role, shape.Block, shape.ExpectAir, shape.ExpectSolid, shape.NegRole, focus, radius, f.Coords) is not { } s
                || kept.Contains(s))
                return false;
            kept.Add(s);
            _replace[c] = s;
            return true;
        }

        /// <summary>
        /// The answer line: every coordinate it names must belong to a kept fact (corrected ones are rewritten), and it must not
        /// repeat a claim the voxels just refuted ("glowstone floor east" when there's void). A model that just made refuted claims
        /// doesn't get its free-text answer passed on either (live 21:14: "the water is about 8 blocks west, no water touching the
        /// lava" rode along with one true coordinate): verified facts only.
        /// </summary>
        public string? CheckAnswer(string? answer)
        {
            if (string.IsNullOrWhiteSpace(answer)) return answer;
            var keptCoords = Facts.Where(x => x.Status is FactStatus.Verified or FactStatus.Corrected).SelectMany(x => x.Coords).ToHashSet();
            bool bad = false;
            answer = TripleRx().Replace(answer, m =>
            {
                var c = (int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value), int.Parse(m.Groups[3].Value));
                if (_replace.TryGetValue(c, out var s)) return $"{s.Item1},{s.Item2},{s.Item3}";
                if (!keptCoords.Contains(c)) bad = true;
                return m.Value;
            });
            bad |= _droppedSubjects.Any(d => d.Length >= 8 && answer.Contains(d, StringComparison.OrdinalIgnoreCase));
            if (bad) { Mismatched++; Checked++; return null; }
            return _refuted ? null : answer;
        }
    }

    /// <summary>"[vision] answer | facts: claim @x,y,z (role); ... | 1 dropped" within <see cref="MaxChars"/>.</summary>
    public static string Format(string question, string? answer, IReadOnlyList<VerifiedFact> facts, int dropped, bool cached, string? grounding = null)
    {
        // Only voxel-verified coordinates ever leave the verifier: any other x,y,z in a claim or answer text becomes "?" (the
        // brain or a worker must never act on a perception coordinate the world didn't confirm).
        var verified = facts.Where(f => f.Status is FactStatus.Verified or FactStatus.Corrected).SelectMany(f => f.Coords).ToHashSet();
        var sb = new StringBuilder("[vision] ");
        if (!string.IsNullOrWhiteSpace(answer)) sb.Append(Trunc(Scrub(answer.Trim().TrimEnd('.'), verified), 120)).Append(". ");
        var parts = facts.Take(5).Select(f => FactText(f, verified)).ToList();
        if (parts.Count > 0) sb.Append("seen: ").Append(string.Join("; ", parts));
        if (dropped > 0) sb.Append(CultureInfo.InvariantCulture, $" [{dropped} wrong claim{(dropped == 1 ? "" : "s")} dropped]");
        if (parts.Count == 0 && string.IsNullOrWhiteSpace(answer)) sb.Append("nothing verifiable seen; use look map");
        if (cached) sb.Append(" (cached)");
        var text = sb.ToString().Trim();
        if (!string.IsNullOrWhiteSpace(grounding)) text += " | " + grounding;
        return text.Length <= MaxChars ? text : text[..(MaxChars - 1)] + "…";
    }

    /// <summary>Any x,y,z the voxels didn't confirm becomes "?".</summary>
    private static string Scrub(string t, HashSet<(int X, int Y, int Z)> verified) => TripleRx().Replace(t, m =>
        verified.Contains((int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value), int.Parse(m.Groups[3].Value))) ? m.Value : "?");

    /// <summary>"claim @x,y,z (uncertain)": the claim's coords not already in its text (≤ 3), and its status.</summary>
    private static string FactText(VerifiedFact f, HashSet<(int X, int Y, int Z)> verified)
    {
        var claim = Trunc(Scrub(f.Claim.Trim().TrimEnd('.'), verified), 80);
        var coords = f.Coords.Where(c => !claim.Contains($"{c.X},{c.Y},{c.Z}", StringComparison.Ordinal)).Take(3).ToList();
        var s = claim + (coords.Count > 0 ? " @" + string.Join(" ", coords.Select(c => $"{c.X},{c.Y},{c.Z}")) : "");
        if (f.Status == FactStatus.Unverified) s += " (uncertain)";
        else if (f.Status == FactStatus.Corrected && f.Note != "some coords dropped") s += " (coords fixed)";
        return s;
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
        // Geometry roles: the semantic role may be more specific (a protected floor, an own-placed wall).
        // Floor vs wall is a matter of words (the floor under a gen cell has a block on top): any solid non-gen, non-portal block.
        if (expected is BlockRole.Floor or BlockRole.Wall) return id != 0 && w.Info(id).Solid && actual is not (BlockRole.GenCell or BlockRole.Portal);
        return BlockRoleMatches(expected, actual, id);
    }

    private static bool BlockRoleMatches(BlockRole expected, BlockRole actual, int id) => expected switch
    {
        BlockRole.Water => Blocks.IsWater(id),
        BlockRole.Lava => Blocks.IsLava(id),
        BlockRole.Station => MineClaw.Bot.WorldIndex.IndexKinds.StationKind(id) != null,
        BlockRole.SoftProtected => IsSoftProtected(actual),
        BlockRole.Void => id == 0 && actual == BlockRole.Void,
        BlockRole.None => id == 0,
        _ => false,
    };

    private static bool IsSoftProtected(BlockRole actual) => actual is BlockRole.NeverBreak or BlockRole.PlayerBuild or BlockRole.Portal;

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
        if (!Snappable(role, block, negRole)) return null;
        (int, int, int)? found = null;
        foreach (var (dx, dy, dz) in new[] { (0, -1, 0), (0, 1, 0), (1, 0, 0), (-1, 0, 0), (0, 0, 1), (0, 0, -1) })
        {
            var n = (c.X + dx, c.Y + dy, c.Z + dz);
            if (claimed.Contains(n) || !Plausible(w, n, focus, radius) || !Matches(w, roles, n, role, block, air, solid, null)) continue;
            if (found != null && dy == 0) return null; // two horizontal candidates: ambiguous, don't guess
            found ??= n;
            if (dy != 0) return found;
        }
        return found;
    }

    /// <summary>
    /// Negative and shape-only claims are true where they point or wrong: nothing to snap to. Generic geometry (floor, wall,
    /// void, plain blocks) nearly always has a matching neighbour: snapping it would "fix" a false claim into a different true
    /// one. Only distinctive targets are snapped.
    /// </summary>
    private static bool Snappable(BlockRole? role, int? block, BlockRole? negRole) =>
        negRole == null && (role != null || block != null)
        && role is not (null or BlockRole.Floor or BlockRole.Wall or BlockRole.Void or BlockRole.None);

    /// <summary>What the voxels say is at <paramref name="c"/>, in the claim's terms ("gen cell", "water", "cobblestone").</summary>
    private static string Canonical(IBlockAccess w, BlockRole? role, bool air, bool solid, (int X, int Y, int Z) c)
    {
        // Crops in the voxels' words, stage from the block's metadata: "crop (ripe 7/7)", never a '$'/"protected" reading.
        if (role is BlockRole.CropRipe or BlockRole.CropGrowing)
            return RoleClassifier.CropText(w.GetBlock(c.X, c.Y, c.Z), w.GetMeta(c.X, c.Y, c.Z)) ?? BlockRoles.Name(role.Value);
        if (role is { } r && r is not (BlockRole.None or BlockRole.Floor or BlockRole.Wall)) return BlockRoles.Name(r);
        if (air) return "air";
        return BlockWords(w, role, solid, c);
    }

    /// <summary>The block's own name: "solid face (cobblestone)", "floor (planks)", "dirt", "air".</summary>
    private static string BlockWords(IBlockAccess w, BlockRole? role, bool solid, (int X, int Y, int Z) c)
    {
        int id = w.GetBlock(c.X, c.Y, c.Z);
        var name = id == 0 ? "air" : Blocks.Get(id).Name.Replace('_', ' ');
        if (solid) return $"solid face ({name})";
        return role is BlockRole.Floor or BlockRole.Wall ? $"{BlockRoles.Name(role.Value)} ({name})" : name;
    }

    private static BlockRole? RoleFromClaim(string claim)
    {
        var c = claim.ToLowerInvariant();
        foreach (var (words, role) in ClaimWords)
            if (words.Any(word => c.Contains(word))) return role;
        if (Regex.IsMatch(c, @"\b(unripe|not ripe|not fully grown|growing|immature|young)\b")) return BlockRole.CropGrowing;
        if (Regex.IsMatch(c, @"\b(ripe|fully grown|mature)\b")) return BlockRole.CropRipe;
        if (Regex.IsMatch(c, @"\bfloor\b")) return BlockRole.Floor;
        return null;
    }

    /// <summary>Plain words naming a role, checked in order before the crop/floor patterns.</summary>
    private static readonly (string[] Words, BlockRole Role)[] ClaimWords =
    [
        (["gen cell", "generator cell", "dig cell", "mine here"], BlockRole.GenCell),
        (["portal"], BlockRole.Portal),
        (["void"], BlockRole.Void),
        (["never-break", "never break"], BlockRole.NeverBreak),
    ];

    private static int? BlockFromClaim(string claim)
    {
        var c = claim.ToLowerInvariant();
        if (c.Contains("soul sand")) return Blocks.SoulSand;
        foreach (Match m in Regex.Matches(c, @"[a-z]+"))
            if (Aliases.TryGetValue(m.Value, out var id)) return id;
        return null;
    }
}
