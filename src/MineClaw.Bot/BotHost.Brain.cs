using MineClaw.Agent;
using MineClaw.Agent.Llm;
using MineClaw.Bot.Bodies;
using MineClaw.Bot.Demo;
using MineClaw.Bot.Game;
using MineClaw.Bot.GameTools;
using MineClaw.Bot.Memory;
using MineClaw.Bot.WorldIndex;
using MineClaw.World.Claims;
using Microsoft.Extensions.Logging;
using MPos = MineClaw.Agent.Memory.BlockPos;

namespace MineClaw.Bot;

public sealed partial class BotHost
{
    /// <summary>
    /// <see cref="CreateBrain"/>'s wiring, one step per subsystem, in the order the steps must run: chat handlers fire in the
    /// order they were added and the tools register in the order the model sees them (a stable cached prefix). Every field is
    /// set once, by the step that creates it.
    /// </summary>
    private sealed class BrainBuilder(IGameClient client, AgentOptions agentOptions, BotOptions botOptions, ILlmClient? llm,
        ILoggerFactory? loggers, TimeProvider? time, bool persist, WorldIndexOptions? worldIndex, ActivityOptions? activity,
        IClaimBoard? claims, Func<IEnumerable<string>>? hiveBots)
    {
        private readonly ILogger? _log = loggers?.CreateLogger("MineClaw.Bot");
        private DemonstrationRecorder _recorder = null!;
        private AgentRuntime _rt = null!;
        private Body _main = null!;
        private BodyRegistry _bodies = null!;
        private BodyRoutingGameClient _toolClient = null!, _adapter = null!;
        private MemoryStoreAdapter _memory = null!;
        private Protection _protection = null!;
        private WorldIndexer? _indexer;
        private PlayerActivityTracker? _activityTracker;
        private WorldMemory? _worldMemory;
        private MechanismWatcher _mechanisms = null!;
        private Perception.DigCells _digCells = null!;
        private Perception.DigCellCorrections _corrections = null!;
        private PlacedBlockStore _placedStore = null!;
        private ProtectionMarks _marks = null!;
        private ProtectionTelemetry _telemetry = null!;
        private Func<double, double, double, string?> _landmark = null!;
        private Func<MineClaw.World.BlockPos, IReadOnlyList<MineClaw.World.BlockPos>> _genCells = null!;
        private AcquisitionSources _acquisition = null!;
        private Perception.GestureTracker _gestures = null!;
        private ProtectionCommands _protectCmds = null!;
        private System.Threading.Timer? _protectionSync;
        private SkillHub _skillHub = null!;
        private Blueprints.BlueprintWiring? _blueprints;

        private TimeProvider Clock => time ?? TimeProvider.System;

        /// <summary>A hive bot by the supervisor's list.</summary>
        private bool InHive(string n) => hiveBots?.Invoke().Contains(n, StringComparer.OrdinalIgnoreCase) == true;

        public BrainContext Build()
        {
            _recorder = new DemonstrationRecorder(client, botOptions, time);
            CreateRuntime();
            CreateBodies();
            CreateStores();
            CreateDigCells();
            AttachHandoffIntents(client, _rt, hiveBots, _log);
            WireDigCells();
            WireProtection();
            WireChatPerception();
            RegisterGameTools();
            CreateGestures();
            WireChatProtection();
            WireFightAndRegion();
            WireSkillsAndLedger();
            // Brain gets the cheap single-shot tools + delegate/ask_memory; the worker gets the game macro tools.
            _rt.ApplyBrainSplit();
            AttachBlueprints();
            return Context();
        }

        private void CreateRuntime()
        {
            _rt = AgentRuntime.Create(agentOptions, SendChatAsync, llm,
                position: () => client.IsPlaying && client.World.Player.Position.ToBlockPos() is var p ? new MPos(p.X, p.Y, p.Z) : null,
                demonstrations: _recorder, loggers: loggers, time: time, persist: persist);
            // Memory is dimension-aware: writers stamp, readers ("where", recall, auto-retrieval, digest) default to this one.
            _rt.Rag.CurrentDimension = () => client.World.Dimension;

            // Outgoing choke point for skills/tools/console chat: no unauthorised /command leaves through the adapter either.
            if (client is MinecraftGameClient gated) gated.OutgoingFilter = _rt.Commands.AllowOutgoing;
        }

        private async Task SendChatAsync(string line, CancellationToken ct)
        {
            // The runtime already passed this line through its command gate (an armed run_command line is consumed
            // there), so the real client's adapter filter is bypassed here; every other sender goes through it.
            var r = client is MinecraftGameClient raw ? await raw.Inner.SendChatAsync(line, ct).ConfigureAwait(false)
                : await client.SendChatAsync(line, ct).ConfigureAwait(false);
            if (!r.Ok) _log?.LogWarning("chat not sent ({Reason}): {Line}", r.Message, line);
        }

        /// <summary>One brain, many bodies: the main body (the runtime's activity slot and standing orders) and the two routers every
        /// tool registers against. Hive: container/block actions of the game tools take a lease first (ClaimAwareGameClient).</summary>
        private void CreateBodies()
        {
            IGameClient mainGame = claims == null ? client : new ClaimAwareGameClient(client, claims);
            _main = new Body(client.Username, isMain: true, client, mainGame, _rt.Activities, _rt.Agent.Orders);
            _bodies = new BodyRegistry(_main);
            var bodyLog = loggers?.CreateLogger("MineClaw.Bodies");
            _toolClient = new BodyRoutingGameClient(_bodies, b => b.Game, bodyLog);
            _adapter = new BodyRoutingGameClient(_bodies, b => b.Adapter, bodyLog);
            _rt.Agent.BodyActivities = name => _bodies.Find(name)?.Activities;
        }

        private void CreateStores()
        {
            _memory = new MemoryStoreAdapter(_rt.Memory, _rt.Observations,
                () => (MineClaw.World.BlockPos?)client.World.Player.Position.ToBlockPos());
            _protection = new Protection(botOptions, _memory, _memory, time);
            foreach (var u in _protection.Unknown) _log?.LogWarning("Bot config: unknown {Name}", u);
            (_indexer, _activityTracker) = StartWorldIndex(client, _rt, _memory, worldIndex, activity, time, loggers, hiveBots);
            _worldMemory = StartWorldMemory(client, _rt, _indexer, worldIndex, time, loggers);
            _mechanisms = StartMechanisms(client, _rt, time, loggers);
            _recorder.Activity = _activityTracker; // teaching sessions reuse the tracker's attribution
        }

        private void CreateDigCells()
        {
            // Generator dig cells (taught or regen-confirmed) are never protected, whatever box/wall protection covers them.
            var indexer = _indexer;
            _digCells = new Perception.DigCells(_rt.Memory, indexer == null ? null : indexer.IsConfirmedDigCell) { Dimension = () => client.World.Dimension };
            // A player's correction ("wrong blocks", "not that one") makes the cells they teach next REPLACE the gen's old dig keys.
            _corrections = new Perception.DigCellCorrections(_digCells, _rt.Memory, time) { Dimension = () => client.World.Dimension };
            _corrections.Changed += line => _log?.LogInformation("dig cells corrected: {Line}", line);
            _rt.Chat.Accepted += m => _corrections.OnChat(m.Player, m.Text);
        }

        private void WireDigCells()
        {
            _digCells.TaughtCell += (p, by) => _corrections.OnTaught(p, by);
            _digCells.FluidsAround = p => Perception.GenLayout.FluidsAround(client.World, p);
            // Stray "cells" off the gen's dig layer (the block over a cell, taught by a punch while a player mined) go, logged.
            try
            {
                foreach (var (cell, why) in _digCells.PurgeOffLayer((p, why) => _corrections.RetractBad(p, why)))
                    _log?.LogInformation("dig cell {Cell} purged: {Why}", cell, why);
            }
            catch (Exception ex) { _log?.LogDebug("dig cell purge skipped: {Err}", ex.Message); }
            // The world check needs the gen's chunks: every minute for the first 15, once they're loaded.
            if (persist) _ = PurgeNonCellsAsync(client, _digCells, _corrections, Clock, _log);
        }

        /// <summary>The protection policy (ARCHITECTURE.md "Protection policy"): every source in one place, soft vs hard, overrides.</summary>
        private void WireProtection()
        {
            _placedStore = new PlacedBlockStore(_rt.Db, client.World.Placed, _log); // rule 1: own/hive placements, shared + persistent
            if (persist) _placedStore.Start();
            _marks = new ProtectionMarks(_rt.Db, time); // rule 3/4: trusted permits, never-break marks (shared db)
            // Gen regen cells take a never-break mark only on that exact cell; vague old ones on confirmed regen cells go (one-time, idempotent).
            _marks.IsGenCell = _digCells.IsDigCell;
            _corrections.Marks = _marks;
            _corrections.UnmarkConfirmedGenCells(_rt.Observations, _rt.Options.Self.TrustedPlayers.FirstOrDefault() ?? "a trusted player");
            _telemetry = new ProtectionTelemetry(_log, persist ? agentOptions.DataDirectory : null, time); // rule 6
            SetProtectionSources();
            _rt.Chat.Accepted += m => _protection.Edits.NoteAsk(m.Player);
            client.IsProtected = _protection.IsProtected;
            if (client is MinecraftGameClient mgx) WireClientProtection(mgx.Inner);
            ApplySpawnProtectionAtStartup(_rt, _log, botOptions.Protection, client.Username);
            client.ReservedItems = _protection.ReservedItems();
        }

        private void SetProtectionSources()
        {
            var indexer = _indexer;
            var protection = _protection;
            protection.IndexerKind = indexer == null ? null : indexer.ProtectionKind;
            protection.BlockMeta = p => client.World.GetMeta(p); // crop growth stage: ripe crops are harvestable anywhere
            protection.IsDigCell = _digCells.IsDigCell;
            protection.Claims = claims; // hive: a block another bot holds a lease on is soft-protected while it works there
            protection.Placed = client.World.Placed;
            protection.Marks = _marks;
            protection.Dimension = () => client.World.Dimension;
            protection.Telemetry = _telemetry;
            // The editing context: a saved design (or the bot's own mark) yields to explicitly targeted work a trusted player asked
            // for (live 10-30: Fox's captured spiral staircase refused every next column he asked main to dig).
            protection.Edits = new EditContext(time)
            {
                LeaseMinutes = botOptions.Protection.PermitMinutes,
                Trusted = () => _rt.Options.Self.TrustedPlayers,
                IsBot = n => n.Equals(client.Username, StringComparison.OrdinalIgnoreCase) || n.Equals(_rt.Chat.Username, StringComparison.OrdinalIgnoreCase)
                             || InHive(n) || _rt.Chat.IsHiveBot?.Invoke(n) == true,
            };
        }

        private void WireClientProtection(MineClaw.Client.MinecraftClient inner)
        {
            inner.ExplainProtection = _protection.Explain;
            inner.SoftOverride = _protection.OverridesSoft;
            inner.EditYields = _protection.EditYields;
            inner.CountRefusal = r => _telemetry.Count(r);
            inner.NeverBreak = p => _marks.HasNever && _marks.NeverAt(p, client.World.Dimension) != null;
            // Autonomous mine: taught / regen-confirmed generator cells are resource sources too.
            inner.MineSource = (p, _) => _digCells.IsDigCell(p) || _indexer?.IsConfirmedDigCell(p) == true;
        }

        private void WireChatPerception()
        {
            var rt = _rt;
            _landmark = (x, y, z) => Agent.Memory.Landmarks.Near(rt.Memory, rt.Observations, x, y, z);
            var landmark = _landmark;
            // Relational block names for questions/needs_input ("the west cell of the gen, right next to the lava").
            Func<int, int, int, string?> blockLandmark = (x, y, z) => Perception.BlockDescriber.Landmark(client.World, new MineClaw.World.BlockPos(x, y, z),
                (a, b, c2) => landmark(a, b, c2));
            rt.Chat.Landmark = blockLandmark;
            // Addressing classifier: only while the bot can actually see the speaker nearby.
            rt.Chat.IsNear = name => client.IsPlaying && client.World.Entities.FindPlayer(name) is { } pe &&
                pe.Position.DistanceTo(client.World.Player.Position) <= rt.Options.Chat.Classifier.NearBlocks;
            // Addressee by gaze/proximity: whom the speaker looks at or stands right next to (us = our username).
            rt.Chat.LookingAt = LookingAt;
            if (rt.Worker != null) rt.Worker.Landmark = blockLandmark;
        }

        private string? LookingAt(string name)
        {
            if (!client.IsPlaying || client.World.Entities.FindPlayer(name) is not { } sp) return null;
            var others = client.World.Entities.All()
                .Where(e => e.Kind == MineClaw.World.EntityKind.Player && e.Name is { Length: > 0 } n && !n.Equals(name, StringComparison.OrdinalIgnoreCase)
                            && !n.Equals(client.Username, StringComparison.OrdinalIgnoreCase))
                .Select(e => (e.Name!, e.Position))
                .Append((client.Username, client.World.Player.Position));
            return MineClaw.Bot.Brainstem.GazeAddress.Target(new MineClaw.Bot.Brainstem.HandoverWatch.PlayerView(name, sp.Position, sp.Yaw, sp.Pitch), others);
        }

        /// <summary>All dig cells of the generator around a block: geometry + the indexer + taught cells (claim-aware harvest picks a free one).</summary>
        private IReadOnlyList<MineClaw.World.BlockPos> GenCellsNear(MineClaw.World.BlockPos p) =>
            Perception.BlockDescriber.GenCellsNear(client.World, p, 4)
                .Concat(_indexer?.GeneratorCellsNear(p, 4) ?? [])
                .Concat(_digCells.Taught().Where(t => Math.Abs(t.X - p.X) <= 4 && Math.Abs(t.Y - p.Y) <= 2 && Math.Abs(t.Z - p.Z) <= 4))
                .Distinct().ToList();

        private void RegisterGameTools()
        {
            var rt = _rt;
            _genCells = GenCellsNear;
            // The main body's acquisition sources (attached to its plain adapter; the router finds them for the body in scope).
            _acquisition = AttachAcquisition(client, rt, _genCells, _digCells, _indexer, _log);
            // A never-touch mark on a chest keeps every obtain/craft withdrawal out of it (clone bodies read this through).
            _acquisition.SkipChest = p => _marks.HasNever && _marks.NeverAt(p, client.World.Dimension) != null ? "marked never-touch" : null;
            GameToolSet.RegisterAll(rt.Tools, _toolClient, _memory, botOptions, landmark: _landmark, generatorCells: _genCells,
                generator: new GameTools.GeneratorSupport(rt.Observations, (label, p) => rt.Memory.SetWaypoint(label, p.X, p.Y, p.Z, "storage chest placed by the generator loop"),
                    BadCell: (p, why) => _corrections.RetractBad(p, why),
                    Diagnose: cell => Perception.GenLayout.Diagnose(client.World, rt.Memory, client.World.Dimension, cell),
                    TaughtStand: cell => GameTools.GeneratorStand.Find(rt.Memory, cell))); // B12: stand where the demo stood
            HumanGameTools.RegisterAskStand(rt.Tools, rt.Chat, _adapter, botOptions.AskStandTimeoutSeconds, digCells: _digCells);
            HumanGameTools.RegisterWatchDemo(rt.Tools, rt.Chat, _recorder, _memory);
        }

        /// <summary>Gesture perception: a punch while looking at a block is pointing (outside demos too); a punched gen cell is taught.</summary>
        private void CreateGestures()
        {
            var landmark = _landmark;
            _gestures = new Perception.GestureTracker(client, isBot: n => InHive(n)
                    || n.Equals(_rt.Chat.Username, StringComparison.OrdinalIgnoreCase), time,
                describe: (w, p, _) => Perception.BlockDescriber.Describe(w, p, (a, b, c2) => landmark(a, b, c2)));
            var punched = new Dictionary<string, List<(MineClaw.World.BlockPos Pos, DateTimeOffset At)>>(StringComparer.OrdinalIgnoreCase);
            _gestures.Pointed += g => OnPointed(g, punched);
        }

        private void OnPointed(Perception.Gesture g, Dictionary<string, List<(MineClaw.World.BlockPos Pos, DateTimeOffset At)>> punched)
        {
            if (!IsTeachablePunch(g)) return;
            bool added = _digCells.Register(g.Pos, g.Player);
            if (added) _log?.LogInformation("dig cell taught by {Player}'s punch: {Pos}", g.Player, g.Pos);
            else _log?.LogInformation("dig cell confirmed by {Player}'s punch (already known): {Pos}", g.Player, g.Pos);
            var now = Clock.GetUtcNow();
            List<MineClaw.World.BlockPos> recent;
            lock (punched)
            {
                if (!punched.TryGetValue(g.Player, out var list)) punched[g.Player] = list = new();
                list.RemoveAll(x => now - x.At > TimeSpan.FromSeconds(90) || x.Pos == g.Pos);
                list.Add((g.Pos, now));
                recent = list.Select(x => x.Pos).ToList();
            }
            // The brain's confirmation counts every punched cell, known ones included ("got 3 of 3").
            _rt.Events.Post(Agent.Events.GameEvent.Notice(
                $"{g.Player} punched gen cell {g.Pos.X},{g.Pos.Y},{g.Pos.Z}{(added ? " (new)" : " (already known)")}; " +
                $"{recent.Count} gen cell(s) punched in the last 90s: {string.Join(" ", recent.Select(p => $"{p.X},{p.Y},{p.Z}"))}",
                Agent.Events.EventPriority.Background));
        }

        /// <summary>A punch that can teach a gen cell; the rejected ones are logged (live 10-27 21:46: Fox punched 3 cells, the log
        /// showed 2 — the third was already known, so nothing was logged).</summary>
        private bool IsTeachablePunch(Perception.Gesture g)
        {
            if (Perception.BlockDescriber.GenCellRejection(client.World, g.Pos) is { } why)
            {
                bool nearGen = _genCells(g.Pos).Any(c => Math.Abs(c.X - g.Pos.X) <= 3 && Math.Abs(c.Y - g.Pos.Y) <= 1 && Math.Abs(c.Z - g.Pos.Z) <= 3);
                if (nearGen) _log?.LogInformation("punch by {Player} at {Pos} next to the gen isn't a dig cell: {Why}", g.Player, g.Pos, why);
                return false;
            }
            // Players mining their own gen punch the cobble that forms over a cell too: off the known dig layer = not a cell.
            if (_digCells.OffLayer(g.Pos) is { } off)
            {
                _log?.LogInformation("punch by {Player} at {Pos} isn't a dig cell: {Why}", g.Player, g.Pos, off);
                return false;
            }
            return true;
        }

        private void WireChatProtection()
        {
            var rt = _rt;
            var gestures = _gestures;
            // The speaker's facing rides along for a while after they talk to me ("that way" = where they face).
            rt.Chat.Accepted += m => { if (m.Player is { Length: > 0 } p) gestures.NoteSpoke(p, m.Text); };
            _protection.PointedByTrusted = PointedByTrustedCheck();
            _protectCmds = new ProtectionCommands(_marks, _protection, client.World, botOptions.Protection, time)
            {
                Trusted = () => rt.Options.Self.TrustedPlayers,
                PointedBy = name => gestures.Recent().LastOrDefault(g => g.Player.Equals(name, StringComparison.OrdinalIgnoreCase)) is { } g ? (g.Pos, g.BlockId) : null,
                NamedArea = name => NamedStructure(rt, client, name),
                GenCells = () => _acquisition.GeneratorCells?.Invoke() ?? [],
                Say = line => _ = rt.Chat.SayAsync(line),
                Notice = line => rt.Events.Post(Agent.Events.GameEvent.Notice(line, Agent.Events.EventPriority.Low)),
                GenCellAllowed = (cell, by, middle) => _corrections.UnmarkGenCell(cell, by, middle),
            };
            rt.Chat.Accepted += m =>
            {
                try { if (_protectCmds.OnChat(m.Player, m.Text) is { } did) _log?.LogInformation("protection: {Line}", did); }
                catch (Exception ex) { _log?.LogWarning(ex, "protection command failed"); }
            };
            rt.Agent.TurnPerception = gestures.Line;
            // Idle check-ins: hourly at most while no human is online (the tab list, hive bots and this bot excluded).
            rt.Agent.HumansOnline = () => client.World.Entities.OnlinePlayers.Any(n => !n.Equals(client.Username, StringComparison.OrdinalIgnoreCase)
                && !InHive(n));
            if (rt.Agent.TurnDigest is { } digest)
                rt.Agent.TurnDigest = since =>
                {
                    var d = digest(since);
                    var older = gestures.Older(since);
                    return older.Length == 0 ? d : string.IsNullOrWhiteSpace(d) ? older : d + "; " + older;
                };
        }

        /// <summary>Protection from chat: trusted "break that" (+ punch) / "you can break it" permits, anyone's "never break X" (zero
        /// tokens). The pathfinder asks per cell: the trusted punches of the last 3 min are re-read at most twice a second.</summary>
        private Func<MineClaw.World.BlockPos, bool> PointedByTrustedCheck()
        {
            (DateTimeOffset At, List<MineClaw.World.BlockPos> Cols) pointedCache = (DateTimeOffset.MinValue, []);
            return p =>
            {
                var now = Clock.GetUtcNow();
                var c = pointedCache;
                if (now - c.At > TimeSpan.FromMilliseconds(500)) c = pointedCache = (now, TrustedPointsSince(now));
                foreach (var g in c.Cols) if (g.X == p.X && g.Z == p.Z && Math.Abs(g.Y - p.Y) <= 2) return true;
                return false;
            };
        }

        private List<MineClaw.World.BlockPos> TrustedPointsSince(DateTimeOffset now)
        {
            var trusted = _rt.Options?.Self?.TrustedPlayers ?? [];
            return trusted.Length == 0 ? [] : (_gestures.Recent() ?? []).Where(g => g.At >= now - TimeSpan.FromMinutes(3)
                && trusted.Any(t => t.Equals(g.Player, StringComparison.OrdinalIgnoreCase))).Select(g => g.Pos).ToList();
        }

        private void WireFightAndRegion()
        {
            var rt = _rt;
            if (client is MinecraftGameClient mg)
            {
                // The fight tools act on the body in scope; the main body's fight reports are wired here (per body in step 2).
                var adapter = _adapter;
                CombatTools.Register(rt.Tools, () => GameClients.Minecraft(adapter)?.Inner ?? mg.Inner);
                CombatTools.AttachEvents(mg.Inner, rt.Events);
                TreeTools.AttachReports(mg.Inner, _indexer, rt.Events);
            }
            BodyLanguageTools.Register(rt.Tools, _adapter); // act {move, target?, times?}: brain-only body language
            if (client is MinecraftGameClient mgp) WireRegion(mgp.Inner);
        }

        private void WireRegion(MineClaw.Client.MinecraftClient inner)
        {
            inner.SpawnProtectionDisabled = botOptions.Protection.SpawnProtectionOff;
            WireProtectedRegion(inner, _rt, _log, claims, hiveBots);
            // Hive: the protected_region fact is shared (one db); a success in ANY process retracts it, so every process
            // drops its in-memory copy, and a region learned elsewhere is adopted (it already needed two undone edits).
            if (persist && (claims != null || hiveBots != null))
                _protectionSync = new System.Threading.Timer(_ => SyncProtectedRegion(inner, _rt, _log), null, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10));
        }

