using MineClaw.Agent.Rag;
using MineClaw.ViewerBridge.Data;

namespace MineClaw.ViewerBridge.Tests;

/// <summary>
/// A design's kind as the bridge reports it (the designs list, one design, each of its revisions). The bridge reads it from the
/// DSL the bot saved: a mine is the statement <c>kind mine</c>, a redstone circuit the comment line <c># kind redstone</c>
/// (the bot writes it first, DesignerSave), anything else a build.
/// </summary>
public class DesignKindTests
{
    // The head of each kind's DSL as the studio saves it.
    private const string Circuit =
        "# kind redstone\n# redstone layout 5e884898da28047151d0e56f8dc6292773603d0d6aabbdd62a11ef721d1542d8\n# drawn in the Design Studio\n"
        + "blueprint xor\nsize 2x1x1\nversion 1.12.2\nfacing south\npalette R=redstone_wire S=stone\nlayer 0\nRS\n";
    private const string Hut = "# drawn in the Design Studio\nblueprint hut\nsize 1x1x1\nversion 1.12.2\nfacing south\npalette S=stone\nlayer 0\nS\n";
    private const string Shaft =
        "# a mine drawn in the Design Studio: dig the clear boxes, then place the blocks\nblueprint shaft\nsize 1x2x1\nversion 1.12.2\n"
        + "kind mine\nanchor absolute 10,11,10\nfacing south\nclear 0,0,0 0,1,0\n";

    private static AgentDb Db()
    {
        var db = AgentDb.Open(null);
        db.Exec("""
            CREATE TABLE blueprints(id INTEGER PRIMARY KEY, name TEXT, status TEXT, requester TEXT, updated INTEGER, png BLOB, dsl TEXT);
            CREATE TABLE blueprint_revisions(blueprint_id INTEGER NOT NULL, rev INTEGER NOT NULL, author TEXT, author_kind TEXT, message TEXT,
                tag TEXT, created INTEGER NOT NULL, n INTEGER NOT NULL, dsl TEXT, blocks TEXT, air TEXT, PRIMARY KEY(blueprint_id, rev));
            """);
        return db;
    }

    private static void Design(AgentDb db, long id, string name, string? dsl) =>
        db.Exec("INSERT INTO blueprints VALUES($id,$n,'accepted','PoofImaFox',1,NULL,$d)", ("$id", id), ("$n", name), ("$d", dsl));

    private static void Revision(AgentDb db, long id, int rev, string dsl) =>
        db.Exec("INSERT INTO blueprint_revisions VALUES($id,$r,'PoofImaFox','player','saved',NULL,$c,2,$d,'','')", ("$id", id), ("$r", rev), ("$c", 1000 * rev),
            ("$d", dsl));

    [Fact]
    public void The_list_and_a_design_report_build_mine_and_redstone()
    {
        using var db = Db();
        Design(db, 1, "hut", Hut);
        Design(db, 2, "shaft", Shaft);
        Design(db, 3, "xor", Circuit);
        var views = new DesignViews(db, "1.12.2");
        var kinds = views.List(null, 0, 24).Items.ToDictionary(d => d.Id, d => d.Kind);
        Assert.Equal("build", kinds[1]);
        Assert.Equal("mine", kinds[2]);
        Assert.Equal("redstone", kinds[3]);
        Assert.Equal("redstone", views.Summary(3)!.Kind);
        Assert.Equal("redstone", views.Detail(3)!.Design.Kind);
        // The tag is a comment to everything else: the version still reads from the same DSL.
        Assert.Equal("1.12.2", views.Summary(3)!.Version);
    }

    [Fact]
    public void Each_revision_reports_the_kind_it_was_saved_as()
    {
        using var db = Db();
        // Drawn as a build, then switched to Redstone in the studio and saved again.
        Design(db, 7, "door opener", Circuit);
        Revision(db, 7, 1, Hut);
        Revision(db, 7, 2, Circuit);
        var views = new DesignViews(db, "1.12.2");
        Assert.Equal([(2, "redstone"), (1, "build")], views.Revisions(7).Select(r => (r.Rev, r.Kind!)));
        Assert.Equal("redstone", views.Summary(7)!.Kind);
        Assert.Equal(["redstone", "build"], views.Detail(7)!.Revisions.Select(r => r.Kind));
    }

    [Theory]
    [InlineData("# kind redstone\nblueprint x", "redstone")]
    [InlineData("#kind redstone\nblueprint x", "redstone")]
    [InlineData("  #  KIND   Redstone  \nblueprint x", "redstone")]
    [InlineData("# anchor 1,0,1\r\n# kind redstone\r\nblueprint x\r\n", "redstone")]
    [InlineData("blueprint x\n# kind redstone", "redstone")]
    // Only the whole comment line is the tag: the words in a name or inside another comment are not.
    [InlineData("# kind redstone torch tower\nblueprint x", "build")]
    [InlineData("# a new kind redstone\nblueprint x", "build")]
    [InlineData("blueprint kind redstone\nsize 1x1x1", "build")]
    [InlineData("kind redstone\nblueprint x", "build")]
    [InlineData("# kind redstones\nblueprint x", "build")]
    // A mine is its own plot: its statement decides, whatever a comment says.
    [InlineData("# kind redstone\nblueprint x\nkind mine\n", "mine")]
    public void Only_the_whole_comment_line_tags_a_circuit(string dsl, string kind)
    {
        using var db = Db();
        Design(db, 1, "x", dsl);
        Assert.Equal(kind, new DesignViews(db, "b1.8.1").Summary(1)!.Kind);
    }

    [Fact]
    public void A_design_without_a_dsl_is_a_build()
    {
        using var db = Db();
        Design(db, 1, "empty", null);
        Assert.Equal("build", new DesignViews(db, "b1.8.1").Summary(1)!.Kind);
    }
}
