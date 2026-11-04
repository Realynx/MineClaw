namespace MineClaw.World;

/// <summary>
/// Port of the b1.8.1 local-player movement (EntityPlayerSP.onLivingUpdate → EntityLiving.onLivingUpdate /
/// moveEntityWithHeading / moveFlying → Entity.moveEntity). One call to <see cref="Tick"/> = one 50 ms client tick.
/// Constants are the vanilla float literals (MCP b1.7.3/b1.8 decompile), including sneak edge protection
/// (Entity.moveEntity: while sneaking on ground, horizontal motion is reduced in 0.05 steps until something
/// remains within 1 block below the hitbox).
/// </summary>
public static class PlayerPhysics
{
    public const double Width = 0.6, Height = 1.8, EyeHeight = 1.62;
    public const double Gravity = 0.08;
    public const double VerticalDrag = 0.98;               // motionY *= 0.98 after gravity
    public const double AirFriction = 0.91f;               // horizontal drag in air
    public const double GroundFriction = 0.6f * 0.91f;     // slipperiness 0.6 × 0.91 = 0.546
    public const double WalkAccel = 0.1f;                  // EntityPlayer.speedOnGround
    public const double AirAccel = 0.02f;                  // EntityPlayer.speedInAir
    public const double SprintMultiplier = 1.3;            // land/air factor += 0.3 × base
    public const double JumpVelocity = 0.42f;              // EntityLiving.jump: motionY = 0.42F
    public const double SprintJumpBoost = 0.2f;
    public const double StepHeight = 0.5f;                 // EntityLiving.stepHeight
    public const double SneakMultiplier = 0.3f;            // MovementInputFromOptions: sneak → input × 0.3
    public const double InputDamping = 0.98f;              // moveStrafing/moveForward *= 0.98F before moving
    public const double LiquidAccel = 0.02f;
    public const double WaterDrag = 0.8, LavaDrag = 0.5, LiquidGravity = 0.02;
    public const double SwimUpAccel = 0.04;                // holding jump in water/lava
    public const double LadderMaxSpeed = 0.15f, LadderClimbSpeed = 0.2;
    public const double SneakEdgeStep = 0.05;
    public const double SprintMinForward = 0.8f;

    [ThreadStatic] private static List<AABB>? _boxes;

    /// <summary>
    /// Advances the state by one tick. If the chunk under the player isn't loaded the state is left untouched and
    /// <see cref="PhysicsState.Frozen"/> is set (vanilla doesn't tick entities in unloaded chunks either).
    /// </summary>
    /// <remarks>Runs the world's version rules (<see cref="IBlockAccess.Physics"/>): b1.8.1 below, 1.12.2
    /// <see cref="Release1122.Physics1122"/>. The simulators (SwimSim, parkour, EdgeGuard) call this.</remarks>
    public static void Tick(IBlockAccess w, PhysicsState s, MoveInput input)
    {
        var rules = w.Physics;
        if (rules is Versioning.Beta181PhysicsRules) TickBeta(w, s, input, StepHeight);
        else rules.Tick(w, s, input);
    }

    /// <summary>The b1.8.1 tick with another step height (whatever the world's version).</summary>
    public static void Tick(IBlockAccess w, PhysicsState s, MoveInput input, double stepHeight) => TickBeta(w, s, input, stepHeight);

    internal static void TickBeta(IBlockAccess w, PhysicsState s, MoveInput input, double stepHeight)
    {
        if (!BeginTick(w, s, input)) return;

        // Entity.onEntityUpdate → handleWaterMovement (also applies water current).
        s.InWater = HandleMaterialAcceleration(w, s, s.BoundingBox.Grow(0, -0.4, 0).Grow(-0.001, -0.001, -0.001), water: true);
        if (s.InWater) s.FallDistance = 0;
        s.InLava = IsLavaInBB(w, s.BoundingBox.Grow(-0.1, -0.4, -0.1));

        // EntityPlayerSP: movement input.
        ScaledInput(s, input, out double strafe, out double forward);
        bool sprint = input.Sprint && forward >= SprintMinForward && !s.CollidedHorizontally && !input.Sneak;
        s.Sprinting = sprint;
        SpeedFactors(s.Sprinting, out double landFactor, out double airFactor);

        // EntityLiving.onLivingUpdate: jumping.
        var v = s.Velocity;
        if (input.Jump) v = Jumped(s, v);
        s.Velocity = v;
        strafe *= InputDamping;
        forward *= InputDamping;

        MoveWithHeading(w, s, strafe, forward, landFactor, airFactor, stepHeight);
    }

