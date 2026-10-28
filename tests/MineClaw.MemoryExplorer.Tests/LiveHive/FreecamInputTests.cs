using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using MineClaw.MemoryExplorer.LiveHive;
using MineClaw.MemoryExplorer.LiveHive.Gpu;

namespace MineClaw.MemoryExplorer.Tests.LiveHive;

/// <summary>Real input through Avalonia (headless): a click focuses the GPU camera, a drag turns it, held WASD moves it per
/// frame, Esc lets go. The profiler drives the camera directly, so this is what proves a user can.</summary>
[Collection("avalonia")]
public class FreecamInputTests
{
    [Fact]
    public Task Click_drag_and_wasd_reach_the_gpu_camera() => HeadlessUi.Run(() =>
    {
        var cam = new FlyCamera();
        cam.Set(new CamPose(0.5, 80, 0.5, 0, 0, 0));
        var model = new GpuFreecam(new HiveWorldCache(), cam, () => null);
        var view = new GlFreecamView { Model = model };
        var overlay = new FreecamOverlay { Model = model };
        var window = new Window { Width = 400, Height = 300, Content = new Panel { Children = { view, overlay } } };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        // Click on the camera: focus + capture; drag right 100 px: yaw turns by 100 × sensitivity.
        window.MouseDown(new Point(200, 150), MouseButton.Left, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();
        Assert.True(view.IsFocused, "a click must focus the camera");
        Assert.True(model.Engaged);
        float yaw0 = cam.Pose.Yaw;
        window.MouseMove(new Point(300, 150), RawInputModifiers.LeftMouseButton);
        window.MouseUp(new Point(300, 150), MouseButton.Left, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(yaw0 + 100 * cam.Sensitivity, cam.Pose.Yaw, 3);

        // Hold W: the camera moves forward on every frame step (yaw ≈ 22° here: forward is −x·sin, +z·cos).
        var p0 = cam.Pose;
        window.KeyPress(Key.W, RawInputModifiers.None, PhysicalKey.W, "w");
        Dispatcher.UIThread.RunJobs();
        for (int i = 0; i < 30; i++) model.Step(1 / 60.0); // half a second of 60 Hz frames (dt per frame; capped at 0.1 s)
        var p1 = cam.Pose;
        double moved = Math.Sqrt((p1.X - p0.X) * (p1.X - p0.X) + (p1.Z - p0.Z) * (p1.Z - p0.Z));
        Assert.Equal(cam.Speed * 0.5, moved, 3);
        // E goes up while W is still held (held keys are a set).
        window.KeyPress(Key.E, RawInputModifiers.None, PhysicalKey.E, "e");
        model.Step(0.1);
        Assert.True(cam.Pose.Y > p1.Y);
        window.KeyRelease(Key.W, RawInputModifiers.None, PhysicalKey.W, "w");
        window.KeyRelease(Key.E, RawInputModifiers.None, PhysicalKey.E, "e");
        Dispatcher.UIThread.RunJobs();
        var p2 = cam.Pose;
        model.Step(0.25);
        Assert.Equal(p2, cam.Pose); // released: no drift

        // Esc lets go of the camera; keys no longer move it.
        window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
        Dispatcher.UIThread.RunJobs();
        Assert.False(model.Engaged);
        window.KeyPress(Key.W, RawInputModifiers.None, PhysicalKey.W, "w");
        model.Step(0.25);
        Assert.Equal(p2, cam.Pose);
        window.Close();
    });
}
