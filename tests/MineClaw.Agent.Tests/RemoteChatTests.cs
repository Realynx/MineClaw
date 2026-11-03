using MineClaw.Agent.Chat;
using MineClaw.Agent.Events;
using MineClaw.Agent.Rag;

namespace MineClaw.Agent.Tests;

/// <summary>
/// The web viewer's chat path (ChatRouter.OnRemoteChat): a trusted player's line to exactly one bot, real speech (not test),
/// never marking the player online, its own rate bucket, and the command gate armed only when the line allows it.
/// </summary>
public class RemoteChatTests
{
    private const string Fox = "poofimafox";

    private static (ChatRouter Router, EventQueue Queue, CommandGate Gate) Make(Action<ChatOptions>? cfg = null, string? audit = null)
    {
        var o = new ChatOptions { Username = "maid_chan", DisplayName = "maid chan", LineDelayMs = 0 };
        cfg?.Invoke(o);
        var q = new EventQueue();
        var r = new ChatRouter(o, q, (_, _) => Task.CompletedTask);
        var gate = new CommandGate(new CommandOptions(), () => [Fox], auditPath: audit);
        r.Commands = gate;
        return (r, q, gate);
    }

    private static RemoteChat Line(string text, string target = "maid_chan", bool allow = false) => new(Fox, text, "viewer", target, allow, "r1");

    [Fact]
    public void A_viewer_line_reaches_the_target_bot_addressed_real_and_without_marking_the_player_online()
    {
        var (r, q, _) = Make();
        Assert.True(r.OnRemoteChat(Line("bring me some cobble")));
        var e = Assert.Single(q.Snapshot());
        Assert.Equal(EventKind.Chat, e.Kind);
        Assert.Equal(Fox, e.Player);
        Assert.Equal("bring me some cobble", e.Text);
        Assert.True(e.Wakes); // addressed
        Assert.Equal(EventPriority.Normal, e.Priority);
        Assert.Equal("viewer", e.Origin);
        Assert.False(e.IsTest);
        Assert.Empty(r.OnlinePlayers);
        Assert.True(r.AddressedWithin(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public void Another_bots_viewer_line_is_ignored_here()
    {
        var (r, q, _) = Make();
        Assert.False(r.OnRemoteChat(Line("come here", target: "maid_chan_67")));
        Assert.Equal(0, q.Count);
        // A clone's arbiter knows its own name (ExplicitlyAddressed): a line for it is taken there.
        var (clone, cq, _) = Make(o => o.Username = "maid_chan_67");
        Assert.True(clone.OnRemoteChat(Line("come here", target: "maid_chan_67")));
        Assert.Equal("viewer", Assert.Single(cq.Snapshot()).Origin);
    }

    [Fact]
    public void A_body_this_brain_drives_takes_the_line_and_the_brain_reads_whom_it_is_for()
    {
        var (r, q, _) = Make();
        r.ExplicitlyAddressed = m => m.Text.Contains("maid_chan_113", StringComparison.OrdinalIgnoreCase);
        Assert.True(r.OnRemoteChat(Line("come here", target: "maid_chan_113")));
        Assert.Equal("maid_chan_113, come here", Assert.Single(q.Snapshot()).Text);
    }

    [Fact]
    public void No_command_grant_unless_the_line_allows_it_and_the_gate_is_never_observed_otherwise()
    {
        var (r, _, gate) = Make();
        r.OnRemoteChat(Line("set it to day"));
        Assert.False(gate.Authorize("/time set day").Ok);

        // An in-game request stays open: a viewer line without commands never reaches Observe (which would close it).
        var (r2, _, gate2) = Make();
        gate2.Observe(new ChatMessage(ChatKind.Public, Fox, "maid chan set it to day", "<poofimafox> maid chan set it to day"), mine: true, explicitAddress: true);
        r2.OnRemoteChat(Line("thanks", target: "maid_chan"));
        r2.OnRemoteChat(Line("hi 67", target: "maid_chan_67")); // not this bot's: nothing at all
        var d = gate2.Authorize("/time set day");
        Assert.True(d.Ok);
        Assert.Equal(Fox, d.AuthorizedBy![..Fox.Length]);
        Assert.DoesNotContain("(viewer)", d.AuthorizedBy);
    }

    [Fact]
    public void An_allowed_viewer_request_arms_a_grant_audited_as_the_player_via_the_viewer()
    {
        using var dir = new TempDir();
        var audit = dir.File("command-audit.log");
        var (r, _, gate) = Make(audit: audit);
        Assert.True(r.OnRemoteChat(Line("set it to day", allow: true)));
        var d = gate.Authorize("/time set day");
        Assert.True(d.Ok);
        Assert.StartsWith("poofimafox (viewer)", d.AuthorizedBy);
        gate.Audit("/time set day", d.AuthorizedBy); // what run_command does after sending
        Assert.Contains("poofimafox (viewer)", File.ReadAllText(audit));
        Assert.True(gate.AllowOutgoing("/time set day"));

        // An exact command in an allowed line is a one-shot grant under the same label.
        r.OnRemoteChat(Line("run /weather clear", allow: true));
        Assert.Equal("poofimafox (viewer)", gate.Authorize("/weather clear").AuthorizedBy);
    }

    [Fact]
    public void Remote_grants_need_a_trusted_player()
    {
        var gate = new CommandGate(new CommandOptions(), () => [Fox]);
        Assert.Equal(0, gate.ObserveRemote("kiwi", "run /time set day", "viewer"));
        Assert.False(gate.Authorize("/time set day").Ok);
    }

    [Fact]
    public void Viewer_lines_have_their_own_rate_bucket()
    {
        var (r, q, _) = Make(o => { o.PerPlayerMaxMessages = 1; o.PerPlayerWindowSeconds = 60; });
        r.OnServerChat($"<{Fox}> maid chan hello");
        Assert.True(r.OnRemoteChat(Line("from the viewer")));
        var events = q.Snapshot();
        Assert.All(events, e => Assert.True(e.Wakes)); // the in-game line used the in-game bucket only
        r.OnRemoteChat(Line("again from the viewer"));
        Assert.Contains(q.Snapshot(), e => e.Text == "again from the viewer" && !e.Wakes); // its own bucket is full: context only
    }

    [Fact]
    public void Memory_reads_are_observed_with_their_owner()
    {
        var owner = new object();
        var seen = new List<(object, string, IReadOnlyList<MemoryAccessHit>)>();
        void On(object o, string s, IReadOnlyList<MemoryAccessHit> h) { if (ReferenceEquals(o, owner)) seen.Add((o, s, h)); }
        MemoryAccess.Accessed += On;
        try
        {
            MemoryAccess.Note(owner, "recall", [new RagHit(12, "note:1", "note", null, "t", "b", null, 0.7, 0, 0, null, DateTimeOffset.UnixEpoch, null)]);
        }
        finally { MemoryAccess.Accessed -= On; }
        var (_, source, hits) = Assert.Single(seen);
        Assert.Equal("recall", source);
        Assert.Equal(new MemoryAccessHit(12, "note", 0.7), Assert.Single(hits));
    }
}
