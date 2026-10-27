using System.Text.Json;
using MineClaw.Agent.Context;
using MineClaw.Agent.Events;
using MineClaw.Agent.Llm;
using MineClaw.Agent.Memory;
using MineClaw.Agent.Rag;

namespace MineClaw.Agent.Tests;

/// <summary>Deterministic stale-lesson corrections (saplings CAN be planted in the dark; only growth needs light 9).</summary>
public class KnowledgeCorrectionTests
{
    private static JsonElement J(object o) => JsonSerializer.SerializeToElement(o);

    /// <summary>Unrelated memories, so the lexical idf gate behaves like a real (not 5-doc) corpus.</summary>
    private static void Filler(MemoryStore mem)
    {
        for (int i = 0; i < 30; i++) mem.AddNote("world", $"cobble generator shift {i}: mined stone into chest {i},64,-5");
    }

    private const string StaleEpisode =
        "10-27 00:42 @12,73,3: [task failed] chop_tree failed: chopped oak tree, 5/5 logs, not replanted | [task failed] #1 chop_tree: " +
        "replant blocked: too dark at 13,65,0 (needs light 8+) (chopped oak tr... | you: tree's down foxy, got 5 logs for you uwu. couldn't replant cuz it's too dark rn";
    private const string StaleNote =
        "Chopped trees at 21,65,3 and 19,66,3 at night: no sapling replanted at 21,64,3 (dirt free). Need saplings from leaves + replant in daylight.";

    // Real stale lines from the live db (episodes, RAPTOR summaries, notes, chat).
    [Theory]
    [InlineData(StaleEpisode)]
    [InlineData(StaleNote)]
    [InlineData("you: Okie foxy, 67 is grabbing an axe to chop and replant uwu. It's night though, and last time a repl...")]
    [InlineData("you: 67 cut down 10 trees, 38 logs are in the chest. It couldn't replant in the dark, but there are 10...")]
    [InlineData("PoofimaFox and the clones spent the night chopping oak trees, with many replants failing in the dark.")]
    [InlineData("Fox directed 67 to chop and replant oak trees (10 trees and 38 logs chopped, replanting failed in the dark), then mine.")]
    [InlineData("The log records a maid bot chopping oak trees, with most trees left unreplanted because it was too dark (replanting needs light 8+).")]
    [InlineData("since saplings won't take in the dark anyway. A starving maid powering through the night")]
    [InlineData("tree spot at 25,65,3: 0 trees, 1 saplings; chopped, not replanted: too dark at 25,65,3 (needs light 8+) (by me)")]
    [InlineData("last time a replant failed because it was too dark, so I told it to put a torch down if that happens.")]
    [InlineData("you need a torch to plant a sapling")]
    public void Matches_StaleDarkPlantingClaims(string text) =>
        Assert.NotNull(new KnowledgeCorrections(KnowledgeCorrections.BuiltIn).Superseding(text));

    [Theory]
    [InlineData("torches stop mobs spawning in the dark")]
    [InlineData("saplings grow only at light 9")]
    [InlineData("saplings won't grow in the dark; put torches nearby")]
    [InlineData("Sapling growth pauses at night unless torches are nearby.")]
    [InlineData("Hostile mobs spawn on solid opaque blocks at light 7 or less; light the island so nothing spawns at night.")]
    [InlineData("was told to replant melon seeds on hoed, wet farmland with torches nearby")]
    [InlineData("Trees chopped at night yielded no saplings and weren't replanted; Fox's replant method was noted.")]
    [InlineData("Skeletons spawn at night and burn in the sun; collect bones in the morning for bonemeal (melons/saplings).")]
    [InlineData("Saplings can be planted any time, night included, if they see the sky; darkness never blocks planting.")]
    [InlineData("replanted oak sapling at 9,65,3, growing (by me)")]
    [InlineData("couldn't place the torch, it's too dark to see the edge")]
    public void Ignores_TrueOrUnrelatedStatements(string text) =>
        Assert.Null(new KnowledgeCorrections(KnowledgeCorrections.BuiltIn).Superseding(text));

