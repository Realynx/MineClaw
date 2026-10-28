namespace MineClaw.World.Release1122;

/// <summary>
/// The 1.12.2 local-player tick (multi-version P3), ported from the decompiled client (MCP stable_39 names). One call = one
/// 50 ms tick, in the client's order (<c>EntityPlayerSP.onUpdate</c>):
/// <list type="number">
/// <item><c>Entity.onEntityUpdate</c>: <c>handleWaterMovement</c> (box grown 0/−0.4/0, shrunk 0.001; the current) and
/// <c>isInLava</c> (fall distance halved).</item>
/// <item><c>EntityPlayerSP.onLivingUpdate</c>: <c>MovementInputFromOptions</c> (sneak ×0.3), using an item ×0.2, the sprint rules
/// (forward ≥ 0.8, food &gt; 6, stops on a horizontal collision).</item>
/// <item><c>EntityLivingBase.onLivingUpdate</c>: motion below 0.003 zeroed, jump (0.42F; sprint boost 0.2F along the yaw; 10
/// <c>jumpTicks</c>), water/lava jump +0.04, input ×0.98F, <c>travel</c>.</item>
/// <item><c>travel</c>: water (0.02F accel, ×0.8F, −0.02, the 0.3 climb-out), lava (×0.5), else ground/air with slipperiness
/// (ice 0.98, slime 0.8), the ladder clamp (0.15, sneak holds) and the 0.2 climb, gravity 0.08 and ×0.98.</item>
/// <item><c>Entity.move</c>: the sneak edge clamp (probing one step height down, three loops), swept collision, the 0.6 step
/// (two candidate step boxes, the farther wins, then the step-down), <c>onLanded</c> (slime / bed bounce), fall damage
/// (<c>onFallenUpon</c>: hay ×0.2, bed ×0.5, slime none unless sneaking), <c>onEntityWalk</c> (slime slowdown),
/// <c>doBlockCollisions</c> (soul sand ×0.4, web).</item>
/// <item><c>EntityPlayer.onLivingUpdate</c> after travel: the next tick's land speed (the movement-speed attribute, sprint
/// modifier ×1.3 total) and air speed (0.02F, +30 % sprinting).</item>
/// <item><c>EntityPlayer.updateSize</c>: 1.65F high while sneaking when the box fits, else 1.8F; eye 1.54F / 1.62F.</item>
/// </list>
/// Not modelled: <c>pushOutOfBlocks</c>, entity collision boxes (boats, shulkers), the world border, potions (jump boost,
/// speed, levitation), Depth Strider, elytra, flying, riding.
/// </summary>
public static class Physics1122
{
    public const float Width = 0.6f, Height = 1.8f, SneakHeight = 1.65f, EyeHeight = 1.62f, SneakEyeHeight = 1.62f - 0.08f;
    public const float StepHeight = 0.6f;
    public const float JumpVelocity = 0.42f, SprintJumpBoost = 0.2f;
    /// <summary><c>EntityPlayer.applyEntityAttributes</c>: movement speed base 0.10000000149011612 (0.1F).</summary>
    public const double WalkSpeedBase = 0.10000000149011612;
    /// <summary><c>EntityLivingBase.SPRINTING_SPEED_BOOST</c>: 0.30000001192092896, operation 2 (multiply total).</summary>
    public const double SprintModifier = 0.30000001192092896;
    public const float SpeedInAir = 0.02f;
    public const double Gravity = 0.08, VerticalDrag = 0.9800000190734863;
    public const double WaterDrag = 0.800000011920929, LavaDrag = 0.5, LiquidGravity = 0.02, SwimUp = 0.03999999910593033;
    public const double LadderSpeed = 0.15000000596046448, LadderClimb = 0.2;

    /// <summary>The land speed of a walking / sprinting player (<c>(float)</c> of the attribute value).</summary>
    public static float LandSpeed(bool sprinting) => (float)(sprinting ? WalkSpeedBase * (1.0 + SprintModifier) : WalkSpeedBase);

    /// <summary><c>jumpMovementFactor</c>: speedInAir, +30 % while sprinting (<c>EntityPlayer.onLivingUpdate</c>).</summary>
    public static float AirSpeed(bool sprinting) => sprinting ? (float)(SpeedInAir + SpeedInAir * 0.3) : SpeedInAir;

    [ThreadStatic] private static List<AABB>? _boxes, _boxes2;