        private void WireSkillsAndLedger()
        {
            var rt = _rt;
            Beliefs.BeliefBot.Attach(rt, _toolClient, claims, time, _log, _indexer); // belief system: claim checks when idle (zero tokens)
            _skillHub = SkillTools.Register(rt, _adapter, claims); // worker: skill + find_skill over the whole catalog (zero prefix growth)
            // Remembered digs (stair patterns, mine rooms, strip mines): shared db, their air kept clear (never bridged, boxed or sealed).
            var digSites = new Excavation.DigSiteStore(rt.Db, _log);
            _skillHub.Registry.Sites = digSites;
            try { MineClaw.Skills.DigSites.Apply(digSites, client.World.KeepAir); }
            catch (Exception ex) { _log?.LogDebug("dig sites not loaded: {Err}", ex.Message); }
            if (client is MinecraftGameClient mgl) AttachLedger(mgl.Inner);
            IntentionalReset.Attach(rt, client, _log); // into_void: announce once, intentional-reset outcome, the void-reset claim's evidence
            // Shared chest memory vs what an open shows: "bread x5 gone since maid_chan opened it 1m ago; Fox may have taken it".
            if (_indexer is { } indexer)
                WorldIndex.ChestChanges.Attach(rt, client, indexer,
                    n => n.Equals(client.Username, StringComparison.OrdinalIgnoreCase) || InHive(n)
                         || rt.Chat.IsHiveBot?.Invoke(n) == true, time, _log,
                    current: () => BodyScope.Current is Body { Indexer: { } own } ? own : indexer); // a clone's withdraw: its own eyes
        }