    [Fact]
    public async Task Apply_HidesStaleEpisodeAndNote_FromRecallAndAutoRetrieval_SurfacesCorrection()
    {
        using var mem = new MemoryStore(null);
        Filler(mem);
        var ix = mem.Index;
        ix.Upsert(new RagDoc("ep:1:0", "episode", "chop_tree failed", StaleEpisode));
        var note = mem.AddNote("trees", StaleNote);
        var mobs = mem.AddNote("base", "torches stop mobs spawning in the dark around the trees");
        var growth = mem.AddNote("trees", "saplings grow only at light 9, so replant and torch the tree farm");

        var cx = KnowledgeCorrections.Apply(ix);
        Assert.Same(cx, ix.Corrections);
        var marked = KnowledgeCorrections.Marked(mem.Db).Select(m => m.Ref).ToList();
        Assert.Equal(["ep:1:0", "note:" + note.Id], marked.Order().ToList());

        var recall = mem.Recall("replant sapling dark trees torch");
        Assert.DoesNotContain("too dark", recall);
        Assert.DoesNotContain("daylight", recall);
        Assert.Contains("CAN be planted in the dark", recall);
        Assert.Single(recall.Split('\n'), l => l.Contains("CAN be planted"));          // one correction, not one per stale hit
        Assert.Contains("mobs spawning", recall);                                      // unrelated torch/dark memory untouched
        Assert.Contains("light 9", recall);

        var auto = new AutoRetriever(ix, new ContextManager());
        var block = await auto.BuildAsync(new EventBatch([GameEvent.Chat("Fox", "can you replant the saplings even though it's dark?")]),
            ModelTier.Main, null, null, default);
        Assert.Contains("CAN be planted", block);
        Assert.DoesNotContain("too dark", block);
        Assert.DoesNotContain("daylight", block);

        // recall_detail on a stale id still answers, but leads with the correction.
        var hit = ix.GetByRef("note:" + note.Id)!;
        Assert.NotNull(cx.Superseding(hit));
        Assert.Null(cx.Superseding(ix.GetByRef("note:" + mobs.Id)!));
        Assert.Null(cx.Superseding(ix.GetByRef("note:" + growth.Id)!));
    }

    [Fact]
    public void Apply_IsIdempotent_AcrossRestarts()
    {
        using var dir = new TempDir();
        var path = dir.File("mineclaw.db");
        long docs, updated;
        using (var mem = new MemoryStore(path))
        {
            mem.AddNote("trees", StaleNote);
            KnowledgeCorrections.Apply(mem.Index);
            KnowledgeCorrections.Apply(mem.Index);
            docs = mem.Db.ScalarLong("SELECT count(*) FROM docs");
            updated = mem.Db.ScalarLong("SELECT updated FROM knowledge_corrections WHERE id = 'sapling_dark'");
            Assert.Equal(1, mem.Db.ScalarLong("SELECT count(*) FROM docs WHERE ref LIKE 'corr:%'"));
        }
        using (var mem = new MemoryStore(path))
        {
            Assert.Null(mem.Index.Corrections);                                       // nothing until applied at startup
            KnowledgeCorrections.Apply(mem.Index);
            Assert.Equal(docs, mem.Db.ScalarLong("SELECT count(*) FROM docs"));
            Assert.Equal(1, mem.Db.ScalarLong("SELECT count(*) FROM knowledge_corrections"));
            Assert.Equal("system", mem.Db.Scalar("SELECT source FROM knowledge_corrections") as string);
            Assert.Equal("system", mem.Db.Scalar("SELECT source FROM docs WHERE ref = 'corr:sapling_dark'") as string);
            Assert.Equal(updated, mem.Db.ScalarLong("SELECT updated FROM knowledge_corrections WHERE id = 'sapling_dark'"));
            Assert.Single(KnowledgeCorrections.Marked(mem.Db));
            Assert.DoesNotContain("daylight", mem.Recall("replant daylight trees"));
        }
    }

