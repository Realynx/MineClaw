namespace MineClaw.Client.Tests;

/// <summary>The pinned chop traces of <see cref="ClientChopTraceGoldenTests"/> (taken before the WP05 refactor of felling).</summary>
internal static class ClientChopTraceGoldenData
{
    public static readonly Dictionary<string, string> Traces = new()
    {
        ["rim-oak"] = """
            ok: chopped oak tree (5 logs, N sapling(s)), replanted at 5,64,1; 2 drop(s) lost near the void
            ----
            dug 17 at 5,64,1
            dug 17 at 5,65,1
            dug 17 at 5,66,1
            dug 17 at 5,67,1
            dug 17 at 5,68,1
            place 6:0 at 5,64,1
            """,
        ["birch"] = """
            ok: chopped birch tree (5 logs, N sapling(s)), replanted at 7,64,4
            ----
            dug 17 at 7,64,4
            dug 17 at 7,65,4
            dug 17 at 7,66,4
            dug 17 at 7,67,4
            dug 17 at 7,68,4
            place 6:2 at 7,64,4
            """,
        ["tall-jump"] = """
            ok: chopped oak tree (8 logs, N sapling(s)), replanted at 8,64,8
            ----
            dug 17 at 8,64,8
            dug 17 at 8,65,8
            dug 17 at 8,66,8
            dug 17 at 8,67,8
            dug 17 at 8,68,8
            dug 17 at 8,69,8
            dug 17 at 8,70,8
            dug 17 at 8,71,8
            place 6:0 at 8,64,8
            """,
        ["cobble-pillar"] = """
            fail: no sapling dropped, NOT replanted (chopped oak tree: 10 logs, N sapling(s); dirt at 8,63,8 is free; broke 0 leaves, 0 decayed)
            ----
            dug 17 at 8,64,8
            dug 17 at 8,65,8
            dug 17 at 8,66,8
            dug 17 at 8,67,8
            dug 17 at 8,68,8
            dug 17 at 8,69,8
            dug 17 at 8,70,8
            dug 17 at 8,71,8
            dug 17 at 8,72,8
            dug 17 at 8,73,8
            dug 4 at 8,65,8
            dug 4 at 8,64,8
            place 4:0 at 8,64,8
            place 4:0 at 8,65,8
            """,
        ["normal-oak-12"] = """
            ok: chopped oak tree (12 logs, N sapling(s)), replanted at 8,64,8
            ----
            dug 17 at 8,64,8
            dug 17 at 8,65,8
            dug 17 at 8,66,8
            dug 17 at 8,67,8
            dug 17 at 8,68,8
            dug 17 at 8,69,8
            dug 17 at 8,70,8
            dug 17 at 8,71,8
            dug 17 at 8,72,8
            dug 17 at 8,73,8
            dug 17 at 8,74,8
            dug 17 at 8,75,8
            dug 17 at 8,69,8
            dug 17 at 8,68,8
            dug 17 at 8,67,8
            dug 17 at 8,66,8
            dug 17 at 8,65,8
            dug 17 at 8,64,8
            place 17:0 at 8,64,8
            place 17:0 at 8,65,8
            place 17:0 at 8,66,8
            place 17:0 at 8,67,8
            place 17:0 at 8,68,8
            place 17:0 at 8,69,8
            place 6:0 at 8,64,8
            """,
        ["normal-birch-8"] = """
            ok: chopped birch tree (8 logs, N sapling(s)), replanted at 8,64,8
            ----
            dug 17 at 8,64,8
            dug 17 at 8,65,8
            dug 17 at 8,66,8
            dug 17 at 8,67,8
            dug 17 at 8,68,8
            dug 17 at 8,69,8
            dug 17 at 8,70,8
            dug 17 at 8,71,8
            place 6:2 at 8,64,8
            """,
        ["remnant-dirt"] = """
            ok: chopped oak tree (7 logs, N sapling(s)), replanted at 8,64,8
            ----
            dug 2 at 3,63,7
            dug 2 at 3,63,9
            dug 2 at 4,63,8
            dug 2 at 4,63,7
            dug 2 at 4,63,9
            dug 2 at 3,63,6
            dug 2 at 3,63,10
            dug 2 at 5,63,8
            dug 2 at 4,63,6
            dug 2 at 4,63,10
            dug 2 at 5,63,7
            dug 17 at 8,72,8
            dug 17 at 8,73,8
            dug 17 at 8,74,8
            dug 17 at 8,75,8
            dug 17 at 8,76,8
            dug 17 at 8,77,8
            dug 17 at 8,78,8
            dug 3 at 8,72,8
            dug 3 at 8,71,8
            dug 3 at 8,70,8
            dug 3 at 8,69,8
            dug 3 at 8,68,8
            dug 3 at 8,67,8
            dug 3 at 8,66,8
            dug 3 at 8,65,8
            dug 3 at 8,64,8
            place 3:0 at 8,64,8
            place 3:0 at 8,65,8
            place 3:0 at 8,66,8
            place 3:0 at 8,67,8
            place 3:0 at 8,68,8
            place 3:0 at 8,69,8
            place 3:0 at 8,70,8
            place 3:0 at 8,71,8
            place 3:0 at 8,72,8
            place 3:0 at 5,63,8
            place 3:0 at 5,63,7
            place 3:0 at 4,63,8
            place 3:0 at 4,63,7
            place 3:0 at 4,63,9
            place 3:0 at 4,63,6
            place 3:0 at 4,63,10
            place 3:0 at 3,63,7
            place 3:0 at 3,63,9
            place 3:0 at 3,63,6
            place 3:0 at 3,63,10
            place 6:0 at 8,64,8
            """,
        ["jungle-2x2"] = """
            ok: chopped jungle tree (48 logs, N sapling(s)), replanted at 8,64,8
            ----
            dug 17 at 8,64,8
            dug 17 at 8,64,9
            dug 17 at 8,65,8
            dug 17 at 8,65,9
            dug 17 at 8,66,8
            dug 17 at 8,66,9
            dug 17 at 8,67,8
            dug 17 at 8,67,9
            dug 17 at 8,68,8
            dug 17 at 9,64,8
            dug 17 at 9,64,9
            dug 17 at 9,65,8
            dug 17 at 9,65,9
            dug 17 at 9,66,8
            dug 17 at 9,66,9
            dug 17 at 9,67,8
            dug 17 at 9,67,9
            dug 17 at 8,68,9
            dug 17 at 9,68,8
            dug 17 at 8,69,8
            dug 17 at 9,68,9
            dug 17 at 8,69,9
            dug 17 at 9,69,8
            dug 17 at 9,69,9
            dug 17 at 8,70,8
            dug 17 at 8,70,9
            dug 17 at 9,70,8
            dug 17 at 9,70,9
            dug 17 at 8,71,8
            dug 17 at 8,71,9
            dug 17 at 9,71,8
            dug 17 at 9,71,9
            dug 17 at 8,72,8
            dug 17 at 8,72,9
            dug 17 at 9,72,8
            dug 17 at 9,72,9
            dug 17 at 8,73,8
            dug 17 at 8,73,9
            dug 17 at 9,73,8
            dug 17 at 9,73,9
            dug 17 at 8,74,8
            dug 17 at 8,74,9
            dug 17 at 9,74,8
            dug 17 at 9,74,9
            dug 17 at 8,75,8
            dug 17 at 8,75,9
            dug 17 at 9,75,8
            dug 17 at 9,75,9
            dug 17 at 8,69,8
            dug 17 at 8,68,8
            dug 17 at 8,67,8
            dug 17 at 8,66,8
            dug 17 at 8,65,8
            dug 17 at 8,64,8
            place 17:3 at 8,64,8
            place 17:3 at 8,65,8
            place 17:3 at 8,66,8
            place 17:3 at 8,67,8
            place 17:3 at 8,68,8
            place 17:3 at 8,69,8
            place 6:3 at 8,64,8
            """,
    };
}
