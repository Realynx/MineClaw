using MineClaw.Data;
using MineClaw.Protocol.Release1122;

namespace MineClaw.Protocol.Tests;

/// <summary>
/// Live 2026-10-29 on alice (no 1.12.2 jar there): chat showed raw keys, "[PoofimaFox: commands.op.success maid_chan]",
/// "commands.generic.permission", "advancements.adventure.kill_a_mob.title". The keys the bot itself depends on are built in,
/// the release ships en_us.lang from the jar (the App's CopyLang1122 target), and an unknown key reads as words, not a key.
/// And the air supply: <c>Entity.AIR</c> (index 1, VarInt) reaches the canonical index 1 like b1.8.1's short.
/// </summary>
public class Release1122LangAndAirTests
{
    [Theory]
    [InlineData("commands.op.success", "Opped maid_chan")]
    [InlineData("commands.generic.permission", "You do not have permission to use this command")]
    [InlineData("commands.whitelist.remove.success", "Removed maid_chan from the whitelist")]
    public void The_live_keys_are_built_in(string key, string want) =>
        Assert.Equal(want, ChatComponent.ToPlain(ChatComponent.Translate(key, "maid_chan"), LangTable.Builtin));

    [Fact]
    public void The_ops_chat_wrapper_translates_with_its_inner_key()
    {
        // [PoofimaFox: Opped maid_chan] = chat.type.admin with a nested translate.
        var json = """{"translate":"chat.type.admin","with":["PoofimaFox",{"translate":"commands.op.success","with":["maid_chan"]}]}""";
        Assert.Equal("[PoofimaFox: Opped maid_chan]", ChatComponent.ToPlain(json, LangTable.Builtin));
    }

    [Theory]
    [InlineData("advancements.adventure.kill_a_mob.title", "kill a mob")]
    [InlineData("commands.generic.syntax", "generic syntax")]
    [InlineData("tile.bed.noSleep", "tile bed noSleep")]
    public void An_unknown_key_reads_as_words(string key, string want)
    {
        Assert.Equal(want, LangTable.CleanKey(key));
        Assert.DoesNotContain('.', ChatComponent.ToPlain(ChatComponent.Translate(key), new LangTable(new Dictionary<string, string>())));
    }

    [Fact]
    public void A_shipped_lang_file_is_read_over_the_built_in_keys()
    {
        var path = Path.Combine(Path.GetTempPath(), $"en_us_{Guid.NewGuid():N}.lang");
        File.WriteAllText(path, "# test\nadvancements.adventure.kill_a_mob.title=Monster Hunter\ncommands.op.success=Opped %s (file)\n");
        try
        {
            var t = LangTable.FromFile(path);
            Assert.Equal("Monster Hunter", ChatComponent.ToPlain(ChatComponent.Translate("advancements.adventure.kill_a_mob.title"), t));
            Assert.Equal("Opped x (file)", ChatComponent.ToPlain(ChatComponent.Translate("commands.op.success", "x"), t));
            Assert.Equal("x drowned", ChatComponent.ToPlain(ChatComponent.Translate("death.attack.drown", "x"), t)); // built-in kept
        }
        finally { File.Delete(path); }
        Assert.Contains(LangTable.CandidateFiles(), f => f.EndsWith(Path.Combine("lang", "en_us.lang"), StringComparison.Ordinal));
    }

    [Fact]
    public void The_installed_jar_has_every_built_in_key_verbatim()
    {
        var jar = LangTable.CandidateJars().FirstOrDefault(File.Exists);
        if (jar is null) return; // no 1.12.2 client installed here
        var t = LangTable.FromJar(jar);
        foreach (var key in new[] { "commands.op.success", "commands.generic.permission", "commands.whitelist.remove.success",
                     "commands.whitelist.remove.failed", "commands.generic.player.notFound", "commands.deop.success" })
        {
            Assert.True(t.TryGet(key, out var fromJar), key);
            Assert.True(LangTable.Builtin.TryGet(key, out var built), key);
            Assert.Equal(fromJar, built);
        }
    }

    [Theory]
    [InlineData(Release1122.EntityWireKind.Player, 0)]
    [InlineData(Release1122.EntityWireKind.Mob, 54)] // a zombie
    public void Air_index_1_maps_to_the_canonical_air(Release1122.EntityWireKind kind, int type)
    {
        int flags = 0, hands = 0;
        var md = EntityMeta1122.ToCanonical(kind, type, [new Meta1122(1, 1, 137)], ref flags, ref hands);
        Assert.Equal(137, md.Get(EntityMeta.Air)?.AsInt());
        var back = EntityMeta1122.FromCanonical(kind, type, md);
        Assert.Contains(back, e => e.Index == 1 && e.Serializer == 1 && Equals(e.Value, 137));
    }
}
