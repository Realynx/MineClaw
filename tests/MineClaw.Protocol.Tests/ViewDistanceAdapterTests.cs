using MineClaw.Protocol.Release1122;
using MineClaw.Protocol.Versioning;

namespace MineClaw.Protocol.Tests;

/// <summary>Per-client view distance: <see cref="IProtocolAdapter.WithViewDistance"/> answers a copy and never changes the
/// process-wide adapter of a profile.</summary>
public class ViewDistanceAdapterTests
{
    [Fact]
    public void Release1122_answers_a_copy_and_leaves_the_instance_alone()
    {
        var four = Release1122ProtocolAdapter.Instance.WithViewDistance(4);
        Assert.Equal(8, Release1122ProtocolAdapter.Instance.ViewDistance);
        var copy = Assert.IsType<Release1122ProtocolAdapter>(four);
        Assert.NotSame(Release1122ProtocolAdapter.Instance, copy);
        Assert.Equal(4, copy.ViewDistance);
        Assert.Same(Release1122ProtocolAdapter.Instance.Lang, copy.Lang);
        Assert.Same(Release1122ProtocolAdapter.Instance, Release1122ProtocolAdapter.Instance.WithViewDistance(8));
        Assert.Equal(2, ((Release1122ProtocolAdapter)Release1122ProtocolAdapter.Instance.WithViewDistance(0)).ViewDistance);
        Assert.Equal(32, ((Release1122ProtocolAdapter)Release1122ProtocolAdapter.Instance.WithViewDistance(99)).ViewDistance);
    }

    [Fact]
    public void Beta181_has_no_view_distance_setting()
    {
        IProtocolAdapter b = Beta181ProtocolAdapter.Instance;
        Assert.Same(b, b.WithViewDistance(4));
    }
}
