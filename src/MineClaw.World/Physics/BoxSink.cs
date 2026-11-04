namespace MineClaw.World;

/// <summary>
/// Appends block-relative boxes (0..1 per axis, fences 1.5 high) to a list, offset to the block's world position x,y,z: the shape
/// builders' <c>Add(x0, y0, z0, x1, y1, z1)</c> as a struct, so passing it to a helper allocates nothing (a captured local function
/// turned into a delegate did, on every call).
/// </summary>
internal readonly struct BoxSink(List<AABB> output, int x, int y, int z)
{
    public int Count => output.Count;

    public void Add(double x0, double y0, double z0, double x1, double y1, double z1) =>
        output.Add(new AABB(x + x0, y + y0, z + z0, x + x1, y + y1, z + z1));

    /// <summary>Adds each box of a flat (x0, y0, z0, x1, y1, z1)* table row.</summary>
    public void AddAll(double[] boxes)
    {
        for (int k = 0; k < boxes.Length; k += 6)
            Add(boxes[k], boxes[k + 1], boxes[k + 2], boxes[k + 3], boxes[k + 4], boxes[k + 5]);
    }
}
