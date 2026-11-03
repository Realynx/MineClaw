using System.Text.Json.Nodes;
using MineClaw.App.Supervisor;
using MineClaw.Hive.Story;

namespace MineClaw.App.Tests;

/// <summary>
/// <c>POST /api/story/colonies/{id}/online</c> {pawns:[name]|null, over_cap?} → {started, blocked:[{pawn, reason}]}: offline pawns are
/// whitelisted again and (re)started, the player cap permitting; over_cap is the owner's explicit choice to use the regulars' seats.
/// </summary>
public sealed class SupervisorStoryOnlineRouteTests
{
    private const string Token = "s3cret";

    private sealed class Host(SupervisorStoryRouteTests.World w) : IStoryHost
    {
        public IStoryWorld? World => w;
        public int N = 101;
        public HashSet<string> Online { get; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> Hosted { get; } = new(StringComparer.OrdinalIgnoreCase);
        public int Free = 1, Kept = 2;
        public List<(List<string> Accounts, bool Over)> Brings { get; } = [];
        public (IReadOnlyList<string> Accounts, string? Why) Spawn(string colony, int n, IReadOnlyList<string>? prefer) =>
            (Enumerable.Range(0, n).Select(_ => "maid_chan_" + N++).ToList(), null);
        public int Release(string colony) => 0;
        public PawnLive Live(string account) => Online.Contains(account) ? new(true, 20, 20, 0.5, 64, 0.5, 0, null, [], true) : PawnLive.Offline;
        public string? StartPawn(string account, string colony) => null;
        public void Interrupt(string account, string why) { }
        public bool InGame => false; // no background seat retry in these tests: only the route brings pawns in
        public PawnSeat Seat(string account) => new(Hosted.Contains(account), null);
        public (int Free, int Kept)? Seats() => (Free, Kept);
        public IReadOnlyList<(string Account, string? Why)> Bring(string colony, IReadOnlyList<string> accounts, bool overCap)
        {
            Brings.Add((accounts.ToList(), overCap));
            return accounts.Select(a =>
            {
                if (Free - 1 >= (overCap ? 0 : Kept)) { Free--; Hosted.Add(a); return (a, (string?)null); }
                return (a, $"server full (player cap: {Free} free, {Kept} kept for regulars)");
            }).ToList();
        }
    }

    private static async Task<(int Status, JsonNode? Json)> Call(SupervisorStoryRoutes r, string path, object? body)
    {
        var json = body is string s ? s : System.Text.Json.JsonSerializer.Serialize(body);
        var reply = await r.TryHandleAsync(new SupervisorRequest("POST", path, new Dictionary<string, string>(), Token, json));
        Assert.NotNull(reply);
        return (reply!.Status, reply.Body.Length > 0 ? JsonNode.Parse(reply.Text) : null);
    }

    private static (SupervisorStoryRoutes R, Host H, string Id) Make()
    {
        var o = new SupervisorOptions { Token = Token, Viewer = { Enabled = true } };
        var h = new Host(new SupervisorStoryRouteTests.World());
        var colonies = new StoryColonies(h);
        var (c, err) = colonies.Create("Riverside", 3, new P3(0, 64, 0)); // maid_chan_101..103
        Assert.Null(err);
        h.Online.Add("maid_chan_101");
        h.Hosted.Add("maid_chan_101");
        return (new SupervisorStoryRoutes(o, colonies), h, c!.Id);
    }

    [Fact]
    public async Task Bring_online_validates_its_body_and_names()
    {
        var (r, _, id) = Make();
        var path = $"/api/story/colonies/{id}/online";
        Assert.Equal(400, (await Call(r, path, new { pawns = "all" })).Status);
        Assert.Equal(400, (await Call(r, path, new { pawns = new[] { 1 } })).Status);
        Assert.Equal(400, (await Call(r, path, new { pawns = Array.Empty<string>() })).Status);
        Assert.Equal(400, (await Call(r, path, new { pawns = (string[]?)null, over_cap = "yes" })).Status);
        Assert.Equal(400, (await Call(r, path, "not json")).Status);
        Assert.Equal(404, (await Call(r, path, new { pawns = new[] { "maid_chan_999" } })).Status);
        Assert.Equal(404, (await Call(r, "/api/story/colonies/story-9/online", new { pawns = (string[]?)null })).Status);
    }

    [Fact]
    public async Task Bring_online_starts_one_all_or_reports_the_cap_and_over_cap_uses_the_regulars_seats()
    {
        var (r, h, id) = Make();
        var path = $"/api/story/colonies/{id}/online";
        h.Free = 4; // one pawn fits under the cap (4 − 1 ≥ 2 kept), then 3 − 1 ≥ 2 is another
        var (s, j) = await Call(r, path, new { pawns = new[] { "102" } }); // its number works like the account
        Assert.Equal(200, s);
        Assert.Equal(["maid_chan_102"], j!["started"]!.AsArray().Select(x => x!.GetValue<string>()));
        Assert.Empty(j["blocked"]!.AsArray());
        Assert.False(h.Brings[^1].Over);

        // All offline (null): 102 is hosted now but not in game, 103 isn't hosted; the cap lets one more in, then refuses.
        h.Free = 3;
        (s, j) = await Call(r, path, new { pawns = (string[]?)null });
        Assert.Equal(200, s);
        Assert.Equal(["maid_chan_102", "maid_chan_103"], h.Brings[^1].Accounts); // 101 is online: left alone
        Assert.Equal(["maid_chan_102"], j!["started"]!.AsArray().Select(x => x!.GetValue<string>()));
        var blocked = j["blocked"]!.AsArray().Single()!;
        Assert.Equal("maid_chan_103", blocked["pawn"]!.GetValue<string>());
        Assert.Equal("server full (player cap: 2 free, 2 kept for regulars)", blocked["reason"]!.GetValue<string>());

        // The state says why 103 is offline, and the seats.
        var colony = r.Colonies.Get(id)!;
        var st = colony.State();
        var p103 = st["pawns"]!.AsArray().Single(p => p!["name"]!.GetValue<string>() == "maid_chan_103")!;
        Assert.Equal("server full (player cap: 2 free, 2 kept for regulars)", p103["offline_reason"]!.GetValue<string>());
        Assert.Null(st["pawns"]!.AsArray().Single(p => p!["name"]!.GetValue<string>() == "maid_chan_101")!["offline_reason"]);
        Assert.Equal(1, st["seats"]!["online"]!.GetValue<int>());
        Assert.Equal(3, st["seats"]!["wanted"]!.GetValue<int>());
        Assert.Equal(2, st["seats"]!["cap_free"]!.GetValue<int>());
        Assert.Equal(2, st["seats"]!["kept_for_regulars"]!.GetValue<int>());

        // The owner's explicit choice: the seats kept for offline regulars.
        (s, j) = await Call(r, path, new { pawns = new[] { "maid_chan_103" }, over_cap = true });
        Assert.Equal(200, s);
        Assert.Equal(["maid_chan_103"], j!["started"]!.AsArray().Select(x => x!.GetValue<string>()));
        Assert.True(h.Brings[^1].Over);
        Assert.Equal("connecting", colony.State()["pawns"]!.AsArray().Single(p => p!["name"]!.GetValue<string>() == "maid_chan_103")!["offline_reason"]!.GetValue<string>());

        // An online pawn named explicitly is simply started (it's in game already).
        (s, j) = await Call(r, path, new { pawns = new[] { "maid_chan_101" } });
        Assert.Equal(["maid_chan_101"], j!["started"]!.AsArray().Select(x => x!.GetValue<string>()));
    }
}