    /// <summary>Vanilla doesn't tick entities in unloaded chunks: false (frozen) when the chunk isn't there.</summary>
    private static bool BeginTick(IBlockAccess w, PhysicsState s, MoveInput input)
    {
        int bx = (int)Math.Floor(s.Position.X), bz = (int)Math.Floor(s.Position.Z);
        if (!w.IsLoaded(bx, bz))
        {
            s.Frozen = true;
            return false;
        }
        s.Frozen = false;
        s.Yaw = input.Yaw;
        s.Pitch = input.Pitch;
        return true;
    }

    /// <summary>MovementInputFromOptions: the input clamped, × 0.3 sneaking (which also lowers the camera, ySize ≥ 0.2).</summary>
    private static void ScaledInput(PhysicsState s, MoveInput input, out double strafe, out double forward)
    {
        forward = Math.Clamp(input.Forward, -1f, 1f);
        strafe = Math.Clamp(input.Strafe, -1f, 1f);
        s.Sneaking = input.Sneak;
        if (input.Sneak)
        {
            forward *= SneakMultiplier;
            strafe *= SneakMultiplier;
            if (s.YSize < 0.2) s.YSize = 0.2;
        }
    }

    /// <summary>The land and air acceleration factors, + 30 % of the base sprinting.</summary>
    private static void SpeedFactors(bool sprinting, out double landFactor, out double airFactor)
    {
        landFactor = WalkAccel;
        airFactor = AirAccel;
        if (sprinting)
        {
            landFactor += WalkAccel * 0.3f;
            airFactor += AirAccel * 0.3f;
        }
    }

    /// <summary>Jump held: up in water or lava, else the jump off the ground (with the sprint boost along the yaw).</summary>
    private static Vec3d Jumped(PhysicsState s, Vec3d v)
    {
        if (s.InWater || s.InLava) return v with { Y = v.Y + SwimUpAccel };
        if (!s.OnGround) return v;
        v = v with { Y = JumpVelocity };
        if (s.Sprinting)
        {
            double f = s.Yaw * 0.01745329f;
            v = v with { X = v.X - MathSin(f) * SprintJumpBoost, Z = v.Z + MathCos(f) * SprintJumpBoost };
        }
        return v;
    }

    private static void MoveWithHeading(IBlockAccess w, PhysicsState s, double strafe, double forward,
        double landFactor, double airFactor, double stepHeight)
    {
        if (s.InWater || s.InLava) MoveInLiquid(w, s, strafe, forward, stepHeight);
        else MoveOnLand(w, s, strafe, forward, landFactor, airFactor, stepHeight);
    }

    /// <summary>moveEntityWithHeading in water (× 0.8) or lava (× 0.5): − 0.02 gravity and the 0.3 climb-out at a wall.</summary>
    private static void MoveInLiquid(IBlockAccess w, PhysicsState s, double strafe, double forward, double stepHeight)
    {
        double drag = s.InWater ? WaterDrag : LavaDrag;
        double y0 = s.Position.Y;
        MoveFlying(s, strafe, forward, LiquidAccel);
        MoveEntity(w, s, s.Velocity.X, s.Velocity.Y, s.Velocity.Z, stepHeight);
        var v = s.Velocity * drag;
        v = v with { Y = v.Y - LiquidGravity };
        s.Velocity = v;
        if (s.CollidedHorizontally &&
            IsOffsetPositionInLiquid(w, s, v.X, v.Y + 0.6 - s.Position.Y + y0, v.Z))
            s.Velocity = v with { Y = 0.3 };
    }

