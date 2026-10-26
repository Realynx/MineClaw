namespace MineClaw.Protocol;

/// <summary>
/// Fixed-point conversions used on the wire (vanilla b1.8 semantics).
/// <list type="bullet">
/// <item>Absolute entity positions (0x14, 0x15, 0x17, 0x18, 0x1A, 0x22, 0x47): int = floor(blocks × 32).</item>
/// <item>Relative moves (0x1F, 0x21): signed byte = Δblocks × 32 (max ±4 blocks per packet).</item>
/// <item>Angles (yaw/pitch bytes): byte = (int)(degrees × 256 / 360), wrapping; read back as byte × 360 / 256.</item>
/// <item>Velocities (0x1C, 0x17): short = blocks per tick × 8000 (vanilla clamps to ±3.9 blocks/tick).</item>
/// <item>Pickup motion (0x15): signed byte = blocks per tick × 128.</item>
/// </list>
/// </summary>
public static class FixedPoint
{
    public static double FromAbsolute(int value) => value / 32.0;

    /// <summary>Vanilla <c>MathHelper.floor(v * 32.0)</c>.</summary>
    public static int ToAbsolute(double blocks) => (int)Math.Floor(blocks * 32.0);

    public static double FromRelative(sbyte value) => value / 32.0;

    /// <summary>Clamps to the sbyte range; vanilla sends a teleport instead when |Δ| ≥ 4 blocks.</summary>
    public static sbyte ToRelative(double deltaBlocks) => (sbyte)Math.Clamp((int)Math.Round(deltaBlocks * 32.0), sbyte.MinValue, sbyte.MaxValue);

    public static float FromAngle(byte angle) => angle * 360f / 256f;

    /// <summary>Angle byte as signed degrees in (-180, 180].</summary>
    public static float FromAngleSigned(byte angle) => (sbyte)angle * 360f / 256f;

    /// <summary>Vanilla <c>(byte)(int)(degrees * 256 / 360)</c>.</summary>
    public static byte ToAngle(float degrees) => unchecked((byte)(int)(degrees * 256f / 360f));

    public static double FromVelocity(short value) => value / 8000.0;

    public static short ToVelocity(double blocksPerTick) =>
        (short)(int)(Math.Clamp(blocksPerTick, -3.9, 3.9) * 8000.0);

    public static double FromPickupMotion(sbyte value) => value / 128.0;
}
