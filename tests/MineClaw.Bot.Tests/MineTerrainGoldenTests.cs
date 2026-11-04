using System.Text;
using MineClaw.Bot.Blueprints.Mine;

namespace MineClaw.Bot.Tests;

/// <summary>
/// Pins <see cref="MineTerrain"/>'s chunk generator (columns, pockets, ores, caves) over several seeds, both versions and chunks
/// on both sides of the origin: an FNV-1a hash of every chunk's states. A refactor must leave <see cref="Expected"/> unchanged
/// (the browser's port is held to the same bits by the plot hashes in <see cref="MineDesignerTests"/>). Regenerate (only for an
/// intended change) by setting WP02_GOLDEN_OUT to a file path and running this test.
/// </summary>
public class MineTerrainGoldenTests
{
    private static readonly string[] Seeds = ["0", "-7", "abc", "123456789012", "hello", "Mine Seed 42"];
    private static readonly string[] Versions = ["b1.8.1", "1.12.2"];

    private const string Expected = """
        b1.8.1 0: 64E1F705 A9844D95 1A28B305 CFE23245 C3F8CD15 5211BEB5 219E1FD5 2A106095 4173FD75 CC791EE5 8676E295 9604DD15 837A7805 F8DC44B5 1DDAA4B5 F8FE2935
        b1.8.1 -7: 54AC40F5 E18F5AE5 08E31BE5 EC310BA5 45AF7CA5 0801A745 1195D9B5 D63A7B35 FF858415 10E2FA65 FD27A745 8D060DB5 CDC31FB5 5D8A3EA5 B8A53915 17CEE6E5
        b1.8.1 abc: FECAFBA5 61DC3695 656B9FE5 752A2485 D06EE235 62D76115 AAB7D855 2FBCD035 835606F5 6D3E76D5 98262E25 8289B875 9F7D59B5 5BD39635 9D0546E5 7E9BF005
        b1.8.1 123456789012: FF8A2115 2BD09E25 624334A5 992474D5 BA185EF5 AE7E4585 3D485415 4C0D8BF5 798A4CF5 C9AB0B15 52FA59F5 5B1F8765 D911C815 CAC15275 AD1186A5 67844175
        b1.8.1 hello: 1A0E0C65 993B3EE5 02AE9705 35338795 F3CF7CE5 5F523A25 39407A55 F71E9985 D9561F65 D6F5F885 7BDF2AA5 157454F5 BD081445 977B9E25 EA5542C5 DF2F7225
        b1.8.1 Mine Seed 42: E9FAB3C5 D52BD355 DD459485 1B29E755 B828FF55 E495B185 0795B525 154FE0F5 F0FB4F75 10A607C5 32893695 52B29CB5 D6B2C1C5 179EE645 A5A7F6B5 577F3365
        1.12.2 0: 68B85F6D 1AAE25CA E5AC8179 87C63BEE DB7ABFC7 3F812039 689656E0 36028097 25217762 D33551AA B71031EA 53FB75F5 7A77C48F B233E201 467B7E5D A4A9BDE7
        1.12.2 -7: 015A87C0 ABC02E3D 86339992 C1FB8BFC 73F02198 C0316432 5A744633 BCC25AC6 06EA1D56 A3E4E0AC 0CCE2125 57684106 669F33BF 65BE3082 C712E2BF C14537DD
        1.12.2 abc: D0CB4431 E41FE26E 048521A9 76F3A02F B91B5B47 B8B4AD54 20C68A96 7432D11A 1BCE1343 3BE3AC47 C909ED99 480E0EE3 00553707 95FE3A7B 8E219C37 7A18C82B
        1.12.2 123456789012: EC64709D 0D9FF571 BB3BADF8 CDA5FA57 EA5ADBC5 3A2E0E26 B1EA0E10 864A3A90 D632E857 B2B65663 66076EEF D395B214 9815345D CF802576 0C8288BC 984CF8DF
        1.12.2 hello: 515CE4F4 F569AE50 53E5D07F 65F13EF1 38C92EAD 85B1DB4A 3DD1C50E 3F96CE2F 500980D3 98AB87C9 CA8BD725 03B07101 D7FA41B1 3425C9B6 D813B203 83E4302F
        1.12.2 Mine Seed 42: 82821B98 A5FC542B AF9D325F 5DCFDF74 72B543DA 17B15F5F 9C0527E7 183B6AE8 118777D0 1CDA21AD C29318C4 C4C209FD 60BF13C5 6E1E9154 EC5584D4 8DCA3C4B
        """;

    private static string Dump()
    {
        var sb = new StringBuilder();
        foreach (var v in Versions)
            foreach (var s in Seeds)
            {
                var t = MineTerrain.For(s, v);
                sb.Append(v).Append(' ').Append(s).Append(':');
                for (int cz = -2; cz <= 1; cz++)
                    for (int cx = -2; cx <= 1; cx++)
                    {
                        uint h = 2166136261;
                        foreach (var c in t.Chunk(cx, cz)) unchecked { h = (h ^ c) * 16777619; }
                        sb.Append(' ').Append(h.ToString("X8"));
                    }
                sb.Append('\n');
            }
        return sb.ToString();
    }

    [Fact]
    public void Chunks_match_the_golden_hashes()
    {
        var actual = Dump();
        if (Environment.GetEnvironmentVariable("WP02_GOLDEN_OUT") is { Length: > 0 } path) File.WriteAllText(path, actual);
        Assert.Equal(Expected.ReplaceLineEndings("\n").TrimEnd('\n'), actual.TrimEnd('\n'));
    }
}
