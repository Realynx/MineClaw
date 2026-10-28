using MineClaw.Agent.Admin;
using MineClaw.MemoryExplorer.Services;

namespace MineClaw.MemoryExplorer.Tests;

/// <summary>The explorer's local edit target: edits go through the bot's stores, re-embed, show up in a fresh read-only open, and
/// leave an audit line next to the db; the form prefill round-trips.</summary>
public sealed class EditTargetTests : IAsyncLifetime
{
    private FixtureDb _fx = null!;

    public async Task InitializeAsync() => _fx = await FixtureDb.CreateAsync();
    public Task DisposeAsync() { _fx.Dispose(); return Task.CompletedTask; }

    [Fact]
    public async Task A_local_fact_edit_reembeds_and_changes_what_search_finds()
    {
        var fake = new HashEmbeddings();
        using (var bank = await MemoryBank.OpenAsync(_fx.Path, FixtureDb.Config()))
        {
            var r = await new SearchService(bank).SearchAsync("melon");
            Assert.Contains(r.Hits, h => h.Hit.Ref == "note:2");
            Assert.Empty((await new SearchService(bank).SearchAsync("pumpkin")).Hits);
        }

        using (var target = new LocalAdminTarget(_fx.Path, FixtureDb.Config(), fake))
        {
            var backup = await target.BackupAsync(CancellationToken.None);
            Assert.True(File.Exists(backup));
            Assert.StartsWith(LocalAdminTarget.BackupDirFor(_fx.Path), backup);

            var row = new MemoryQueries(target.Context.Db).Rows("facts").Single(f => f.Id == "2");
            var before = EditPrefill.For(MemoryKinds.Fact, row);
            var after = new Dictionary<string, string?>(before) { ["text"] = "the pumpkin patch needs water on both sides" };
            var changed = EditPrefill.Changed(before, after);
            Assert.Equal(["text"], changed.Keys);
            var res = await target.ApplyAsync(new MemoryEdit("update", MemoryKinds.Fact, "2", changed, "admin:test", "test"), CancellationToken.None);
            Assert.True(res.Ok, res.Message);
            Assert.Contains("melon", res.Before);
            Assert.Contains("pumpkin", res.After);
            Assert.True(fake.Calls > 0);
        }

        using (var bank = await MemoryBank.OpenAsync(_fx.Path, FixtureDb.Config()))
        {
            Assert.Contains((await new SearchService(bank).SearchAsync("pumpkin")).Hits, h => h.Hit.Ref == "note:2");
            Assert.DoesNotContain((await new SearchService(bank).SearchAsync("melon")).Hits, h => h.Hit.Ref == "note:2");
        }

        var audit = LocalAdminTarget.AuditPathFor(_fx.Path);
        Assert.True(File.Exists(audit));
        var entries = new AdminAudit(audit).Read();
        var e = Assert.Single(entries, x => x.Op == "update");
        Assert.Equal("admin:test", e.Who);
        Assert.Equal(MemoryKinds.Fact, e.Kind);
        Assert.StartsWith("local:", e.Target);
        Assert.True(e.Ok);
    }

    [Fact]
    public async Task Prefill_maps_rows_to_the_schema_fields()
    {
        using var bank = await MemoryBank.OpenAsync(_fx.Path, FixtureDb.Config());
        var q = new MemoryQueries(bank.Db);
        var place = EditPrefill.For(MemoryKinds.Place, q.Rows("places").Single(p => p.Name == "nether hub"));
        Assert.Equal(("10", "70", "-3", "nether"), (place["x"], place["y"], place["z"], place["dim"]));
        var st = EditPrefill.For(MemoryKinds.Structure, q.Rows("structures").Single());
        Assert.Equal("stand at 3,65,-2", st["steps"]);
        Assert.Equal("cobble_generator", st["kind"]);
        foreach (var kind in new[] { MemoryKinds.Fact, MemoryKinds.Place, MemoryKinds.Structure })
        {
            var names = MemoryEditSchema.For(kind).Select(f => f.Name).ToHashSet();
            var row = kind switch { MemoryKinds.Fact => q.Rows("facts")[0], MemoryKinds.Place => q.Rows("places")[0], _ => q.Rows("structures")[0] };
            Assert.All(EditPrefill.For(kind, row).Keys, k => Assert.Contains(k, names));
        }
    }

    [Fact]
    public async Task Adding_and_deleting_a_place_round_trips()
    {
        using var target = new LocalAdminTarget(_fx.Path, FixtureDb.Config(), new HashEmbeddings());
        var add = await target.ApplyAsync(new MemoryEdit("add", MemoryKinds.Place, null,
            new Dictionary<string, string?> { ["name"] = "melon farm", ["x"] = "20", ["y"] = "64", ["z"] = "-8" }, "admin:test"), CancellationToken.None);
        Assert.True(add.Ok, add.Message);
        Assert.Contains(new MemoryQueries(target.Context.Db).Rows("places"), p => p.Name == "melon farm");
        var del = await target.ApplyAsync(new MemoryEdit("delete", MemoryKinds.Place, "melon farm", new Dictionary<string, string?>(), "admin:test"), CancellationToken.None);
        Assert.True(del.Ok, del.Message);
        Assert.DoesNotContain(new MemoryQueries(target.Context.Db).Rows("places"), p => p.Name == "melon farm");
        Assert.Equal(2, (await target.AuditAsync(10, CancellationToken.None)).Count);
    }
}
