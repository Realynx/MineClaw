using System.Text;
using MineClaw.Data;
using MineClaw.World;
using MineClaw.World.Versioning;
using Xunit;
using static MineClaw.World.Tests.Golden;

namespace MineClaw.World.Tests;

/// <summary>
/// Characterisation goldens of <see cref="LightEngine"/>, taken before it was rewritten: the exact sky and block light of every
/// cell (a hash per column plus its height map) and the engine's own counters (updates run, queue entries visited, which pins
/// the propagation order) over deterministic generated worlds. Covered: lighting a region that came without light arrays
/// (sky straight down plus emitters), keeping arrays that came from the server, the full relight with the sideways sky spread,
/// a long run of random edits (opaque, transparent, water, leaves, emitters, removals, out of range, unloaded and edge-of-loaded
/// cells, chunk borders), a roof built and taken down over open sky, the nether (no sky height updates), both world heights and
/// both versions' light tables, the light level reads and the day cycle's sky darkening.
/// </summary>
public class LightEngineGoldenTests
{
    private const int Origin = -32, Size = 80;

    /// <summary>A small deterministic generator (xorshift), independent of the runtime's <see cref="Random"/>.</summary>
    private sealed class Rng(ulong seed)
    {
        private ulong _state = seed * 0x9E3779B97F4A7C15UL | 1;

        public int Next(int min, int maxExclusive)
        {
            _state ^= _state << 13;
            _state ^= _state >> 7;
            _state ^= _state << 17;
            return min + (int)(_state % (ulong)(maxExclusive - min));
        }
    }

    private static int Mix(int x, int y, int z, int salt)
    {
        unchecked
        {
            uint h = (uint)(x * 73856093 ^ y * 19349663 ^ z * 83492791 ^ salt * 2654435761u);
            h ^= h >> 15; h *= 0x2C1B3C6D; h ^= h >> 12; h *= 0x297A2D39; h ^= h >> 15;
            return (int)(h & 0x7FFFFFFF);
        }
    }

    private static int BaseLevel(int height) => height == 256 ? 150 : 60;
    private static int Ground(int x, int z, int height) => BaseLevel(height) + Mix(x, 0, z, 1) % 7;

    /// <summary>The block of the generated terrain at a cell: bedrock, stone with caves (some lit), a varied surface layer,
    /// leaf blobs, glass posts and a cobblestone roof strip that overhangs open ground across a chunk border.</summary>
    private static int TerrainBlock(int x, int y, int z, int height)
    {
        int ground = Ground(x, z, height), roll = Mix(x, y, z, 2) % 100;
        if (y == 0) return Blocks.Bedrock;
        if (y < ground - 1)
        {
            bool cave = y > BaseLevel(height) - 20 && y < ground - 3 && Mix(x >> 1, y >> 1, z >> 1, 3) % 100 < 22;
            if (!cave) return roll < 4 ? Blocks.CoalOre : Blocks.Stone;
            return roll < 3 ? Blocks.Glowstone : roll < 6 ? Blocks.Torch : 0;
        }
        if (y == ground - 1)
            return roll switch
            {
                < 6 => Blocks.Water, < 9 => Blocks.Ice, < 12 => Blocks.Glass, < 14 => Blocks.Slab, < 16 => Blocks.Farmland,
                < 18 => 53, < 20 => Blocks.CobblestoneStairs, < 22 => Blocks.Lava, _ => Blocks.Grass,
            };
        if (y == ground && roll < 3) return Blocks.Torch;
        if (y == ground && roll < 4) return Blocks.LitFurnace;
        if (y >= ground + 3 && y <= ground + 5 && Mix(x >> 2, 0, z >> 2, 4) % 100 < 18 && roll < 70) return Blocks.Leaves;
        if (y < ground + 4 && Mix(x, 0, z, 5) % 100 < 3) return Blocks.Glass;
        if (y == BaseLevel(height) + 12 && x >= 10 && x <= 20 && z >= -6 && z <= 18) return Blocks.Cobblestone;
        return 0;
    }

