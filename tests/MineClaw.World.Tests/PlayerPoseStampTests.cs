using System.Diagnostics;
using MineClaw.World;

namespace MineClaw.World.Tests;

/// <summary>The pose stamp the viewer dates each body's sample by: set where the pose is published, read with it, never older.</summary>
public class PlayerPoseStampTests
{
    [Fact]
    public void A_fresh_player_has_no_pose_stamp()
    {
        var p = new PlayerState();
        Assert.Equal(0, p.PoseStamp);
        p.PoseSnapshot(out long stamp);
        Assert.Equal(0, stamp);
    }

    [Fact]
    public void Server_teleport_stamps_the_pose_it_publishes()
    {
        var p = new PlayerState();
        long before = Stopwatch.GetTimestamp();
        p.ServerTeleport(new Vec3d(10.5, 64, -3.5), 65.62, 90, 10, true);
        long after = Stopwatch.GetTimestamp();

        var snap = p.PoseSnapshot(out long stamp);
        Assert.InRange(stamp, before, after);
        Assert.Equal((new Vec3d(10.5, 64, -3.5), 90f, 10f), (snap.Position, snap.Yaw, snap.Pitch));
        Assert.Equal(stamp, p.PoseStamp);
    }

    [Fact]
    public void Each_physics_sync_stamps_again_and_the_stamp_never_decreases()
    {
        var p = new PlayerState();
        p.ServerTeleport(new Vec3d(0.5, 64, 0.5), 65.62, 0, 0, true);
        long last = p.PoseStamp;
        for (int tick = 1; tick <= 5; tick++)
        {
            p.Physics.Position = new Vec3d(0.5 + tick * 0.2, 64, 0.5);
            long before = Stopwatch.GetTimestamp();
            p.SyncFromPhysics();
            var snap = p.PoseSnapshot(out long stamp);
            Assert.True(stamp >= last && stamp >= before, $"tick {tick}: stamp {stamp} after {last} (read at {before})");
            Assert.Equal(0.5 + tick * 0.2, snap.Position.X, 9);
            last = stamp;
        }
    }

    [Fact]
    public void Vitals_and_settings_do_not_move_the_pose_stamp()
    {
        var p = new PlayerState();
        p.SyncFromPhysics();
        long stamp = p.PoseStamp;
        p.SetHealth(12, 15, 2);
        p.SetExperience(0.5f, 3, 40);
        p.HeldSlot = 4;
        p.Respawned(-1);
        Assert.Equal(stamp, p.PoseStamp);
        Assert.Equal(12, p.Snapshot.Health);
    }
}
