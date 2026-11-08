using MineClaw.Agent.Rag;
using MineClaw.ViewerBridge.Data;

namespace MineClaw.ViewerBridge.Tests;

public class DesignLibraryTests
{
    [Fact]
    public void Library_tombstone_filters_paging_search_and_counts_but_keeps_world_details_available()
    {
        using var db = AgentDb.Open(null);
        db.Exec("""
            CREATE TABLE blueprints(id INTEGER PRIMARY KEY, name TEXT, status TEXT, requester TEXT, updated INTEGER, png BLOB, dsl TEXT);
            INSERT INTO blueprints VALUES(1,'Storehouse','building','builder',1,NULL,'version 1.12.2');
            INSERT INTO blueprints VALUES(2,'Farm','accepted','builder',2,NULL,'version 1.12.2');
            """);
        var views = new DesignViews(db, "1.12.2");
        Assert.Equal(2, views.List(null, 0, 24).Total); // Also works with a pre-upgrade database.
        db.Exec("CREATE TABLE designer_library_deleted(design_id INTEGER PRIMARY KEY); INSERT INTO designer_library_deleted VALUES(1);");
        var list = views.List(null, 0, 24);
        Assert.Equal(1, list.Total);
        Assert.Equal(2, Assert.Single(list.Items).Id);
        Assert.Empty(views.List("Storehouse", 0, 24).Items);
        Assert.Empty(views.List(null, 1, 24).Items);
        Assert.NotNull(views.Summary(1)); // Existing build references and history still resolve.
        Assert.Equal("building", views.Summary(1)!.Status);
    }
}
