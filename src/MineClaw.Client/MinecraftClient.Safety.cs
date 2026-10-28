using System.Collections.Concurrent;
using MineClaw.Data;
using MineClaw.Protocol.Packets;
using MineClaw.World;

namespace MineClaw.Client;

/// <summary>
/// Always-on safety below every action (tick thread): <see cref="EdgeGuard"/> input filtering, server velocity
/// (knockback) applied like vanilla, last-chance clutch placement, hostile-near-edge retreat, and the post-teleport
/// freeze. None of this depends on which action (if any) is running.
/// </summary>
public sealed partial class MinecraftClient
{
    private EdgeGuard? _guardInstance;
    private EdgeGuard Guard => _guardInstance ??= new EdgeGuard(Options.EdgeGuard);

    /// <summary>0x1C for our own entity, handed from the reader thread to the tick thread.</summary>
    private readonly ConcurrentQueue<Vec3d> _selfVelocity = new();

    // Tick-thread-only safety state.
    private volatile bool _safetyFreeze;
    private int _freezeTicks;
    private bool _freezeAlerted;
    private GuardActions _lastGuardActions;
    private long _lastGuardLogTick = -100;
    private bool _wasEdgeSneaking;
    private bool _voidEpisode;
    private int _clutchPlaced;
    private bool _clutchReported;
    private long _lastClutchTick = -100;
    private BlockPos? _retreatTarget;
    private long _retreatUntil;
    private long _knockbacks;

    /// <summary>Counters of every safety intervention since the client was created.</summary>
    public EdgeGuardStats SafetyStats => Guard.Stats;

    /// <summary>Server velocity packets (knockback) applied to the bot.</summary>
    public long KnockbacksReceived => Interlocked.Read(ref _knockbacks);

    /// <summary>True while movement is frozen after a teleport/respawn waiting for verified ground.</summary>
    public bool IsFrozenForSafety => _safetyFreeze;

    /// <summary>The server's last 0x0D, not confirmed yet because the ground under it isn't verified (tick thread).</summary>
    private Protocol.Packets.PlayerPositionLookPacket? _pendingEcho;
    private Vec3d? _voidHoldAt;

    /// <summary>
    /// Where the bot is holding still with loaded chunks and only void below (a spawn/teleport into the void: live 10-27 07:49,
    /// fresh clone maid_chan_420 spawned at -5.5,0.62,-8.5), null otherwise. It stays there until verified ground is under it
    /// (a block placed below) or the server moves it (a /tp), then <see cref="VoidHoldEnded"/>.
    /// </summary>
    public Vec3d? HoldingOverVoidAt => _voidHoldAt;

    /// <summary>Raised once per hold when the bot is stuck still over the void (loaded area, nothing below).</summary>
    public event Action<Vec3d>? HoldingOverVoid;
    /// <summary>Raised when a void hold ends on verified ground (after a /tp or a block below): the bot may move again.</summary>
    public event Action<Vec3d>? VoidHoldEnded;

    /// <summary>
    /// Whether to hold back the C→S confirmation of a server 0x0D. b1.8.1 (mc-dev 218a2a71) NetLoginHandler.java:104 and every
    /// teleport go through NetServerHandler.a(x,y,z,yaw,pitch) (NetServerHandler.java:237), which sets checkMovement = false;
    /// only a C→S position with exactly that X and Z and |Y−n| &lt; 0.1 sets it true again (NetServerHandler.java:66-71; a bare
    /// 0x0A carries x = z = 0, Packet10Flying.java:8). With checkMovement false the whole movement handler is skipped,
    /// including the floating counter (NetServerHandler.java:216-226: no block within the box grown 0.0625 and lowered 0.55
    /// → ++g each flying packet — d6 is always zeroed at :194, so even falling counts — and g &gt; 80 kicks "Flying is not enabled
    /// on this server" unless allow-flight). So the safest legal hold over the void is: never confirm, send only 0x0A, stay put.
    /// The server keeps us at its own m,n,o; a /tp is a new 0x0D. Ground verified → the held-back echo goes out.
    /// </summary>
    /// <remarks>
    /// Only over a LOADED void. The real server streams chunks only after the confirmation (EntityPlayerMP.a(true) runs from
    /// NetServerHandler.a(Packet10Flying) only once checkMovement is set), so withholding it while the chunks are still missing
    /// deadlocks the login: live 10-27 22:33, main and 420 both stuck at "chunks … not loaded yet; holding still".
    /// </remarks>
    private bool DeferEcho(Vec3d at) => Options.FreezeUntilSupportVerified && AreaLoaded(at) && World.IsPlayerOverVoid(at);

