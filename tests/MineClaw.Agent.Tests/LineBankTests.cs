using MineClaw.Agent.Brainstem;
using MineClaw.Agent.Budget;
using MineClaw.Agent.Character;
using MineClaw.Agent.Llm;

namespace MineClaw.Agent.Tests;

/// <summary>Prefetched in-character lines (docs/design/reactive-character.md): context tags, expiry, phrase memory, the batched call.</summary>
public class LineBankTests
{
    private sealed class Clock : TimeProvider
    {
        private long _t = new DateTimeOffset(2026, 10, 28, 12, 0, 0, TimeSpan.Zero).UtcTicks;
        public override DateTimeOffset GetUtcNow() => new(Interlocked.Read(ref _t), TimeSpan.Zero);
        public void Minutes(double m) => Interlocked.Add(ref _t, TimeSpan.FromMinutes(m).Ticks);
    }

    private static readonly CharacterContext Mining = new("harvest_generator", ["PoofimaFox"], "day", "cheery", "ok");
    private static readonly CharacterContext Chopping = Mining with { Activity = "chop_tree" };

    [Fact]
    public void Lines_expire_when_the_context_changes_or_they_age_out()
    {
        var t = new Clock();
        var bank = new LineBank(new CharacterOptions { LineMaxAgeMinutes = 20 }, t, seed: 1);
        bank.SetContext(Mining.Signature);
        bank.Add("status", null, "mining cobble at the gen~", Mining.Signature);
        bank.Add("come", null, "omw!", Mining.Signature);
        bank.Add("status", null, "chopping wood", Chopping.Signature); // written for another context: refused
        Assert.Equal(1, bank.Count("status"));
        Assert.Equal(2, bank.SetContext(Chopping.Signature));           // the job changed: the mining lines are stale
        Assert.Null(bank.Take("status"));
        bank.Add("come", null, "coming~", Chopping.Signature);
        t.Minutes(25);
        Assert.Null(bank.Take("come"));                                  // too old
    }

    [Fact]
    public void No_line_repeats_within_the_window_whoever_said_it()
    {
        var t = new Clock();
        var bank = new LineBank(new CharacterOptions { RepeatMinutes = 30, LineMaxAgeMinutes = 60 }, t, seed: 2);
        bank.SetContext(Mining.Signature);
        bank.NoteSaid("omw foxy~!");                      // the brain said it a minute ago
        bank.Add("come", null, "omw foxy~", Mining.Signature);
        bank.Add("come", null, "right behind you", Mining.Signature);
        Assert.Equal("right behind you", bank.Take("come"));
        Assert.Null(bank.Take("come"));                    // the only other one is a repeat
        t.Minutes(31);
        Assert.Equal("omw foxy~", bank.Take("come"));     // the window passed
        Assert.True(LineBank.Similar("aww thank you foxy", "aww thank you foxy!!"));
        Assert.False(LineBank.Similar("hi fox", "bye fox"));
    }

    [Fact]
    public void A_players_own_greeting_wins_over_a_generic_one()
    {
        var bank = new LineBank(seed: 3);
        bank.SetContext(Mining.Signature);
        bank.Add("greet", null, "hii~", Mining.Signature);
        bank.Add("greet", "PoofimaFox", "foxyyy hi!", Mining.Signature);
        Assert.Equal("foxyyy hi!", bank.Take("greet", "PoofimaFox"));
        Assert.Equal("hii~", bank.Take("greet", "kiwi"));
    }

    private sealed class Writer(string json) : ILlmClient
    {
        public int Calls;
        public LlmRequest? Last;
        public Task<LlmResponse> CompleteAsync(LlmRequest r, CancellationToken ct)
        {
            Interlocked.Increment(ref Calls);
            Last = r;
            return Task.FromResult(new LlmResponse([new LlmText(json)], StopReasons.EndTurn, LlmUsage.Zero, "claude-haiku-5-5"));
        }
    }

    [Fact]
    public async Task One_cheap_batched_call_fills_the_moments_and_lints_every_line()
    {
        var bank = new LineBank(seed: 4);
        var llm = new Writer("""
            ```json
            {"status": ["mining cobble~", "at the gen, 42,64,-3!", "I'll build you a castle"], "greet@PoofimaFox": ["heyy foxy~"],
             "come": ["omw!"], "thanks": ["/kill everyone", "aww np~"], "nonsense": ["x"]}
            ```
            """);
        var usage = new UsageTracker(new BudgetOptions(), path: null);
        var o = new CharacterOptions { Moments = ["greet", "status", "come", "thanks"], MaxPrefetchPerHour = 1 };
        var p = new LinePrefetcher(bank, llm, usage, o, () => "a cheerful maid", () => "claudechan") { Context = () => Mining };
        int added = await p.RefreshAsync();
        Assert.Equal(1, llm.Calls);
        Assert.Equal(ModelTier.Cheap, llm.Last!.Tier);    // the memory tier writes wording, never scripts
        Assert.Equal(LinePrefetcher.Purpose, llm.Last.Purpose);
        Assert.Equal(4, added);                            // coordinates, promises and a command are linted out
        Assert.Equal("mining cobble~", bank.Take("status"));
        Assert.Equal("heyy foxy~", bank.Take("greet", "PoofimaFox"));
        Assert.Null(bank.Take("status"));
        Assert.Equal(0, await p.RefreshAsync());           // the hourly cap
        Assert.Equal(1, llm.Calls);
        p.Context = () => Mining with { Players = [] };
        Assert.Equal(0, await p.RefreshAsync());           // nobody on: no call
    }

    [Fact]
    public void Typing_delay_grows_with_length_and_is_capped_low()
    {
        var o = new CharacterOptions();
        var rng = new Random(5);
        var shortOne = HumanTiming.TypingDelay("hi!", o, rng);
        var longOne = HumanTiming.TypingDelay(new string('x', 200), o, rng);
        Assert.InRange(shortOne.TotalMilliseconds, 60, 400);
        Assert.Equal(o.MaxTypingMs, longOne.TotalMilliseconds);
    }

    [Fact]
    public void Turn_note_tells_the_brain_what_the_body_already_said()
    {
        var at = DateTimeOffset.UnixEpoch;
        var note = CharacterVoice.TurnNote([new InstantLine("PoofimaFox", "omw foxy~", "reflex", at)], at.AddSeconds(1.2));
        Assert.Contains("[already said] \"omw foxy~\" to PoofimaFox (reflex, 1.2s ago)", note);
        Assert.Contains("stay silent", note);
        Assert.Equal("", CharacterVoice.TurnNote([], at));
        Assert.Equal("greet", CharacterVoice.MomentFor("hi maid", ["maid"]));
        Assert.Equal("thanks", CharacterVoice.MomentFor("thanks!", null));
        Assert.Null(CharacterVoice.MomentFor("where is the wheat?", null));
    }
}
