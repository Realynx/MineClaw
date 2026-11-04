using System.Text.Json.Nodes;

namespace MineClaw.Hive.Story;

/// <summary>
/// Drafted combat (docs/web/story-mode.md "Drafted combat"): an attack order on any entity in view (a monster, an animal, a player,
/// another bot; never one of the colony's own pawns) sends every selected pawn at it with the best weapon it carries; a player or a
/// bot needs the owner's confirmation in the request (<c>confirm: true</c>), and only then may the pawn's body hit exactly that entity
/// (<see cref="IStoryHost.PermitAttack"/>: the client's hard gate refuses every other player). Drafted pawns without an order engage
/// monsters that come within <see cref="EngageRadius"/> of them and walk back to their post after; they never pick a fight with a
/// player on their own.
/// </summary>
public sealed partial class StoryColony
{
    /// <summary>A drafted pawn engages a monster this close to it (or to its post).</summary>
    public const double EngageRadius = 6;
    /// <summary>A permit to fight one player lasts this long and is renewed with each attack job.</summary>
    public static readonly TimeSpan PermitTtl = TimeSpan.FromMinutes(3);
    /// <summary>Transient refusals of an attack order ("busy: fighting …") before the order is given up.</summary>
    public const int AttackRetries = 5;

    public const string ConfirmError = "confirm";

    private readonly Dictionary<string, HashSet<int>> _permits = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> _attackRetries = new(StringComparer.Ordinal);

    /// <summary>
    /// An attack order's target, checked when it is given: a player or bot without <c>confirm: true</c> answers
    /// <c>({confirm:{entity, name, kind}}, "confirm: …")</c> (the route's 409); one of the colony's own pawns, a drop or an unseen entity
    /// is refused. Null = fine.
    /// </summary>
    internal (JsonObject? Confirm, string? Error) AttackCheck(int? entity, JsonNode? confirm)
    {
        if (entity is not { } id) return (null, null);
        var e = World?.Entities().FirstOrDefault(x => x.Id == id);
        if (e == null) return (null, "attack target: no such entity in view");
        if (e.Kind == "item") return (null, "attack target is a dropped item");
        if (e.Kind != "player") return (null, null);
        return PlayerAttackCheck(id, e.Name, confirm);
    }

    /// <summary>A player or bot: never one of the colony's own pawns; anyone else only with <c>confirm: true</c>.</summary>
    private (JsonObject? Confirm, string? Error) PlayerAttackCheck(int id, string name, JsonNode? confirm)
    {
        if (Pawns.ContainsKey(name) || Pawns.Values.Any(p => p.Display.Equals(name, StringComparison.OrdinalIgnoreCase)))
            return (null, $"won't attack {name}: it is one of the colony's own pawns");
        bool ok = confirm is JsonValue v && v.TryGetValue<bool>(out var b) && b;
        if (ok) return (null, null);
        bool bot = IsBotSafe(name);
        var info = new JsonObject { ["entity"] = id, ["name"] = name, ["kind"] = bot ? "bot" : "player" };
        return (info, $"{ConfirmError}: attacking {(bot ? "the bot" : "the player")} {name} needs confirm: true");
    }

    /// <summary>Whether the host runs a bot by that name (false when it can't tell).</summary>
    private bool IsBotSafe(string name)
    {
        try { return Host.IsBot(name); } catch (Exception) { return false; }
    }

    /// <summary>An attack order's job: the exact entity (by id), every ordered pawn at once (no lease: they gang up), a player only
    /// with a permit for this pawn's body.</summary>
    private ActiveJob AttackJob(PawnState p, Order o, StoryEntity? e, P3? at)
    {
        if (e is { Kind: "player" })
        {
            if (!_permits.TryGetValue(p.Name, out var set)) _permits[p.Name] = set = new HashSet<int>();
            set.Add(e.Id);
            try { Host.PermitAttack(p.Name, e.Id, true); } catch (Exception) { /* the body refuses it then */ }
        }
        return Job("order", "attack", $"attacking {e?.Name ?? "target"}", at, null, "order", o.Id, "fighting", j =>
        {
            j["mob"] = e?.Name ?? "";
            if (e != null) j["target"] = e.Id;
        }, xp: 0);
    }

    private void RevokePermits(PawnState p)
    {
        if (!_permits.Remove(p.Name, out var set)) return;
        foreach (var id in set)
            try { Host.PermitAttack(p.Name, id, false); } catch (Exception) { /* gone */ }
    }

