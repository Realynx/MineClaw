using MineClaw.Agent;
using MineClaw.Agent.Events;
using MineClaw.Agent.Memory;
using MineClaw.Agent.Rag;
using MineClaw.Bot.Game;
using MineClaw.Bot.GameTools;
using MineClaw.Bot.WorldIndex;
using MineClaw.World;

namespace MineClaw.Bot.Brainstem;

/// <summary>
/// The "[here]" pre-observation attached to every chat-triggered brain turn (uncached tail, ≤~250 tokens), so the brain can
/// act in round 0 instead of spending rounds on look/inventory/where_is:
/// <c>[here] you: 12 N of cobble_gen | inv: cobblestone x64, stone pickaxe | PoofimaFox 14 SW of you, facing W |
/// near: chest 1 E, cobble_generator 12 S, tree 9 NE | void W6</c>.
/// Built from live state every time it is asked (never cached), so it can't go stale; the brain loop dedupes it against
/// the state header (pos, held item, task), [pointing] and [memory].
/// </summary>
public sealed class HereBlock(IGameClient client, MemoryStore? memory, ObservationStore? observations,
    Func<double, double, double, string?>? landmark)
{
    private static readonly HashSet<string> SkipKinds = new(StringComparer.OrdinalIgnoreCase)
        { "island", "area", "region", IndexKinds.Activity, IndexKinds.Hangout, "protected_region", IndexKinds.Hazard, IndexKinds.UnknownStructure };

    public string? Build(EventBatch batch)
    {
        if (!client.IsPlaying) return null;
        var w = client.World;
        var me = w.Player.Position;
        var parts = new List<string>();
        // The dimension first: in the nether every island/landmark reflex is wrong (live: "back at spawn" in the nether).
        if (w.Dimension != 0)
            parts.Add((w.Dimension == -1 ? "IN THE NETHER" : "IN DIMENSION " + w.Dimension)
                      + (MineClaw.Client.PortalEntry.InPortal(w, me) ? " (standing in the portal: step out, then back in for 4 s to travel)" : ""));
        try { if (landmark?.Invoke(me.X, me.Y, me.Z) is { Length: > 0 } lm) parts.Add("you: " + lm); } catch (Exception) { }
        parts.Add("inv: " + Inventory(w));

        var speakers = batch.Waking.Where(EventQueue.IsTyped).Select(e => e.Player!).Distinct(StringComparer.OrdinalIgnoreCase).Take(2);
        foreach (var who in speakers)
        {
            if (who.Equals(client.Username, StringComparison.OrdinalIgnoreCase)) continue;
            if (w.Entities.FindPlayer(who) is not { } e) { parts.Add($"{who} not in sight"); continue; }
            var off = Landmarks.Offset(me.X, me.Z, e.Position.X, e.Position.Z);
            var at = off == "here" ? $"{who} right by you" : $"{who} {off} of you";
            parts.Add($"{at}, facing {Compass(e.Yaw)}" + (w.IsPlayerOverVoid(e.Position) ? " (over void)" : ""));
        }

        var near = Near(me);
        if (near.Count > 0) parts.Add("near: " + string.Join(", ", near));
        if (VoidEdges(w, me) is { Length: > 0 } v) parts.Add(v);
        return "[here] " + string.Join(" | ", parts);
    }

    /// <summary>"cobblestone x64, stone pickaxe, dirt x3 +2 more" (grouped by item, biggest first).</summary>
    internal static string Inventory(WorldState w)
    {
        var slots = w.Inventory.PlayerSlots();
        var groups = new Dictionary<string, int>(StringComparer.Ordinal);
        for (int i = 9; i < slots.Length && i < 45; i++)
        {
            var s = slots[i];
            if (s.IsEmpty) continue;
            var name = Names.Item(s.Id, s.Damage > 0 && MineClaw.Data.Items.Exists(s.Id) && MineClaw.Data.Items.Get(s.Id).MaxDamage > 0 ? 0 : s.Damage);
            groups[name] = groups.GetValueOrDefault(name) + s.Count;
        }
        if (groups.Count == 0) return "empty";
        var top = groups.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key).ToList();
        var shown = top.Take(6).Select(kv => kv.Value > 1 ? $"{kv.Key} x{kv.Value}" : kv.Key);
        return string.Join(", ", shown) + (top.Count > 6 ? $" +{top.Count - 6} more" : "");
    }

    private List<string> Near(Vec3d me)
    {
        var list = new List<(double D, string Text)>();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (memory != null)
            {
                int dim = memory.CurrentDimension;
                var (wps, sts) = memory.Read(d => (d.Waypoints.Where(x => x.Dimension == dim).ToList(), d.Structures.Where(x => x.Dimension == dim).ToList()));
                foreach (var wp in wps) Add(wp.Name, wp.X + 0.5, wp.Y, wp.Z + 0.5);
                foreach (var s in sts)
                {
                    if (s.Min == null) continue;
                    var mx = s.Max ?? s.Min;
                    Add(s.Name, (s.Min.X + mx.X) / 2.0 + 0.5, (s.Min.Y + mx.Y) / 2.0, (s.Min.Z + mx.Z) / 2.0 + 0.5);
                }
            }
        }
        catch (Exception) { /* memory busy: fewer hints */ }
        try
        {
            if (observations != null)
                foreach (var (row, d) in observations.Nearest(null, me.X, me.Y, me.Z, limit: 8, maxDistance: 32))
                {
                    if (SkipKinds.Contains(row.Kind) || row.Bounds is not { } b) continue;
                    var cx = (b.MinX + b.MaxX) / 2.0 + 0.5;
                    var cz = (b.MinZ + b.MaxZ) / 2.0 + 0.5;
                    var off = Landmarks.Offset(me.X, me.Z, cx, cz);
                    list.Add((d + 0.1, $"{row.Kind.Replace('_', ' ')} {(off == "here" ? "here" : off)}"));
                }
        }
        catch (Exception) { /* index busy */ }
        // Nearest first, each thing once, at most 5.
        return list.OrderBy(t => t.D).Select(t => t.Text).Distinct(StringComparer.OrdinalIgnoreCase).Take(5).ToList();

        void Add(string name, double x, double y, double z)
        {
            if (Math.Abs(y - me.Y) > 12 || !names.Add(name)) return;
            double dx = x - me.X, dz = z - me.Z, d = Math.Sqrt(dx * dx + dz * dz);
            if (d > 32) return;
            var off = Landmarks.Offset(me.X, me.Z, x, z);
            list.Add((d, $"{name} {(off == "here" ? "here" : off)}"));
        }
    }

    /// <summary>"void N3 W6": the nearest void edge within 8 blocks per direction.</summary>
    internal static string VoidEdges(WorldState w, Vec3d me)
    {
        var dirs = new (string N, int Dx, int Dz)[] { ("N", 0, -1), ("E", 1, 0), ("S", 0, 1), ("W", -1, 0) };
        var parts = new List<string>();
        foreach (var (n, dx, dz) in dirs)
            for (int d = 1; d <= 8; d++)
            {
                var p = new Vec3d(Math.Floor(me.X) + 0.5 + dx * d, me.Y, Math.Floor(me.Z) + 0.5 + dz * d);
                if (!w.IsPlayerOverVoid(p)) continue;
                parts.Add(n + d);
                break;
            }
        return parts.Count == 0 ? "" : "void " + string.Join(" ", parts);
    }

    /// <summary>Beta yaw (0 = south, 90 = west) → compass.</summary>
    internal static string Compass(float yaw)
    {
        var y = ((yaw % 360) + 360) % 360;
        string[] names = ["S", "SW", "W", "NW", "N", "NE", "E", "SE"];
        return names[(int)Math.Round(y / 45.0) % 8];
    }
}
