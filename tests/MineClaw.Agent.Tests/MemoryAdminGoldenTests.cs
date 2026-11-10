using System.Security.Cryptography;
using System.Text;
using MineClaw.Agent.Admin;
using MineClaw.Agent.Rag;

namespace MineClaw.Agent.Tests;

/// <summary>
/// Pins <see cref="MemoryAdmin.ApplyAsync"/> (WP06 refactor) over a scripted run of good and bad edits on every kind: each
/// result, each audit entry (minus time and target path) and the docs table afterwards.
/// </summary>
public sealed class MemoryAdminGoldenTests : IDisposable
{
    private const string Expected = "F6FD0EC6B0D274AC779B1BE5A2D5D609D6B132AC352E9B2EA637FF4B27FB8288";

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "mineclaw-admin-golden-" + Guid.NewGuid().ToString("N"));

    public MemoryAdminGoldenTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private static MemoryEdit E(string op, string kind, string? id, params (string K, string? V)[] fields) =>
        new(op, kind, id, fields.ToDictionary(f => f.K, f => f.V), "PoofImaFox", "golden");

    [Fact]
    public async Task Scripted_edits_apply_the_same()
    {
        var dbPath = Path.Combine(_dir, "memory.db");
        using var ctx = MemoryAdminContext.OpenLocal(dbPath, new AgentOptions { KnowledgeDirectory = null, DataDirectory = "unused" },
            new FakeEmbedder(), RaptorIntegrationTests.SummaryLlm());
        var audit = new AdminAudit(Path.Combine(_dir, "admin-audit.jsonl"));
        var admin = new MemoryAdmin(ctx, audit, "local:golden");
        var sb = new StringBuilder();
        async Task<MemoryEditResult> Run(MemoryEdit e)
        {
            var r = await admin.ApplyAsync(e);
            sb.Append(e.Op).Append(' ').Append(e.Kind).Append(' ').Append(e.Id ?? "-").Append(" => ").Append(r.Ok).Append(" | ").Append(r.Message)
              .Append(" | ").Append(r.Id ?? "null").Append(" | ").Append(r.Before ?? "null").Append(" | ").Append(r.After ?? "null").Append('\n');
            return r;
        }

        // validation
        await Run(E("add", "unicorn", null));
        await Run(E("frobnicate", "fact", "1"));
        await Run(new MemoryEdit("  ADD ", "Fact", null, new Dictionary<string, string?> { ["subject"] = "base", ["text"] = "upper-case op" }, "  ", null));
        await Run(E("add", "fact", null, ("subject", "base")));
        await Run(E("add", "fact", null, ("subject", "base"), ("bogus", "y")));

        // fact
        var f1 = await Run(E("add", "fact", null, ("subject", "base"), ("text", "the wheat farm is north of spawn")));
        await Run(E("add", "fact", null, ("subject", "base"), ("text", "the wheat farm is north of spawn")));
        await Run(E("update", "fact", f1.Id, ("text", "the wheat farm is east of spawn")));
        await Run(E("update", "fact", f1.Id));
        await Run(E("update", "fact", "abc", ("text", "x")));
        await Run(E("update", "fact", null, ("text", "x")));
        await Run(E("update", "fact", "#999", ("text", "x")));
        await Run(E("update", "fact", "#" + f1.Id, ("subject", "farm")));
        await Run(E("delete", "fact", f1.Id));
        await Run(E("delete", "fact", f1.Id));

        // place
        await Run(E("add", "place", null, ("name", "home"), ("x", "1"), ("y", "64"), ("z", "2"), ("note", "the hut")));
        await Run(E("add", "place", null, ("name", "home"), ("x", "1"), ("y", "64"), ("z", "2")));
        await Run(E("add", "place", null, ("name", "shed"), ("y", "64"), ("z", "2")));
        await Run(E("add", "place", null, ("name", "nether hub"), ("x", "8"), ("y", "70"), ("z", "-8"), ("dim", "nether"), ("note", "")));
        await Run(E("update", "place", "home", ("name", "old hut"), ("x", "5"), ("note", ""), ("dim", "nether")));
        await Run(E("add", "place", null, ("name", "barn"), ("x", "0"), ("y", "0"), ("z", "0")));
        await Run(E("update", "place", "old hut", ("name", "Barn")));
        await Run(E("update", "place", "old hut", ("name", "OLD HUT"), ("note", "renamed")));
        await Run(E("update", "place", "nowhere", ("x", "1")));
        await Run(E("update", "place", "barn"));
        await Run(E("update", "place", "barn", ("y", "70"), ("z", "3"), ("dim", "overworld")));
        await Run(E("update", "place", "barn", ("x", "abc")));
        await Run(E("delete", "place", "barn"));
        await Run(E("delete", "place", null));

        // structure
        await Run(E("add", "structure", null, ("name", "gen"), ("kind", "cobble_generator"), ("steps", "stand at 1,64,1\nmine 2,64,1"), ("min", "0,63,0"), ("max", "3,65,3")));
        await Run(E("add", "structure", null, ("name", "gen")));
        await Run(E("update", "structure", "gen", ("confidence", "1.5")));
        await Run(E("update", "structure", "gen", ("confidence", "0.9"), ("note", " works "), ("kind", ""), ("steps", "a\n\n  b  \n")));
        await Run(E("update", "structure", "gen", ("min", "bad")));
        await Run(E("update", "structure", "gen", ("dim", "nether"), ("max", ""), ("note", "")));
        await Run(E("add", "structure", null, ("name", " tower "), ("kind", " lookout "), ("dim", "end")));
        await Run(E("update", "structure", "gen", ("name", "Tower")));
        await Run(E("update", "structure", "gen", ("name", "Gen2")));
        await Run(E("update", "structure", "gen2"));
        await Run(E("delete", "structure", "Gen2"));
        await Run(E("update", "structure", "missing", ("note", "x")));

        // claim
        var c1 = await Run(E("add", "claim", null, ("claimant", "Robin"), ("text", "there are diamonds in the chest at 3,64,7")));
        await Run(E("add", "claim", null, ("claimant", "Robin"), ("text", "there is iron in the chest at 5,64,9"), ("status", "junk")));
        await Run(E("add", "claim", null, ("claimant", "Fox"), ("text", "there is gold in the chest at 6,64,9"), ("status", "refuted"), ("reason", "looked")));
        await Run(E("add", "claim", null, ("claimant", "Fox"), ("text", "the furnace at 1,64,1 has coal"), ("confidence", "0.4")));
        await Run(E("add", "claim", null, ("claimant", "Fox"), ("text", "hello there")));
        await Run(E("update", "claim", c1.Id, ("status", "refuted"), ("confidence", "0.1"), ("reason", "checked by hand")));
        await Run(E("update", "claim", c1.Id));
        await Run(E("update", "claim", c1.Id, ("status", "unverified")));
        await Run(E("update", "claim", c1.Id, ("confidence", "0.5")));
        await Run(E("update", "claim", c1.Id, ("confidence", "2")));
        await Run(E("update", "claim", c1.Id, ("reason", "")));
        await Run(E("update", "claim", "x" + c1.Id, ("reason", "x")));
        await Run(E("delete", "claim", c1.Id));
        await Run(E("delete", "claim", c1.Id));

        // trait
        var t1 = await Run(E("add", "trait", null, ("section", "like"), ("text", "watching the sunrise from the island edge")));
        await Run(E("add", "trait", null, ("section", "nonsense"), ("text", "x")));
        var t2 = await Run(E("add", "trait", null, ("section", "like"), ("text", "fishing at the dock"), ("player", "Robin"), ("reason", "she said so")));
        await Run(E("update", "trait", t1.Id, ("text", "watching the sunset from the island edge")));
        await Run(E("update", "trait", t1.Id, ("section", "dislike")));
        await Run(E("update", "trait", t1.Id, ("section", "like"), ("text", "watching the stars")));
        await Run(E("update", "trait", t2.Id, ("player", "")));
        await Run(E("update", "trait", t2.Id, ("player", "Fox")));
        await Run(E("update", "trait", t1.Id));
        await Run(E("delete", "trait", t1.Id));
        await Run(E("update", "trait", "777", ("text", "x")));

        // lesson
        await Run(E("add", "lesson", null, ("title", "lava buckets"), ("correction", "superseded: lava CAN be picked up with an empty bucket."),
            ("patterns", "lava && (?:can'?t|cannot) && bucket")));
        await Run(E("add", "lesson", null, ("title", "lava buckets"), ("correction", "x"), ("patterns", "lava")));
        await Run(E("add", "lesson", null, ("title", "!!!"), ("correction", "x"), ("patterns", "lava")));
        await Run(E("add", "lesson", null, ("title", "bad regex"), ("id", "Bad Regex"), ("correction", "x"), ("patterns", "* (unclosed")));
        await Run(E("update", "lesson", "lava_buckets", ("correction", "superseded: an empty bucket picks up lava source blocks.")));
        await Run(E("update", "lesson", "lava_buckets", ("title", "Lava & buckets"), ("patterns", "* lava\\s+is\\s+unpickable")));
        await Run(E("update", "lesson", "lava_buckets"));
        await Run(E("delete", "lesson", KnowledgeCorrections.SaplingDark.Id));
        await Run(E("update", "lesson", KnowledgeCorrections.SaplingDark.Id, ("correction", "saplings need light level 9.")));
        await Run(E("delete", "lesson", KnowledgeCorrections.SaplingDark.Id));
        await Run(E("delete", "lesson", "lava_buckets"));
        await Run(E("update", "lesson", "nope", ("title", "x")));

        // observation
        var row = ctx.Observations.Upsert(new Observation("chest", "3,64,7", "chest with 4 iron") { Dimension = 0 });
        var nether = ctx.Observations.Upsert(new Observation("portal", "3,70,4", "a nether portal") { Dimension = -1 });
        await Run(E("update", "observation", row.DocRef, ("text", "chest with 4 iron and 2 gold"), ("annotation", "checked")));
        await Run(E("update", "observation", nether.DocRef, ("annotation", "lit")));
        await Run(E("update", "observation", row.DocRef, ("text", "")));
        await Run(E("update", "observation", row.DocRef));
        await Run(E("update", "observation", row.Id.ToString(), ("text", "chest with 5 iron")));
        await Run(E("update", "observation", "obs:bad", ("text", "x")));
        await Run(E("update", "observation", "zzz", ("text", "x")));
        await Run(E("delete", "observation", row.Id.ToString()));
        await Run(E("delete", "observation", nether.DocRef));

        sb.Append("-- audit\n");
        foreach (var a in audit.Read())
            sb.Append(a.Who).Append(' ').Append(a.Op).Append(' ').Append(a.Kind).Append(' ').Append(a.Id ?? "null").Append(' ').Append(a.Ok)
              .Append(' ').Append(a.Reason ?? "null").Append(" | ").Append(a.Message).Append(" | ").Append(a.Before ?? "null").Append(" | ").Append(a.After ?? "null").Append('\n');
        sb.Append("-- docs\n");
        foreach (var d in ctx.Db.Query("SELECT ref, kind, coalesce(tag,''), title, body, level, dirty FROM docs ORDER BY ref",
                     r => $"{r.GetString(0)} {r.GetString(1)} {r.GetString(2)} {r.GetString(3)} | {r.GetString(4)} | {r.GetInt64(5)} {r.GetInt64(6)}"))
            sb.Append(d.ReplaceLineEndings("\\n")).Append('\n');

        // Self-profile change episodes carry the wall clock (yyyy-MM-dd HH:mm).
        var dump = System.Text.RegularExpressions.Regex.Replace(sb.ToString(), @"\d{4}-\d{2}-\d{2} \d{2}:\d{2}", "<time>");
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(dump)));
        if (hash != Expected)
        {
            var dir = Path.Combine(Path.GetTempPath(), "mineclaw-golden");
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "memory-admin.txt"), dump);
        }
        Assert.Equal(Expected, hash);
    }
}
