using MineClaw.Data;
using MineClaw.Protocol.Packets;
using MineClaw.World;

namespace MineClaw.Client.Tests;

public sealed class StorageSignRemovalTests
{
    private static readonly BlockPos At = new(9, 64, 8);
    private static readonly string[] Lines = ["wood", "", "", ""];

    private static async Task<(MinecraftClient Client, FakeServer Server, BlockPos Chest)> Start(bool modern, bool standing = false)
    {
        var chest = standing ? At.Down() : At.Offset(0, 0, 1);
        var (client, server) = await Harness.StartAsync(s =>
        {
            s.Blocks[(At.X, At.Y, At.Z)] = standing ? Blocks.SignPost : Blocks.WallSign;
            s.Metas[(At.X, At.Y, At.Z)] = standing ? 0 : 2;
            s.Blocks[(chest.X, chest.Y, chest.Z)] = Blocks.Chest;
            s.Chests[(chest.X, chest.Y, chest.Z)] = Enumerable.Repeat(Harness.Stack(Items.Diamond, 64), 27).ToArray();
            s.Blocks[(7, 64, 8)] = Blocks.SignPost;
        }, release1122: modern);
        server.Send(new UpdateSignPacket { X = At.X, Y = (short)At.Y, Z = At.Z, Lines = Lines });
        Assert.True(await Harness.WaitAsync(() => client.World.BlockEntities.Get(At)?.Lines?.SequenceEqual(Lines) == true));
        client.IsProtected = (_, _) => true;
        return (client, server, chest);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task Removes_only_the_exact_label_without_touching_chest_contents_or_other_signs(bool modern, bool standing)
    {
        var (c, s, chest) = await Start(modern, standing);
        await using var client = c;
        using var server = s;
        Assert.False((await c.DigBlockAsync(At.X, At.Y, At.Z)).Ok);
        var r = await c.RemoveStorageSignAsync(At, chest, Lines);
        Assert.True(r.Ok, r.Message);
        Assert.Equal(Blocks.Air, c.World.GetBlock(At));
        Assert.Equal(Blocks.Chest, c.World.GetBlock(chest));
        Assert.All(s.Chests[(chest.X, chest.Y, chest.Z)], item => Assert.Equal(64, item.Count));
        Assert.False((await c.DigBlockAsync(7, 64, 8)).Ok);
        Assert.Single(s.Dug);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Wrong_text_attachment_or_hard_protection_never_authorizes_a_dig(bool modern)
    {
        var (c, s, chest) = await Start(modern);
        await using var client = c;
        using var server = s;
        Assert.False((await c.RemoveStorageSignAsync(At, chest, ["stone", "", "", ""])).Ok);
        Assert.False((await c.RemoveStorageSignAsync(At, chest.Offset(1, 0, 0), Lines)).Ok);
        Assert.False((await c.RemoveStorageSignAsync(chest, chest, Lines)).Ok); // cannot turn sign authority into a chest dig
        c.NeverBreak = p => p == At;
        Assert.False((await c.RemoveStorageSignAsync(At, chest, Lines)).Ok);
        Assert.Empty(s.Got<PlayerDiggingPacket>());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Editing_the_sign_during_its_removal_aborts_before_breaking_it(bool modern)
    {
        var (c, s, chest) = await Start(modern);
        await using var client = c;
        using var server = s;
        s.Hook = packet =>
        {
            if (packet is PlayerDiggingPacket { Status: PlayerDiggingPacket.StatusStarted, X: 9, Y: 64, Z: 8 })
                s.Send(new UpdateSignPacket { X = At.X, Y = (short)At.Y, Z = At.Z, Lines = ["stone", "", "", ""] });
            return false;
        };
        var r = await c.RemoveStorageSignAsync(At, chest, Lines);
        Assert.False(r.Ok);
        Assert.Contains("text no longer matches", r.Message);
        Assert.Empty(s.Dug);
        Assert.DoesNotContain(s.Got<PlayerDiggingPacket>(), p => p.Status == PlayerDiggingPacket.StatusFinished);
    }
}