    /// <summary>A 5×5-column world (x/z -32..47) of the generated terrain, arriving with or without light arrays.</summary>
    private static WorldState Terrain(bool release1122, bool serverLight, int dimension = 0)
    {
        var w = release1122 ? new WorldState(height: 256, physics: Release1122PhysicsRules.Instance) : new WorldState();
        w.Dimension = dimension;
        int h = w.WorldHeight;
        var ids = new byte[Size * h * Size];
        byte[]? sky = serverLight ? new byte[ids.Length] : null, block = serverLight ? new byte[ids.Length] : null;
        for (int dx = 0; dx < Size; dx++)
        for (int dz = 0; dz < Size; dz++)
        for (int y = 0; y < h; y++)
        {
            int x = Origin + dx, z = Origin + dz, i = (dx * Size + dz) * h + y;
            ids[i] = (byte)TerrainBlock(x, y, z, h);
            if (!serverLight) continue;
            int ground = Ground(x, z, h);
            sky![i] = (byte)(y >= ground ? 15 : Math.Max(0, 15 - (ground - y) * 4));
            block![i] = (byte)(Mix(x, y, z, 6) % 3 == 0 ? Mix(x, y, z, 7) % 16 : 0);
        }
        w.ApplyChunkRegion(Origin, 0, Origin, Size, h, Size, ids, null, block, sky);
        return w;
    }

    private static readonly int[] EditIds =
        [0, 0, 0, Blocks.Stone, Blocks.Cobblestone, Blocks.Torch, Blocks.Glowstone, Blocks.Glass, Blocks.Leaves, Blocks.Water,
         Blocks.Ice, Blocks.Slab, Blocks.Farmland, 53, Blocks.LitFurnace, Blocks.RedstoneTorch, Blocks.BrownMushroom, Blocks.Lava];

    /// <summary>Random edits around the surface, a few out of range or over unloaded columns.</summary>
    private static void Edit(StringBuilder sb, WorldState w, Rng rng, int count)
    {
        int h = w.WorldHeight;
        for (int i = 0; i < count; i++)
        {
            int x = rng.Next(Origin - 3, Origin + Size + 3), z = rng.Next(Origin - 3, Origin + Size + 3);
            int y = rng.Next(0, 20) == 0 ? rng.Next(-2, 2) * (h + 1) : Ground(x, z, h) + rng.Next(-7, 6);
            int id = EditIds[rng.Next(0, EditIds.Length)];
            bool changed = w.SetBlock(x, y, z, id, id == Blocks.Torch ? 5 : 0);
            if (i % 25 == 24) Counters(sb.Append(" after ").Append(i + 1).Append(" edits (last ").Append(changed).Append(')'), w);
        }
    }

    /// <summary>A roof built cell by cell over open ground across the x 16 chunk border, then half of it taken down.</summary>
    private static void Roof(StringBuilder sb, WorldState w)
    {
        int y = BaseLevel(w.WorldHeight) + 18;
        for (int x = 8; x <= 23; x++) for (int z = 2; z <= 14; z++) w.SetBlock(x, y, z, Blocks.Stone, 0);
        Counters(sb.Append(" roof built"), w);
        for (int x = 8; x <= 23; x++) for (int z = 2; z <= 8; z++) w.SetBlock(x, y, z, 0, 0);
        Counters(sb.Append(" roof halved"), w);
    }

    private static void Counters(StringBuilder sb, WorldState w) =>
        sb.Append(" updates=").Append(w.Light.Updates).Append(" visits=").Append(w.Light.Visits).Append('\n');

    /// <summary>Every world column: its height map and FNV hashes of its sky and block light from y 0 up.</summary>
    private static void Columns(StringBuilder sb, WorldState w, string label)
    {
        sb.Append("== ").Append(label).Append('\n');
        Counters(sb, w);
        for (int x = Origin; x < Origin + Size; x++)
        for (int z = Origin; z < Origin + Size; z++)
        {
            ulong sky = 14695981039346656037UL, block = sky;
            for (int y = 0; y < w.WorldHeight; y++)
            {
                sky = (sky ^ (ulong)w.Light.Get(true, x, y, z)) * 1099511628211UL;
                block = (block ^ (ulong)w.Light.Get(false, x, y, z)) * 1099511628211UL;
            }
            sb.Append(x).Append(',').Append(z).Append(" h=").Append(w.Light.Height(x, z))
                .Append(" s=").Append(sky.ToString("X16")).Append(" b=").Append(block.ToString("X16")).Append('\n');
        }
    }

