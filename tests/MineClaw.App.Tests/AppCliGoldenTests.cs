using System.Globalization;
using System.Text;
using MineClaw.App.Hosting;

namespace MineClaw.App.Tests;

/// <summary>
/// Pins <see cref="AppCli.Parse"/> (WP07): every flag, both value forms, missing and bad values, the cross-flag checks and the
/// error texts. A refactor must leave <see cref="AppCliGoldenData.Expected"/> unchanged.
/// </summary>
public class AppCliGoldenTests
{
    private static readonly string[][] Cases =
    [
        [],
        ["--script", "a.txt"], ["--script=b.txt"], ["--script"], ["--script", "--tui"], ["--script="],
        ["--minutes", "2.5"], ["--minutes=0"], ["--minutes", "-1"], ["--minutes", "abc"], ["--minutes"], ["--minutes", "1e2"],
        ["--void-abort"], ["--record"], ["--record", "dir"], ["--record=d2"], ["--record", "--tui"],
        ["--no-key-check"], ["--no-tui"], ["--tui"], ["--tui", "--no-tui"], ["--stdin-commands"], ["--demo"],
        ["--demo-bots", "9"], ["--demo-bots", "0"], ["--demo-bots", "2.7"], ["--demo-bots"], ["--demo-bots", "x"],
        ["--demo", "--frame"], ["--demo", "--frame", "80x24"], ["--demo", "--frame=80X24"], ["--demo", "--frame", "10x5"], ["--demo", "--frame", "wide"],
        ["--frame", "80x24"], ["--demo", "--frame", "80x24x2"], ["--demo", "--frame", "--tui"],
        ["--bot", "3"], ["--bot", "0"], ["--bot", "x"], ["--bot"], ["--view", "top"], ["--view"],
        ["--data-dir", "d"], ["--log-dir", "l"], ["--config", "c.json"], ["--config=x=y"], ["--shutdown-timeout", "7"], ["--shutdown-timeout", "z"],
        ["--log-format", "JSON"], ["--log-format", "xml"], ["--log-format"], ["--log-format=systemd"],
        ["--clone", "maid_chan_67"], ["--parent-pipe", "p"], ["--clone", "maid_chan_67", "--parent-pipe", "p"], ["--clone"],
        ["--server-version", "1.12.2"], ["--server-version", "b1.8.1"], ["--server-version", "auto"], ["--server-version", "9.9"], ["--server-version"],
        ["-h"], ["--help"], ["-?"], ["--schema-version"],
        ["--reset-memory"], ["--reset-memory", "--pack", "p.mcshard"], ["--reset-memory", "--pack", " "], ["--pack", "p.mcshard"],
        ["--reset-memory", "--pack", "p", "--modes", "none"], ["--reset-memory", "--pack", "p", "--modes", "a.mcshard, b.mcshard,,"],
        ["--reset-memory", "--pack", "p", "--modes", "NONE"], ["--modes", "a"], ["--modes", "none"], ["--modes"],
        ["--no-sdk"], ["--reset-memory", "--pack", "p", "--no-sdk", "--backup-dir", "bk", "--yes"], ["--backup-dir", "bk"], ["-y"], ["--yes"],
        ["--reset-memory", "--pack", "p", "--clone", "c", "--parent-pipe", "p"],
        ["--bogus"], ["bare"], ["-x"], ["--=x"], ["--a=b"], ["--tui=on"], ["--no-tui=1"],
        ["--minutes", "1", "--script", "s", "--bot", "2", "--view", "iso", "--demo", "--demo-bots", "4", "--frame", "100x30"],
    ];

    private static string Fmt(double? d) => d?.ToString("R", CultureInfo.InvariantCulture) ?? "-";

    internal static string Dump()
    {
        var sb = new StringBuilder();
        foreach (var args in Cases)
        {
            var c = AppCli.Parse(args);
            sb.Append('[').Append(string.Join(" ", args.Select(a => "'" + a + "'"))).Append("]\n");
            sb.Append(CultureInfo.InvariantCulture,
                $"  script={c.Script ?? "-"} minutes={Fmt(c.Minutes)} void={c.VoidAbort} nokey={c.NoKeyCheck} tui={c.Tui} stdin={c.StdinCommands} demo={c.Demo} bots={c.DemoBots}")
              .Append(CultureInfo.InvariantCulture,
                $" frame={(c.Frame is { } f ? $"{f.Width}x{f.Height}" : "-")} bot={c.Bot} view={c.ViewMode ?? "-"} data={c.DataDir ?? "-"} log={c.LogDir ?? "-"} config={c.ConfigPath ?? "-"}")
              .Append(CultureInfo.InvariantCulture,
                $" shutdown={Fmt(c.ShutdownTimeoutSeconds)} logfmt={c.LogFormat ?? "-"} help={c.Help} schema={c.PrintSchemaVersion} clone={c.CloneName ?? "-"} pipe={c.ParentPipe ?? "-"}")
              .Append(CultureInfo.InvariantCulture,
                $" record={c.Record ?? "-"} sv={c.ServerVersion ?? "-"} reset={c.ResetMemory} pack={c.ResetPack ?? "-"} modes=[{string.Join("|", c.ResetModes)}] nosdk={c.ResetNoSdk} backup={c.ResetBackupDir ?? "-"} yes={c.Yes}\n");
            foreach (var e in c.Errors) sb.Append("  ! ").Append(e).Append('\n');
        }
        return sb.ToString().Replace(Path.DirectorySeparatorChar, '/');
    }

    [Fact]
    public void Parse_matches_the_golden_dump() => RouteGolden.Check("appcli", AppCliGoldenData.Expected, Dump());
}
