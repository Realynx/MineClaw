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
    public static void Tick(IBlockAccess w, PhysicsState s, MoveInput input) => Tick(w, s, input, StepHeight);

    /// <summary><see cref="Tick(IBlockAccess, PhysicsState, MoveInput)"/> with another step height (1.12.2: 0.6; the rest of
    /// the 1.12.2 move is pending the decompiled source, multi-version P3).</summary>
    public static void Tick(IBlockAccess w, PhysicsState s, MoveInput input, double stepHeight)
    {
        int bx = (int)Math.Floor(s.Position.X), bz = (int)Math.Floor(s.Position.Z);
        if (!w.IsLoaded(bx, bz))
        {
            s.Frozen = true;
            return;
        }
        s.Frozen = false;
        s.Yaw = input.Yaw;
        s.Pitch = input.Pitch;

        // Entity.onEntityUpdate → handleWaterMovement (also applies water current).
        s.InWater = HandleMaterialAcceleration(w, s, s.BoundingBox.Grow(0, -0.4, 0).Grow(-0.001, -0.001, -0.001), water: true);
        if (s.InWater) s.FallDistance = 0;
        s.InLava = IsLavaInBB(w, s.BoundingBox.Grow(-0.1, -0.4, -0.1));

        // EntityPlayerSP: movement input.
        double forward = Math.Clamp(input.Forward, -1f, 1f);
        double strafe = Math.Clamp(input.Strafe, -1f, 1f);
        s.Sneaking = input.Sneak;
        if (input.Sneak)
        {
            forward *= SneakMultiplier;
            strafe *= SneakMultiplier;
            if (s.YSize < 0.2) s.YSize = 0.2;
        }
        bool sprint = input.Sprint && forward >= SprintMinForward && !s.CollidedHorizontally && !input.Sneak;
        s.Sprinting = sprint;

        double landFactor = WalkAccel, airFactor = AirAccel;
        if (s.Sprinting)
        {
            landFactor += WalkAccel * 0.3f;
            airFactor += AirAccel * 0.3f;
        }

        // EntityLiving.onLivingUpdate: jumping.
        var v = s.Velocity;
        if (input.Jump)
        {
            if (s.InWater || s.InLava) v = v with { Y = v.Y + SwimUpAccel };
            else if (s.OnGround)
            {
                v = v with { Y = JumpVelocity };
                if (s.Sprinting)
                {
                    double f = s.Yaw * 0.01745329f;
                    v = v with { X = v.X - MathSin(f) * SprintJumpBoost, Z = v.Z + MathCos(f) * SprintJumpBoost };
                }
            }
        }
        s.Velocity = v;
        strafe *= InputDamping;
        forward *= InputDamping;

        MoveWithHeading(w, s, strafe, forward, landFactor, airFactor, stepHeight);
    }

    private static void MoveWithHeading(IBlockAccess w, PhysicsState s, double strafe, double forward,
        double landFactor, double airFactor, double stepHeight)
    {
        if (s.InWater || s.InLava)
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
            return;
        }

        double friction = AirFriction;
        if (s.OnGround) friction = GroundFrictionBelow(w, s);
        double f1 = 0.1627714f / (friction * friction * friction);
        double accel = s.OnGround ? landFactor * f1 : airFactor;
        MoveFlying(s, strafe, forward, accel);

        friction = s.OnGround ? GroundFrictionBelow(w, s) : AirFriction;

        s.OnLadder = IsOnLadder(w, s);
        if (s.OnLadder)
        {
            var lv = s.Velocity;
            double lx = Math.Clamp(lv.X, -LadderMaxSpeed, LadderMaxSpeed);
            double lz = Math.Clamp(lv.Z, -LadderMaxSpeed, LadderMaxSpeed);
            double ly = lv.Y < -LadderMaxSpeed ? -LadderMaxSpeed : lv.Y;
            if (s.Sneaking && ly < 0) ly = 0;
            s.Velocity = new Vec3d(lx, ly, lz);
            s.FallDistance = 0;
        }

        MoveEntity(w, s, s.Velocity.X, s.Velocity.Y, s.Velocity.Z, stepHeight);

        var v2 = s.Velocity;
        if (s.CollidedHorizontally && IsOnLadder(w, s)) v2 = v2 with { Y = LadderClimbSpeed };
        v2 = new Vec3d(v2.X * friction, (v2.Y - Gravity) * VerticalDrag, v2.Z * friction);
        s.Velocity = v2;
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
        if (s.InWeb)
        {
            s.InWeb = false;
            dx *= 0.25; dy *= 0.05; dz *= 0.25;
            s.Velocity = Vec3d.Zero;
        }

        double ox = dx, oy = dy, oz = dz;
        var box = s.BoundingBox;
        var startBox = box;
        bool sneakOnGround = s.OnGround && s.Sneaking; // mc-dev Entity.move: flag = onGround && isSneaking()

        if (sneakOnGround)
        {
            const double step = SneakEdgeStep;
            while (dx != 0 && BlockShapes.GetCollidingBoxes(w, box.Offset(dx, -1, 0), boxes).Count == 0)
            {
                if (dx < step && dx >= -step) dx = 0;
                else if (dx > 0) dx -= step;
                else dx += step;
                ox = dx;
            }
            while (dz != 0 && BlockShapes.GetCollidingBoxes(w, box.Offset(0, -1, dz), boxes).Count == 0)
            {
                if (dz < step && dz >= -step) dz = 0;
                else if (dz > 0) dz -= step;
                else dz += step;
                oz = dz;
            }
        }

        BlockShapes.GetCollidingBoxes(w, box.Expand(dx, dy, dz), boxes);
        foreach (var b in boxes) dy = b.CalculateYOffset(box, dy);
        box = box.Offset(0, dy, 0);
        bool canStep = s.OnGround || (oy != dy && oy < 0);
        foreach (var b in boxes) dx = b.CalculateXOffset(box, dx);
        box = box.Offset(dx, 0, 0);
        foreach (var b in boxes) dz = b.CalculateZOffset(box, dz);
        box = box.Offset(0, 0, dz);

        // mc-dev 1.8 Entity.move: `bI > 0 && flag1 && (flag || bH < 0.05F)`: sneaking on ground may step up whatever ySize is.
        if (stepHeight > 0 && canStep && (sneakOnGround || s.YSize < 0.05f) && (ox != dx || oz != dz))
        {
            double sx = dx, sy = dy, sz = dz;
            var afterBox = box;
            dx = ox; dy = stepHeight; dz = oz;
            box = startBox;
            BlockShapes.GetCollidingBoxes(w, box.Expand(dx, dy, dz), boxes);
            foreach (var b in boxes) dy = b.CalculateYOffset(box, dy);
            box = box.Offset(0, dy, 0);
            foreach (var b in boxes) dx = b.CalculateXOffset(box, dx);
            box = box.Offset(dx, 0, 0);
            foreach (var b in boxes) dz = b.CalculateZOffset(box, dz);
            box = box.Offset(0, 0, dz);
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

        s.Position = new Vec3d((box.MinX + box.MaxX) / 2, box.MinY, (box.MinZ + box.MaxZ) / 2);
        s.CollidedHorizontally = ox != dx || oz != dz;
        s.CollidedVertically = oy != dy;
        s.OnGround = oy != dy && oy < 0;

        // updateFallState
        if (s.OnGround) s.FallDistance = 0;
        else if (dy < 0) s.FallDistance -= dy;

        var v = s.Velocity;
        if (ox != dx) v = v with { X = 0 };
        if (oy != dy) v = v with { Y = 0 };
        if (oz != dz) v = v with { Z = 0 };
        s.Velocity = v;

        DoBlockCollisions(w, s);
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
        for (int i = 0; i < 4; i++)
        {
            int nx = x, nz = z;
            if (i == 0) nx--;
            if (i == 1) nz--;
            if (i == 2) nx++;
            if (i == 3) nz++;
            int l1 = FlowDecay(w, nx, y, nz, water);
            if (l1 < 0)
            {
                // BlockFluids.java:106: the neighbour's material must not block movement (signs and plates do; ladders don't).
                if (!BlockShapes.MaterialBlocksMovement(w.GetBlock(nx, y, nz)))
                {
                    l1 = FlowDecay(w, nx, y - 1, nz, water);
                    if (l1 >= 0)
                    {
                        int d = l1 - (l - 8);
                        vec += new Vec3d((nx - x) * d, 0, (nz - z) * d);
                    }
                }
                continue;
            }
            int j = l1 - l;
            vec += new Vec3d((nx - x) * j, 0, (nz - z) * j);
        }
        if (w.GetMeta(x, y, z) >= 8)
        {
            bool solidSide = SolidForFlow(w, x, y, z - 1, water) || SolidForFlow(w, x, y, z + 1, water)
                || SolidForFlow(w, x - 1, y, z, water) || SolidForFlow(w, x + 1, y, z, water)
                || SolidForFlow(w, x, y + 1, z - 1, water) || SolidForFlow(w, x, y + 1, z + 1, water)
                || SolidForFlow(w, x - 1, y + 1, z, water) || SolidForFlow(w, x + 1, y + 1, z, water);
            if (solidSide) vec = vec.Normalized() + new Vec3d(0, -6, 0);
        }
        return vec.Normalized();
    }

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
