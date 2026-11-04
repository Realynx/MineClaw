using System.Security.Cryptography;
using System.Text;
using MineClaw.Agent.Chat;

namespace MineClaw.Agent.Tests;

/// <summary>
/// Pins every allow/deny decision of <see cref="CommandGate"/> (WP06 refactor; security-relevant): a corpus of lines × configs
/// (default, allowlist, blocked /tp /time /effect, requests off) × arming paths (none, model Authorize, trusted chat grants and
/// requests, remote requests, the hive's managed /whitelist, Story /tp /time /effect with the 90 s window). Hashed against
/// <see cref="Expected"/>; on a mismatch the dump lands in the temp dir.
/// </summary>
public class CommandGateGoldenTests
{
    private const string ExpectedHash = "FB19950C826B82C0411CC6B6A3BB1AC43149FA22F89D13D192E9634109BE685A";

    private static readonly string[] Lines =
    [
        "", "hello", " /me waves", "/me", "/tell kiwi hi", "/msg fox yo", "/MSG fox yo", "/time set day", "/time set 6000", "/time set 123456",
        "/time set day now", "/tp maid_67 10 64 -20", "/tp maid_67 10 64", "/tp PoofImaFox 10 64 -20", "/tp maid_67 1.5 64 2", "/tp ma 1 2 3",
        "/tp maid_67 123456789 64 1", "/tp maid_67 1 1234 1", "/effect maid_67 minecraft:speed 0", "/effect maid_67 minecraft:speed 30 1 true",
        "/effect maid_67 minecraft:speed 30 5 true", "/effect PoofImaFox minecraft:speed 0", "/effect maid_67 minecraft:haste 0",
        "/whitelist add maid_68", "/whitelist remove maid_68", "/whitelist add PoofImaFox", "/whitelist list", "/whitelist add ab",
        "/op bob", "/minecraft:op bob", "/OP bob", "/deop bob", "/stop", "/ban x", "/ban-ip 1.2.3.4", "/pardon x", "/kick x", "/save-off",
        "/execute as @a run op bob", "/execute as @a run say hi", "/execute as @a run execute as @s run op bob", "/execute as @a run /op bob",
        "/execute run", "/execute", "/weather clear", "/give PoofImaFox stone 1", "/gamemode creative", "/minecraft:time set day",
        "/a", "/", "//wand", "/x" + new string('y', 120), "\t/time set day", "/time\tset day",
    ];

    private static readonly (string Name, Action<CommandOptions> Configure)[] Configs =
    [
        ("default", _ => { }),
        ("allowlist", o => o.Allowed = ["/tp", "/time", "/weather"]),
        ("blocked-story", o => o.Blocked = [.. o.Blocked, "/tp", "/time", "/effect"]),
        ("requests-off", o => { o.RequestSeconds = 0; o.AuthorizationSeconds = 5; }),
        ("execute-allowed", o => o.Blocked = [.. o.Blocked.Where(b => b != "/execute")]),
    ];

    private static (CommandGate Gate, ManualTime Time, List<string> Audit) Gate(Action<CommandOptions> configure)
    {
        var o = new CommandOptions();
        configure(o);
        var time = new ManualTime();
        var gate = new CommandGate(o, () => ["PoofImaFox", "kiwi"], time) { ManagedAccount = n => n.StartsWith("maid_", StringComparison.Ordinal) };
        var audit = new List<string>();
        gate.Executed += (c, by, at) => audit.Add($"{c} by {by} at {at:O}");
        return (gate, time, audit);
    }

    private static string D(CommandDecision d) => $"{(d.Ok ? "OK" : "NO")} \"{d.Text}\" by={d.AuthorizedBy ?? "-"}";

    private static string Dump()
    {
        var sb = new StringBuilder();
        foreach (var (cname, configure) in Configs)
        {
            sb.Append("#### ").Append(cname).Append('\n');
            foreach (var line in Lines)
            {
                var (g, time, audit) = Gate(configure);
                sb.Append('[').Append(line).Append("] name=").Append(CommandGate.NameOf(line) ?? "-").Append(" blocked=").Append(g.IsBlocked(line))
                  .Append(" always=").Append(g.IsAlwaysAllowed(line)).Append(" out=").Append(g.AllowOutgoing(line)).Append('\n');
                sb.Append("  authorize: ").Append(D(g.Authorize(line))).Append(" then out=").Append(g.AllowOutgoing(line)).Append(',').Append(g.AllowOutgoing(line)).Append('\n');
                // Scoped C#-only exceptions: armed once, sent once, the window.
                var (h, ht, haudit) = Gate(configure);
                bool wl = h.ArmManagedWhitelist(line), tp = h.ArmManagedTeleport(line, "story:fox"), tm = h.ArmStoryTime(line, "story:fox"), sp = h.ArmManagedSpeed(line, "story:fox");
                sb.Append("  arm wl=").Append(wl).Append(" tp=").Append(tp).Append(" time=").Append(tm).Append(" speed=").Append(sp)
                  .Append(" out=").Append(h.AllowOutgoing(line)).Append(',').Append(h.AllowOutgoing(line));
                var (k, kt, _) = Gate(configure);
                k.ArmManagedWhitelist(line); k.ArmManagedTeleport(line, "story:fox");
                kt.Advance(TimeSpan.FromSeconds(16));
                sb.Append(" after16s=").Append(k.AllowOutgoing(line));
                kt.Advance(TimeSpan.FromSeconds(75));
                sb.Append(" after91s=").Append(k.AllowOutgoing(line)).Append('\n');
                foreach (var a in haudit) sb.Append("  audit ").Append(a).Append('\n');
            }
            Chats(sb, configure);
        }
        return sb.ToString().Replace("\r\n", "\n", StringComparison.Ordinal);
    }

