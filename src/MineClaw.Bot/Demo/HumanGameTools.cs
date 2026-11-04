using System.Text.RegularExpressions;
using MineClaw.Agent.Chat;
using MineClaw.Agent.Human;
using MineClaw.Agent.Tools;
using MineClaw.Bot.Game;
using MineClaw.Bot.Memory;
using MineClaw.World;
using MPos = MineClaw.Agent.Memory.BlockPos;

namespace MineClaw.Bot.Demo;

/// <summary>Player-in-the-loop game tools: <c>watch_demo</c> (with the bot's richer summary) and <c>ask_stand</c>.</summary>
public static partial class HumanGameTools
{
    /// <summary>
    /// Replaces the runtime's <c>watch_demo</c> (same name and inputs) so steps use <see cref="DemoSummary"/>
    /// ("stood at .. facing lava; right-clicked water bucket on ..; cobblestone appeared at ..").
    /// </summary>
    public static void RegisterWatchDemo(ToolRegistry tools, ChatRouter chat, IDemonstrationSource source, IStructureMemory structures)
    {
        tools.Register(new DelegateTool("watch_demo",
            DemoWait.ToolDescription,
            ToolSchema.Create().Str("player").Str("structure").Int("max_seconds", "default 120", required: false).Build(),
            async (input, ct) =>
            {
                var player = input.ReqStr("player");
                var name = input.ReqStr("structure");
                int max = Math.Clamp(input.Int("max_seconds") ?? 120, 10, 600);
                var known = structures.Area(name);
                (MPos, MPos)? area = known is { } k
                    ? (new MPos(k.Min.X - 4, k.Min.Y - 3, k.Min.Z - 4), new MPos(k.Max.X + 4, k.Max.Y + 3, k.Max.Z + 4))
                    : null;

                await using var session = await source.StartAsync(player, area, ct).ConfigureAwait(false);
                var waited = await DemoWait.RunAsync(chat, session, player, max, null, ct).ConfigureAwait(false);
                var actions = await session.StopAsync(ct).ConfigureAwait(false);
                // Absolute coordinates: offsets from the structure's min corner (live2 "stood at -1,+1,-1") were misread as
                // places ("is the clone's brick the one at 3,64,5?") and carried no anchor.
                var steps = DemoSummary.Summarize(actions);
                if (steps.Count == 0) return ToolResult.Error("saw nothing to learn" + DemoWait.SaidSuffix(waited));
                structures.SaveSteps(name, steps, player);
                return ToolResult.Success($"learned {name} ({steps.Count} steps): " + string.Join("; ", steps) + DemoWait.SaidSuffix(waited));
            }));
    }

    /// <summary>
    /// <c>ask_stand</c>: with <c>at</c>, asks the player to stand there and waits until their position confirms it;
    /// without, asks them to stand at the spot they mean and say "here", then reports where they stand.
    /// </summary>
    /// <param name="digCells">When given, a spot at a cobblestone generator ("stand where the clone should mine") registers the
    /// generator cell the player faces/stands by as a dig cell.</param>
    public static void RegisterAskStand(ToolRegistry tools, ChatRouter chat, IGameClient client, int timeoutSeconds = 90,
        TimeSpan? pollInterval = null, Perception.DigCells? digCells = null)
    {
        var poll = pollInterval ?? TimeSpan.FromMilliseconds(500);
        tools.Register("ask_stand", "Ask a player to stand at x,y,z and wait until they do; no at = they go where they mean and say here.",
            ToolSchema.Create().Str("player").Str("at", "x,y,z", required: false).Str("say", "message to send", required: false).Build(),
            async (input, ct) =>
            {
                var player = input.ReqStr("player");
                var target = Locations.ParseCoords(input.Str("at"));
                if (input.Str("at") is { Length: > 0 } && target == null) return ToolResult.Error("at must be x,y,z");
                // Hive: this bot owns the conversation with that player until they answer or it times out.
                using var thread = chat.HoldThread(player, TimeSpan.FromSeconds(timeoutSeconds + 5));
                var ask = new AskStand(chat, client, player, input.Str("say"), timeoutSeconds, poll, digCells);
                return target is { } t
                    ? await ask.StandAtAsync(t, ct).ConfigureAwait(false)
                    : await ask.StandHereAsync(ct).ConfigureAwait(false);
            }, cheapTier: false);
    }