    /// <summary>
    /// Hovering over a loaded void after the confirmation already went out (a login: chunks only arrive once confirmed): send no
    /// flying packets at all, so the server's floating counter (NetServerHandler.java:216-226) never advances. Keepalives keep
    /// the connection; a /tp is a new 0x0D.
    /// </summary>
    private bool SilentOverVoid(Vec3d at) => Options.FreezeUntilSupportVerified && AreaLoaded(at) && World.IsPlayerOverVoid(at);

    /// <summary>Actions that work in place while holding over the void (no walking, no block in reach needed).</summary>
    private static readonly HashSet<string> VoidHoldActions = new(StringComparer.Ordinal)
    {
        "eat", "use", "release_use", "look", "spin", "wave", "crouch", "punch", "click", "close", "resync", "transfer", "craft",
        "place", "shoot", "use_entity", "dismount",
    };

    /// <summary>
    /// Holding over the void: walking/pathing/reaching jobs fail at once with the reason (live 10-27 21:30: 420 at -6,0,-9 tried
    /// a walk back to the island, "pathing back failed", from y 0). Null = the action may run.
    /// </summary>
    private string? VoidHoldBlocks(string action) =>
        _voidHoldAt is { } v && !VoidHoldActions.Contains(action)
            ? $"can't {action}: holding still over the void at {Fmt(v.ToBlockPos())} (nothing to stand on or reach; any move falls). " +
              "Wait for a /tp: ask a trusted player once"
            : null;

    private void Debug(string msg) => Options.DebugLog?.Invoke(msg);

    private void ResetSafety()
    {
        while (_selfVelocity.TryDequeue(out _)) { }
        _safetyFreeze = false;
        _freezeTicks = 0;
        _freezeAlerted = false;
        _voidEpisode = false;
        _clutchPlaced = 0;
        _clutchReported = false;
        _retreatTarget = null;
        _wasEdgeSneaking = false;
        _guardInstance?.Reset();
        _pendingEcho = null;
        _voidHoldAt = null;
    }

    /// <summary>Called for every server 0x0D (login, correction, respawn): hold still until the ground is verified.</summary>
    private void OnServerTeleportForSafety()
    {
        while (_selfVelocity.TryDequeue(out _)) { } // vanilla setPositionAndRotation zeroes motion
        Guard.Reset();
        _voidEpisode = false;
        _clutchPlaced = 0;
        _clutchReported = false;
        _retreatTarget = null;
        _flight = null;
        if (Options.FreezeUntilSupportVerified)
        {
            _safetyFreeze = true;
            _freezeTicks = 0;
            _freezeAlerted = false;
        }
    }

    /// <summary>Vanilla Entity.setVelocity: the packet replaces the motion (it is not added).</summary>
    private void ApplyServerVelocity(PhysicsState phys)
    {
        Vec3d? last = null;
        while (_selfVelocity.TryDequeue(out var v)) last = v;
        if (last is not { } vel) return;
        phys.Velocity = vel;
        Interlocked.Increment(ref _knockbacks);
        bool near = Guard.IsNearEdge(World, phys);
        _combatInstance?.OnKnockback(vel, near);
        Debug($"safety: server velocity {vel.X:0.###},{vel.Y:0.###},{vel.Z:0.###} at {Fmt(phys.Position)}{(near ? " NEAR EDGE" : "")}");
    }

