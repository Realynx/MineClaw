using MineClaw.Agent.Rag;
using MineClaw.Data;
using MineClaw.MemoryExplorer.Services;
using MineClaw.MemoryExplorer.ViewModels;
using MineClaw.World;
using MineClaw.World.Designs;

namespace MineClaw.MemoryExplorer.Tests;

/// <summary>
/// The Designs page reads the bot's design history straight from the db (Fox 10-30: "similar to Git style"): the revision list
/// with author and message, a diff between two revisions, and a revision against the world as the bot last saw it.
/// </summary>
public class DesignHistoryExplorerTests
{
    /// <summary>The bot's own schema (Bot/Blueprints: BlueprintStore + DesignRevisions), written as the bot writes it.</summary>
    private static AgentDb Db()
    {
        var db = AgentDb.Open(null);
        db.Exec("""
            CREATE TABLE blueprints(id INTEGER PRIMARY KEY AUTOINCREMENT, name TEXT NOT NULL, description TEXT NOT NULL, requester TEXT, bot TEXT NOT NULL,
                dsl TEXT NOT NULL, status TEXT NOT NULL, dim INTEGER NOT NULL, sx INTEGER, sy INTEGER, sz INTEGER, ox INTEGER, oy INTEGER, oz INTEGER,
                rev INTEGER NOT NULL DEFAULT 0, summary TEXT, eval TEXT, critique TEXT, bill TEXT, cost_usd REAL NOT NULL DEFAULT 0, iterations INTEGER NOT NULL DEFAULT 0,
                accepted_by TEXT, png BLOB, created INTEGER NOT NULL, updated INTEGER NOT NULL, anchor TEXT);
            CREATE TABLE blueprint_revisions(blueprint_id INTEGER NOT NULL, rev INTEGER NOT NULL, author TEXT NOT NULL, author_kind TEXT NOT NULL,
                message TEXT NOT NULL, tag TEXT, created INTEGER NOT NULL, dim INTEGER NOT NULL, dsl TEXT NOT NULL, ox INTEGER, oy INTEGER, oz INTEGER,
                blocks TEXT NOT NULL, air TEXT NOT NULL, n INTEGER NOT NULL, hash TEXT NOT NULL, PRIMARY KEY(blueprint_id, rev));
            CREATE TABLE blueprint_world(blueprint_id INTEGER PRIMARY KEY, rev INTEGER NOT NULL, seen INTEGER NOT NULL, seen_by TEXT NOT NULL,
                blocks TEXT NOT NULL, air TEXT NOT NULL, diff TEXT NOT NULL, causes TEXT NOT NULL);
            INSERT INTO blueprints(name, description, requester, bot, dsl, status, dim, created, updated)
                VALUES('spiral_staircase', 'captured', 'PoofImaFox', 'maid_chan', 'blueprint x', 'captured', 0, 1, 2);
            """);
        return db;
    }

    private static void Rev(AgentDb db, int rev, string author, string message, DesignSnapshot s, string? tag = null) =>
        db.Exec("INSERT INTO blueprint_revisions VALUES(1,$r,$a,'player',$m,$t,$c,0,'',0,64,0,$b,$air,$n,$h)", ("$r", rev), ("$a", author), ("$m", message),
            ("$t", tag), ("$c", 1_000 * rev), ("$b", DesignSnapshot.EncodeStates(s.Blocks)), ("$air", DesignSnapshot.EncodeCells(s.Air)), ("$n", s.Count), ("$h", s.Hash()));

    [Fact]
    public void Revisions_list_newest_first_and_diff_between_two_revisions_and_against_the_world()
    {
        using var db = Db();
        var r1 = new DesignSnapshot();
        for (int y = 64; y <= 67; y++) r1.Blocks[new BlockPos(0, y, 0)] = (Blocks.Cobblestone, 0);
        r1.Blocks[new BlockPos(1, 64, 0)] = (Blocks.WoodenStairs, 0);
        r1.Air.Add(new BlockPos(1, 65, 0));
        var r2 = r1.Clone();
        r2.Blocks[new BlockPos(1, 65, 0)] = (Blocks.Planks, 0);
        r2.Blocks[new BlockPos(1, 64, 0)] = (Blocks.WoodenStairs, 2);
        r2.Blocks.Remove(new BlockPos(0, 67, 0));
        Rev(db, 1, "PoofImaFox", "captured by PoofImaFox", r1);
        Rev(db, 2, "PoofImaFox", "a landing", r2, "good");
        var seen = r2.Clone(); // the bot's last look: someone took a step away since
        seen.Blocks.Remove(new BlockPos(0, 64, 0));
        seen.Air.Add(new BlockPos(0, 64, 0));
        db.Exec("INSERT INTO blueprint_world VALUES(1,2,5000,'maid_chan',$b,$a,'−1 cobblestone','kiwi 1')",
            ("$b", DesignSnapshot.EncodeStates(seen.Blocks)), ("$a", DesignSnapshot.EncodeCells(seen.Air)));

        var src = new DesignHistorySource(db);
        var d = Assert.Single(src.Designs());
        Assert.Equal((2, 2), (d.Revisions, d.Head));
        Assert.Equal("captured · r2 · 2 revisions · for PoofImaFox", new DesignRow(d).Meta[..44]);
        var revs = src.Revisions(1);
        Assert.Equal([2, 1], revs.Select(r => r.Rev));
        var row = new DesignRevisionRow(revs[0]);
        Assert.StartsWith("r2 · PoofImaFox (player) · ", row.Label);
        Assert.EndsWith("· [good]", row.Label);
        Assert.Equal("'a landing' · 5 blocks", row.Message);

        var between = src.Between(1, 2, 1)!.Value; // either order: older → newer
        Assert.StartsWith("+1 planks, −1 cobblestone, ~1 ", between.Summary);
        Assert.EndsWith("(1 added, 1 removed, 1 changed)", between.Summary);
        Assert.Contains("+ 1,65,0  planks", between.Cells);
        Assert.Contains("− 0,67,0  cobblestone", between.Cells);

        var world = src.AgainstWorld(1, 2)!.Value;
        Assert.StartsWith("−1 cobblestone", world.Summary);
        Assert.Equal(["− 0,64,0  cobblestone"], world.Cells);
        Assert.Equal("kiwi 1", src.World(1)!.Causes);
    }

    [Fact]
    public void An_older_db_without_revisions_lists_its_designs_with_none()
    {
        using var db = AgentDb.Open(null);
        var src = new DesignHistorySource(db);
        Assert.False(src.Available);
        Assert.Empty(src.Designs());
        Assert.Empty(src.Revisions(1));
        Assert.Null(src.AgainstWorld(1, 1));
    }
}
