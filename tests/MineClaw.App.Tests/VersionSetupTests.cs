using MineClaw.App.Hosting;
using MineClaw.Client.Versioning;
using MineClaw.Protocol.Versioning;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace MineClaw.App.Tests;

/// <summary>Multi-version P0 (docs/design/multi-version.md): the composition root's version decision.</summary>
[Collection(GameDataDefaultCollection.Name)]
public class VersionSetupTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("mineclaw-version-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
        GameDataDefaultCollection.Restore(); // VersionSetup sets the process-wide default
    }

    private static IConfiguration Config(string? version) =>
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { [VersionSetup.ConfigKey] = version }).Build();

    private static readonly MinecraftSettings Mc = new() { Host = "127.0.0.1", Port = 25565, Username = "maid_chan" };

    private Task<VersionSetup.Outcome> Resolve(string? version, Func<CancellationToken, Task<ServerPingResult>> ping) =>
        VersionSetup.ResolveAsync(Config(version), Mc, _dir, NullLogger.Instance, CancellationToken.None, ping);

    [Fact]
    public void CliOptionParsesAndFeedsTheConfigKey()
    {
        var cli = AppCli.Parse(["--server-version", "1.12.2"]);
        Assert.Empty(cli.Errors);
        Assert.Equal("1.12.2", cli.ServerVersion);
        var config = AppConfig.Build(cli, _dir, new Dictionary<string, string?>());
        Assert.Equal("1.12.2", config[VersionSetup.ConfigKey]);
        Assert.Contains("--server-version", AppCli.Parse(["--server-version", "1.99"]).Errors.Single());
        Assert.Equal("auto", AppCli.Parse(["--server-version=auto"]).ServerVersion);
    }

    [Fact]
    public async Task AutoWithABetaServerPicksBeta181AndRemembersIt()
    {
        var o = await Resolve(null, _ => Task.FromResult(ServerPing.ParseLegacy("motd§0§20")));
        Assert.Same(GameVersionProfiles.Beta181, o.Profile);
        Assert.True(o.Resolution!.Detected);
        Assert.Equal("b1.8.1", File.ReadAllText(Path.Combine(_dir, VersionSetup.LastKnownFile)).Trim());
    }

    [Fact]
    public async Task ServerDownUsesTheRememberedVersion()
    {
        File.WriteAllText(Path.Combine(_dir, VersionSetup.LastKnownFile), "b1.8.1\n");
        var o = await Resolve("auto", _ => throw new ServerPingException("connection refused"));
        Assert.Same(GameVersionProfiles.Beta181, o.Profile);
        Assert.False(o.Resolution!.Detected);
        Assert.Contains("last detected", o.Resolution.How);
    }

    [Fact]
    public async Task ExplicitVersionSkipsThePing()
    {
        var o = await Resolve("b1.8.1", _ => throw new InvalidOperationException("must not ping"));
        Assert.Same(GameVersionProfiles.Beta181, o.Profile);
        Assert.False(File.Exists(Path.Combine(_dir, VersionSetup.LastKnownFile)));
    }

    [Fact]
    public async Task A1122ServerGetsThe1122Profile()
    {
        // Multi-version P1: the "recognised but not implemented" exit is gone for 1.12.2.
        var o = await Resolve("auto", _ => Task.FromResult(ServerPing.ParseStatusJson("""{"version":{"name":"1.12.2","protocol":340}}""")));
        Assert.Null(o.Error);
        Assert.Same(GameVersionProfiles.Java1122, o.Profile);
        Assert.Equal("1.12.2", File.ReadAllText(Path.Combine(_dir, VersionSetup.LastKnownFile)).Trim());
        var bad = await Resolve("1.7.10", _ => throw new InvalidOperationException());
        Assert.Contains("not a version MineClaw knows", bad.Error);
    }
}