    /// <summary>One <c>ask_stand</c> call: the player, the optional line to say, the deadline.</summary>
    private sealed class AskStand(ChatRouter chat, IGameClient client, string player, string? say, int timeoutSeconds, TimeSpan poll,
        Perception.DigCells? digCells)
    {
        private readonly DateTimeOffset _deadline = DateTimeOffset.UtcNow.AddSeconds(timeoutSeconds);

        // One prompt only: the brain's own line wins; the tool speaks only if the brain said nothing this turn.
        private readonly bool _quiet = chat.ModelSpokeThisTurn && say == null;

        /// <summary>Asks them to stand at <paramref name="t"/>; done once two polls in a row see them there.</summary>
        public async Task<ToolResult> StandAtAsync(BlockPos t, CancellationToken ct)
        {
            if (!_quiet)
                await chat.SayAsync(say ?? $"{player}, can you stand at {chat.Landmark?.Invoke(t.X, t.Y, t.Z) ?? Locations.Fmt(t)}?",
                    audience: [player], ct: ct).ConfigureAwait(false);
            int hits = 0;
            while (DateTimeOffset.UtcNow < _deadline)
            {
                var e = client.World.Entities.FindPlayer(player);
                if (e != null && IsAt(e.Position, t)) { if (++hits >= 2) return ToolResult.Success($"{player} is at {Locations.Fmt(t)}"); }
                else hits = 0;
                await Task.Delay(poll, ct).ConfigureAwait(false);
            }
            var last = client.World.Entities.FindPlayer(player);
            return ToolResult.Error($"{player} not there after {timeoutSeconds}s" + (last != null ? $" (at {Locations.Fmt(last.Position)})" : " (can't see them)"));
        }

        /// <summary>Asks them to stand on the spot they mean and say "here"; reports where they stand (and a dig cell there).</summary>
        public async Task<ToolResult> StandHereAsync(CancellationToken ct)
        {
            var wait = chat.WaitForReplyAsync(player, TimeSpan.FromSeconds(timeoutSeconds), ct);
            if (!_quiet)
                await chat.SayAsync(say ?? $"{player}, stand on the spot and tell me when you're there", audience: [player], ct: ct).ConfigureAwait(false);
            var said = new List<string>();
            string? reply = await HereReplyAsync(await wait.ConfigureAwait(false), said, ct).ConfigureAwait(false);
            if (reply == null) return NotConfirmed(said);
            var p = client.World.Entities.FindPlayer(player);
            if (p == null) return ToolResult.Error($"{player} said \"{reply}\" but I can't see them");
            return Stands(p, reply);
        }

        /// <summary>"here", "here is the cell", "ok", "this spot"...; anything else first: keep waiting a little, it may be chatter
        /// (collected in <paramref name="said"/>). Null when no "here" came.</summary>
        private async Task<string?> HereReplyAsync(string? reply, List<string> said, CancellationToken ct)
        {
            while (reply != null && !ReplyIntent.IsHere(reply) && said.Count < 3 && DateTimeOffset.UtcNow < _deadline)
            {
                said.Add(ReplyIntent.Strip(reply));
                reply = await chat.WaitForReplyAsync(player, _deadline - DateTimeOffset.UtcNow, ct).ConfigureAwait(false);
            }
            if (reply != null && !ReplyIntent.IsHere(reply)) { said.Add(ReplyIntent.Strip(reply)); reply = null; }
            return reply;
        }

        /// <summary>Live2 01:12:31: chatter ("that is wood not cobble 67") was taken as the answer and its spot as the place.
        /// Without a "here" the spot is NOT confirmed: say so, with where they are, and let the brain ask.</summary>
        private ToolResult NotConfirmed(List<string> said)
        {
            if (said.Count == 0) return ToolResult.Error($"no reply in {timeoutSeconds}s");
            var now = client.World.Entities.FindPlayer(player);
            return ToolResult.Error($"not confirmed: {player} didn't say \"here\" (said \"{Short(said[^1])}\")"
                + (now != null ? $"; they're at {Locations.Fmt(now.Position)} now, which may not be the spot" : "") + "; ask again");
        }

        /// <summary>Where they stand and face; at a generator, the cell they mean is saved as a dig cell.</summary>
        private ToolResult Stands(Entity p, string reply)
        {
            var look = Demo.DemonstrationRecorder.LookTarget(client.World, p.Position, p.Yaw, p.Pitch, solidOnly: true);
            var facing = look is { } lt ? $" facing {GameTools.Names.Block(lt.Id)} at {Locations.Fmt(lt.Pos)}" : "";
            string taught = "";
            if (digCells != null && DigCellAt(client.World, p.Position.ToBlockPos(), look?.Pos) is { } cell)
            {
                digCells.Register(cell, player);
                taught = $"; dig cell {Locations.Fmt(cell)} ({Perception.BlockDescriber.GenCellName(client.World, cell) ?? "gen cell"}) saved, never protected";
            }
            return ToolResult.Success($"{player} stands at {Locations.Fmt(p.Position)}{facing} (said \"{ReplyIntent.Strip(reply)}\"){taught}");
        }
    }

    /// <summary>
    /// The generator cell a player at <paramref name="feet"/> means: the gen cell they face, else the nearest one within reach
    /// of where they stand (a spot at a cobble generator during a gen conversation is a dig cell). Null when none.
    /// </summary>
    public static BlockPos? DigCellAt(WorldState w, BlockPos feet, BlockPos? facing)
    {
        if (facing is { } f && Perception.BlockDescriber.IsGenCell(w, f)) return f;
        var near = Perception.BlockDescriber.GenCellsNear(w, feet, 3)
            .Where(c => new Vec3d(feet.X + 0.5, feet.Y + 1.62, feet.Z + 0.5).DistanceTo(c.Center) <= 4.5).ToList();
        return near.Count > 0 ? near[0] : null;
    }

    private static string Short(string s) => s.Length <= 80 ? s : s[..77] + "...";

    /// <summary>Same cell horizontally (±1 tolerance) and within 1 block vertically.</summary>
    public static bool IsAt(Vec3d pos, BlockPos t)
    {
        var c = pos.ToBlockPos();
        return Math.Abs(c.X - t.X) <= 1 && Math.Abs(c.Z - t.Z) <= 1 && Math.Abs(c.Y - t.Y) <= 1 &&
               Math.Abs(pos.X - (t.X + 0.5)) <= 1.0 && Math.Abs(pos.Z - (t.Z + 0.5)) <= 1.0;
    }
}