    /// <summary>moveEntityWithHeading on the ground or in the air: slipperiness, the ladder clamp and climb, gravity and drag.</summary>
    private static void MoveOnLand(IBlockAccess w, PhysicsState s, double strafe, double forward,
        double landFactor, double airFactor, double stepHeight)
    {
        double friction = AirFriction;
        if (s.OnGround) friction = GroundFrictionBelow(w, s);
        double f1 = 0.1627714f / (friction * friction * friction);
        double accel = s.OnGround ? landFactor * f1 : airFactor;
        MoveFlying(s, strafe, forward, accel);

        friction = s.OnGround ? GroundFrictionBelow(w, s) : AirFriction;

        s.OnLadder = IsOnLadder(w, s);
        if (s.OnLadder) ClampOnLadder(s);

        MoveEntity(w, s, s.Velocity.X, s.Velocity.Y, s.Velocity.Z, stepHeight);

        var v2 = s.Velocity;
        if (s.CollidedHorizontally && IsOnLadder(w, s)) v2 = v2 with { Y = LadderClimbSpeed };
        v2 = new Vec3d(v2.X * friction, (v2.Y - Gravity) * VerticalDrag, v2.Z * friction);
        s.Velocity = v2;
    }

    /// <summary>On a ladder: horizontal motion within ±0.15, falling no faster than 0.15, and sneaking holds on.</summary>
    private static void ClampOnLadder(PhysicsState s)
    {
        var lv = s.Velocity;
        double lx = Math.Clamp(lv.X, -LadderMaxSpeed, LadderMaxSpeed);
        double lz = Math.Clamp(lv.Z, -LadderMaxSpeed, LadderMaxSpeed);
        double ly = lv.Y < -LadderMaxSpeed ? -LadderMaxSpeed : lv.Y;
        if (s.Sneaking && ly < 0) ly = 0;
        s.Velocity = new Vec3d(lx, ly, lz);
        s.FallDistance = 0;
    }

    private static double GroundFrictionBelow(IBlockAccess w, PhysicsState s)
    {
        int id = w.GetBlock((int)Math.Floor(s.Position.X), (int)Math.Floor(s.BoundingBox.MinY) - 1, (int)Math.Floor(s.Position.Z));
        if (id <= 0) return 0.5460001f;
        return (float)BlockShapes.Slipperiness(id) * 0.91f;
    }

    /// <summary>EntityLiving.isOnLadder: ladder or vine in the feet cell.</summary>
    public static bool IsOnLadder(IBlockAccess w, PhysicsState s)
    {
        var rules = w.Physics;
        if (rules is not Versioning.Beta181PhysicsRules)
            return rules.IsClimbable(w, (int)Math.Floor(s.Position.X), (int)Math.Floor(s.BoundingBox.MinY), (int)Math.Floor(s.Position.Z));
        int id = w.GetBlock((int)Math.Floor(s.Position.X), (int)Math.Floor(s.BoundingBox.MinY), (int)Math.Floor(s.Position.Z));
        return BlockShapes.IsClimbable(w, id);
    }

    /// <summary>Entity.moveFlying: adds input acceleration rotated by yaw.</summary>
    private static void MoveFlying(PhysicsState s, double strafe, double forward, double speed)
    {
        double f = Math.Sqrt(strafe * strafe + forward * forward);
        if (f < 0.01f) return;
        if (f < 1) f = 1;
        f = speed / f;
        strafe *= f;
        forward *= f;
        double rad = s.Yaw * Math.PI / 180.0;
        double sin = MathSin(rad), cos = MathCos(rad);
        var v = s.Velocity;
        s.Velocity = new Vec3d(v.X + strafe * cos - forward * sin, v.Y, v.Z + forward * cos + strafe * sin);
    }