    [Fact]
    public void Apply_RemovesCorrectionsNoLongerListed_AndLoadsKnowledgeFile()
    {
        using var dir = new TempDir();
        File.WriteAllText(Path.Combine(dir.Path, KnowledgeCorrections.FileName), """
            [{"id":"cactus_water","title":"correction: cactus","correction":"superseded: cactus does not need water",
              "clauses":[{"all":["\\bcactus\\b","\\bneeds?\\s+water\\b"]}]}]
            """);
        using var mem = new MemoryStore(null);
        Filler(mem);
        var n = mem.AddNote("farm", "cactus needs water next to it");
        var cx = KnowledgeCorrections.Apply(mem.Index, dir.Path);
        Assert.Equal(2, cx.Items.Count);
        Assert.Contains("does not need water", mem.Recall("cactus water"));
        Assert.DoesNotContain("next to it", mem.Recall("cactus water"));

        File.Delete(Path.Combine(dir.Path, KnowledgeCorrections.FileName));
        KnowledgeCorrections.Apply(mem.Index, dir.Path);
        Assert.Null(mem.Index.GetByRef("corr:cactus_water"));
        Assert.Contains("next to it", mem.Recall("cactus water"));
        Assert.NotNull(mem.Index.GetByRef("note:" + n.Id));
    }

    [Fact]
    public async Task MemoryWrittenAfterApply_IsCaughtAtRetrieval_UnrelatedUntouched()
    {
        using var mem = new MemoryStore(null);
        Filler(mem);
        KnowledgeCorrections.Apply(mem.Index);
        mem.AddNote("trees", "replanting failed again at 9,65,3 because it was too dark, place a torch first");
        mem.Index.Upsert(new RagDoc("raptor:9", RagIndex.SummaryKind, "summary L1", "Foxy chopped trees; most stayed unreplanted because it was too dark."));
        mem.AddNote("base", "torches stop mobs spawning in the dark");
        mem.AddNote("trees", "saplings grow only at light 9");

        var recall = mem.Recall("replanted dark sapling torch light", maxHits: 8, maxChars: 2000);
        Assert.DoesNotContain("9,65,3", recall);
        Assert.DoesNotContain("unreplanted", recall);
        Assert.Contains("CAN be planted", recall);
        Assert.Contains("torches stop mobs spawning in the dark", recall);
        Assert.Contains("saplings grow only at light 9", recall);

        // Explicit kind filters still get the correction in place of the stale hit.
        var notes = await mem.Index.SearchAsync(new RagQuery { Text = "replanting too dark", Kinds = ["note", "player"] });
        Assert.DoesNotContain(notes, h => h.Body.Contains("9,65,3"));
        Assert.Contains(notes, h => h.Ref == "corr:sapling_dark");
    }

    [Fact]
    public async Task Runtime_AppliesAtStartup_RecallDetailAndChatAreAnnotated()
    {
        var (rt, _, _) = TestRuntime.Create();
        using var _r = rt;
        Filler(rt.Memory);
        Assert.NotNull(rt.Rag.Corrections);
        Assert.NotNull(rt.Rag.GetByRef("corr:sapling_dark"));

        var t = DateTimeOffset.UtcNow;
        rt.Rag.LogChat(t, "bot", "Clawd_bot", "Okie foxy, 67 is grabbing an axe to chop and replant uwu. It's night though, and last time a replant");
        rt.Rag.LogChat(t.AddMilliseconds(300), "bot", "Clawd_bot", "failed because it was too dark, so I told it to put a torch down if that happens.");
        rt.Rag.LogChat(t.AddSeconds(1), "chat", "Fox", "torches stop mobs spawning in the dark");
        var chat = (await rt.Tools.ExecuteAsync("recall", J(new { query = "torch dark", kind = "chat" }), default)).Text;
        Assert.Contains("[superseded:", chat);
        Assert.Contains(chat.Split('\n'), l => l.Contains("Fox") && !l.Contains("superseded"));

        var note = rt.Memory.AddNote("trees", StaleNote);
        var id = rt.Rag.GetByRef("note:" + note.Id)!.Id;
        var detail = (await rt.Tools.ExecuteAsync("recall_detail", J(new { id = "#" + id }), default)).Text;
        Assert.StartsWith("[superseded: saplings", detail);
        var recall = (await rt.Tools.ExecuteAsync("recall", J(new { query = "replant daylight" }), default)).Text;
        Assert.DoesNotContain("Need saplings from leaves", recall);
        Assert.Contains("CAN be planted", recall);
    }
}
