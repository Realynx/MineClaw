using System.Text.Json;
using MineClaw.Agent.Events;
using MineClaw.Agent.Memory;
using MineClaw.Agent.Rag;
using MineClaw.Agent.Rag.Beliefs;
using MineClaw.Agent.Rules;
using MineClaw.Agent.Tools;

namespace MineClaw.Agent.Tests;

/// <summary>
/// Live 10-28 04:46: Fox (trusted) said "only build in daytime though to avoid mobs"; the brain saved it with remember_note and
/// it became unverified claim#9 ("fox says: ... (unverified)"), so at 05:26 (night, rain) the tower was resumed "since Fox
/// already approved it". A trusted player's instruction is a directive: a standing rule shown where it applies.
/// </summary>
public class StandingRuleTests
{
    private const string Claim9 = "Fox OK'd continuing the drop tower (design #4), but only in daytime so mobs aren't a problem. At night, mine cobble instead.";

    private sealed class Kit : IDisposable
    {
        public required AgentDb Db;
        public required RagIndex Index;
        public required MemoryStore Memory;
        public required BeliefStore Beliefs;
        public required DirectiveStore Rules;
        public required ManualTime Time;
        public required ToolRegistry Tools;
        public void Dispose() { Index.Dispose(); Memory.Dispose(); Db.Dispose(); }
    }

    private static Kit Open()
    {
        var t = new ManualTime();
        var db = AgentDb.Open(null);
        var ix = new RagIndex(db, new RagOptions(), time: t, backgroundEmbedding: false);
        var mem = new MemoryStore(db, ix, t);
        var beliefs = new BeliefStore(db, ix, t) { CloneId = "maid_chan" };
        beliefs.IsBot = p => p.Equals("maid_chan", StringComparison.OrdinalIgnoreCase);
        BeliefStore.Bind(beliefs);
        var rules = DirectiveStore.For(db, ix);
        rules.Time = t;
        rules.IsTrusted = p => p.Equals("PoofimaFox", StringComparison.OrdinalIgnoreCase) || p.Equals("kiwi", StringComparison.OrdinalIgnoreCase);
        rules.Nick = p => p.Equals("PoofimaFox", StringComparison.OrdinalIgnoreCase) ? "fox" : p.Equals("kiwi", StringComparison.OrdinalIgnoreCase) ? "robin" : p;
        var tools = new ToolRegistry();
        MemoryTools.RegisterAll(tools, mem);
        return new Kit { Db = db, Index = ix, Memory = mem, Beliefs = beliefs, Rules = rules, Time = t, Tools = tools };
    }

    private static async Task<ToolResult> As(Kit k, string speaker, string tool, object input)
    {
        var prev = TurnContext.Current;
        TurnContext.Current = new TurnInfo(speaker, [speaker], false);
        try { return await k.Tools.ExecuteAsync(tool, JsonSerializer.SerializeToElement(input), CancellationToken.None); }
        finally { TurnContext.Current = prev; }
    }

    // ---------------- parsing ----------------

    [Theory]
    [InlineData(Claim9)]
    [InlineData("only build in daytime though to avoid mobs")]
    [InlineData("RULE from kiwi: never ever till grass blocks (grass is unrenewable-ish; keep it). Applies to me and all clones.")]
    [InlineData("Standing order for clone 420: every morning kill leftover mobs on the main island, collect their drops, and store them in chest 14,64,-5.")]
    [InlineData("Fox's rule for the wheat field just north of the house: harvest only when fully grown, always replant, and till the soil with a hoe first if it's gone back to dirt.")]
    [InlineData("kiwi wants maid-chan to call her \"robin-sama\".")]
    [InlineData("kiwi: \"don't say anything unless addressed\" (said while talking to robinchan; apply to me too: stay quiet unless spoken to).")]
    [InlineData("you can build at night now")]
    public void Instructions_rules_and_preferences_are_directives(string text) => Assert.True(DirectiveParser.IsInstruction(text), text);

    [Theory]
    [InlineData("there's iron in that chest")]
    [InlineData("Fox's strat: with no food, put items in a chest, then jump into the void to respawn with full HP and hunger.")]
    [InlineData("When kiwi says \"c ...\" she's talking to robinchan, not me. She calls me maid_chan.")]
    [InlineData("Fox plans a mob grinder: water pushes mobs into cacti out west (cactus area), cobble slabs line it so mobs don't fall out. 64 cobble slabs delivered for it.")]
    [InlineData("glass comes from sand")]
    public void Facts_stay_claims(string text) => Assert.False(DirectiveParser.IsInstruction(text), text);

