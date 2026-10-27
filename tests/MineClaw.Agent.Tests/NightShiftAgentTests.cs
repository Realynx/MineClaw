using MineClaw.Agent.Chat;
using MineClaw.Agent.Events;
using MineClaw.Agent.Rag;

namespace MineClaw.Agent.Tests;

/// <summary>
/// Night shift 10-27: main posted "4,64,6 cobblestone" to public chat (a scan dump), the clone's first query embedding timed
/// out on the container (DNS + first TLS), and an unattended hive must not check in more than hourly.
/// </summary>
public class NightShiftAgentTests
{
    // ---------------------------------------------------------------- pure data never reaches chat

    [Theory]
    [InlineData("4,64,6 cobblestone")]
    [InlineData("5,64,6 water")]
    [InlineData("4,6=cobblestone 5,6=water 6,6=water 4,7=lava 5,7=cobblestone")]
    [InlineData("(3,5=stone_slab 4,5=air 5,5=stone_slab)")]
    [InlineData("stone_slab@7,64,6 | lava@4,64,7 x6")]
    public void Pure_data_lines_are_dropped(string line)
    {
        Assert.True(MetaSpeech.IsData(line), line);
        Assert.Equal("", MetaSpeech.Strip(line, out var dropped));
        Assert.Equal([line], dropped);
    }

    [Theory]
    [InlineData("the chest is at 14,64,-5 foxy")]
    [InlineData("14,64,-5")]
    [InlineData("I'm at 4,64,6 uwu")]
    [InlineData("mining cobblestone at 4,64,6 now")]
    [InlineData("Cobblestone, 64 of it!")]
    public void Speech_with_coordinates_is_kept(string line)
    {
        Assert.False(MetaSpeech.IsData(line), line);
        Assert.Equal(line, MetaSpeech.Strip(line, out _));
    }

    [Fact]
    public async Task The_exact_leaked_line_never_reaches_chat_and_is_logged_as_meta()
    {
        var (rt, llm, sent) = TestRuntime.Create();
        llm.ThenText("4,64,6 cobblestone");
        await rt.Agent.HandleBatchAsync(new EventBatch([GameEvent.Chat("PoofimaFox", "maid, which block is the cell?")]), default);
        Assert.Empty(sent);
        llm.ThenText("4,64,6 cobblestone\nthat's the west cell foxy, right next to the lava uwu");
        await rt.Agent.HandleBatchAsync(new EventBatch([GameEvent.Chat("PoofimaFox", "maid, which block is the cell?")]), default);
        Assert.Equal(["that's the west cell foxy, right next to the lava uwu"], sent);
    }

    // ---------------------------------------------------------------- idle check-ins while unattended

    [Fact]
    public void Idle_check_ins_are_hourly_at_most_while_nobody_is_online()
    {
        var (rt, _, _) = TestRuntime.Create(o => { o.IdleCheckInMinutes = 20; o.IdleCheckInMinutesNoPlayers = 60; });
        bool humans = true;
        rt.Agent.HumansOnline = () => humans;
        Assert.Equal(20, rt.Agent.IdleCheckInMinutesNow());
        humans = false;
        Assert.Equal(60, rt.Agent.IdleCheckInMinutesNow());
        Assert.Equal(60, new AgentOptions().IdleCheckInMinutesNoPlayers); // the default
    }

    [Fact]
    public void No_players_and_no_unattended_interval_means_no_check_ins()
    {
        var (rt, _, _) = TestRuntime.Create(o => { o.IdleCheckInMinutes = 20; o.IdleCheckInMinutesNoPlayers = 0; });
        rt.Agent.HumansOnline = () => false;
        Assert.Equal(0, rt.Agent.IdleCheckInMinutesNow());
    }

    // ---------------------------------------------------------------- query embeddings on a cold container

    /// <summary>Call number <paramref name="slowCall"/> pays DNS + TLS (slow); the others are fast. Counts cancellations.</summary>
    private sealed class ColdEmbedder(TimeSpan delay, int slowCall = 1) : IEmbeddingProvider
    {
        private readonly FakeEmbedder _inner = new();
        public int Calls;
        public int Cancelled;
        public string Model => "cold";
        public int Dimensions => 256;
        public bool IsAvailable => true;

        public async Task<EmbeddingResponse> EmbedAsync(IReadOnlyList<string> inputs, CancellationToken ct)
        {
            if (Interlocked.Increment(ref Calls) == slowCall)
            {
                try { await Task.Delay(delay, ct); }
                catch (OperationCanceledException) { Interlocked.Increment(ref Cancelled); throw; }
            }
            return await _inner.EmbedAsync(inputs, ct);
        }
    }

    [Fact]
    public async Task Warm_up_at_startup_makes_the_first_turns_query_embedding_fast()
    {
        using var db = AgentDb.Open(null);
        var emb = new ColdEmbedder(TimeSpan.FromMilliseconds(1200));
        using var ix = new RagIndex(db, new RagOptions { QueryEmbedTimeoutMs = 400, ColdQueryExtraMs = 0 }, emb, backgroundEmbedding: false);
        ix.Upsert(new RagDoc("n:1", "note", "gen", "the dual cobble generator"));
        Assert.False(ix.EmbeddingsWarm);
        await ix.WarmUpAsync();                 // startup: pays the slow first call, off the turn path
        Assert.True(ix.EmbeddingsWarm);
        await ix.FlushEmbeddingsAsync();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        Assert.NotNull(await ix.EmbedQueryAsync("where is the cobble gen", default));
        Assert.True(sw.ElapsedMilliseconds < 400, $"{sw.ElapsedMilliseconds} ms");
    }

    [Fact]
    public async Task A_slow_query_embedding_never_blocks_the_turn_and_finishes_into_the_cache()
    {
        using var db = AgentDb.Open(null);
        var emb = new ColdEmbedder(TimeSpan.FromMilliseconds(1500), slowCall: 2); // 1 = the doc, 2 = the query
        using var ix = new RagIndex(db, new RagOptions { QueryEmbedTimeoutMs = 200, ColdQueryExtraMs = 0, WarmUpAttempts = 0 }, emb, backgroundEmbedding: false);
        ix.Upsert(new RagDoc("n:1", "note", "gen", "the dual cobble generator"));
        await ix.FlushEmbeddingsAsync();
        Assert.True(ix.VectorCount > 0);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        Assert.Null(await ix.EmbedQueryAsync("where is the cobble gen", default)); // FTS-only for this turn...
        Assert.True(sw.ElapsedMilliseconds < 1000, $"turn blocked {sw.ElapsedMilliseconds} ms");
        await Task.Delay(1700);                                                     // ...the request finishes anyway
        Assert.Equal(0, emb.Cancelled);
        sw.Restart();
        Assert.NotNull(await ix.EmbedQueryAsync("where is the cobble gen", default)); // next turn: from the cache
        Assert.True(sw.ElapsedMilliseconds < 100, $"{sw.ElapsedMilliseconds} ms");
        Assert.Equal(2, emb.Calls);
    }
}