    /// <summary>The read API over random cells (in and out of range, loaded or not).</summary>
    private static void Reads(StringBuilder sb, WorldState w, Rng rng)
    {
        int h = w.WorldHeight;
        for (int i = 0; i < 300; i++)
        {
            int x = rng.Next(Origin - 3, Origin + Size + 3), z = rng.Next(Origin - 3, Origin + Size + 3);
            int y = rng.Next(0, 15) == 0 ? rng.Next(-3, 3) * (h + 2) : Ground(x, z, h) + rng.Next(-6, 6);
            sb.Append(x).Append(',').Append(y).Append(',').Append(z)
                .Append(" sky=").Append(w.Light.Get(true, x, y, z)).Append(" blk=").Append(w.Light.Get(false, x, y, z))
                .Append(" lvl=").Append(w.Light.LightLevel(x, y, z, 0)).Append('/').Append(w.Light.LightLevel(x, y, z, 11))
                .Append('/').Append(w.Light.LightLevel(x, y, z, 4, false)).Append('/').Append(w.Light.LightLevel(x, y, z, 20))
                .Append(" seesky=").Append(w.Light.CanSeeSky(x, y, z)).Append(" h=").Append(w.Light.Height(x, z)).Append('\n');
        }
    }

    [Theory]
    [InlineData(false, "468D2A01F8402BD4543B73DB692B34B730974A0EF25AD134E676320A125A822D")]
    [InlineData(true, "21339BDF0C063D916DC98F6DEF269958717834E05F6E67A6351C2587CE925223")]
    public void Unlit_region_relight_and_edits_match_the_golden(bool release1122, string digest) =>
        Check($"light-unlit-{(release1122 ? "1122" : "b181")}", digest, sb =>
        {
            var w = Terrain(release1122, serverLight: false);
            Columns(sb, w, "initial");
            for (int cx = -2; cx <= 2; cx++) for (int cz = -2; cz <= 2; cz++) w.Light.Relight(cx, cz);
            w.Light.Relight(9, 9); // not loaded: nothing
            Columns(sb, w, "relit");
            Edit(sb, w, new Rng(release1122 ? 11UL : 7UL), 250);
            Roof(sb, w);
            Columns(sb, w, "edited");
            Reads(sb, w, new Rng(3));
        });

    [Theory]
    [InlineData(false, "D895E938E8F6FECDEA38DA6AC947BEEDF75BAE055E24F88B448764ED515A28F1")]
    [InlineData(true, "EE8F3856E4B98DCC21B5164A2C97B2F27894F5AC55CE2B14162AC07BBAC688BA")]
    public void Server_lit_region_and_edits_match_the_golden(bool release1122, string digest) =>
        Check($"light-server-{(release1122 ? "1122" : "b181")}", digest, sb =>
        {
            var w = Terrain(release1122, serverLight: true);
            Columns(sb, w, "as sent");
            Edit(sb, w, new Rng(release1122 ? 23UL : 19UL), 150);
            Roof(sb, w);
            Columns(sb, w, "edited");
            Reads(sb, w, new Rng(5));
        });