    public static void Tick(IBlockAccess w, PhysicsState s, MoveInput input)
    {
        int bx = (int)Math.Floor(s.Position.X), bz = (int)Math.Floor(s.Position.Z);
        if (!w.IsLoaded(bx, bz))
        {
            s.Frozen = true;                                               // EntityPlayerSP.onUpdate: isBlockLoaded(posX, 0, posZ)
            return;
        }
        s.Frozen = false;
        s.FallDamage = 0;
        if (s.Width != Width) s.Width = Width;
        s.Yaw = input.Yaw;
        s.Pitch = input.Pitch;

        // Entity.onEntityUpdate
        HandleWaterMovement(w, s);
        if (IsInLava(w, s)) s.FallDistance = (float)(s.FallDistance * 0.5f);

        // EntityPlayerSP.onLivingUpdate: MovementInputFromOptions, item use, sprint.
        float forward = Math.Clamp(input.Forward, -1f, 1f), strafe = Math.Clamp(input.Strafe, -1f, 1f);
        s.Sneaking = input.Sneak;
        if (input.Sneak)
        {
            strafe = (float)(strafe * 0.3);
            forward = (float)(forward * 0.3);
        }
        if (input.UsingItem)
        {
            strafe *= 0.2f;
            forward *= 0.2f;
        }
        bool foodOk = !input.Hungry;
        if (!s.Sprinting && input.Sprint && forward >= 0.8f && foodOk && !input.UsingItem) s.Sprinting = true;
        if (s.Sprinting && (forward < 0.8f || s.CollidedHorizontally || !foodOk || !input.Sprint)) s.Sprinting = false;

        // EntityLivingBase.onLivingUpdate
        if (s.JumpTicks > 0) s.JumpTicks--;
        var v = s.Velocity;
        double vx = Math.Abs(v.X) < 0.003 ? 0 : v.X, vy = Math.Abs(v.Y) < 0.003 ? 0 : v.Y, vz = Math.Abs(v.Z) < 0.003 ? 0 : v.Z;
        if (input.Jump)
        {
            if (s.InWater) vy += SwimUp;
            else if (IsInLava(w, s)) vy += SwimUp;
            else if (s.OnGround && s.JumpTicks == 0)
            {
                vy = JumpVelocity;                                         // jump(): getJumpUpwardsMotion
                if (s.Sprinting)
                {
                    float f = s.Yaw * 0.017453292f;
                    vx -= Sin(f) * SprintJumpBoost;
                    vz += Cos(f) * SprintJumpBoost;
                }
                s.JumpTicks = 10;
            }
        }
        else s.JumpTicks = 0;
        s.Velocity = new Vec3d(vx, vy, vz);
        strafe *= 0.98f;
        forward *= 0.98f;

        Travel(w, s, strafe, forward);

        // EntityPlayer.onLivingUpdate after super: next tick's speeds.
        s.AirSpeed = AirSpeed(s.Sprinting);
        s.LandSpeed = LandSpeed(s.Sprinting);

        // EntityPlayer.onUpdate: updateSize
        float h = s.Sneaking ? SneakHeight : Height;
        if (s.Height != h)
        {
            var box = AABB.ForEntity(s.Position, Width, h);
            var list = _boxes ??= new List<AABB>(32);
            if (Shapes1122.GetCollidingBoxes(w, box, list).Count == 0) s.Height = h;
        }
        s.EyeHeight = s.Sneaking || s.Height == SneakHeight ? SneakEyeHeight : EyeHeight;
    }

