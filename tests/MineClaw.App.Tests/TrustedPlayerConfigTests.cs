using MineClaw.App.Hosting;
using MineClaw.Client;
using Microsoft.Extensions.Configuration;

namespace MineClaw.App.Tests;

/// <summary>
/// Who the bot trusts, guards, spars with and calls by a nickname comes only from configuration: the code defaults name nobody,
/// and the Minecraft section's player lists reach every body's client options.
/// </summary>
public class TrustedPlayerConfigTests
{
    private static readonly string Root = Path.Combine(Path.GetTempPath(), "mineclaw-trust-config");

    private static AppSettings Bind(Dictionary<string, string?> values) =>
        AppSettings.Bind(new ConfigurationBuilder().AddInMemoryCollection(values).Build(), Root);

    [Fact]
    public void Nobody_is_trusted_guarded_sparred_or_nicknamed_by_default()
    {
        var s = Bind(new());
        Assert.Empty(s.Agent.Self.TrustedPlayers);
        Assert.Empty(s.Agent.Self.RegularPlayers);
        Assert.Empty(s.Agent.Chat.PlayerNicknames);
        Assert.Empty(s.Agent.Chat.OtherBots);
        var client = s.Mc.WithPlayers(new ClientOptions());
        Assert.Empty(client.ParkourTrustedPlayers);
        Assert.Empty(client.Combat.GuardPlayers);
        Assert.Empty(client.Combat.SparPlayers);
        Assert.Empty(new ClientOptions().ParkourTrustedPlayers);
        Assert.Empty(new MineClaw.Client.Combat.CombatOptions().GuardPlayers);
        Assert.Empty(new MineClaw.Client.Combat.CombatOptions().SparPlayers);
        Assert.Empty(new MineClaw.Agent.Rag.Beliefs.BeliefOptions().Nicknames);
    }

    [Fact]
    public void Configured_players_reach_the_client_and_the_chat_options()
    {
        var s = Bind(new()
        {
            ["Minecraft:ParkourTrustedPlayers:0"] = "alex",
            ["Minecraft:GuardPlayers:0"] = "alex", ["Minecraft:GuardPlayers:1"] = "sam",
            ["Minecraft:SparPlayers:0"] = "sam",
            ["Agent:Self:TrustedPlayers:0"] = "alex",
            ["Agent:Chat:PlayerNicknames:alex:0"] = "al", ["Agent:Chat:PlayerNicknames:alex:1"] = "boss",
            ["Agent:Chat:OtherBots:0"] = "helperbot",
        });
        var client = s.Mc.WithPlayers(new ClientOptions { Username = "maid_chan", Combat = new() { GuardRadius = 9 } });
        Assert.Equal(new[] { "alex" }, client.ParkourTrustedPlayers);
        Assert.Equal(new[] { "alex", "sam" }, client.Combat.GuardPlayers);
        Assert.Equal(new[] { "sam" }, client.Combat.SparPlayers);
        Assert.Equal(9, client.Combat.GuardRadius); // the body's other combat settings stay
        Assert.Equal("maid_chan", client.Username);
        Assert.Equal(new[] { "alex" }, s.Agent.Self.TrustedPlayers);
        Assert.Equal(new[] { "al", "boss" }, s.Agent.Chat.PlayerNicknames["ALEX"]); // a login in any case
        Assert.Equal(new[] { "helperbot" }, s.Agent.Chat.OtherBots);
        Assert.Equal("al", new MineClaw.Agent.Rag.Beliefs.BeliefOptions().WithChatNicknames(s.Agent.Chat.PlayerNicknames).Nicknames["alex"]);
    }

    [Fact]
    public void Trusted_players_with_empty_minecraft_player_lists_get_a_startup_note()
    {
        Assert.Empty(ConfigCheck.Notes(Bind(new()).Mc, Bind(new()).Agent)); // nobody trusted: nothing to say
        var s = Bind(new() { ["Agent:Self:TrustedPlayers:0"] = "alex", ["Minecraft:GuardPlayers:0"] = "alex" });
        Assert.Equal("Agent:Self:TrustedPlayers is set but these lists are empty, so that is off for everyone: Minecraft:SparPlayers (sparring), Minecraft:ParkourTrustedPlayers (void parkour)",
            Assert.Single(ConfigCheck.Notes(s.Mc, s.Agent)));
        s = Bind(new() { ["Agent:Self:TrustedPlayers:0"] = "alex", ["Minecraft:GuardPlayers:0"] = "alex", ["Minecraft:SparPlayers:0"] = "alex",
            ["Minecraft:ParkourTrustedPlayers:0"] = "alex" });
        Assert.Empty(ConfigCheck.Notes(s.Mc, s.Agent));
        Assert.DoesNotContain(ConfigCheck.Validate(s.Mc, s.Agent), p => p.Contains("Players")); // a note, never a startup error
    }

    [Fact]
    public void The_example_config_shows_every_player_list_with_placeholder_names()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "MineClaw.sln"))) dir = dir.Parent;
        Assert.NotNull(dir);
        var config = new ConfigurationBuilder().AddJsonFile(Path.Combine(dir!.FullName, "src", "MineClaw.App", "appsettings.example.json")).Build();
        foreach (var key in new[] { "Minecraft:ParkourTrustedPlayers", "Minecraft:GuardPlayers", "Minecraft:SparPlayers", "Agent:Self:TrustedPlayers", "Hive:RegularPlayers" })
            Assert.Equal(new[] { "your_name", "friend_name" }, config.GetSection(key).Get<string[]>());
        Assert.Equal(new[] { "friend_name", "your_name" }, config.GetSection("Agent:Chat:PlayerNicknames").GetChildren().Select(c => c.Key).Order());
    }
}