    /// <summary>Entity.moveEntity: swept AABB collision with step-up and sneak edge protection.</summary>
    public static void MoveEntity(IBlockAccess w, PhysicsState s, double dx, double dy, double dz) => MoveEntity(w, s, dx, dy, dz, StepHeight);

    public static void MoveEntity(IBlockAccess w, PhysicsState s, double dx, double dy, double dz, double stepHeight)
    {
        var boxes = _boxes ??= new List<AABB>(32);
        s.YSize *= 0.4;
        if (s.InWeb) SlowInWeb(s, ref dx, ref dy, ref dz);

        double ox = dx, oy = dy, oz = dz;
        var box = s.BoundingBox;
        var startBox = box;
        bool sneakOnGround = s.OnGround && s.Sneaking; // mc-dev Entity.move: flag = onGround && isSneaking()
        if (sneakOnGround) ClampToEdge(w, box, boxes, ref dx, ref dz, ref ox, ref oz);

        BlockShapes.GetCollidingBoxes(w, box.Expand(dx, dy, dz), boxes);
        foreach (var b in boxes) dy = b.CalculateYOffset(box, dy);
        box = box.Offset(0, dy, 0);
        bool canStep = CanStep(s.OnGround, oy, dy);
        CollideXz(boxes, ref box, ref dx, ref dz);

        // mc-dev 1.8 Entity.move: `bI > 0 && flag1 && (flag || bH < 0.05F)`: sneaking on ground may step up whatever ySize is.
        if (ShouldStepUp(s, stepHeight, canStep, sneakOnGround, ox != dx || oz != dz))
            StepUp(w, s, boxes, startBox, stepHeight, ox, oz, ref box, ref dx, ref dy, ref dz);

        FinishMove(s, box, ox, oy, oz, dx, dy, dz);
        DoBlockCollisions(w, s);
    }

    /// <summary>The moved box becomes the position; collision flags, updateFallState and the blocked axes' velocity.</summary>
    private static void FinishMove(PhysicsState s, AABB box, double ox, double oy, double oz, double dx, double dy, double dz)
    {
        s.Position = new Vec3d((box.MinX + box.MaxX) / 2, box.MinY, (box.MinZ + box.MaxZ) / 2);
        s.CollidedHorizontally = ox != dx || oz != dz;
        s.CollidedVertically = oy != dy;
        s.OnGround = oy != dy && oy < 0;

        // updateFallState
        if (s.OnGround) s.FallDistance = 0;
        else if (dy < 0) s.FallDistance -= dy;

        s.Velocity = StopBlockedAxes(s.Velocity, ox != dx, oy != dy, oz != dz);
    }

    /// <summary>On the ground, or the fall was stopped by a floor this move.</summary>
    private static bool CanStep(bool onGround, double oy, double dy) => onGround || (oy != dy && oy < 0);

    /// <summary>mc-dev 1.8 Entity.move: <c>bI &gt; 0 &amp;&amp; flag1 &amp;&amp; (flag || bH &lt; 0.05F)</c>, and a horizontal axis was blocked.</summary>
    private static bool ShouldStepUp(PhysicsState s, double stepHeight, bool canStep, bool sneakOnGround, bool blocked) =>
        stepHeight > 0 && canStep && (sneakOnGround || s.YSize < 0.05f) && blocked;

    private static void SlowInWeb(PhysicsState s, ref double dx, ref double dy, ref double dz)
    {
        s.InWeb = false;
        dx *= 0.25; dy *= 0.05; dz *= 0.25;
        s.Velocity = Vec3d.Zero;
    }

    /// <summary>Sneaking on the ground: each horizontal axis is cut back in <see cref="SneakEdgeStep"/> steps until the box still
    /// has floor under it.</summary>
    private static void ClampToEdge(IBlockAccess w, AABB box, List<AABB> boxes, ref double dx, ref double dz, ref double ox, ref double oz)
    {
        while (dx != 0 && BlockShapes.GetCollidingBoxes(w, box.Offset(dx, -1, 0), boxes).Count == 0)
        {
            dx = TowardZero(dx);
            ox = dx;
        }
        while (dz != 0 && BlockShapes.GetCollidingBoxes(w, box.Offset(0, -1, dz), boxes).Count == 0)
        {
            dz = TowardZero(dz);
            oz = dz;
        }
    }