    private static void Travel(IBlockAccess w, PhysicsState s, float strafe, float forward)
    {
        if (s.InWater || IsInLava(w, s))
        {
            bool water = s.InWater;
            double y0 = s.Position.Y;
            MoveRelative(s, strafe, forward, 0.02f);
            Move(w, s, s.Velocity.X, s.Velocity.Y, s.Velocity.Z);
            double drag = water ? WaterDrag : LavaDrag;
            var v = s.Velocity;
            v = new Vec3d(v.X * drag, v.Y * drag - LiquidGravity, v.Z * drag);
            s.Velocity = v;
            if (s.CollidedHorizontally && IsOffsetPositionInLiquid(w, s, v.X, v.Y + 0.6000000238418579 - s.Position.Y + y0, v.Z))
                s.Velocity = v with { Y = 0.30000001192092896 };
            return;
        }

        float o = 0.91f;
        if (s.OnGround) o = Shapes1122.Slipperiness(GroundBlock(w, s)) * 0.91f;
        float r = 0.16277136f / (o * o * o);
        float land = float.IsNaN(s.LandSpeed) ? LandSpeed(false) : s.LandSpeed;
        float air = float.IsNaN(s.AirSpeed) ? AirSpeed(false) : s.AirSpeed;
        float t = s.OnGround ? land * r : air;
        MoveRelative(s, strafe, forward, t);
        o = 0.91f;
        if (s.OnGround) o = Shapes1122.Slipperiness(GroundBlock(w, s)) * 0.91f;

        bool ladder = IsOnLadder(w, s);
        s.OnLadder = ladder;
        if (ladder)
        {
            var lv = s.Velocity;
            double lx = Math.Clamp(lv.X, -LadderSpeed, LadderSpeed), lz = Math.Clamp(lv.Z, -LadderSpeed, LadderSpeed);
            double ly = lv.Y < -0.15 ? -0.15 : lv.Y;
            s.FallDistance = 0;
            if (s.Sneaking && ly < 0) ly = 0;
            s.Velocity = new Vec3d(lx, ly, lz);
        }

        Move(w, s, s.Velocity.X, s.Velocity.Y, s.Velocity.Z);
        var v2 = s.Velocity;
        if (s.CollidedHorizontally && IsOnLadder(w, s)) v2 = v2 with { Y = LadderClimb };
        double ny = (v2.Y - Gravity) * VerticalDrag;
        s.Velocity = new Vec3d(v2.X * o, ny, v2.Z * o);
    }

    /// <summary>The block whose slipperiness applies: <c>BlockPos(posX, minY − 1, posZ)</c>.</summary>
    private static int GroundBlock(IBlockAccess w, PhysicsState s) =>
        w.GetBlock((int)Math.Floor(s.Position.X), (int)Math.Floor(s.Position.Y - 1.0), (int)Math.Floor(s.Position.Z));

    /// <summary><c>Entity.moveRelative</c> (all float, the yaw through the sine table).</summary>
    private static void MoveRelative(PhysicsState s, float strafe, float forward, float friction)
    {
        float f = strafe * strafe + forward * forward;
        if (f < 1.0E-4f) return;
        f = MathF.Sqrt(f);
        if (f < 1.0f) f = 1.0f;
        f = friction / f;
        strafe *= f;
        forward *= f;
        float g = Sin(s.Yaw * 0.017453292f), h = Cos(s.Yaw * 0.017453292f);
        var v = s.Velocity;
        s.Velocity = new Vec3d(v.X + (strafe * h - forward * g), v.Y, v.Z + (forward * h + strafe * g));
    }

    /// <summary><c>EntityLivingBase.isOnLadder</c>.</summary>
    public static bool IsOnLadder(IBlockAccess w, PhysicsState s) =>
        Shapes1122.IsClimbable(w, (int)Math.Floor(s.Position.X), (int)Math.Floor(s.BoundingBox.MinY), (int)Math.Floor(s.Position.Z));

