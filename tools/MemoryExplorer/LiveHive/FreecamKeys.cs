using System.Collections.Frozen;
using Avalonia.Input;

namespace MineClaw.MemoryExplorer.LiveHive;

/// <summary>The keys that fly the free camera (both views: the CPU raycaster's and the GPU one): WASD and the arrows, E/Space/PageUp
/// up, Q/PageDown down, Shift fast.</summary>
public static class FreecamKeys
{
    private static readonly FrozenSet<Key> Moves = new[]
    {
        Key.W, Key.A, Key.S, Key.D, Key.Q, Key.E, Key.Up, Key.Down, Key.Left, Key.Right, Key.Space, Key.LeftShift, Key.RightShift, Key.PageUp, Key.PageDown,
    }.ToFrozenSet();

    public static bool IsMovement(Key k) => Moves.Contains(k);
}