    private static double TowardZero(double v)
    {
        const double step = SneakEdgeStep;
        if (v < step && v >= -step) return 0;
        return v > 0 ? v - step : v + step;
    }

    /// <summary>The x then z motion clipped against the boxes (the box moves after each).</summary>
    private static void CollideXz(List<AABB> boxes, ref AABB box, ref double dx, ref double dz)
    {
        foreach (var b in boxes) dx = b.CalculateXOffset(box, dx);
        box = box.Offset(dx, 0, 0);
        foreach (var b in boxes) dz = b.CalculateZOffset(box, dz);
        box = box.Offset(0, 0, dz);
    }

    /// <summary>The step-up retry from the start box: kept (and ySize raised) only when it gets farther than the plain move.</summary>
    private static void StepUp(IBlockAccess w, PhysicsState s, List<AABB> boxes, AABB startBox, double stepHeight, double ox, double oz,
        ref AABB box, ref double dx, ref double dy, ref double dz)
    {
        double sx = dx, sy = dy, sz = dz;
        var afterBox = box;
        dx = ox; dy = stepHeight; dz = oz;
        box = startBox;
        BlockShapes.GetCollidingBoxes(w, box.Expand(dx, dy, dz), boxes);
        foreach (var b in boxes) dy = b.CalculateYOffset(box, dy);
        box = box.Offset(0, dy, 0);
        CollideXz(boxes, ref box, ref dx, ref dz);
        if (sx * sx + sz * sz >= dx * dx + dz * dz)
        {
            dx = sx; dy = sy; dz = sz;
            box = afterBox;
        }
        else
        {
            s.YSize += 0.5;
        }
    }

    private static Vec3d StopBlockedAxes(Vec3d v, bool x, bool y, bool z)
    {
        if (x) v = v with { X = 0 };
        if (y) v = v with { Y = 0 };
        if (z) v = v with { Z = 0 };
        return v;
    }

    /// <summary>Entity.doBlockCollisions: web and soul sand effects for blocks inside the (slightly shrunk) hitbox.</summary>
    private static void DoBlockCollisions(IBlockAccess w, PhysicsState s)
    {
        var bb = s.BoundingBox.Grow(-0.001, -0.001, -0.001);
        int x0 = (int)Math.Floor(bb.MinX), x1 = (int)Math.Floor(bb.MaxX);
        int y0 = (int)Math.Floor(bb.MinY), y1 = (int)Math.Floor(bb.MaxY);
        int z0 = (int)Math.Floor(bb.MinZ), z1 = (int)Math.Floor(bb.MaxZ);
        for (int x = x0; x <= x1; x++)
        for (int y = y0; y <= y1; y++)
        for (int z = z0; z <= z1; z++)
        {
            int id = w.GetBlock(x, y, z);
            if (id == BlockShapes.Web) s.InWeb = true;
            else if (id == BlockShapes.SoulSand) s.Velocity = new Vec3d(s.Velocity.X * 0.4, s.Velocity.Y, s.Velocity.Z * 0.4);
        }
    }

    /// <summary>Entity.isOffsetPositionInLiquid: no collision at the offset box and no liquid in it.</summary>
    private static bool IsOffsetPositionInLiquid(IBlockAccess w, PhysicsState s, double dx, double dy, double dz)
    {
        var bb = s.BoundingBox.Offset(dx, dy, dz);
        var boxes = _boxes ??= new List<AABB>(32);
        if (BlockShapes.GetCollidingBoxes(w, bb, boxes).Count > 0) return false;
        return !IsAnyLiquid(w, bb);
    }

