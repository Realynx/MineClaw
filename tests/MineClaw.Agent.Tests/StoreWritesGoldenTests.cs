using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using MineClaw.Agent.Rag;
using MineClaw.Agent.Self;

namespace MineClaw.Agent.Tests;

/// <summary>
/// Pins two store write paths (WP06 refactor): <see cref="SelfProfile.Apply"/> (every op, refusal, reinforce, eviction, history rows)
/// and <see cref="RagIndex.Upsert"/> (insert, unchanged, touched, text/kind/bounds/embed changes; the docs rows, vectors and the
/// vector search that follows).
/// </summary>
public class StoreWritesGoldenTests
{
    private const string ExpectedSelf = "A7169FB2D275C7C330E934CB0D68371D3AD0D2440328708BBEA419C6933B8496";
    private const string ExpectedRag = "C96999D038671855C98EA8BBAB6F11E9CAAFB4FB0EF36045D75E0D0937A4C789";

    private static string Hash(string dump, string file)
    {
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(dump)));
        var dir = Path.Combine(Path.GetTempPath(), "mineclaw-golden");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, file), dump);
        return hash;
    }

    [Fact]
    public void Self_profile_changes_apply_the_same()
    {
        using var db = AgentDb.Open(null);
        var time = new ManualTime();
        var o = new SelfOptions { MaxLikes = 2, BlockedWords = ["darn"] };
        var p = new SelfProfile(db, o, null, time);
        var sb = new StringBuilder();
        void Run(SelfChange c, string source = "reflection", IReadOnlyList<long>? eps = null, string? refs = null)
        {
            var r = p.Apply(c, source, eps, refs);
            sb.Append(c.Op).Append(' ').Append(c.Section).Append(' ').Append(c.Text ?? "-").Append(" => ").Append(r.Ok).Append(" | ").Append(r.Message)
              .Append(" | ").Append(r.EntryId?.ToString(CultureInfo.InvariantCulture) ?? "null").Append(" | ").Append(r.Version?.ToString(CultureInfo.InvariantCulture) ?? "null").Append('\n');
            time.Advance(TimeSpan.FromMinutes(7));
        }

        Run(new SelfChange("add", SelfSection.Like, "smelting iron at dawn"));
        Run(new SelfChange("add", SelfSection.Like, "smelting iron at dawn", Reason: "said it twice"));
        Run(new SelfChange("add", SelfSection.Like, "smelting iron at dawn", Reason: "again"), eps: [3, 4]);
        Run(new SelfChange("add", SelfSection.Like, "smelting iron at dawn"), SelfProfile.SourceAdmin);
        Run(new SelfChange(" ADD ", SelfSection.Like, "fishing by the dock", Reason: "  calm\nmornings  "), refs: "s12,s40");
        Run(new SelfChange("add", SelfSection.Like, "tidy chests", Reason: "full section"));
        Run(new SelfChange("add", SelfSection.Like, "", Reason: "empty"));
        Run(new SelfChange("add", SelfSection.Like, "darn creepers", Reason: "blocked"));
        Run(new SelfChange("add", SelfSection.Like, new string('x', 200), Reason: "long"));
        Run(new SelfChange("add", SelfSection.Relationship, "Fox is my oldest friend", Reason: "no player"));
        Run(new SelfChange("add", SelfSection.Relationship, "Fox is my oldest friend", "PoofImaFox", Reason: "player"));
        Run(new SelfChange("add", SelfSection.Relationship, "Fox is my oldest friend", "kiwi", Reason: "other player"));
        Run(new SelfChange("add", SelfSection.Relationship, "x", "bad name!!", Reason: "bad player"));
        Run(new SelfChange("reinforce", SelfSection.Like, Old: "smelting iron", Reason: "by text"));
        Run(new SelfChange("reinforce", SelfSection.Like, Old: "#1", Reason: "by hash id"));
        Run(new SelfChange("reinforce", SelfSection.Trait, Id: 99, Reason: "missing"));
        Run(new SelfChange("reinforce", SelfSection.Trait, Reason: "no target"));
        Run(new SelfChange("adjust", SelfSection.Like, "smelting gold at dusk", Id: 1, Reason: "taste changed"));
        Run(new SelfChange("adjust", SelfSection.Relationship, "Fox is my best friend", "PoofImaFox", Old: "Fox is my oldest friend", Reason: "closer"));
        Run(new SelfChange("adjust", SelfSection.Like, "", Id: 1, Reason: "empty"));
        Run(new SelfChange("adjust", SelfSection.Like, "x", Id: 77, Reason: "missing"));
        Run(new SelfChange("adjust", SelfSection.Like, "smelting gold at noon", Id: 1), SelfProfile.SourceAdmin);
        // Make the weakest like weaker than new, then fill the section so the next add evicts it.
        time.Advance(TimeSpan.FromDays(9));
        p.Decay();
        time.Advance(TimeSpan.FromDays(9));
        sb.Append("decay ").Append(p.Decay()).Append('\n');
        Run(new SelfChange("add", SelfSection.Like, "watching the sunrise", Reason: "evicts"));
        Run(new SelfChange("remove", SelfSection.Like, Id: 1, Reason: "gone"));
        Run(new SelfChange("remove", SelfSection.Like, Old: "fishing by the dock", Reason: "gone"));
        Run(new SelfChange("remove", SelfSection.Like, Id: 1, Reason: "again"));
        Run(new SelfChange("frob", SelfSection.Like, "x", Reason: "bad op"));
        Run(new SelfChange("add", SelfSection.Milestone, "first night on the island", Reason: "milestone"), "chat", [7]);

        foreach (var e in p.Entries)
            sb.Append("entry ").Append(e.Id).Append(' ').Append(e.Section).Append(' ').Append(e.Text).Append(' ').Append(e.Player ?? "null").Append(' ')
              .Append(e.Strength.ToString("R", CultureInfo.InvariantCulture)).Append(' ').Append(e.Observations).Append(' ').Append(e.Reason ?? "null").Append('\n');
        foreach (var h in db.Query("SELECT version, at, op, section, entry_id, text, reason, source, episodes, snapshot, scope FROM self_profile_history ORDER BY version",
                     r => string.Join(" | ", Enumerable.Range(0, r.FieldCount).Select(i => r.IsDBNull(i) ? "null" : Convert.ToString(r.GetValue(i), CultureInfo.InvariantCulture)))))
            sb.Append("hist ").Append(h).Append('\n');
        Assert.Equal(ExpectedSelf, Hash(sb.ToString(), "self-apply.txt"));
    }

    [Fact]
    public async Task Rag_upserts_write_the_same()
    {
        using var db = AgentDb.Open(null);
        var emb = new FakeEmbedder();
        using var rag = new RagIndex(db, new RagOptions(), emb, usage: null, backgroundEmbedding: false);
        var t0 = new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);
        var sb = new StringBuilder();
        long Up(RagDoc d)
        {
            long id = rag.Upsert(d);
            sb.Append("upsert ").Append(d.Ref).Append(" -> ").Append(id).Append('\n');
            return id;
        }
        async Task Dump(string label)
        {
            await rag.FlushEmbeddingsAsync();
            sb.Append("-- ").Append(label).Append('\n');
            foreach (var row in db.Query("""
                SELECT d.id, d.ref, d.kind, coalesce(d.tag,''), d.title, d.body, d.hash, coalesce(d.source,''), d.embed, d.weight, d.created, d.updated,
                       coalesce(d.min_x,''), coalesce(d.max_z,''), d.level, d.token_count, d.dirty, d.dimension,
                       (SELECT count(*) FROM vectors v WHERE v.doc_id = d.id)
                FROM docs d ORDER BY d.id
                """, r => string.Join(" | ", Enumerable.Range(0, r.FieldCount).Select(i => Convert.ToString(r.GetValue(i), CultureInfo.InvariantCulture)))))
                sb.Append(row).Append('\n');
            foreach (var q in new[] { "metal furnace", "stone wall", "wheat" })
                foreach (var kinds in new string[]?[] { null, ["note"], ["place"] })
                {
                    var hits = rag.Search(new RagQuery { Text = q, Kinds = kinds, Limit = 5 });
                    sb.Append("q ").Append(q).Append(' ').Append(kinds == null ? "*" : string.Join(",", kinds)).Append(':');
                    foreach (var h in hits) sb.Append(' ').Append(h.Ref);
                    sb.Append('\n');
                }
        }

        Up(new RagDoc("n:1", "note", "iron", "iron ingots smelt in the furnace") { At = t0 });
        Up(new RagDoc("n:2", "note", "wall", "cobblestone wall by the farm") { At = t0, Tag = "base" });
        Up(new RagDoc("p:1", "place", "farm", "the wheat farm") { At = t0, Bounds = new Box(0, 60, 0, 10, 70, 10), Dimension = 0 });
        Up(new RagDoc("x:1", "note", "silent", "not embedded wheat") { At = t0, Embed = false });
        await Dump("inserted");
        Up(new RagDoc("n:1", "note", "iron", "iron ingots smelt in the furnace"));
        Up(new RagDoc("n:2", "note", "wall", "cobblestone wall by the farm") { At = t0.AddHours(1), Tag = "base" });
        await Dump("unchanged + touched");
        Up(new RagDoc("n:1", "note", "iron", "iron ingots and coal smelt in the furnace") { At = t0.AddHours(2) });
        Up(new RagDoc("n:2", "place", "wall", "cobblestone wall by the farm") { At = t0.AddHours(2), Tag = "base" });
        Up(new RagDoc("p:1", "place", "farm", "the wheat farm") { At = t0.AddHours(2), Bounds = new Box(0, 60, 0, 12, 70, 12), Dimension = -1 });
        Up(new RagDoc("x:1", "note", "silent", "not embedded wheat") { At = t0.AddHours(2), Embed = true, Weight = 2 });
        await Dump("text, kind, bounds, embed on");
        Up(new RagDoc("n:1", "note", "iron", "iron ingots and coal smelt in the furnace") { At = t0.AddHours(3), Embed = false });
        Up(new RagDoc("n:2", "place", "wall", "cobblestone wall by the farm") { At = t0.AddHours(3), Tag = "walls" });
        Up(new RagDoc("p:1", "place", "farm", "the wheat farm") { At = t0.AddHours(3), Source = "observed" });
        Up(new RagDoc("p:1", "place", "farm", "the wheat farm") { At = t0.AddHours(3), Source = "observed", Bounds = new Box(0, 60, 0, 12, 70, 12), Dimension = -1 });
        await Dump("embed off, tag, bounds dropped and back");
        Assert.Equal(ExpectedRag, Hash(sb.ToString(), "rag-upsert.txt"));
    }
}