    /// <summary>
    /// True while frozen after a teleport: the columns under and around the hitbox must be loaded and something solid
    /// must be below it. Over the void we stay frozen, the teleport unconfirmed, so the server never counts us as floating
    /// (<see cref="DeferEcho"/>) and we never fall.
    /// </summary>
    private bool HoldForSafety(PhysicsState phys)
    {
        if (!_safetyFreeze) return false;
        if (SupportVerified(phys.Position))
        {
            _safetyFreeze = false;
            if (_freezeTicks > 0) Log($"safety: ground verified under {Fmt(phys.Position)} after {_freezeTicks} ticks; moving again");
            if (_freezeAlerted)
                RaiseDanger(DangerKind.Recovered, $"ground loaded under me at {Fmt(phys.Position)}; moving again", abort: false);
            if (_voidHoldAt is not null)
            {
                _voidHoldAt = null;
                var at = phys.Position;
                Raise(() => VoidHoldEnded?.Invoke(at));
            }
            return false;
        }
        _freezeTicks++;
        bool loaded = AreaLoaded(phys.Position);
        // Void under a loaded area: tell the Agent after 2 s. Chunks still streaming in: only after 10 s.
        if (!_freezeAlerted && _freezeTicks >= (loaded ? 40 : 200))
        {
            _freezeAlerted = true;
            var msg = loaded
                ? $"teleported to {Fmt(phys.Position)} with only void below; holding still until someone /tp's me or puts a block under me (walking is impossible)"
                : $"chunks under {Fmt(phys.Position)} not loaded yet; holding still";
            Log("safety: " + msg);
            RaiseDanger(DangerKind.UnsafePosition, msg, abort: true, critical: loaded);
        }
        // Loaded and still nothing below for 2 s (the chunks may have come in after a "not loaded yet"): a void hold, once.
        if (loaded && _voidHoldAt is null && _freezeTicks >= 40)
        {
            var at = phys.Position;
            _voidHoldAt = at;
            Raise(() => HoldingOverVoid?.Invoke(at));
        }
        return true;
    }

    private bool AreaLoaded(Vec3d feet)
    {
        var bb = AABB.ForPlayer(feet).Grow(1, 0, 1);
        for (int x = (int)Math.Floor(bb.MinX); x <= (int)Math.Floor(bb.MaxX); x++)
        for (int z = (int)Math.Floor(bb.MinZ); z <= (int)Math.Floor(bb.MaxZ); z++)
            if (!World.IsLoaded(x, z)) return false;
        return true;
    }

    private bool SupportVerified(Vec3d feet) => AreaLoaded(feet) && !World.IsPlayerOverVoid(feet);

    /// <summary>Hostile mob close while the bot is near an edge: walk toward the island interior.</summary>
    private MoveInput RetreatOverride(PhysicsState phys, MoveInput input)
    {
        if (Options.EdgeRetreatHostileRadius <= 0 || !phys.OnGround) { _retreatTarget = null; return input; }
        if (_retreatTarget is null && TickCount % 5 == 0)
        {
            bool hostile = World.Entities.Nearby(phys.Position, Options.EdgeRetreatHostileRadius, e => e.IsHostileMob).Count > 0;
            if (hostile && Guard.IsNearEdge(World, phys) && Guard.FindInterior(World, phys) is { } cell)
            {
                _retreatTarget = cell;
                _retreatUntil = TickCount + 60;
                Log($"safety: hostile mob close while near the edge; stepping inward to {Fmt(cell)}");
            }
        }
        if (_retreatTarget is not { } t) return input;
        var c = new Vec3d(t.X + 0.5, phys.Position.Y, t.Z + 0.5);
        double dx = c.X - phys.Position.X, dz = c.Z - phys.Position.Z;
        if (TickCount > _retreatUntil || dx * dx + dz * dz < 0.3 * 0.3)
        {
            _retreatTarget = null;
            return input;
        }
        var yaw = PlayerPhysics.YawPitchTo(phys.Position, c).Yaw;
        return new MoveInput(Forward: 1, Yaw: yaw, Pitch: input.Pitch, Sneak: true);
    }