    private static bool IsAnyLiquid(IBlockAccess w, AABB bb)
    {
        int x0 = (int)Math.Floor(bb.MinX), x1 = (int)Math.Floor(bb.MaxX + 1);
        int y0 = (int)Math.Floor(bb.MinY), y1 = (int)Math.Floor(bb.MaxY + 1);
        int z0 = (int)Math.Floor(bb.MinZ), z1 = (int)Math.Floor(bb.MaxZ + 1);
        for (int x = x0; x < x1; x++)
        for (int y = y0; y < y1; y++)
        for (int z = z0; z < z1; z++)
            if (BlockShapes.IsLiquid(w.GetBlock(x, y, z))) return true;
        return false;
    }

    private static bool IsLavaInBB(IBlockAccess w, AABB bb)
    {
        int x0 = (int)Math.Floor(bb.MinX), x1 = (int)Math.Floor(bb.MaxX + 1);
        int y0 = (int)Math.Floor(bb.MinY), y1 = (int)Math.Floor(bb.MaxY + 1);
        int z0 = (int)Math.Floor(bb.MinZ), z1 = (int)Math.Floor(bb.MaxZ + 1);
        for (int x = x0; x < x1; x++)
        for (int y = y0; y < y1; y++)
        for (int z = z0; z < z1; z++)
            if (BlockShapes.IsLava(w.GetBlock(x, y, z))) return true;
        return false;
    }

    /// <summary>World.handleMaterialAcceleration for water: true if the box touches water; applies the current (0.014/tick).</summary>
    private static bool HandleMaterialAcceleration(IBlockAccess w, PhysicsState s, AABB bb, bool water)
    {
        int x0 = (int)Math.Floor(bb.MinX), x1 = (int)Math.Floor(bb.MaxX + 1);
        int y0 = (int)Math.Floor(bb.MinY), y1 = (int)Math.Floor(bb.MaxY + 1);
        int z0 = (int)Math.Floor(bb.MinZ), z1 = (int)Math.Floor(bb.MaxZ + 1);
        bool found = false;
        var flow = Vec3d.Zero;
        for (int x = x0; x < x1; x++)
        for (int y = y0; y < y1; y++)
        for (int z = z0; z < z1; z++)
        {
            int id = w.GetBlock(x, y, z);
            if (!(water ? BlockShapes.IsWater(id) : BlockShapes.IsLava(id))) continue;
            double surface = (y + 1) - PercentAir(w.GetMeta(x, y, z));
            if (y1 >= surface)
            {
                found = true;
                flow += FlowVector(w, x, y, z, water);
            }
        }
        if (flow.Length > 0)
        {
            flow = flow.Normalized() * 0.014;
            s.Velocity += flow;
        }
        return found;
    }

    /// <summary>BlockFluid.getPercentAir.</summary>
    private static double PercentAir(int meta)
    {
        if (meta >= 8) meta = 0;
        return (meta + 1) / 9f;
    }

    private static int FlowDecay(IBlockAccess w, int x, int y, int z, bool water)
    {
        int id = w.GetBlock(x, y, z);
        if (!(water ? BlockShapes.IsWater(id) : BlockShapes.IsLava(id))) return -1;
        int m = w.GetMeta(x, y, z);
        return m >= 8 ? 0 : m;
    }

    /// <summary>BlockFluid.getFlowVector (normalized).</summary>
    public static Vec3d FlowVector(IBlockAccess w, int x, int y, int z, bool water = true)
    {
        var vec = Vec3d.Zero;
        int l = FlowDecay(w, x, y, z, water);
        foreach (var (dx, dz) in FlowSides)
            if (SideFlow(w, x + dx, y, z + dz, l, water, out int d)) vec += new Vec3d(dx * d, 0, dz * d);
        if (w.GetMeta(x, y, z) >= 8 && HasSolidSide(w, x, y, z, water)) vec = vec.Normalized() + new Vec3d(0, -6, 0);
        return vec.Normalized();
    }