    /// <summary>Server light that no block explains: sky and block light falling off by the Manhattan distance from two points
    /// between two stone floors, with extra glowstones every <paramref name="spacing"/> cells around the block light's centre.
    /// Removing the centre glowstone darkens its whole diamond, and the many glowstones re-brighten it in overlapping waves:
    /// with spacing 3 the queue reaches its limit and the brightening pass stops spreading from some cells (its "queue nearly
    /// full" guard). The darkening pass cannot reach its own limit: a chain of 14 one-step drops reaches at most ≈ 4100 cells,
    /// each queueing at most 6 neighbours.</summary>
    internal static WorldState Overloaded(int spacing)
    {
        var w = new WorldState();
        int h = w.WorldHeight;
        var ids = new byte[Size * h * Size];
        var sky = new byte[ids.Length];
        var block = new byte[ids.Length];
        for (int dx = 0; dx < Size; dx++)
        for (int dz = 0; dz < Size; dz++)
        for (int y = 0; y < h; y++)
        {
            int x = Origin + dx, z = Origin + dz, i = (dx * Size + dz) * h + y;
            if (y == 40 || y == 90) ids[i] = (byte)Blocks.Stone;
            int toBlock = Math.Abs(x - 8) + Math.Abs(y - 64) + Math.Abs(z - 8);
            int toSky = Math.Abs(x - 6) + Math.Abs(y - 66) + Math.Abs(z - 10);
            block[i] = (byte)Math.Max(0, 15 - toBlock);
            sky[i] = (byte)(y > 90 ? 15 : Math.Max(0, 15 - toSky));
            if (y > 40 && y < 90 && toBlock >= 3 && toBlock <= 12 && (x - 8) % spacing == 0 && (y - 64) % spacing == 0 && (z - 8) % spacing == 0)
            {
                ids[i] = (byte)Blocks.Glowstone;
                block[i] = 15;
            }
        }
        ids[((8 - Origin) * Size + 8 - Origin) * h + 64] = (byte)Blocks.Glowstone;
        ids[((10 - Origin) * Size + 8 - Origin) * h + 64] = (byte)Blocks.Glowstone;
        w.ApplyChunkRegion(Origin, 0, Origin, Size, h, Size, ids, null, block, sky);
        return w;
    }

    [Theory]
    [InlineData(2, "E650F72FFAE5906540DEA352196BF9E350BF16F610D470CE3914924BE0EB284A")]
    [InlineData(3, "8797D6625F11B559D60AD6833DD167D2334180FA3966F3411C2546DE486D4249")]
    public void Overloaded_queue_matches_the_golden(int spacing, string digest) =>
        Check($"light-overload-{spacing}", digest, sb =>
        {
            var w = Overloaded(spacing);
            w.SetBlock(8, 64, 8, 0, 0);
            Columns(sb, w, "glowstone removed");
            w.SetBlock(6, 66, 10, Blocks.Stone, 0);
            Columns(sb, w, "sky source covered");
            w.SetBlock(6, 66, 10, Blocks.Glowstone, 0);
            w.SetBlock(5, 66, 10, Blocks.Glowstone, 0);
            w.SetBlock(6, 66, 10, 0, 0);
            Columns(sb, w, "glowstones moved");
        });

    [Fact]
    public void Nether_edits_match_the_golden() =>
        Check("light-nether", "187C14BA1E5CE485F32D0F091A8DF21C231AE35B5E08498FB7923B552096AEAA", sb =>
        {
            var w = Terrain(false, serverLight: false, dimension: -1);
            Columns(sb, w, "initial");
            Edit(sb, w, new Rng(31), 150);
            Roof(sb, w);
            Columns(sb, w, "edited");
        });

    [Fact]
    public void Disabled_engine_leaves_the_light_alone() =>
        Check("light-disabled", "69794FCAB7792B8EEAD30EFDF92AA048980E370061B0BAD9516C8018E46629BE", sb =>
        {
            var w = Terrain(false, serverLight: true);
            w.Light.Enabled = false;
            Edit(sb, w, new Rng(41), 60);
            Columns(sb, w, "disabled edits");
        });

    [Fact]
    public void Day_cycle_matches_the_golden() =>
        Check("light-day", "237471D33FBB9C21BC5FD9880CC8E6305D817F479DE47D3745A611272CC44B8D", sb =>
        {
            foreach (float rain in new[] { 0f, 0.3f, 1f })
            foreach (float thunder in new[] { 0f, 1f })
            {
                sb.Append("rain ").Append(R(rain)).Append(" thunder ").Append(R(thunder)).Append(':');
                for (long t = -24000; t <= 48000; t += 125) sb.Append(' ').Append(LightEngine.SkylightSubtracted(t, rain, thunder));
                sb.Append('\n');
            }
            foreach (long t in new long[] { 0, 1, 5999, 6000, 12000, 13000, 18000, 23999, 24000, 123457 })
            foreach (float partial in new[] { 0f, 0.5f, 1f })
                sb.Append(t).Append('+').Append(R(partial)).Append(' ').Append(R(LightEngine.CelestialAngle(t, partial))).Append('\n');
        });
}