    /// <summary>A transient attack failure ("busy", already fighting, can't reach it yet) keeps the order for another go.</summary>
    internal bool AttackRetry(Order o, string? note)
    {
        if (o.Kind != "attack" || note == null) return false;
        bool transient = note.StartsWith("busy", StringComparison.OrdinalIgnoreCase) || note.Contains("already fighting", StringComparison.OrdinalIgnoreCase)
            || Transient(note) || note.Contains("couldn't reach", StringComparison.OrdinalIgnoreCase);
        if (!transient) { _attackRetries.Remove(o.Id); return false; }
        int n = _attackRetries[o.Id] = _attackRetries.GetValueOrDefault(o.Id) + 1;
        if (n < AttackRetries) return true;
        _attackRetries.Remove(o.Id);
        return false;
    }

    /// <summary>A drafted pawn's move order sets its post (it walks back there after a fight).</summary>
    internal void NoteOrder(PawnState p, Order o)
    {
        if (p.Drafted && o.Kind == "move" && o.At is { } at) p.Gear.Post = at;
        if (o.Kind is "stop" && p.Drafted) RevokePermits(p);
    }

    /// <summary>
    /// A drafted pawn with no order to follow (none queued, or holding) engages the nearest monster within <see cref="EngageRadius"/>
    /// of it or of its post; several drafted pawns may engage the same one. Players and animals are never engaged by themselves.
    /// </summary>
    internal ActiveJob? DraftedEngage(PawnState p, PawnLive live)
    {
        if (!p.Drafted || !live.Online || p.Queue.Count > 0 && p.Queue[0].Kind != "hold") return null;
        var post = p.Gear.Post;
        var mob = Ents().Where(e => e.Kind == "mob" && e.Hostile && Math.Abs(e.Y - live.Y) <= 6
                && (Near(e, live.X, live.Z) || post is { } ps && Near(e, ps.X + 0.5, ps.Z + 0.5)))
            .OrderBy(e => (e.X - live.X) * (e.X - live.X) + (e.Z - live.Z) * (e.Z - live.Z)).FirstOrDefault();
        if (mob == null) return null;
        return Job("order", "attack", $"engaging a {mob.Name}", mob.Block, null, "engage", mob.Id.ToString(), "fighting", j =>
        {
            j["mob"] = mob.Name;
            j["target"] = mob.Id;
        }, xp: 0);

        static bool Near(StoryEntity e, double x, double z) => (e.X - x) * (e.X - x) + (e.Z - z) * (e.Z - z) <= EngageRadius * EngageRadius;
    }

    /// <summary>
    /// A mine order's job, with the tool its block takes (fetched from storage when the pawn has none). With no such tool anywhere the
    /// block is left (an area order goes on with its other blocks; a single block's order ends) and the owner hears why: never a
    /// block dug for nothing.
    /// </summary>
    private ActiveJob MineOrderJob(PawnState p, PawnLive live, Order o, P3? at)
    {
        var plan = at is { } c ? PlanTool(p, live, DigBlockAt(c)) : null;
        if (plan is { Wait: { } why } && at is { } target)
        {
            if (o.Targets is { Count: > 1 } ts) ts.Remove(target); else p.Queue.Remove(o);
            p.Rev = Touch();
            if (!_idleAlert.TryGetValue("tool:" + o.Id, out var last) || Now - last > TimeSpan.FromMinutes(5))
            {
                _idleAlert["tool:" + o.Id] = Now;
                Alert("warn", $"{p.Display} can't mine the {ItemName(DigBlockAt(target), 0).Replace('_', ' ')} at {target}: {why}", p.Name);
            }
            return Hold(why, p.Drafted ? "drafted" : "idle", s: 1, type: "order");
        }
        return WithTool(plan, Job(WorkTypes.Mine, "dig", "mining (order)", at, at is { } m ? "b:" + m : null, "order", o.Id));
    }

    /// <summary>A drafted pawn away from its post (after a fight) walks back to it.</summary>
    internal ActiveJob? DraftedReturn(PawnState p, PawnLive live)
    {
        if (!p.Drafted || p.Gear.Post is not { } post || post.Dist(live.X, live.Y, live.Z) <= 2.5) return null;
        return Job("order", "go", "back to its post", post, null, "post", null, "moving", j => j["dist"] = 0, xp: 0);
    }
}