    private static readonly (string Label, ChatMessage Msg, bool Mine, bool Explicit)[] Messages =
    [
        ("own public", new(ChatKind.Public, "PoofImaFox", "maid run /time set day", "<PoofImaFox> maid run /time set day"), true, true),
        ("own public not explicit", new(ChatKind.Public, "PoofImaFox", "run /time set day", "<PoofImaFox> run /time set day"), true, false),
        ("not mine", new(ChatKind.Public, "PoofImaFox", "maid run /time set day", "<PoofImaFox> maid run /time set day"), false, true),
        ("untrusted", new(ChatKind.Public, "stranger", "maid run /time set day", "<stranger> maid run /time set day"), true, true),
        ("spoofed frame", new(ChatKind.Public, "PoofImaFox", "maid run /time set day", "[x] PoofImaFox: maid run /time set day"), true, true),
        ("prefixed own", new(ChatKind.Public, "PoofImaFox", "maid run /time set day", "<[Admin] PoofImaFox> maid run /time set day"), true, true),
        ("whisper", new(ChatKind.Whisper, "kiwi", "/tp maid_67 1 2 3 and `/weather clear`", "kiwi whispers to you: /tp maid_67 1 2 3"), true, false),
        ("emote", new(ChatKind.Emote, "kiwi", "runs /time set day", "* kiwi runs /time set day"), true, true),
        ("request words", new(ChatKind.Public, "kiwi", "maid set it to day pls", "<kiwi> maid set it to day pls"), true, true),
        ("blocked ask", new(ChatKind.Public, "kiwi", "maid run /op kiwi", "<kiwi> maid run /op kiwi"), true, true),
    ];

    private static readonly string[] Attempts = ["/time set day", "/weather clear", "/tp maid_67 1 2 3", "/op kiwi", "/give kiwi stone 1", "/time set day"];

    private static void Chats(StringBuilder sb, Action<CommandOptions> configure)
    {
        foreach (var (label, msg, mine, expl) in Messages)
        {
            var (g, time, audit) = Gate(configure);
            sb.Append("chat ").Append(label).Append(": observed=").Append(g.Observe(msg, mine, expl)).Append('\n');
            foreach (var a in Attempts)
            {
                var d = g.Authorize(a);
                sb.Append("  ").Append(a).Append(" -> ").Append(D(d)).Append(" out=").Append(g.AllowOutgoing(a)).Append('\n');
            }
            time.Advance(TimeSpan.FromSeconds(61));
            sb.Append("  +61s ").Append(D(g.Authorize("/weather clear"))).Append('\n');
            time.Advance(TimeSpan.FromSeconds(60));
            sb.Append("  +121s ").Append(D(g.Authorize("/weather clear"))).Append('\n');
        }
        // A request, then the player moves on to another bot: the request is over.
        var (m, mt, _) = Gate(configure);
        m.Observe(new(ChatKind.Public, "kiwi", "maid make it day", "<kiwi> maid make it day"), true, true);
        m.Observe(new(ChatKind.Public, "kiwi", "c follow", "<kiwi> c follow"), false, false);
        sb.Append("moved on: ").Append(D(m.Authorize("/time set day"))).Append('\n');
        // Request budget: RequestCommands uses.
        var (b, _, _) = Gate(configure);
        b.Observe(new(ChatKind.Public, "kiwi", "maid give everyone a pickaxe", "<kiwi> maid give everyone a pickaxe"), true, true);
        for (int i = 0; i < 10; i++) sb.Append("budget ").Append(i).Append(": ").Append(D(b.Authorize($"/give p{i} stone_pickaxe 1"))).Append('\n');
        // Remote (viewer) requests and exact commands.
        var (r, _, _) = Gate(configure);
        sb.Append("remote untrusted=").Append(r.ObserveRemote("stranger", "maid make it day", "viewer"))
          .Append(" empty=").Append(r.ObserveRemote("kiwi", "  ", "viewer"))
          .Append(" request=").Append(r.ObserveRemote("kiwi", "make it day", "viewer"))
          .Append(" exact=").Append(r.ObserveRemote("PoofImaFox", "run /weather clear", "viewer")).Append('\n');
        foreach (var a in Attempts) sb.Append("  remote ").Append(a).Append(" -> ").Append(D(r.Authorize(a))).Append('\n');
    }

    [Fact]
    public void Every_gate_decision_is_pinned()
    {
        var dump = Dump();
        Assert.Equal(dump, Dump());
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(dump)));
        if (hash != ExpectedHash)
        {
            var dir = Path.Combine(Path.GetTempPath(), "mineclaw-golden");
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "command-gate.txt"), dump);
        }
        Assert.True(hash == ExpectedHash, $"command gate decisions changed: {hash}");
    }
}
