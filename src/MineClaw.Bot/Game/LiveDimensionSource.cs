using MineClaw.Bot.WorldIndex;
using MineClaw.Client;

namespace MineClaw.Bot.Game;

/// <summary>
/// <see cref="IDimensionSource"/> over the real client: <see cref="Dimension"/> from the world copy, and the client's
/// <c>DimensionChanged(from, to)</c> event forwarded (consumers still poll as a fallback).
/// </summary>
public sealed class LiveDimensionSource(MinecraftClient client) : IDimensionSource
{
    public int Dimension => client.World.Dimension;

    public event Action<int, int>? DimensionChanged
    {
        add => client.DimensionChanged += value;
        remove => client.DimensionChanged -= value;
    }
}
