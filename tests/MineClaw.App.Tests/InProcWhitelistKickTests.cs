using MineClaw.App.Hosting;
using MineClaw.Hive.Hierarchy;

namespace MineClaw.App.Tests;

/// <summary>
/// Live 11-03: four story pawns were kicked "You are not white-listed" every 60 s for good (the add ran only before a body's first
/// login). A kicked body now asks main to add it again and retries soon, with a bounded back-off.
/// </summary>
public sealed class InProcWhitelistKickTests
{
    [Fact]
    public void The_retry_after_a_re_add_backs_off_and_is_bounded()
    {
        Assert.Equal([2, 4, 8, 16, 30, 30, 30], Enumerable.Range(1, 7).Select(k => InProcBodies.WhitelistRetryDelay(k).TotalSeconds));
        Assert.Equal(2, InProcBodies.WhitelistRetryDelay(0).TotalSeconds);
    }

    [Theory]
    [InlineData("kicked: You are not white-listed on this server!", true)]
    [InlineData("You are not whitelisted on this server", true)]
    [InlineData("kicked: The server is full!", false)]
    [InlineData("You logged in from another location", false)]
    public void Only_the_whitelist_kick_triggers_a_re_add(string reason, bool kick) =>
        Assert.Equal(kick, WhitelistManager.IsNotWhitelistedKick(reason));
}
