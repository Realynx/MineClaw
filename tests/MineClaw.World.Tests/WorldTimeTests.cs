using MineClaw.World;

namespace MineClaw.World.Tests;

/// <summary>The world's time of day (a stopped 1.12.2 daylight cycle is sent negative: it must not read as the other half of the day).</summary>
public class WorldTimeTests
{
    [Theory]
    [InlineData(0, 0)]
    [InlineData(6000, 6000)]
    [InlineData(24000 * 7 + 18000, 18000)]
    [InlineData(-6000, 6000)] // doDaylightCycle false: the absolute value is the time (noon), not 18000 (midnight)
    [InlineData(-24000 - 13000, 13000)]
    [InlineData(long.MinValue, 0)]
    public void Time_of_day_counts_a_negative_time_by_its_absolute_value(long time, long tod)
    {
        var w = new WorldState { Time = time };
        Assert.Equal(tod, w.TimeOfDay);
    }

    [Fact]
    public void Frozen_noon_is_day()
    {
        var w = new WorldState { Time = -6000, TimeFrozen = true };
        Assert.False(w.IsNight);
        Assert.True(w.TimeFrozen);
    }
}