    /// <summary>getFlowVector's side order: −x, −z, +x, +z.</summary>
    private static readonly (int Dx, int Dz)[] FlowSides = [(-1, 0), (0, -1), (1, 0), (0, 1)];

    /// <summary>A side's pull on the flow: its decay − ours, or (through a side whose material doesn't block movement) the
    /// decay below it − (ours − 8). False when the side adds nothing.</summary>
    private static bool SideFlow(IBlockAccess w, int nx, int y, int nz, int l, bool water, out int d)
    {
        int l1 = FlowDecay(w, nx, y, nz, water);
        if (l1 >= 0)
        {
            d = l1 - l;
            return true;
        }
        d = 0;
        // BlockFluids.java:106: the neighbour's material must not block movement (signs and plates do; ladders don't).
        if (BlockShapes.MaterialBlocksMovement(w.GetBlock(nx, y, nz))) return false;
        l1 = FlowDecay(w, nx, y - 1, nz, water);
        if (l1 < 0) return false;
        d = l1 - (l - 8);
        return true;
    }

    /// <summary>Falling liquid beside a solid side, at its level or one above.</summary>
    private static bool HasSolidSide(IBlockAccess w, int x, int y, int z, bool water) =>
        SolidForFlow(w, x, y, z - 1, water) || SolidForFlow(w, x, y, z + 1, water)
        || SolidForFlow(w, x - 1, y, z, water) || SolidForFlow(w, x + 1, y, z, water)
        || SolidForFlow(w, x, y + 1, z - 1, water) || SolidForFlow(w, x, y + 1, z + 1, water)
        || SolidForFlow(w, x - 1, y + 1, z, water) || SolidForFlow(w, x + 1, y + 1, z, water);

    /// <summary>BlockFluids.b(IBlockAccess, x, y, z, face) (BlockFluids.java:60-64 → Block.java:240): not the same
    /// liquid, not ice, and a buildable material (slabs, fences, glass, web... count; plants, torches, snow layers don't).</summary>
    private static bool SolidForFlow(IBlockAccess w, int x, int y, int z, bool water)
    {
        int id = w.GetBlock(x, y, z);
        if (water ? BlockShapes.IsWater(id) : BlockShapes.IsLava(id)) return false;
        if (id == BlockShapes.Ice) return false;
        return BlockShapes.MaterialIsBuildable(id);
    }

    // MathHelper.sin/cos use a 65536-entry table; replicate for bit-closeness with the vanilla client.
    private static readonly float[] SinTable = BuildSinTable();

    private static float[] BuildSinTable()
    {
        var t = new float[65536];
        for (int i = 0; i < t.Length; i++) t[i] = (float)Math.Sin(i * Math.PI * 2.0 / 65536.0);
        return t;
    }

    public static double MathSin(double rad) => SinTable[(int)(float)(rad * 10430.378f) & 0xFFFF];
    public static double MathCos(double rad) => SinTable[(int)(float)(rad * 10430.378f + 16384f) & 0xFFFF];

    /// <summary>Unit look vector for a yaw/pitch in degrees (vanilla convention: yaw 0 = +Z, yaw 90 = -X, pitch +90 = down).</summary>
    public static Vec3d LookVector(float yaw, float pitch)
    {
        double y = yaw * Math.PI / 180, p = pitch * Math.PI / 180;
        return new Vec3d(-Math.Sin(y) * Math.Cos(p), -Math.Sin(p), Math.Cos(y) * Math.Cos(p));
    }

    /// <summary>Yaw/pitch (degrees) that look from <paramref name="from"/> towards <paramref name="to"/>.</summary>
    public static (float Yaw, float Pitch) YawPitchTo(Vec3d from, Vec3d to)
    {
        var d = to - from;
        double h = Math.Sqrt(d.X * d.X + d.Z * d.Z);
        float yaw = (float)(Math.Atan2(-d.X, d.Z) * 180 / Math.PI);
        float pitch = (float)(-Math.Atan2(d.Y, h) * 180 / Math.PI);
        return (yaw, pitch);
    }
}