    /// <summary>The EdgeGuard pass: runs every tick right before physics, for drivers and idle alike.</summary>
    private GuardDecision SafetyFilter(PhysicsState phys, MoveInput input, bool allowDrop, ParkourCommit? parkour = null)
    {
        var d = Guard.Filter(World, phys, input, allowDrop, parkour);
        if ((d.Actions & GuardActions.ParkourRejected) != 0 && (_lastGuardActions & GuardActions.ParkourRejected) == 0)
            Log($"safety: gap jump to {Fmt(parkour!.Value.Plan.To)} no longer lands safely at {Fmt(phys.Position)} (tick {parkour.Value.Tick}); guard takes over");
        bool edgeSneak = (d.Actions & GuardActions.ForcedSneak) != 0;
        if (edgeSneak != _wasEdgeSneaking)
        {
            _wasEdgeSneaking = edgeSneak;
            Debug(edgeSneak ? $"safety: near void edge at {Fmt(phys.Position)}, sneaking" : "safety: away from the edge");
        }
        if (d.Notable && (d.Actions != _lastGuardActions || TickCount - _lastGuardLogTick >= 20))
        {
            _lastGuardLogTick = TickCount;
            Debug($"safety: {d.Actions} at {Fmt(phys.Position)} (asked {Describe(input)}, applied {Describe(d.Input)}) [{Guard.Stats}]");
        }
        _lastGuardActions = d.Actions;
        if (d.Unrecoverable && !_voidEpisode)
        {
            _voidEpisode = true;
            Log($"safety: no input avoids {(World.IsPlayerOverVoid(phys.Position) ? "the void" : "a high fall")} at {Fmt(phys.Position)} (velocity {phys.Velocity.X:0.##},{phys.Velocity.Y:0.##},{phys.Velocity.Z:0.##})");
        }
        return d;
    }

    private static string Describe(MoveInput i) =>
        $"f{i.Forward:0.#} s{i.Strafe:0.#}{(i.Jump ? " jump" : "")}{(i.Sneak ? " sneak" : "")}{(i.Sprint ? " sprint" : "")} yaw {i.Yaw:0}";

    /// <summary>After physics + the movement packet: clutch if doomed, and close finished void episodes.</summary>
    private void AfterTickSafety(Session s, PhysicsState phys, GuardDecision d)
    {
        if (d.Unrecoverable)
        {
            if (Options.ClutchEnabled) TryClutch(s, phys);
            else ReportFall(phys, "clutching is disabled");
        }

        if (_voidEpisode && (phys.OnGround || phys.OnLadder || phys.InWater) && !World.IsPlayerOverVoid(phys.Position))
        {
            _voidEpisode = false;
            _voidAlerted = false;
            if (_clutchPlaced > 0 || _clutchReported)
            {
                var msg = $"recovered: landed at {Fmt(phys.Position)}" + (_clutchPlaced > 0 ? $" after placing {_clutchPlaced} clutch block(s)" : "");
                Log("safety: " + msg);
                RaiseDanger(DangerKind.Recovered, msg, abort: false);
            }
            _clutchPlaced = 0;
            _clutchReported = false;
        }
    }

