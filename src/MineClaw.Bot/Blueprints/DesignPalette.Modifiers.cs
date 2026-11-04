using System.Globalization;
using MineClaw.World;

namespace MineClaw.Bot.Blueprints;

/// <summary>Modifiers after a block name (<see cref="DesignPalette.TryResolve"/>): a direction, <c>wall=side</c>, a raw meta, or a
/// word (up/down, top/bottom, open/closed, an axis).</summary>
public sealed partial class DesignPalette
{
    private static readonly HashSet<string> ModifierWords =
    [
        "up", "down", "top", "upper", "bottom", "lower", "upside_down", "open", "x", "y", "z", "floor", "ground", "standing", "ceiling", "closed",
    ];

    /// <summary>Keys of <c>key=side</c>: the supporting block's side, or the facing.</summary>
    private static readonly HashSet<string> WallKeys = ["wall", "on", "against", "support"], FacingKeys = ["facing", "front", "dir"];

    private static bool IsModifier(string a) => Dirs.Parse(a) != null || a.Contains('=') || ModifierWords.Contains(a);

    /// <summary>What a modifier word sets: the new meta, or -1 when it doesn't apply to the family.</summary>
    private static readonly Dictionary<string, Func<StateEdit, string, int>> WordRules = new()
    {
        ["up"] = (s, a) => s.Up(a),
        ["floor"] = (s, a) => s.Up(a),
        ["ground"] = (s, a) => s.Up(a),
        ["standing"] = (s, a) => s.Up(a),
        ["down"] = (s, a) => s.Down(a),
        ["ceiling"] = (s, a) => s.Down(a),
        ["top"] = (s, _) => s.Top(),
        ["upper"] = (s, _) => s.Top(),
        ["upside_down"] = (s, _) => s.Top(),
        ["bottom"] = (s, _) => s.Bottom(),
        ["lower"] = (s, _) => s.Bottom(),
        ["open"] = (s, a) => s.Open(a),
        ["closed"] = (s, a) => s.Open(a),
        ["x"] = (s, a) => s.Axis(a),
        ["y"] = (s, a) => s.Axis(a),
        ["z"] = (s, a) => s.Axis(a),
    };

    /// <summary>One modifier applied to a state ("north", "wall=west", "top", "open", "x", "5").</summary>
    private bool Modify(int id, int meta, string a, out int result, out string? why)
    {
        var s = new StateEdit(id, meta, DesignFacing.FamilyOf(id), _byId[id][0].Base, Version == DesignVersions.Release);
        if (int.TryParse(a, NumberStyles.None, CultureInfo.InvariantCulture, out var n)) { result = n; why = null; return true; }
        int eq = a.IndexOf('=');
        if (eq > 0) return s.Keyed(a[..eq], a[(eq + 1)..], a, out result, out why);
        if (Dirs.Parse(a) is { } d) return s.HangsOnWall ? s.OnWall(d, out result, out why) : s.Facing(d, out result, out why);
        result = meta;
        if (!WordRules.TryGetValue(a, out var rule)) { why = $"'{a}' isn't a facing, variant or meta for {s.Name}"; return false; }
        int m = rule(s, a);
        if (m < 0) { why = $"'{a}' doesn't apply to {s.Name} in {Version}"; return false; }
        result = m;
        why = null;
        return true;
    }

    private static readonly Dir[] SWNE = [Dir.South, Dir.West, Dir.North, Dir.East];
    private static readonly Dir[] FrontDirs = [Dir.North, Dir.South, Dir.West, Dir.East];
    private static readonly Dir[] TorchWalls = [Dir.West, Dir.East, Dir.North, Dir.South];
    private static readonly Dir[] ButtonFaces = [Dir.East, Dir.West, Dir.South, Dir.North];