    /// <summary><c>Entity.move(MoverType.SELF, x, y, z)</c> for the local player.</summary>
    public static void Move(IBlockAccess w, PhysicsState s, double x, double y, double z)
    {
        var list = _boxes ??= new List<AABB>(32);
        if (s.InWeb)
        {
            s.InWeb = false;
            x *= 0.25;
            y *= 0.05000000074505806;
            z *= 0.25;
            s.Velocity = Vec3d.Zero;
        }
        double f = x, g = y, h = z;
        var box = s.BoundingBox;
        double step = StepHeight;

        if (s.OnGround && s.Sneaking)
        {
            const double d = 0.05;
            while (x != 0 && Empty(w, box.Offset(x, -step, 0), list))
            {
                if (x < d && x >= -d) x = 0; else if (x > 0) x -= d; else x += d;
                f = x;
            }
            while (z != 0 && Empty(w, box.Offset(0, -step, z), list))
            {
                if (z < d && z >= -d) z = 0; else if (z > 0) z -= d; else z += d;
                h = z;
            }
            while (x != 0 && z != 0 && Empty(w, box.Offset(x, -step, z), list))
            {
                if (x < d && x >= -d) x = 0; else if (x > 0) x -= d; else x += d;
                f = x;
                if (z < d && z >= -d) z = 0; else if (z > 0) z -= d; else z += d;
                h = z;
            }
        }

        Shapes1122.GetCollidingBoxes(w, box.Expand(x, y, z), list);
        var start = box;
        if (y != 0)
        {
            foreach (var b in list) y = b.CalculateYOffset(box, y);
            box = box.Offset(0, y, 0);
        }
        if (x != 0)
        {
            foreach (var b in list) x = b.CalculateXOffset(box, x);
            if (x != 0) box = box.Offset(x, 0, 0);
        }
        if (z != 0)
        {
            foreach (var b in list) z = b.CalculateZOffset(box, z);
            if (z != 0) box = box.Offset(0, 0, z);
        }

        bool canStep = s.OnGround || (g != y && g < 0);
        if (canStep && (f != x || h != z))
        {
            double bq = x, o = y, p = z;
            var bb1 = box;
            var steps = _boxes2 ??= new List<AABB>(32);
            Shapes1122.GetCollidingBoxes(w, start.Expand(f, step, h), steps);

            // A: rise as far as the horizontally expanded box allows, then move.
            var bb2 = start;
            var bb3 = bb2.Expand(f, 0, h);
            double q = step;
            foreach (var b in steps) q = b.CalculateYOffset(bb3, q);
            bb2 = bb2.Offset(0, q, 0);
            double bw = f;
            foreach (var b in steps) bw = b.CalculateXOffset(bb2, bw);
            bb2 = bb2.Offset(bw, 0, 0);
            double by = h;
            foreach (var b in steps) by = b.CalculateZOffset(bb2, by);
            bb2 = bb2.Offset(0, 0, by);

            // B: rise as far as the box itself allows, then move.
            var bb4 = start;
            double ca = step;
            foreach (var b in steps) ca = b.CalculateYOffset(bb4, ca);
            var bb5 = bb4.Offset(0, ca, 0);
            double cb = f;
            foreach (var b in steps) cb = b.CalculateXOffset(bb5, cb);
            var bb6 = bb5.Offset(cb, 0, 0);
            double cc = h;
            foreach (var b in steps) cc = b.CalculateZOffset(bb6, cc);
            var bb7 = bb6.Offset(0, 0, cc);

            if (bw * bw + by * by > cb * cb + cc * cc) { x = bw; z = by; y = -q; box = bb2; }
            else { x = cb; z = cc; y = -ca; box = bb7; }
            foreach (var b in steps) y = b.CalculateYOffset(box, y);
            box = box.Offset(0, y, 0);
            if (bq * bq + p * p >= x * x + z * z) { x = bq; y = o; z = p; box = bb1; }
        }

        s.Position = new Vec3d((box.MinX + box.MaxX) / 2.0, box.MinY, (box.MinZ + box.MaxZ) / 2.0);
        s.CollidedHorizontally = f != x || h != z;
        s.CollidedVertically = g != y;
        s.OnGround = s.CollidedVertically && g < 0;

        // The block landed on: under the feet (posY − 0.2), or the fence/wall/gate below an air cell.
        int px = (int)Math.Floor(s.Position.X), pz = (int)Math.Floor(s.Position.Z), py = (int)Math.Floor(s.Position.Y - 0.20000000298023224);
        int landId = w.GetBlock(px, py, pz);
        if (landId == 0)
        {
            int below = w.GetBlock(px, py - 1, pz);
            if (Shapes1122.IsFence(below) || Shapes1122.IsWall(below) || Shapes1122.IsFenceGate(below)) landId = below;
        }

        UpdateFallState(w, s, y, landId);
        var v = s.Velocity;
        if (f != x) v = v with { X = 0 };
        if (h != z) v = v with { Z = 0 };
        if (g != y) v = OnLanded(landId, s.Sneaking, v);
        // canTriggerWalking && !(onGround && sneaking): onEntityWalk (slime slows a walker that isn't bouncing).
        if (s.OnGround && !s.Sneaking && landId == Shapes1122.Slime && Math.Abs(v.Y) < 0.1)
        {
            double d = 0.4 + Math.Abs(v.Y) * 0.2;
            v = new Vec3d(v.X * d, v.Y, v.Z * d);
        }
        s.Velocity = v;
        DoBlockCollisions(w, s);
    }

    private static bool Empty(IBlockAccess w, AABB box, List<AABB> list) => Shapes1122.GetCollidingBoxes(w, box, list).Count == 0;