    /// <summary>
    /// Last chance: place any block from the inventory so the fall ends on it — against the edge block just left, or
    /// any solid face in reach. Hotbar blocks are selected instantly; a block in the main inventory is swapped in with
    /// window clicks sent without waiting. The placement is predicted locally like the vanilla client does.
    /// </summary>
    private void TryClutch(Session s, PhysicsState phys)
    {
        if (_clutchPlaced >= 6 || TickCount == _lastClutchTick) return;
        _lastClutchTick = TickCount;
        bool voidBelow = VoidAhead(phys); // before the clutch block changes what's below
        int hb = PrepareClutchBlock(s, out int blockId);
        if (hb < 0)
        {
            ReportFall(phys, "no placeable block in my inventory to catch myself");
            return;
        }
        var plan = Guard.PlanClutch(World, phys, Options.Reach, blockId);
        if (plan is not { } p)
        {
            ReportFall(phys, "no solid face within reach to place a block against");
            return;
        }
        if (World.Player.HeldSlot != hb || _serverHeldSlot != hb)
        {
            World.Player.HeldSlot = hb;
            _serverHeldSlot = hb;
            Send(s, new HoldingChangePacket { Slot = (short)hb });
        }
        var a = p.Aim;
        Send(s, new PlayerBlockPlacementPacket
        {
            X = a.Against.X, Y = a.Against.Y, Z = a.Against.Z, Direction = (byte)a.Face, HeldItem = World.Inventory.HeldItem(hb),
        });
        Send(s, new AnimationPacket { EntityId = World.Player.EntityId, Animation = 1 });
        World.SetBlock(p.Target.X, p.Target.Y, p.Target.Z, blockId, 0); // client-side prediction (vanilla ItemBlock.onItemUse)
        Guard.NoteClutchPlaced(p.Target);
        Guard.Stats.CountClutch(p.Saves);
        _clutchPlaced++;
        Log($"safety: clutch {BlockName(blockId, 0)} at {Fmt(p.Target)} against {Fmt(a.Against)} ({(p.Saves ? $"predicted landing, fall {p.PredictedFall:0.#}" : "chaining, landing not yet assured")})");
        if (!_clutchReported)
        {
            _clutchReported = true;
            _voidAlerted = true;
            var msg = (voidBelow ? $"knocked off toward the void at {Fmt(phys.Position)}" : $"falling {FallText(phys)} at {Fmt(phys.Position)}")
                      + $"; placed {BlockName(blockId, 0)} at {Fmt(p.Target)} to catch myself"
                      + (p.Saves ? " (should land safely)" : " (not sure it is enough)");
            RaiseDanger(voidBelow ? DangerKind.FallingIntoVoid : DangerKind.HighFall, msg, abort: false, critical: true);
            AbortCurrentAction("falling into the void");
        }
    }

    private void ReportFall(PhysicsState phys, string why)
    {
        if (_clutchReported) return;
        _clutchReported = true;
        _voidAlerted = true;
        // Classify honestly: "the void" only when nothing is below; a long drop onto ground is a high fall (damage, no item loss).
        bool voidBelow = VoidAhead(phys);
        var msg = (voidBelow ? $"falling into the void at {Fmt(phys.Position)}" : $"falling {FallText(phys)} at {Fmt(phys.Position)}")
                  + $": {why}" + (_clutchPlaced > 0 ? $" (placed {_clutchPlaced} block(s) already)" : "");
        Log("safety: " + msg);
        RaiseDanger(voidBelow ? DangerKind.FallingIntoVoid : DangerKind.HighFall, msg, abort: false, critical: true);
        AbortCurrentAction("falling into the void");
    }

    /// <summary>The void is under me or where I'm drifting (knockback carries me past an edge): ground right under a
    /// sideways fall isn't where it lands. A purely vertical drop over ground is a high fall.</summary>
    private bool VoidAhead(PhysicsState phys)
    {
        var v = phys.Velocity;
        return World.IsPlayerOverVoid(phys.Position)
               || World.IsPlayerOverVoid(phys.Position + new Vec3d(v.X * 10, 0, v.Z * 10))
               || World.IsPlayerOverVoid(phys.Position + new Vec3d(v.X * 4, 0, v.Z * 4));
    }

    /// <summary>"~6 blocks onto ground at y 65 (not the void)": the highest block under the hitbox.</summary>
    private string FallText(PhysicsState phys)
    {
        var box = phys.BoundingBox;
        int fy = (int)Math.Floor(phys.Position.Y - 0.01), ground = -1;
        for (int x = (int)Math.Floor(box.MinX); x <= (int)Math.Ceiling(box.MaxX) - 1; x++)
        for (int z = (int)Math.Floor(box.MinZ); z <= (int)Math.Ceiling(box.MaxZ) - 1; z++)
            ground = Math.Max(ground, Safety.SupportBelow(World, x, fy, z));
        return ground < 0 ? "a long way" : $"~{Math.Max(1, phys.Position.Y - (ground + 1)):0} blocks onto ground at y {ground + 1} (not the void)";
    }

    private static readonly int[] PreferredClutchBlocks =
        [Blocks.Cobblestone, Blocks.Dirt, Blocks.Netherrack, Blocks.Stone, Blocks.Planks, Blocks.Log];

    /// <summary>Full, solid, non-interactable, non-falling block that can be placed as itself.</summary>
    private static bool IsClutchBlock(int id)
    {
        if (id <= 0 || id >= 256) return false;
        var b = Blocks.Get(id);
        return b.Solid && b.CollisionHeight >= 1 && !b.Interactable && !b.Hazard && !b.Liquid
               && id is not (Blocks.Sand or Blocks.Gravel);
    }

    private int ClutchScore(ItemStack st)
    {
        if (st.IsEmpty || !IsClutchBlock(st.Id)) return -1;
        int pref = Array.IndexOf(PreferredClutchBlocks, st.Id);
        int score = pref >= 0 ? 100 - pref : 10;
        if (ReservedItems.Contains(st.Id)) score -= 50; // still better than the void
        return score;
    }

    /// <summary>Gets a clutch block into the hotbar right now. Returns the hotbar index or -1.</summary>
    private int PrepareClutchBlock(Session s, out int blockId)
    {
        blockId = 0;
        var inv = World.Inventory;
        int best = -1, bestScore = -1;
        for (int i = 0; i < 9; i++)
        {
            int sc = ClutchScore(inv.GetPlayerSlot(36 + i));
            if (sc > bestScore) { bestScore = sc; best = i; }
        }
        if (best >= 0)
        {
            blockId = inv.GetPlayerSlot(36 + best).Id;
            return best;
        }

        int src = -1;
        for (int slot = 9; slot < 36; slot++)
        {
            int sc = ClutchScore(inv.GetPlayerSlot(slot));
            if (sc > bestScore) { bestScore = sc; src = slot; }
        }
        if (src < 0) return -1;
        if (inv.OpenWindow is { } open)
        {
            Send(s, new CloseWindowPacket { WindowId = (sbyte)open.Id });
            inv.Close(open.Id);
        }
        if (!inv.Cursor.IsEmpty) return -1;
        int hb = ChooseHotbarTarget();
        blockId = inv.GetPlayerSlot(src).Id;
        // No time to wait for 0x6A: the server processes these clicks before the placement that follows.
        ClickNoWait(s, src);
        ClickNoWait(s, 36 + hb);
        if (!inv.Cursor.IsEmpty) ClickNoWait(s, src);
        Debug($"safety: moved {BlockName(blockId, 0)} from slot {src} to hotbar {hb} for a clutch");
        return inv.GetPlayerSlot(36 + hb).Id == blockId ? hb : -1;
    }

    private void ClickNoWait(Session s, int slot)
    {
        var r = World.Inventory.ApplyClick(slot, false, false);
        Send(s, new WindowClickPacket
        {
            WindowId = (sbyte)r.WindowId, Slot = (short)slot, RightClick = false, ActionNumber = r.ActionNumber, Shift = false,
            ClickedItem = r.ExpectedItem,
        });
    }

    // ------------------------------------------------------------------ action-level helpers

    /// <summary>Why <paramref name="goal"/> can't be stood at (every candidate cell is over the void), or null.</summary>
    private string? GoalOverVoid(Goal goal)
    {
        switch (goal)
        {
            case GoalBlock b when World.IsLoaded(b.Target.X, b.Target.Z) && World.IsOverVoid(b.Target.X, b.Target.Y, b.Target.Z):
                return $"refusing to go to {Fmt(b.Target)}: there is nothing under it but void";
            case GoalNear n when n.Range <= 6 && World.IsLoaded(n.Target.X, n.Target.Z):
            {
                int r = (int)Math.Ceiling(n.Range);
                for (int x = n.Target.X - r; x <= n.Target.X + r; x++)
                for (int y = n.Target.Y - r; y <= n.Target.Y + r; y++)
                for (int z = n.Target.Z - r; z <= n.Target.Z + r; z++)
                    if (n.IsReached(x, y, z) && !World.IsOverVoid(x, y, z)) return null;
                return $"refusing to go near {Fmt(n.Target)}: everything within {n.Range:0.#} is over the void";
            }
            default:
                return null;
        }
    }

    /// <summary>Lava touching <paramref name="p"/> on a side or from above would flow into the cell once it is dug.</summary>
    private bool LavaCouldFlowInto(BlockPos p)
    {
        foreach (var f in BlockFaceExtensions.All)
        {
            if (f == BlockFace.Bottom) continue;
            if (BlockShapes.IsLava(World.GetBlock(p.Offset(f)))) return true;
        }
        return false;
    }

    /// <summary>The bot's feet or head cell is within one block of <paramref name="p"/> (or anywhere below it in the
    /// 3x3 column), i.e. lava released at p could reach it.</summary>
    private bool LavaPathToMe(BlockPos p)
    {
        var feet = World.Player.Position;
        var bb = AABB.ForPlayer(feet);
        for (int x = (int)Math.Floor(bb.MinX); x <= (int)Math.Ceiling(bb.MaxX) - 1; x++)
        for (int z = (int)Math.Floor(bb.MinZ); z <= (int)Math.Ceiling(bb.MaxZ) - 1; z++)
        {
            if (Math.Abs(x - p.X) > 1 || Math.Abs(z - p.Z) > 1) continue;
            int fy = (int)Math.Floor(feet.Y + 1e-6);
            if (fy <= p.Y + 1 && fy + 1 >= p.Y - 6) return true;
        }
        return false;
    }

    /// <summary>A reach goal that prefers standing away from edges and lava: the strict version first, else plain.</summary>
    private async Task<ActionResult> GotoReachSafelyAsync(BlockPos pos, bool generator, CancellationToken ct)
    {
        var reach = new GoalWithinReach(pos, Options.Reach - 0.6);
        var o = GotoOptions.Default with { Timeout = TimeSpan.FromSeconds(30) };
        // Never a spot another body stands on (two bots pushing each other off one spot: "goto: stuck, re-planning").
        Goal strict = new GoalFiltered(reach, (x, y, z) =>
                Safety.EdgeDistance(World, x, y, z, 2) >= 2 && !Safety.LavaNear(World, x, y, z) && OccupantOf(new BlockPos(x, y, z)) == null
                && (!generator || Math.Max(Math.Abs(x - pos.X), Math.Abs(z - pos.Z)) >= 2 || y > pos.Y + 1),
            generator ? "safe generator spot" : "inward side, no lava");
        var g = await GotoCoreAsync(strict, o with { MaxReplans = 2, AllowPartial = false }, ct).ConfigureAwait(false);
        if (g.Ok) return g;
        Goal relaxed = new GoalFiltered(reach, (x, y, z) => !Safety.LavaNear(World, x, y, z) && OccupantOf(new BlockPos(x, y, z)) == null
                && (!generator || Math.Max(Math.Abs(x - pos.X), Math.Abs(z - pos.Z)) >= 2 || y > pos.Y + 1),
            "no lava");
        return await GotoCoreAsync(relaxed, o, ct).ConfigureAwait(false);
    }

    /// <summary>Standing on the rim of the island (an edge cell) while a dig target is also on the rim.</summary>
    private bool OnRimDiggingRim(BlockPos target)
    {
        var p = World.Player.Position;
        int fx = (int)Math.Floor(p.X), fy = (int)Math.Floor(p.Y + 1e-6), fz = (int)Math.Floor(p.Z);
        return Safety.EdgeDistance(World, fx, fy, fz, 1) <= 1 && Safety.EdgeDistance(World, target.X, target.Y + 1, target.Z, 1) <= 1;
    }
}