        /// <summary>Inventory ledger: every toss/drop/use/consume/correction is recorded; inventory/where_is {item} answer "where did
        /// X go". The main body's ledger (a ledger per body comes with in-process clone bodies, step 2).</summary>
        private void AttachLedger(MineClaw.Client.MinecraftClient inner)
        {
            var ledger = Ledger.InventoryLedgerTools.Attach(_rt, inner, _skillHub.Registry, _log, time);
            _main.Ledger = ledger; // a clone body's own ledger: BodyFactory.CreateClone
            // A worker's time slice reports what it gathered and where it went ("ok: time slice done (5 min): 32 cobblestone; ...").
            if (_rt.Worker != null)
                _rt.Worker.GainsTracker = () =>
                {
                    var t0 = Clock.GetUtcNow();
                    return () => ledger.GainsSince(t0);
                };
        }

        /// <summary>Build from a description: design (brain, Opus authoring) + hidden build/blueprint tools (Lua build{}/blueprint()),
        /// chat review. The builder resolves its body per call through the routers (never a captured client).</summary>
        private void AttachBlueprints()
        {
            if (!botOptions.Blueprints.Enabled) return;
            _blueprints = MineClaw.Bot.Blueprints.BlueprintWiring.Attach(_rt, _adapter, _toolClient, botOptions.Blueprints, _protection, _acquisition, _genCells, claims, time, _log, persist);
            _skillHub.Registry.Footprints ??= _blueprints.Service.Footprints; // expand_island area: does the floor cover the design?
            _skillHub.Registry.Design ??= _blueprints.Service.RegionOf; // light_area/spawn_map design= and its deck
        }

        private BrainContext Context()
        {
            var rt = _rt;
            Func<string, bool> isBot = n => InHive(n) || rt.Chat.IsHiveBot?.Invoke(n) == true;
            return new BrainContext
            {
                Runtime = rt, AgentOptions = agentOptions, BotOptions = botOptions,
                Bodies = _bodies, Tools = _toolClient, Adapter = _adapter,
                Protection = _protection, Memory = _memory, Recorder = _recorder, DigCells = _digCells, Gestures = _gestures,
                Acquisition = _acquisition, Skills = _skillHub, Landmark = _landmark, GenCells = _genCells, IsBot = isBot,
                Indexer = _indexer, Activity = _activityTracker, WorldMemory = _worldMemory, Mechanisms = _mechanisms, Blueprints = _blueprints,
                PlacedBlocks = _placedStore, ProtectionMarks = _marks, ProtectionCommands = _protectCmds, ProtectionTelemetry = _telemetry,
                ProtectionSync = _protectionSync, Claims = claims, HiveBots = hiveBots,
                Loggers = loggers, Log = _log, Time = time, Persist = persist,
            };
        }
    }
}