    /// <summary><c>EntityLivingBase.updateFallState</c> → <c>Entity.updateFallState</c>: water first (it resets the fall), then a
    /// landing applies <c>onFallenUpon</c> → <c>fall(distance, multiplier)</c>.</summary>
    private static void UpdateFallState(IBlockAccess w, PhysicsState s, double y, int landId)
    {
        if (!s.InWater) HandleWaterMovement(w, s);
        if (s.OnGround)
        {
            if (s.FallDistance > 0) s.FallDamage = FallDamage((float)s.FallDistance, landId, s.Sneaking);
            s.FallDistance = 0;
        }
        else if (y < 0) s.FallDistance = (float)(s.FallDistance - y);
    }

    /// <summary>Damage of a landing: <c>ceil((distance − 3) × multiplier)</c> (<c>EntityLivingBase.fall</c>), the multiplier from
    /// <c>onFallenUpon</c>: 1, hay bale 0.2F (<c>BlockHay</c>), bed 0.5 of the distance (<c>BlockBed</c>), slime 0 unless
    /// sneaking (<c>BlockSlime</c>). No jump boost.</summary>
    public static int FallDamage(float distance, int landId, bool sneaking)
    {
        float mult = 1f;
        if (landId == Shapes1122.HayBale) mult = 0.2f;
        else if (landId == Shapes1122.Bed) distance *= 0.5f;
        else if (landId == Shapes1122.Slime && !sneaking) mult = 0f;
        int dmg = (int)MathF.Ceiling((distance - 3.0f - 0f) * mult);
        return dmg > 0 ? dmg : 0;
    }

    /// <summary><c>Block.onLanded</c>: motionY = 0; slime bounces (−motionY), a bed bounces at 0.66, both only falling and not sneaking.</summary>
    private static Vec3d OnLanded(int landId, bool sneaking, Vec3d v)
    {
        if (landId == Shapes1122.Slime && !sneaking) return v.Y < 0 ? v with { Y = -v.Y } : v;
        if (landId == Shapes1122.Bed && !sneaking) return v.Y < 0 ? v with { Y = -v.Y * 0.6600000262260437 } : v;
        return v with { Y = 0 };
    }

    /// <summary><c>Entity.doBlockCollisions</c>: soul sand (×0.4 horizontally) and web in the box shrunk by 0.001.</summary>
    private static void DoBlockCollisions(IBlockAccess w, PhysicsState s)
    {
        var bb = s.BoundingBox;
        int x0 = (int)Math.Floor(bb.MinX + 0.001), x1 = (int)Math.Floor(bb.MaxX - 0.001);
        int y0 = (int)Math.Floor(bb.MinY + 0.001), y1 = (int)Math.Floor(bb.MaxY - 0.001);
        int z0 = (int)Math.Floor(bb.MinZ + 0.001), z1 = (int)Math.Floor(bb.MaxZ - 0.001);
        for (int x = x0; x <= x1; x++)
        for (int y = y0; y <= y1; y++)
        for (int z = z0; z <= z1; z++)
        {
            int id = w.GetBlock(x, y, z);
            if (id == Shapes1122.Web) s.InWeb = true;
            else if (id == Shapes1122.SoulSand) s.Velocity = new Vec3d(s.Velocity.X * 0.4, s.Velocity.Y, s.Velocity.Z * 0.4);
        }
    }

    // ---- liquids ----

    private static void HandleWaterMovement(IBlockAccess w, PhysicsState s)
    {
        var bb = s.BoundingBox.Grow(0, -0.4000000059604645, 0).Grow(-0.001, -0.001, -0.001);
        if (HandleMaterialAcceleration(w, s, bb))
        {
            s.FallDistance = 0;
            s.InWater = true;
        }
        else s.InWater = false;
    }

    /// <summary><c>Entity.isInLava</c>: lava in the box grown −0.1F / −0.4F / −0.1F.</summary>
    public static bool IsInLava(IBlockAccess w, PhysicsState s)
    {
        var bb = s.BoundingBox.Grow(-0.10000000149011612, -0.4000000059604645, -0.10000000149011612);
        bool lava = AnyIn(w, bb, Shapes1122.IsLava);
        s.InLava = lava;
        return lava;
    }

