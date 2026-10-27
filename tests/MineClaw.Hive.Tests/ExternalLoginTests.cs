using MineClaw.Agent.Events;
using MineClaw.Client;
using MineClaw.Client.Tests;
using MineClaw.Hive.Clone;
using MineClaw.Hive.Slots;
using MineClaw.Hive.Supervisor;
using MineClaw.Protocol.Packets;

namespace MineClaw.Hive.Tests;

/// <summary>
/// Someone else (a player's viewer) logged in with a clone's name. b1.8.1 kicks the OLDER session with "You logged in from
/// another location", so the hive must never fight the login: (a) a clone name on the server's player list that isn't ours
/// is skipped before launch, (b) a clone kicked that way exits without reconnecting, and main leaves the slot alone for 10 min
/// and is told "slot X is in use by a player's viewer".
/// </summary>
public class ExternalLoginTests
{
    private static HiveConfig Cfg(params string[] clones)
    {
        var c = TestHive.Config();
        c.CloneUsernames = clones;
        c.OwnLogoutGraceSeconds = 0;
        return c;
    }

    [Fact]
    public async Task A_clone_name_on_the_servers_player_list_that_isnt_ours_is_skipped_before_launch()
    {
        // Main's real client on the fake server: the 0xC9 tab list is the player list the supervisor checks.
        using var srv = new FakeServer();
        await using var main = new MinecraftClient(new ClientOptions
        {
            Username = "maid_chan", TickInterval = TimeSpan.FromMilliseconds(5), HostileAlertRadius = 0, ConnectionFactory = srv.CreateClientConnection,
        });
        using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10))) await main.ConnectAsync(cts.Token);
        await using var h = new TestHive(Cfg("maid_chan_1337", "maid_chan_67")); // 1337 first: it would be picked on a tie
        h.Facts = () => new SlotFacts(main.World.MaxPlayers, main.World.Entities.OnlinePlayers);
        h.Supervisor.SeenOnServer = n => main.World.Entities.FindPlayer(n) != null;

        srv.Send(new PlayerListItemPacket { PlayerName = "maid_chan_1337", Online = true, Ping = 5 }); // Fox's viewer
        await Wait.Until(() => main.World.Entities.OnlinePlayers.Contains("maid_chan_1337"), 5, "tab list entry");

        var named = h.Supervisor.Spawn("mine 5 cobble then done", name: "1337");
        Assert.StartsWith("no: 1337 is in use by a player's viewer", named);
        Assert.Contains("67 is free", named);
        var unnamed = h.Supervisor.Spawn("mine 5 cobble then done");
        Assert.StartsWith("ok: 67 spawning", unnamed);
        var s1337 = h.Supervisor.Slot("maid_chan_1337")!;
        Assert.Equal(CloneState.Idle, s1337.State);
        Assert.Null(s1337.Process);
        Assert.Contains("slot 1337 is in use by a player's viewer", h.NoticeText());
        Assert.Contains("1337 is in use by a player's viewer", h.Supervisor.List());
        // Told once, not on every pick.
        h.Supervisor.Spawn("chop a tree", name: "1337");
        lock (h.Notices) Assert.Single(h.Notices, n => n.Text.Contains("slot 1337 is in use"));

        // The viewer logs out: the slot is free again (no cooldown: ours was never kicked).
        srv.Send(new PlayerListItemPacket { PlayerName = "maid_chan_1337", Online = false, Ping = 0 });
        await Wait.Until(() => !main.World.Entities.OnlinePlayers.Contains("maid_chan_1337"), 5, "tab list removal");
        Assert.Null(h.Supervisor.ExternalOccupant(s1337));
        Assert.DoesNotContain("in use", h.Supervisor.List().Split(" | ")[0]);
    }

    [Fact]
    public async Task A_clone_kicked_by_a_duplicate_login_exits_without_reconnecting_and_main_leaves_the_slot_alone()
    {
        var marker = Path.Combine(Path.GetTempPath(), "mineclaw-dupkick-" + Guid.NewGuid().ToString("N")[..8] + ".txt");
        await using var h = new TestHive(Cfg("maid_chan_1337", "maid_chan_67"), mode: "dupkick", stubArgs: ["--marker", marker]);
        var r = h.Supervisor.Spawn("stand by for the viewer test", name: "1337");
        Assert.StartsWith("ok", r);
        var slot = h.Supervisor.Slot("maid_chan_1337")!;

        // The stub's real client is kicked by its fake server with b1.8.1's duplicate-login text.
        await Wait.Until(() => h.NoticeText().Contains("slot 1337 is in use by a player's viewer"), 30, "occupied notice");
        Assert.Equal(CloneState.Idle, slot.State);
        Assert.Empty(slot.Crashes);                                        // not a crash: no restart, no retirement
        Assert.InRange(slot.ExternalUntil - DateTimeOffset.UtcNow, TimeSpan.FromMinutes(9.5), TimeSpan.FromMinutes(10.1));
        GameEvent notice;
        lock (h.Notices) notice = h.Notices.Last(n => n.Text.Contains("slot 1337 is in use"));
        Assert.Contains("not using that slot for 10 min", notice.Text);
        Assert.Contains("\"stand by for the viewer test\" is unfinished: give it to another clone", notice.Text);
        Assert.True(notice.MustWake);
        Assert.Equal(EventPriority.Normal, notice.Priority);

        // The clone process exited 76 and its client never dialled again (no fight for the account).
        await Wait.Until(() => File.Exists(marker), 10, "stub exit marker");
        Assert.Equal($"EXIT {CloneExitCodes.ExternalLogin} connects=1 playing=False", File.ReadAllText(marker));

        // Within the cooldown the slot isn't used, named or not, even though nobody is on the tab list.
        Assert.StartsWith("no: 1337 is in use by a player's viewer (someone else logged in as maid_chan_1337)", h.Supervisor.Spawn("mine cobble", name: "1337"));
        var other = h.Supervisor.Spawn("mine cobble then done");
        Assert.StartsWith("ok: 67", other);
        Assert.Contains("1337 is in use by a player's viewer", h.Supervisor.List());
        try { File.Delete(marker); } catch (Exception) { }
    }

    [Theory]
    [InlineData("You logged in from another location", true)]
    [InlineData("you LOGGED IN FROM ANOTHER LOCATION", true)]
    [InlineData("Server closed", false)]
    [InlineData("The server is full!", false)]
    [InlineData(null, false)]
    public void Only_the_duplicate_login_kick_counts(string? reason, bool dup) => Assert.Equal(dup, CloneHost.IsDuplicateLogin(reason));
}
