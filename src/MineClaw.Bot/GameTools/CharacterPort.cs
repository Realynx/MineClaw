using System.Text.Json;
using System.Text.Json.Nodes;
using MineClaw.Agent;
using MineClaw.Agent.Brainstem;
using MineClaw.Bot.Game;
using MineClaw.Scripting;
using MineClaw.Scripting.Character;
using MineClaw.World;

namespace MineClaw.Bot.GameTools;

/// <summary>
/// The <c>character</c> SDK module on main's body: gaze through the client's look overlay, gestures through the brainstem's
/// body-language player (its social-priority rules), lines from the prefetched line bank, the self-profile's mood and
/// relationships (read-only), memory through the same recall/remember_note tools the models use (groomed), recent chat.
/// </summary>
public sealed class CharacterPort : ICharacterPort
{
    private readonly AgentRuntime _rt;
    private readonly IGameClient _c;
    private readonly Func<Brainstem.Gestures?> _gestures;
    private readonly TimeProvider _time;
    private readonly object _lock = new();
    private readonly LinkedList<(string Player, string Text, string Intent, DateTimeOffset At)> _heard = new();

    public CharacterPort(AgentRuntime rt, IGameClient client, Func<Brainstem.Gestures?> gestures, TimeProvider? time = null)
    {
        _rt = rt;
        _c = client;
        _gestures = gestures;
        _time = time ?? TimeProvider.System;
        rt.Chat.AddressDecided += (m, _) =>
        {
            if (m.Player is not { Length: > 0 } p || p.Equals(client.Username, StringComparison.OrdinalIgnoreCase)) return;
            lock (_lock)
            {
                _heard.AddLast((p, m.Text, Intent(m.Text), _time.GetUtcNow()));
                while (_heard.Count > 60) _heard.RemoveFirst();
            }
        };
    }

    private IEnumerable<string> Names => [_rt.Chat.Username, _rt.Chat.DisplayName, .. _rt.Chat.Aliases];

    public bool Look(string? player, BlockPos? pos)
    {
        if (player != null)
            return _c.World.Entities.FindPlayer(player) is { } e && _c.FaceIdle(e.Position + new Vec3d(0, PlayerPhysics.EyeHeight, 0));
        return pos is { } p && _c.FaceIdle(new Vec3d(p.X + 0.5, p.Y + 0.5, p.Z + 0.5));
    }

    public ActResult Gesture(string name, string? target)
    {
        if (_gestures() is not { } g) return ActResult.Fail("no body language here");
        var played = g.Play(name, target);
        return played != null ? ActResult.Success("gestured " + played) : ActResult.Fail("gesture not now: " + (g.LastSuppressed ?? "refused"));
    }

    public string? Line(string kind, string? player) => _rt.Agent.Voice?.Bank.Take(kind, player);

    public void Said(string line)
    {
        _rt.Agent.Voice?.Bank.NoteSaid(line);
        if (TurnContext.Current?.Speaker is { } p) _rt.Agent.Voice?.NoteInstant(p, line, "script");
    }

    public JsonNode? Mood()
    {
        if (_rt.Growth is not { } g) return null;
        var m = g.Mood();
        return new JsonObject
        {
            ["mood"] = m.Words() is { Length: > 0 } w ? w : "neutral", ["cheer"] = Math.Round(m.Cheer, 2), ["energy"] = Math.Round(m.Energy, 2),
            ["confidence"] = Math.Round(m.Confidence, 2), ["affection"] = Math.Round(m.Affection, 2),
        };
    }

    public JsonNode? Relation(string player)
    {
        bool trusted = _rt.Options.Self.TrustedPlayers.Any(t => t.Equals(player, StringComparison.OrdinalIgnoreCase));
        bool regular = trusted || _rt.Options.Self.RegularPlayers.Any(t => t.Equals(player, StringComparison.OrdinalIgnoreCase));
        if (_rt.Growth is not { } g) return trusted || regular ? new JsonObject { ["player"] = player, ["trusted"] = trusted, ["regular"] = regular } : null;
        var r = g.Relationship(player);
        if (r.Episodes == 0 && !regular) return null;
        return new JsonObject
        {
            ["player"] = player, ["affinity"] = Math.Round(r.Affinity, 2), ["trust"] = Math.Round(r.Trust, 2), ["playful"] = Math.Round(r.Playful, 2),
            ["nick"] = r.Nickname, ["trusted"] = trusted, ["regular"] = regular, ["note"] = r.Describe(),
        };
    }

    public JsonNode? Recall(string query, int k)
    {
        var r = _rt.Tools.ExecuteAsync("recall", JsonSerializer.SerializeToElement(new { query, limit = k }), CancellationToken.None).GetAwaiter().GetResult();
        var list = new JsonArray();
        if (!r.Ok) return list;
        foreach (var line in r.Text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Take(k))
        {
            var m = System.Text.RegularExpressions.Regex.Match(line, @"#(\d+)");
            list.Add(new JsonObject { ["id"] = m.Success ? int.Parse(m.Groups[1].Value) : null, ["text"] = line });
        }
        return list;
    }

    public ActResult Remember(string text)
    {
        // The bot's own note ("seen:" = its observation, never a player's claim), groomed like every memory write.
        var r = _rt.Tools.ExecuteAsync("remember_note", JsonSerializer.SerializeToElement(new { subject = "me", text = "seen: " + text, by = "me" }), CancellationToken.None)
            .GetAwaiter().GetResult();
        return new ActResult(r.Ok, r.Text);
    }

    public JsonNode Heard(string? player, double seconds)
    {
        var now = _time.GetUtcNow();
        var list = new JsonArray();
        lock (_lock)
            foreach (var h in _heard)
                if (now - h.At <= TimeSpan.FromSeconds(seconds) && (player == null || h.Player.Equals(player, StringComparison.OrdinalIgnoreCase)))
                    list.Add(new JsonObject { ["player"] = h.Player, ["text"] = h.Text, ["intent"] = h.Intent, ["ago"] = Math.Round((now - h.At).TotalSeconds, 1) });
        return list;
    }

    public string Intent(string text) => ChatIntent.Classify(text, Names).Intent;
}