    private static bool AnyIn(IBlockAccess w, AABB bb, Func<int, bool> match)
    {
        int i = (int)Math.Floor(bb.MinX), j = (int)Math.Ceiling(bb.MaxX);
        int k = (int)Math.Floor(bb.MinY), l = (int)Math.Ceiling(bb.MaxY);
        int m = (int)Math.Floor(bb.MinZ), n = (int)Math.Ceiling(bb.MaxZ);
        for (int x = i; x < j; x++)
        for (int y = k; y < l; y++)
        for (int z = m; z < n; z++)
            if (match(w.GetBlock(x, y, z))) return true;
        return false;
    }

    /// <summary><c>World.handleMaterialAcceleration(bb, WATER, player)</c>: true when water reaches into the box; adds the summed
    /// flow, normalised, × 0.014.</summary>
    private static bool HandleMaterialAcceleration(IBlockAccess w, PhysicsState s, AABB bb)
    {
        int i = (int)Math.Floor(bb.MinX), j = (int)Math.Ceiling(bb.MaxX);
        int k = (int)Math.Floor(bb.MinY), l = (int)Math.Ceiling(bb.MaxY);
        int m = (int)Math.Floor(bb.MinZ), n = (int)Math.Ceiling(bb.MaxZ);
        for (int x = i; x < j; x++)
        for (int z = m; z < n; z++)
            if (!w.IsLoaded(x, z)) return false;                               // isAreaLoaded
        bool found = false;
        double fx = 0, fy = 0, fz = 0;
        for (int x = i; x < j; x++)
        for (int y = k; y < l; y++)
        for (int z = m; z < n; z++)
        {
            if (!Shapes1122.IsWater(w.GetBlock(x, y, z))) continue;
            double surface = y + 1 - LiquidHeightPercent(w.GetMeta(x, y, z));
            if (l >= surface)
            {
                found = true;
                var flow = Flow(w, x, y, z);
                fx += flow.X; fy += flow.Y; fz += flow.Z;
            }
        }
        var sum = Normalize(new Vec3d(fx, fy, fz));
        if (Math.Sqrt(fx * fx + fy * fy + fz * fz) > 0)
            s.Velocity = new Vec3d(s.Velocity.X + sum.X * 0.014, s.Velocity.Y + sum.Y * 0.014, s.Velocity.Z + sum.Z * 0.014);
        return found;
    }

    /// <summary><c>BlockLiquid.getLiquidHeightPercent</c>.</summary>
    public static float LiquidHeightPercent(int level)
    {
        if (level >= 8) level = 0;
        return (level + 1) / 9.0f;
    }

    private static int RenderedDepth(IBlockAccess w, int x, int y, int z)
    {
        if (!Shapes1122.IsWater(w.GetBlock(x, y, z))) return -1;
        int m = w.GetMeta(x, y, z);
        return m >= 8 ? 0 : m;
    }

    /// <summary><c>BlockLiquid.getFlow</c> for water (normalised; falling water with a solid side gets −6 down).</summary>
    public static Vec3d Flow(IBlockAccess w, int x, int y, int z)
    {
        double d = 0, f = 0;
        int i = RenderedDepth(w, x, y, z);
        foreach (var face in HorizontalFaces)
        {
            var (dx, dz) = face switch
            {
                Shapes1122.Face.North => (0, -1), Shapes1122.Face.South => (0, 1), Shapes1122.Face.West => (-1, 0), _ => (1, 0),
            };
            int nx = x + dx, nz = z + dz;
            int j = RenderedDepth(w, nx, y, nz);
            if (j < 0)
            {
                if (!BlocksMovement(w.GetBlock(nx, y, nz)))
                {
                    j = RenderedDepth(w, nx, y - 1, nz);
                    if (j >= 0)
                    {
                        int k = j - (i - 8);
                        d += dx * k; f += dz * k;
                    }
                }
            }
            else
            {
                int l = j - i;
                d += dx * l; f += dz * l;
            }
        }
        var vec = new Vec3d(d, 0, f);
        if (w.GetMeta(x, y, z) >= 8)
        {
            foreach (var face in HorizontalFaces)
            {
                var (dx, dz) = face switch
                {
                    Shapes1122.Face.North => (0, -1), Shapes1122.Face.South => (0, 1), Shapes1122.Face.West => (-1, 0), _ => (1, 0),
                };
                if (DownwardCurrent(w, x + dx, y, z + dz, face) || DownwardCurrent(w, x + dx, y + 1, z + dz, face))
                {
                    var n = Normalize(vec);
                    vec = new Vec3d(n.X, n.Y - 6.0, n.Z);
                    break;
                }
            }
        }
        return Normalize(vec);
    }

