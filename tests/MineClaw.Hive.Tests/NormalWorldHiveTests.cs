using MineClaw.Agent.Rag;
using MineClaw.Data.Versioning;
using MineClaw.Hive.Clone;
using MineClaw.Hive.Db;
using MineClaw.Protocol.Release1122;

namespace MineClaw.Hive.Tests;

/// <summary>Multi-version P6: login refusals (not whitelisted, full), the confirmed whitelist add, and void-spawn facts only on skyblock.</summary>
public class NormalWorldHiveTests
{
    [Theory]
    [InlineData("You are not white-listed on this server!", CloneExitCodes.NotWhitelisted)]
    [InlineData("The server is full!", CloneExitCodes.ServerFull)]
    [InlineData("You logged in from another location", null)]
    [InlineData("Server closed", null)]
    [InlineData(null, null)]
    public void Login_refusals_have_their_own_exit(string? reason, int? code) => Assert.Equal(code, CloneHost.RefusalCode(reason));

    [Fact]
    public void The_1122_lang_knows_the_refusals_and_the_whitelist_reply()
    {
        static string T(string key, params string[] with) => ChatComponent.ToPlain(ChatComponent.Translate(key, with));
        Assert.Equal("You are not white-listed on this server!", T("multiplayer.disconnect.not_whitelisted"));
        Assert.Equal("The server is full!", T("multiplayer.disconnect.server_full"));
        Assert.Equal("Added maid_chan_67 to the whitelist", T("commands.whitelist.add.success", "maid_chan_67"));
    }

    [Fact]
    public async Task A_whitelist_add_counts_only_after_the_servers_confirmation()
    {
        var kit = new HierarchyKit();
        var (acc, _) = kit.H.Allocate(new MineClaw.Hive.Ipc.HiveSpawnOptions());
        Assert.NotNull(acc);
        kit.Whitelist.ConfirmTimeout = TimeSpan.FromMilliseconds(200);
        kit.Whitelist.NoteServerLine("<kiwi> hi"); // the reply feed is wired
        Assert.False(await kit.Whitelist.AddAsync(acc!));                 // sent, never confirmed
        Assert.False(kit.Managed.All().Single(a => a.Name == acc).Whitelisted);

        kit.Whitelist.ConfirmTimeout = TimeSpan.FromSeconds(5);
        var add = kit.Whitelist.AddAsync(acc!);
        await Task.Delay(20);
        kit.Whitelist.NoteServerLine($"Added {acc} to the whitelist");
        Assert.True(await add);
        Assert.True(kit.Managed.All().Single(a => a.Name == acc).Whitelisted);
    }

    [Fact]
    public void Fresh_void_spawn_is_a_skyblock_fact_only()
    {
        var dir = Path.Combine(Path.GetTempPath(), "mineclaw-p6-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        try
        {
            using (var db = AgentDb.Open(Path.Combine(dir, "mineclaw.db")))
            {
                var store = new HiveStore(db, "maid_chan");
                store.NoteFreshVoidSpawn("maid_chan_420", -5.5, 0.62, -8.5);
                Assert.NotNull(store.FreshVoidSpawn());
                using var _ = WorldMode.Override(WorldMode.Normal);
                Assert.Null(store.FreshVoidSpawn());
            }
        }
        finally { try { Directory.Delete(dir, true); } catch (Exception) { } }
    }
}
