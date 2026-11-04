namespace MineClaw.Client.Tests;

/// <summary>The pinned gather traces of <see cref="ClientGatherTraceGoldenTests"/> (taken before the WP05 refactor reshaped them).</summary>
internal static class ClientGatherTraceGoldenData
{
    public static readonly Dictionary<string, string> Traces = new()
    {
        ["collect"] = """
            ok: picked up 5 item(s); 2 drop(s) left near the void (2 item(s) lost)
            ----

            """,
        ["mine-dirt-normal"] = """
            fail: mined 0 dirt, +0 dirt (have 0); too many failures, last: can't reach dirt at 3,62,5
            ----

            """,
        ["mine-none"] = """
            fail: mined 0 diamond_ore, +0 diamond ore (have 0); no more reachable diamond_ore within 16
            ----

            """,
        ["mine-placed-cobble"] = """
            fail: mined 0 cobblestone, +0 cobblestone (have 0); no more reachable cobblestone within 16; left 2 placed cobblestone alone (floors/walls/builds): mine only takes generator cells, ores, sand/gravel, tree logs and my own blocks (for cobble use harvest_generator); a trusted player can point at one and say 'you can break it'
            ----

            """,
        ["mine-sand"] = """
            ok: mined 3 sand, +3 sand (have 3)
            ----
            dug 12 at 6,64,3
            dug 12 at 2,64,2
            dug 12 at 3,64,7
            """,
    };
}