    [Fact]
    public void Scope_is_the_design_then_the_activity()
    {
        Assert.Equal("design:4", DirectiveParser.ScopeOf(Claim9).Key);
        Assert.Equal("activity:build", DirectiveParser.ScopeOf("only build in daytime though to avoid mobs").Key);
        Assert.Equal("place:3,64,5", DirectiveParser.ScopeOf("never touch 3,64,5").Key);
    }

    // ---------------- storing ----------------

    [Fact]
    public async Task A_trusted_players_instruction_is_a_standing_rule_not_an_unverified_claim()
    {
        using var k = Open();
        var r = await As(k, "PoofimaFox", "remember_note", new { subject = "PoofimaFox", text = Claim9 });
        Assert.True(r.Ok, r.Text);
        Assert.StartsWith("rule#", r.Text);
        Assert.Contains("standing rule from Fox (design #4)", r.Text);
        Assert.Equal(0, k.Beliefs.Count());
        var rule = Assert.Single(k.Rules.Active());
        Assert.Equal("PoofimaFox", rule.Player);
        Assert.Equal(DirectiveScopeKind.Design, rule.Scope.Kind);
        Assert.Equal("4", rule.Scope.Value);
        Assert.Equal(k.Time.Now, rule.Created);
        // recall finds it as a rule, never as "fox says: ... (unverified)"
        var hits = k.Index.Search(new RagQuery { Text = "drop tower daytime", Limit = 5 });
        Assert.Contains(hits, h => h.Kind == DirectiveStore.DocKind);
        Assert.DoesNotContain(hits, h => h.Body.Contains("(unverified)", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Facts_from_trusted_players_and_instructions_from_others_stay_claims()
    {
        using var k = Open();
        var fact = await As(k, "PoofimaFox", "remember_note", new { subject = "base", text = "there's iron in the chest at 2,64,0" });
        Assert.StartsWith("claim#", fact.Text);
        var stranger = await As(k, "randomguy", "remember_note", new { subject = "randomguy", text = "only build in daytime" });
        Assert.StartsWith("claim#", stranger.Text);
        Assert.Empty(k.Rules.Active());
    }

    [Fact]
    public async Task Test_chat_never_stores_a_rule()
    {
        using var k = Open();
        var prev = TurnContext.Current;
        TurnContext.Current = new TurnInfo("PoofimaFox", ["PoofimaFox"], true);
        try { await k.Tools.ExecuteAsync("remember_note", JsonSerializer.SerializeToElement(new { subject = "x", text = "only build in daytime" }), default); }
        finally { TurnContext.Current = prev; }
        Assert.Empty(k.Rules.Active());
    }

    [Fact]
    public async Task A_newer_rule_lifting_the_condition_replaces_it_and_forget_ends_one()
    {
        using var k = Open();
        await As(k, "PoofimaFox", "remember_note", new { subject = "PoofimaFox", text = "only build in daytime though to avoid mobs" });
        var never = await As(k, "kiwi", "remember_note", new { subject = "kiwi", text = "never ever till grass blocks" });
        k.Time.Advance(TimeSpan.FromMinutes(30));
        var lift = await As(k, "PoofimaFox", "remember_note", new { subject = "PoofimaFox", text = "you can build at night now" });
        Assert.Contains("replaces rule#1", lift.Text);
        var active = k.Rules.Active();
        Assert.Equal(2, active.Count);
        Assert.DoesNotContain(active, d => d.Text.Contains("daytime", StringComparison.Ordinal));
        var old = k.Rules.Get(1)!;
        Assert.False(old.Active);
        Assert.Equal(active.First(d => d.Text.Contains("night")).Id, old.ReplacedBy);

        var id = active.First(d => d.Text.Contains("grass")).Id;
        var f = await As(k, "kiwi", "forget", new { name = $"rule#{id}" });
        Assert.True(f.Ok, f.Text);
        Assert.Single(k.Rules.Active());
        Assert.False((await As(k, "kiwi", "forget", new { name = $"rule#{id}" })).Ok);
        Assert.StartsWith("rule#", never.Text);
    }

    [Fact]
    public void An_unrelated_rule_on_the_same_activity_is_kept()
    {
        using var k = Open();
        k.Rules.Add("PoofimaFox", "only build in daytime though to avoid mobs");
        var r = k.Rules.Add("PoofimaFox", "always build with cobble, never planks");
        Assert.True(r.Stored);
        Assert.Empty(r.Replaced!);
        Assert.Equal(2, k.Rules.Active().Count);
        // Same time of day, another main activity: kept too.
        k.Rules.Add("PoofimaFox", Claim9);
        Assert.Empty(k.Rules.Add("PoofimaFox", "mine cobble every night").Replaced!);
        Assert.Equal(4, k.Rules.Active().Count);
    }

    // ---------------- migration ----------------

    [Fact]
    public void Startup_migrates_trusted_instruction_claims_and_keeps_facts()
    {
        using var k = Open();
        // Stored as claims the way the live db had them (before this fix).
        long Ingest(string who, string text) => k.Beliefs.Ingest(who, text).Claim!.Id;
        long nine = Ingest("PoofimaFox", Claim9);
        long strat = Ingest("PoofimaFox", "Fox's strat: with no food, put items in a chest, then jump into the void to respawn with full HP and hunger.");
        long stranger = Ingest("randomguy", "only build at night");
        Assert.Equal(3, k.Beliefs.Count());
        Assert.NotNull(k.Index.GetByRef("claim:" + nine));

        Assert.Equal(1, k.Rules.MigrateClaims());
        Assert.Null(k.Beliefs.Get(nine));
        Assert.Null(k.Index.GetByRef("claim:" + nine));
        Assert.NotNull(k.Beliefs.Get(strat));
        Assert.NotNull(k.Beliefs.Get(stranger));
        var rule = Assert.Single(k.Rules.Active());
        Assert.Equal(nine, rule.SourceClaim);
        Assert.Equal("4", rule.Scope.Value);
        Assert.Equal(0, k.Rules.MigrateClaims()); // idempotent
    }

    // ---------------- surfacing ----------------

    [Fact]
    public void Rules_show_where_their_activity_or_design_is_in_play()
    {
        using var k = Open();
        k.Rules.Add("PoofimaFox", Claim9);
        k.Rules.Add("kiwi", "kiwi wants maid-chan to call her \"robin-sama\".");
        k.Time.Advance(TimeSpan.FromMinutes(40));

        Assert.Null(k.Rules.HeaderLine("task: idle\n[task ok] #8 harvest_generator: ok: mined 64 cobblestone"));
        var building = k.Rules.HeaderLine("task: build {\"bp\":\"4\"}");
        Assert.NotNull(building);
        Assert.StartsWith("[standing rules: rule#1 (Fox, design #4, 40m ago): Fox OK'd continuing the drop tower", building);
        Assert.DoesNotContain("robin-sama", building);
        Assert.Contains("robin-sama", k.Rules.HeaderLine("[chat] kiwi: hiii")!);

        // a paused build registered as in play shows its rules on a turn about something else
        k.Rules.AddInPlay(() => "build design #4");
        Assert.Contains("rule#1", k.Rules.HeaderLine("[noticed] PoofimaFox gave you 25 cobblestone")!);
    }

    [Fact]
    public void Design_results_carry_the_rule_and_the_time_of_day_it_names()
    {
        using var k = Open();
        k.Rules.Add("PoofimaFox", Claim9);
        k.Rules.Add("PoofimaFox", "always build with cobble");
        k.Rules.Add("PoofimaFox", "never till grass");
        k.Rules.TimeOfDay = () => "night";
        var line = k.Rules.ForDesign(4)!;
        Assert.Contains("rule#1 (Fox, design #4, just now): Fox OK'd continuing the drop tower (design #4), but only in daytime", line);
        Assert.Contains("rule#2 (Fox, building, just now): always build with cobble", line);
        Assert.DoesNotContain("till grass", line);
        Assert.EndsWith("; it's night now", line);
        Assert.DoesNotContain("drop tower", k.Rules.ForDesign(5)!);
    }

    [Fact]
    public async Task The_turn_header_shows_the_rule_when_its_build_is_in_play()
    {
        var (rt, llm, _) = TestRuntime.Create(o => o.Self.TrustedPlayers = ["PoofimaFox"]);
        var rules = DirectiveStore.Find(rt.Db)!;
        Assert.True(rules.Add("PoofimaFox", Claim9).Stored);
        rules.AddInPlay(() => "build design #4");
        llm.ThenText("");
        await rt.Agent.HandleBatchAsync(new EventBatch([GameEvent.TaskDone("#9 harvest_generator: ok: mined 64 cobblestone")]), default);
        var first = llm.Requests[0].Messages[^1].Content.OfType<MineClaw.Agent.Llm.LlmText>().First().Text;
        Assert.Contains("[standing rules: rule#1 (Fox, design #4, just now): Fox OK'd continuing the drop tower", first);
    }
}