    /// <summary><c>EnumFacing.Plane.HORIZONTAL</c> order: north, east, south, west.</summary>
    private static readonly Shapes1122.Face[] HorizontalFaces = [Shapes1122.Face.North, Shapes1122.Face.East, Shapes1122.Face.South, Shapes1122.Face.West];

    /// <summary><c>BlockLiquid.causesDownwardCurrent</c> for a horizontal side: not water, not ice, not an attach exception or
    /// stairs, and a SOLID face.</summary>
    private static bool DownwardCurrent(IBlockAccess w, int x, int y, int z, Shapes1122.Face side)
    {
        int id = w.GetBlock(x, y, z);
        if (Shapes1122.IsWater(id)) return false;
        if (id is Shapes1122.Ice or Shapes1122.PackedIce or Shapes1122.FrostedIce) return false;   // Material.ICE / PACKED_ICE
        if (Shapes1122.IsStairs(id)) return false;
        bool exception = id is >= Shapes1122.ShulkerFirst and <= Shapes1122.ShulkerLast or Shapes1122.Leaves or Shapes1122.Leaves2
            or Shapes1122.Trapdoor or Shapes1122.IronTrapdoor or Shapes1122.Beacon or Shapes1122.Cauldron or Shapes1122.Glass
            or Shapes1122.Glowstone or Shapes1122.Ice or Shapes1122.SeaLantern or Shapes1122.StainedGlass or Shapes1122.Piston
            or Shapes1122.StickyPiston or Shapes1122.PistonHead;
        return !exception && Shapes1122.SideSolid(w, id, x, y, z, side);
    }

    /// <summary><c>Material.blocksMovement</c>: false for air, liquids, plants, circuits (torches, rails, redstone, buttons, levers,
    /// tripwire, comparators, end rods), carpets, snow layers, fire, portals, web and structure voids.</summary>
    public static bool BlocksMovement(int id) => id switch
    {
        0 or 6 or (>= 8 and <= 11) or 27 or 28 or 30 or 31 or 32 or (>= 37 and <= 40) or 50 or 51 or 55 or 59 or 65 or 66 or 69 or 75
            or 76 or 77 or 78 or 83 or 90 or 93 or 94 or 104 or 105 or 106 or 111 or 115 or 119 or 127 or 131 or 132 or 141 or 142
            or 143 or 149 or 150 or 157 or 171 or 175 or 198 or 207 or 209 or 217 => false,
        _ => true,
    };

    private static bool IsOffsetPositionInLiquid(IBlockAccess w, PhysicsState s, double dx, double dy, double dz)
    {
        var bb = s.BoundingBox.Offset(dx, dy, dz);
        var list = _boxes ??= new List<AABB>(32);
        if (Shapes1122.GetCollidingBoxes(w, bb, list).Count > 0) return false;
        return !AnyIn(w, bb, Shapes1122.IsLiquid);                             // containsAnyLiquid
    }

    /// <summary><c>Vec3d.normalize</c>: zero below 1.0E-4.</summary>
    private static Vec3d Normalize(Vec3d v)
    {
        double d = Math.Sqrt(v.X * v.X + v.Y * v.Y + v.Z * v.Z);
        return d < 1.0E-4 ? Vec3d.Zero : new Vec3d(v.X / d, v.Y / d, v.Z / d);
    }

    // ---- MathHelper ----

    private static readonly float[] SinTable = BuildSinTable();

    private static float[] BuildSinTable()
    {
        var t = new float[65536];
        for (int i = 0; i < t.Length; i++) t[i] = (float)Math.Sin(i * Math.PI * 2.0 / 65536.0);
        return t;
    }

    /// <summary><c>MathHelper.sin(float)</c>: <c>SIN_TABLE[(int)(value * 10430.378F) &amp; 65535]</c>.</summary>
    public static float Sin(float value) => SinTable[(int)(value * 10430.378f) & 0xFFFF];

    /// <summary><c>MathHelper.cos(float)</c>: <c>SIN_TABLE[(int)(value * 10430.378F + 16384.0F) &amp; 65535]</c>.</summary>
    public static float Cos(float value) => SinTable[(int)(value * 10430.378f + 16384.0f) & 0xFFFF];
}