    /// <summary>The state a modifier edits: its id, meta and family, the base name for messages, and whether it's 1.12.2.</summary>
    private readonly record struct StateEdit(int Id, int Meta, DesignFamily Fam, string Name, bool Modern)
    {
        /// <summary>A direction means the side the supporting block is on (else: the way it faces).</summary>
        public bool HangsOnWall => Fam is DesignFamily.Torch or DesignFamily.Wall or DesignFamily.Skull or DesignFamily.Button or DesignFamily.Lever or DesignFamily.Vine;

        /// <summary>wall=side / on=side … (the supporting block's side) or facing=dir / front=dir / dir=dir.</summary>
        public bool Keyed(string key, string value, string a, out int result, out string? why)
        {
            result = Meta;
            why = null;
            if (!WallKeys.Contains(key) && !FacingKeys.Contains(key)) { why = $"'{a}': use wall=<side> or a direction"; return false; }
            if (FacingKeys.Contains(key) && Dirs.Parse(value) is { } fd) return Facing(fd, out result, out why);
            if (Dirs.Parse(value) is not { } wd) { why = $"'{value}': the side is north, east, south or west (where the supporting block is)"; return false; }
            return OnWall(wd, out result, out why);
        }

        public int Up(string a) => Fam switch
        {
            DesignFamily.Facing6 when a == "up" => 1,
            DesignFamily.Torch => 5,
            DesignFamily.Skull => 1,
            DesignFamily.Button when Modern => (Meta & 8) | 5,
            DesignFamily.Lever => (Meta & 8) | 5,
            DesignFamily.Log when Modern && a == "up" => Meta & 3,
            DesignFamily.Pillar when a == "up" => 0,
            _ => -1,
        };

        public int Down(string a) => Fam switch
        {
            DesignFamily.Facing6 or DesignFamily.Hopper when a == "down" => 0,
            DesignFamily.Button when Modern => Meta & 8,
            DesignFamily.Lever when Modern => Meta & 8,
            _ => -1,
        };

        public int Top() => Fam switch
        {
            DesignFamily.Slab when Modern => (Meta & 7) | 8,
            DesignFamily.Stairs when Modern => Meta | 4,
            DesignFamily.Trapdoor when Modern => Meta | 8,
            _ => -1,
        };

        public int Bottom() => Fam switch { DesignFamily.Slab => Meta & 7, DesignFamily.Stairs => Meta & 3, DesignFamily.Trapdoor => Meta & 7, _ => -1 };

        public int Open(string a) =>
            Fam is DesignFamily.Door or DesignFamily.Gate or DesignFamily.Trapdoor ? (a == "open" ? Meta | 4 : Meta & ~4) : -1;

        public int Axis(string a) => Fam switch
        {
            DesignFamily.Log when Modern => (Meta & 3) | AxisBits(a, 4, 8, 0),
            DesignFamily.Pillar => AxisBits(a, 4, 8, 0),
            DesignFamily.Quartz when Meta >= 2 => AxisBits(a, 3, 4, 2),
            DesignFamily.Rail or DesignFamily.PoweredRail when a != "y" => (Meta & 8) | (a == "x" ? 1 : 0),
            _ => -1,
        };

        private static int AxisBits(string a, int x, int z, int y) => a == "x" ? x : a == "z" ? z : y;

        public bool Facing(Dir dir, out int r, out string? w)
        {
            w = null;
            r = FacingMeta(dir);
            if (r < 0) { w = $"{Name} has no facing"; r = Meta; return false; }
            return true;
        }

        private int FacingMeta(Dir dir) => Fam switch
        {
            DesignFamily.Door => DesignFacing.WithFacing(Id, Meta & 7, dir.Opposite()),
            _ when TurnsByFacing(Fam) => DesignFacing.WithFacing(Id, Meta, dir),
            _ when FrontFacing(Fam) => (Fam == DesignFamily.Facing6 ? Meta & 8 : 0) | (Array.IndexOf(FrontDirs, dir) + 2),
            DesignFamily.Rotation16 => Rotation16(dir),
            _ => AxisFacing(dir is Dir.East or Dir.West),
        };

        private static bool TurnsByFacing(DesignFamily f) =>
            f is DesignFamily.Stairs or DesignFamily.Bed or DesignFamily.Horizontal or DesignFamily.Gate or DesignFamily.Trapdoor;

        private static bool FrontFacing(DesignFamily f) => f is DesignFamily.Front or DesignFamily.Facing6 or DesignFamily.Hopper;

        private static int Rotation16(Dir dir) => dir switch { Dir.South => 0, Dir.West => 4, Dir.North => 8, _ => 12 };

        /// <summary>A direction on an axis block (rails, logs, pillars): east/west vs north/south.</summary>
        private int AxisFacing(bool eastWest) => Fam switch
        {
            DesignFamily.Rail or DesignFamily.PoweredRail => (Meta & 8) | (eastWest ? 1 : 0),
            DesignFamily.Log when Modern => (Meta & 3) | (eastWest ? 4 : 8),
            DesignFamily.Pillar => eastWest ? 4 : 8,
            DesignFamily.Quartz when Meta >= 2 => eastWest ? 3 : 4,
            _ => -1,
        };

        public bool OnWall(Dir wall, out int r, out string? w)
        {
            w = null;
            r = Fam switch
            {
                DesignFamily.Torch => Array.IndexOf(TorchWalls, wall) + 1,
                DesignFamily.Wall or DesignFamily.Skull => Array.IndexOf(FrontDirs, wall.Opposite()) + 2,
                DesignFamily.Button or DesignFamily.Lever => (Meta & 8) | (Array.IndexOf(ButtonFaces, wall.Opposite()) + 1),
                DesignFamily.Vine => VineMeta(wall),
                DesignFamily.Horizontal when Id == 131 => Array.IndexOf(SWNE, wall.Opposite()),
                _ => -1,
            };
            if (r < 0) { w = $"{Name} doesn't hang on a wall (use a direction)"; r = Meta; return false; }
            return true;
        }

        private static int VineMeta(Dir wall) => wall switch { Dir.South => 1, Dir.West => 2, Dir.North => 4, _ => 8 };
    }
}
