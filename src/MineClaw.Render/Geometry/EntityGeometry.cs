using System.Numerics;
using MineClaw.Data;

namespace MineClaw.Render;

/// <summary>One box of a model part (ModelRenderer.addBox): origin + size in model pixels, texture offset, inflate, mirror.</summary>
public readonly record struct ModelBox(float X, float Y, float Z, int W, int H, int D, int U, int V, float Inflate = 0, bool Mirror = false);

/// <summary>A posed model part: pivot (rotationPoint) and angles in radians, applied as GL does (translate, Z, Y, X).</summary>
public sealed class ModelPart(string name, float px, float py, float pz, params ModelBox[] boxes)
{
    public string Name { get; } = name;
    public float Px = px, Py = py, Pz = pz, Rx, Ry, Rz;
    public List<ModelBox> Boxes { get; } = [.. boxes];
    /// <summary>Sheep wool and similar layers: a separate texture and tint for this part.</summary>
    public int Texture = -1;
    public uint Tint;
    public bool Hidden;

    public Matrix4x4 Local => Matrix4x4.CreateRotationX(Rx) * Matrix4x4.CreateRotationY(Ry) * Matrix4x4.CreateRotationZ(Rz) * Matrix4x4.CreateTranslation(Px, Py, Pz);
}

/// <summary>A posed model: parts, its texture, the RenderLiving pre-render scale, shadow size and the entity's height.</summary>
public sealed class EntityModel
{
    public required string Kind { get; init; }
    public required int Texture { get; init; }
    public int TexW { get; init; } = 64;
    public int TexH { get; init; } = 32;
    public List<ModelPart> Parts { get; } = [];
    public Vector3 Scale { get; set; } = Vector3.One;
    public float Shadow { get; init; } = 0.5f;
    public float Height { get; init; } = 1.8f;
    /// <summary>Translucent outer body (slime).</summary>
    public bool Translucent { get; init; }
    public ModelPart this[string name] => Parts.First(p => p.Name == name);
}

/// <summary>
/// Renderer-agnostic entity geometry: the b1.8.1 models (ModelBiped for players, zombies, skeletons, pigmen; ModelCreeper,
/// ModelSpider, ModelPig/Cow/Sheep (ModelQuadruped), ModelChicken, ModelEnderman, ModelSlime, wolves) built from the same
/// part layouts and texture offsets as the client, posed (body yaw, head yaw/pitch from the look packets, walk swing from the
/// speed, sneak) and emitted as textured quads relative to the entity's feet. Dropped items follow RenderItem: block items
/// as a spinning 0.25 cube (render types 0/10/11/13/16), everything else as a camera-facing billboard from items.png or the
/// block's terrain tile, 1-4 copies by stack size, bobbing. World transform = RenderLiving: translate(pos) ·
/// rotateY(180° − bodyYaw) · scale(−1, −1, 1) · preRender scale · translate(0, −1.5078, 0) · part (pixels / 16).
/// </summary>
public static class EntityGeometry
{
    private const float F5 = 1 / 16f;
    private static readonly Vector3 L1 = Vector3.Normalize(new(0.2f, 1f, -0.7f)), L2 = Vector3.Normalize(new(-0.2f, 1f, 0.7f));

    /// <summary>RenderHelper.enableStandardItemLighting: ambient 0.4 + 0.6 × two directional lights.</summary>
    public static float ModelShade(Vector3 n) => Math.Min(1f, 0.4f + 0.6f * (Math.Max(0, Vector3.Dot(n, L1)) + Math.Max(0, Vector3.Dot(n, L2))));

    /// <summary>The model kind for an entity ("player", "zombie", ..., "item"), null = draw nothing (xp orbs, unknown objects).</summary>
    public static string ModelKind(VisionEntity e) => e.Kind switch
    {
        "player" or "hive" or "self" => "player",
        "item" => "item",
        _ => (e.Mob ?? e.Name ?? "zombie").ToLowerInvariant() switch
        {
            "zombie" or "giant" => "zombie",
            "zombie_pigman" or "pigzombie" or "pig_zombie" => "pigzombie",
            "skeleton" => "skeleton",
            "creeper" => "creeper",
            "spider" => "spider",
            "cave_spider" or "cavespider" => "cavespider",
            "pig" => "pig",
            "cow" => "cow",
            "sheep" => "sheep",
            "chicken" => "chicken",
            "enderman" => "enderman",
            "slime" => "slime",
            "wolf" => "wolf",
            "squid" => "squid",
            "ghast" => "ghast",
            "silverfish" => "silverfish",
            var other => other.StartsWith("mob_") ? "zombie" : other,
        },
    };

    // ------------------------------------------------------------------ model layouts (client Model* constructors)

    private static ModelPart P(string n, float x, float y, float z, params ModelBox[] b) => new(n, x, y, z, b);

    /// <summary>A model in its rest pose (texture offsets and boxes as in the b1.8.1 client).</summary>
    public static EntityModel Create(string kind)
    {
        EntityModel m;
        switch (kind)
        {
            case "player":
            case "zombie":
            case "pigzombie":
                m = new EntityModel { Kind = kind, Texture = EntityTextures.Id(kind == "player" ? "char" : kind), Height = 1.8f };
                Biped(m, 4);
                break;
            case "skeleton":
                m = new EntityModel { Kind = kind, Texture = EntityTextures.Id("skeleton"), Height = 1.8f };
                Biped(m, 2);
                break;
            case "enderman":
                m = new EntityModel { Kind = kind, Texture = EntityTextures.Id("enderman"), Height = 2.9f };
                m.Parts.Add(P("head", 0, -13, 0, new ModelBox(-4, -8, -4, 8, 8, 8, 0, 0)));
                m.Parts.Add(P("hat", 0, -13, 0, new ModelBox(-4, -8, -4, 8, 8, 8, 0, 16, -0.5f)));
                m.Parts.Add(P("body", 0, -14, 0, new ModelBox(-4, 0, -2, 8, 12, 4, 32, 16)));
                m.Parts.Add(P("rarm", -3, -12, 0, new ModelBox(-1, -2, -1, 2, 30, 2, 56, 0)));
                m.Parts.Add(P("larm", 5, -12, 0, new ModelBox(-1, -2, -1, 2, 30, 2, 56, 0, 0, true)));
                m.Parts.Add(P("rleg", -2, -5, 0, new ModelBox(-1, 0, -1, 2, 30, 2, 56, 0)));
                m.Parts.Add(P("lleg", 2, -5, 0, new ModelBox(-1, 0, -1, 2, 30, 2, 56, 0, 0, true)));
                break;
            case "creeper":
                m = new EntityModel { Kind = kind, Texture = EntityTextures.Id("creeper"), Height = 1.7f };
                m.Parts.Add(P("head", 0, 4, 0, new ModelBox(-4, -8, -4, 8, 8, 8, 0, 0)));
                m.Parts.Add(P("body", 0, 4, 0, new ModelBox(-4, 0, -2, 8, 12, 4, 16, 16)));
                m.Parts.Add(P("leg1", -2, 16, 4, new ModelBox(-2, 0, -2, 4, 6, 4, 0, 16)));
                m.Parts.Add(P("leg2", 2, 16, 4, new ModelBox(-2, 0, -2, 4, 6, 4, 0, 16)));
                m.Parts.Add(P("leg3", -2, 16, -4, new ModelBox(-2, 0, -2, 4, 6, 4, 0, 16)));
                m.Parts.Add(P("leg4", 2, 16, -4, new ModelBox(-2, 0, -2, 4, 6, 4, 0, 16)));
                break;
            case "spider":
            case "cavespider":
                m = new EntityModel { Kind = kind, Texture = EntityTextures.Id("spider"), Height = 0.9f, Shadow = 1f, Scale = new Vector3(kind == "cavespider" ? 0.7f : 1f) };
                m.Parts.Add(P("head", 0, 15, -3, new ModelBox(-4, -4, -8, 8, 8, 8, 32, 4)));
                m.Parts.Add(P("neck", 0, 15, 0, new ModelBox(-3, -3, -3, 6, 6, 6, 0, 0)));
                m.Parts.Add(P("body", 0, 15, 9, new ModelBox(-5, -4, -6, 10, 8, 12, 0, 12)));
                for (int i = 0; i < 8; i++)
                {
                    bool right = i % 2 == 0;
                    float z = 2 - i / 2;
                    m.Parts.Add(P("leg" + (i + 1), right ? -4 : 4, 15, z, new ModelBox(right ? -15 : -1, -1, -1, 16, 2, 2, 18, 0)));
                }
                break;
            case "pig":
                m = new EntityModel { Kind = kind, Texture = EntityTextures.Id("pig"), Height = 0.9f, Shadow = 0.5f };
                Quadruped(m, 6);
                m["head"].Boxes.Add(new ModelBox(-2, 0, -9, 4, 3, 1, 16, 16));
                break;
            case "cow":
                m = new EntityModel { Kind = kind, Texture = EntityTextures.Id("cow"), Height = 1.3f, Shadow = 0.7f };
                m.Parts.Add(P("head", 0, 4, -8, new ModelBox(-4, -4, -6, 8, 8, 6, 0, 0), new ModelBox(-5, -5, -4, 1, 3, 1, 22, 0), new ModelBox(4, -5, -4, 1, 3, 1, 22, 0)));
                var cb = P("body", 0, 5, 2, new ModelBox(-6, -10, -7, 12, 18, 10, 18, 4), new ModelBox(-2, 2, -8, 4, 6, 1, 52, 0));
                cb.Rx = MathF.PI / 2;
                m.Parts.Add(cb);
                m.Parts.Add(P("leg1", -4, 12, 7, new ModelBox(-2, 0, -2, 4, 12, 4, 0, 16)));
                m.Parts.Add(P("leg2", 4, 12, 7, new ModelBox(-2, 0, -2, 4, 12, 4, 0, 16)));
                m.Parts.Add(P("leg3", -4, 12, -6, new ModelBox(-2, 0, -2, 4, 12, 4, 0, 16)));
                m.Parts.Add(P("leg4", 4, 12, -6, new ModelBox(-2, 0, -2, 4, 12, 4, 0, 16)));
                break;
            case "sheep":
                m = new EntityModel { Kind = kind, Texture = EntityTextures.Id("sheep"), Height = 1.3f, Shadow = 0.7f };
                m.Parts.Add(P("head", 0, 6, -8, new ModelBox(-3, -4, -6, 6, 6, 8, 0, 0)));
                var sb = P("body", 0, 5, 2, new ModelBox(-4, -10, -7, 8, 16, 6, 28, 8));
                sb.Rx = MathF.PI / 2;
                m.Parts.Add(sb);
                for (int i = 0; i < 4; i++)
                    m.Parts.Add(P("leg" + (i + 1), i % 2 == 0 ? -3 : 3, 12, i < 2 ? 7 : -5, new ModelBox(-2, 0, -2, 4, 12, 4, 0, 16)));
                // ModelSheep1: the wool layer (sheep_fur.png), drawn unless sheared.
                var wh = P("woolhead", 0, 6, -8, new ModelBox(-3, -4, -4, 6, 6, 6, 0, 0, 0.6f));
                var wb = P("woolbody", 0, 5, 2, new ModelBox(-4, -10, -7, 8, 16, 6, 28, 8, 1.75f));
                wb.Rx = MathF.PI / 2;
                m.Parts.Add(wh);
                m.Parts.Add(wb);
                for (int i = 0; i < 4; i++)
                    m.Parts.Add(P("woolleg" + (i + 1), i % 2 == 0 ? -3 : 3, 12, i < 2 ? 7 : -5, new ModelBox(-2, 0, -2, 4, 6, 4, 0, 16, 0.5f)));
                foreach (var p in m.Parts.Where(p => p.Name.StartsWith("wool"))) p.Texture = EntityTextures.Id("sheep_fur");
                break;
            case "chicken":
                m = new EntityModel { Kind = kind, Texture = EntityTextures.Id("chicken"), Height = 0.7f, Shadow = 0.3f };
                m.Parts.Add(P("head", 0, 15, -4, new ModelBox(-2, -6, -2, 4, 6, 3, 0, 0), new ModelBox(-2, -4, -4, 4, 2, 2, 14, 0), new ModelBox(-1, -2, -3, 2, 2, 2, 14, 4)));
                var chb = P("body", 0, 16, 0, new ModelBox(-3, -4, -3, 6, 8, 6, 0, 9));
                chb.Rx = MathF.PI / 2;
                m.Parts.Add(chb);
                m.Parts.Add(P("leg1", -2, 19, 1, new ModelBox(-1, 0, -3, 3, 5, 3, 26, 0)));
                m.Parts.Add(P("leg2", 1, 19, 1, new ModelBox(-1, 0, -3, 3, 5, 3, 26, 0)));
                m.Parts.Add(P("wing1", -4, 13, 0, new ModelBox(0, 0, -3, 1, 4, 6, 24, 13)));
                m.Parts.Add(P("wing2", 4, 13, 0, new ModelBox(-1, 0, -3, 1, 4, 6, 24, 13)));
                break;
            case "wolf":
                m = new EntityModel { Kind = kind, Texture = EntityTextures.Id("wolf"), Height = 0.8f, Shadow = 0.5f };
                m.Parts.Add(P("head", -1, 13.5f, -7, new ModelBox(-3, -3, -2, 6, 6, 4, 0, 0), new ModelBox(-3, -5, 0, 2, 2, 1, 16, 14), new ModelBox(1, -5, 0, 2, 2, 1, 16, 14), new ModelBox(-1.5f, 0, -5, 3, 3, 4, 0, 10)));
                var wbod = P("body", 0, 14, 2, new ModelBox(-4, -2, -3, 6, 9, 6, 18, 14));
                wbod.Rx = MathF.PI / 2;
                m.Parts.Add(wbod);
                var mane = P("mane", -1, 14, 2, new ModelBox(-4, -3, -3, 8, 6, 7, 21, 0));
                mane.Rx = MathF.PI / 2;
                m.Parts.Add(mane);
                m.Parts.Add(P("leg1", -2.5f, 16, 7, new ModelBox(-1, 0, -1, 2, 8, 2, 0, 18)));
                m.Parts.Add(P("leg2", 0.5f, 16, 7, new ModelBox(-1, 0, -1, 2, 8, 2, 0, 18)));
                m.Parts.Add(P("leg3", -2.5f, 16, -4, new ModelBox(-1, 0, -1, 2, 8, 2, 0, 18)));
                m.Parts.Add(P("leg4", 0.5f, 16, -4, new ModelBox(-1, 0, -1, 2, 8, 2, 0, 18)));
                var tail = P("tail", -1, 12, 8, new ModelBox(-1, 0, -1, 2, 8, 2, 9, 18));
                tail.Rx = 0.6f;
                m.Parts.Add(tail);
                break;
            case "slime":
                m = new EntityModel { Kind = kind, Texture = EntityTextures.Id("slime"), Height = 0.5f, Shadow = 0.25f, Translucent = true };
                m.Parts.Add(P("inner", 0, 0, 0, new ModelBox(-3, 17, -3, 6, 6, 6, 0, 16), new ModelBox(-3.25f, 18, -3.5f, 2, 2, 2, 32, 0), new ModelBox(1.25f, 18, -3.5f, 2, 2, 2, 32, 4), new ModelBox(0, 21, -3.5f, 1, 1, 1, 32, 8)));
                m.Parts.Add(P("outer", 0, 0, 0, new ModelBox(-4, 16, -4, 8, 8, 8, 0, 0)));
                break;
            case "squid":
                m = new EntityModel { Kind = kind, Texture = EntityTextures.Id("squid"), Height = 0.95f, Shadow = 0.5f };
                m.Parts.Add(P("body", 0, 8, 0, new ModelBox(-6, -8, -6, 12, 16, 12, 0, 0)));
                for (int i = 0; i < 8; i++)
                {
                    double a = i * Math.PI * 2 / 8;
                    var t = P("t" + i, (float)Math.Cos(a) * 5, 15, (float)Math.Sin(a) * 5, new ModelBox(-1, 0, -1, 2, 18, 2, 48, 0));
                    t.Ry = (float)(i * Math.PI * -2 / 8 + Math.PI / 2);
                    m.Parts.Add(t);
                }
                break;
            case "ghast":
                m = new EntityModel { Kind = kind, Texture = EntityTextures.Id("ghast"), Height = 4f, Shadow = 3f, Scale = new Vector3(4.5f) };
                m.Parts.Add(P("body", 0, 8, 0, new ModelBox(-8, -8, -8, 16, 16, 16, 0, 0)));
                for (int i = 0; i < 9; i++)
                    m.Parts.Add(P("t" + i, ((i % 3 - (i / 3 % 2) * 0.5f + 0.25f) / 2f * 2f - 1f) * 5f, 15, (i / 3 / 2f * 2f - 1f) * 5f, new ModelBox(-1, 0, -1, 2, 9, 2, 0, 0)));
                break;
            case "silverfish":
                m = new EntityModel { Kind = kind, Texture = EntityTextures.Id("silverfish"), Height = 0.3f, Shadow = 0.3f };
                m.Parts.Add(P("b0", 0, 22, -3.5f, new ModelBox(-1.5f, 0, -1, 3, 2, 2, 0, 0)));
                m.Parts.Add(P("b1", 0, 21, -1.5f, new ModelBox(-2, 0, -1, 4, 3, 2, 0, 4)));
                m.Parts.Add(P("b2", 0, 20, 1, new ModelBox(-3, 0, -1.5f, 6, 4, 3, 0, 9)));
                m.Parts.Add(P("b3", 0, 21, 4, new ModelBox(-1.5f, 0, -1.5f, 3, 3, 3, 0, 16)));
                break;
            default:
                m = new EntityModel { Kind = kind, Texture = EntityTextures.Id("zombie"), Height = 1.8f };
                Biped(m, 4);
                break;
        }
        return m;
    }

    /// <summary>ModelBiped: head 8³, hat +0.5, body 8×12×4, arms and legs <paramref name="limb"/>×12×limb (4; skeletons 2).</summary>
    private static void Biped(EntityModel m, int limb)
    {
        float h = limb / 2f;
        m.Parts.Add(P("head", 0, 0, 0, new ModelBox(-4, -8, -4, 8, 8, 8, 0, 0)));
        m.Parts.Add(P("hat", 0, 0, 0, new ModelBox(-4, -8, -4, 8, 8, 8, 32, 0, 0.5f)));
        m.Parts.Add(P("body", 0, 0, 0, new ModelBox(-4, 0, -2, 8, 12, 4, 16, 16)));
        m.Parts.Add(P("rarm", -5, 2, 0, new ModelBox(limb == 4 ? -3 : -1, -2, -h, limb, 12, limb, 40, 16)));
        m.Parts.Add(P("larm", 5, 2, 0, new ModelBox(-1, -2, -h, limb, 12, limb, 40, 16, 0, true)));
        m.Parts.Add(P("rleg", -2, 12, 0, new ModelBox(-h, 0, -h, limb, 12, limb, 0, 16)));
        m.Parts.Add(P("lleg", 2, 12, 0, new ModelBox(-h, 0, -h, limb, 12, limb, 0, 16, 0, true)));
    }

    /// <summary>ModelQuadruped(legHeight): head, body rotated 90° about x, four legs.</summary>
    private static void Quadruped(EntityModel m, int i)
    {
        m.Parts.Add(P("head", 0, 18 - i, -6, new ModelBox(-4, -4, -8, 8, 8, 8, 0, 0)));
        var b = P("body", 0, 17 - i, 2, new ModelBox(-5, -10, -7, 10, 16, 8, 28, 8));
        b.Rx = MathF.PI / 2;
        m.Parts.Add(b);
        m.Parts.Add(P("leg1", -3, 24 - i, 7, new ModelBox(-2, 0, -2, 4, i, 4, 0, 16)));
        m.Parts.Add(P("leg2", 3, 24 - i, 7, new ModelBox(-2, 0, -2, 4, i, 4, 0, 16)));
        m.Parts.Add(P("leg3", -3, 24 - i, -5, new ModelBox(-2, 0, -2, 4, i, 4, 0, 16)));
        m.Parts.Add(P("leg4", 3, 24 - i, -5, new ModelBox(-2, 0, -2, 4, i, 4, 0, 16)));
    }

    // ------------------------------------------------------------------ pose (setRotationAngles)

    /// <summary>Body yaw (degrees, MC: 0 south, 90 west): the look yaw, turned toward the walk direction like renderYawOffset.</summary>
    public static float BodyYaw(VisionEntity e)
    {
        float yaw = e.Yaw ?? 0;
        if (e.Speed > 0.02 && (e.Vx != 0 || e.Vz != 0))
        {
            float walk = (float)(Math.Atan2(e.Vz, e.Vx) * 180 / Math.PI) - 90f;
            float d = Wrap(walk - yaw);
            yaw += Math.Clamp(d, -75f, 75f);
        }
        return yaw;
    }

    private static float Wrap(float a) { a %= 360; if (a >= 180) a -= 360; if (a < -180) a += 360; return a; }

    /// <summary>Applies the b1.8.1 setRotationAngles of the model kind (limb swing f, amount f1, age f2, head yaw f3, pitch f4).</summary>
    public static void Pose(EntityModel m, VisionEntity e)
    {
        float body = BodyYaw(e);
        float f3 = Wrap((e.Yaw ?? body) - body), f4 = Math.Clamp(e.Pitch, -90, 90);
        float f1 = (float)Math.Min(1, e.Speed * 4 * 4); // limbYaw ≈ speed·4, settled
        float f = (float)((Math.Abs(e.X) + Math.Abs(e.Z)) * 1.6); // limbSwing phase ≈ distance walked
        float f2 = (float)(e.Seed % 1000);
        const float d2r = MathF.PI / 180f;
        switch (m.Kind)
        {
            case "player" or "zombie" or "pigzombie" or "skeleton" or "enderman":
            {
                var head = m["head"]; var hat = m["hat"]; var bodyP = m["body"];
                var ra = m["rarm"]; var la = m["larm"]; var rl = m["rleg"]; var ll = m["lleg"];
                head.Ry = hat.Ry = f3 * d2r;
                head.Rx = hat.Rx = f4 * d2r;
                ra.Rx = MathF.Cos(f * 0.6662f + MathF.PI) * 2f * f1 * 0.5f;
                la.Rx = MathF.Cos(f * 0.6662f) * 2f * f1 * 0.5f;
                rl.Rx = MathF.Cos(f * 0.6662f) * 1.4f * f1;
                ll.Rx = MathF.Cos(f * 0.6662f + MathF.PI) * 1.4f * f1;
                if (m.Kind == "player" && e.HeldItem > 0) ra.Rx = ra.Rx * 0.5f - MathF.PI / 10f;
                if (m.Kind == "enderman") { ra.Rx *= 0.5f; la.Rx *= 0.5f; rl.Rx = Math.Clamp(rl.Rx * 0.5f, -0.4f, 0.4f); ll.Rx = Math.Clamp(ll.Rx * 0.5f, -0.4f, 0.4f); }
                if (m.Kind is "zombie" or "pigzombie" or "skeleton")
                {
                    // ModelZombie: arms held straight forward.
                    ra.Ry = -0.1f; la.Ry = 0.1f;
                    ra.Rx = la.Rx = -MathF.PI / 2f;
                }
                if (e.Sneaking && m.Kind == "player")
                {
                    bodyP.Rx = 0.5f;
                    ra.Rx += 0.4f; la.Rx += 0.4f;
                    rl.Pz = ll.Pz = 4f;
                    rl.Py = ll.Py = 9f;
                    head.Py = hat.Py = 1f;
                }
                ra.Rz = MathF.Cos(f2 * 0.09f) * 0.05f + 0.05f;
                la.Rz = -(MathF.Cos(f2 * 0.09f) * 0.05f + 0.05f);
                ra.Rx += MathF.Sin(f2 * 0.067f) * 0.05f;
                la.Rx -= MathF.Sin(f2 * 0.067f) * 0.05f;
                break;
            }
            case "creeper":
                m["head"].Ry = f3 * d2r; m["head"].Rx = f4 * d2r;
                m["leg1"].Rx = MathF.Cos(f * 0.6662f) * 1.4f * f1;
                m["leg2"].Rx = MathF.Cos(f * 0.6662f + MathF.PI) * 1.4f * f1;
                m["leg3"].Rx = MathF.Cos(f * 0.6662f + MathF.PI) * 1.4f * f1;
                m["leg4"].Rx = MathF.Cos(f * 0.6662f) * 1.4f * f1;
                break;
            case "spider" or "cavespider":
            {
                m["head"].Ry = f3 * d2r; m["head"].Rx = f4 * d2r;
                const float q = MathF.PI / 4f, s8 = 0.3926991f;
                float[] rz = [-q, q, -q * 0.74f, q * 0.74f, -q * 0.74f, q * 0.74f, -q, q];
                float[] ry = [s8 * 2, -s8 * 2, s8, -s8, -s8, s8, -s8 * 2, s8 * 2];
                float[] sw =
                [
                    -(MathF.Cos(f * 0.6662f * 2f) * 0.4f) * f1, -(MathF.Cos(f * 0.6662f * 2f + MathF.PI) * 0.4f) * f1,
                    -(MathF.Cos(f * 0.6662f * 2f + MathF.PI / 2) * 0.4f) * f1, -(MathF.Cos(f * 0.6662f * 2f + MathF.PI * 1.5f) * 0.4f) * f1,
                ];
                for (int i = 0; i < 8; i++)
                {
                    var leg = m["leg" + (i + 1)];
                    leg.Rz = rz[i];
                    leg.Ry = ry[i] + (i % 2 == 0 ? sw[i / 2] : -sw[i / 2]);
                }
                break;
            }
            case "pig" or "cow" or "sheep":
                m["head"].Ry = f3 * d2r; m["head"].Rx = f4 * d2r;
                for (int i = 1; i <= 4; i++)
                {
                    float a = MathF.Cos(f * 0.6662f + (i is 2 or 3 ? MathF.PI : 0)) * 1.4f * f1;
                    m["leg" + i].Rx = a;
                    if (m.Kind == "sheep") m["woolleg" + i].Rx = a;
                }
                if (m.Kind == "sheep") { m["woolhead"].Ry = m["head"].Ry; m["woolhead"].Rx = m["head"].Rx; }
                break;
            case "chicken":
                m["head"].Ry = f3 * d2r; m["head"].Rx = -f4 * d2r;
                m["leg1"].Rx = MathF.Cos(f * 0.6662f) * 1.4f * f1;
                m["leg2"].Rx = MathF.Cos(f * 0.6662f + MathF.PI) * 1.4f * f1;
                break;
            case "wolf":
                m["head"].Ry = f3 * d2r; m["head"].Rx = f4 * d2r;
                for (int i = 1; i <= 4; i++) m["leg" + i].Rx = MathF.Cos(f * 0.6662f + (i is 2 or 3 ? MathF.PI : 0)) * 1.4f * f1;
                break;
        }
        if (m.Kind == "sheep" && (e.Variant & 16) != 0)
            foreach (var p in m.Parts.Where(p => p.Name.StartsWith("wool"))) p.Hidden = true;
        if (m.Kind == "sheep")
        {
            uint fleece = EntityTextures.Fleece(e.Variant & 15);
            foreach (var p in m.Parts.Where(p => p.Name.StartsWith("wool"))) p.Tint = fleece;
        }
        if (m.Kind == "slime") m.Scale = new Vector3(Math.Max(1, e.Variant));
    }

    // ------------------------------------------------------------------ quads

    /// <summary>The root transform of a model (RenderLiving.doRenderLiving), relative to the feet.</summary>
    public static Matrix4x4 Root(EntityModel m, VisionEntity e)
    {
        float body = BodyYaw(e);
        float sneak = e.Sneaking && m.Kind == "player" ? -0.125f : 0;
        var pre = m.Kind == "player" ? new Vector3(0.9375f) : m.Scale;
        return Matrix4x4.CreateTranslation(0, -24 * F5 - 0.0078125f, 0) * Matrix4x4.CreateScale(pre) * Matrix4x4.CreateScale(-1, -1, 1)
            * Matrix4x4.CreateRotationY((180f - body) * MathF.PI / 180f) * Matrix4x4.CreateTranslation(0, sneak, 0);
    }

    /// <summary>Emits a posed model's quads (relative to the feet; <see cref="QuadTex.Entity"/>) into <paramref name="dst"/>.</summary>
    public static void Emit(EntityModel m, Matrix4x4 root, List<GeoQuad> dst)
    {
        foreach (var part in m.Parts)
        {
            if (part.Hidden) continue;
            var xf = part.Local * Matrix4x4.CreateScale(F5) * root;
            int tex = part.Texture >= 0 ? part.Texture : m.Texture;
            bool trans = m.Translucent && part.Name == "outer";
            foreach (var b in part.Boxes) EmitBox(b, xf, tex, m.TexW, m.TexH, part.Tint, trans, dst);
        }
    }

    /// <summary>ModelBox's six TexturedQuads (vertex order and UV rectangles as the client builds them; mirror flips).</summary>
    public static void EmitBox(ModelBox b, Matrix4x4 xf, int tex, int tw, int th, uint tint, bool translucent, List<GeoQuad> dst)
    {
        float x0 = b.X - b.Inflate, y0 = b.Y - b.Inflate, z0 = b.Z - b.Inflate;
        float x1 = b.X + b.W + b.Inflate, y1 = b.Y + b.H + b.Inflate, z1 = b.Z + b.D + b.Inflate;
        if (b.Mirror) (x0, x1) = (x1, x0);
        Span<Vector3> v =
        [
            new(x0, y0, z0), new(x1, y0, z0), new(x1, y1, z0), new(x0, y1, z0),
            new(x0, y0, z1), new(x1, y0, z1), new(x1, y1, z1), new(x0, y1, z1),
        ];
        int u = b.U, vv = b.V, w = b.W, h = b.H, d = b.D;
        Face4(v[5], v[1], v[2], v[6], u + d + w, vv + d, u + d + w + d, vv + d + h);
        Face4(v[0], v[4], v[7], v[3], u, vv + d, u + d, vv + d + h);
        Face4(v[5], v[4], v[0], v[1], u + d, vv, u + d + w, vv + d);
        Face4(v[2], v[3], v[7], v[6], u + d + w, vv, u + d + w + w, vv + d);
        Face4(v[1], v[0], v[3], v[2], u + d, vv + d, u + d + w, vv + d + h);
        Face4(v[4], v[5], v[6], v[7], u + d + w + d, vv + d, u + d + w + d + w, vv + d + h);

        void Face4(Vector3 a, Vector3 bb, Vector3 c, Vector3 dd, float u1, float v1, float u2, float v2)
        {
            // TexturedQuad: vertex 0 (u2, v1), 1 (u1, v1), 2 (u1, v2), 3 (u2, v2); mirror reverses the vertex order.
            Vector2 ta = new(u2 / tw, v1 / th), tb = new(u1 / tw, v1 / th), tc = new(u1 / tw, v2 / th), td = new(u2 / tw, v2 / th);
            var q = b.Mirror
                ? GeoQuad.Make(Vector3.Transform(dd, xf), Vector3.Transform(c, xf), Vector3.Transform(bb, xf), Vector3.Transform(a, xf), td, tc, tb, ta)
                : GeoQuad.Make(Vector3.Transform(a, xf), Vector3.Transform(bb, xf), Vector3.Transform(c, xf), Vector3.Transform(dd, xf), ta, tb, tc, td);
            q.Tex = QuadTex.Entity; q.Tile = (short)tex; q.Tint = tint; q.Pass = translucent ? RenderPass.Translucent : RenderPass.Cutout;
            q.Alpha = translucent ? 0.55f : 0; q.Flat = 0x808080;
            // Outward normals: the root's scale(−1, −1, 1) has determinant +1 and a mirrored box reverses its vertices.
            q.Shade = ModelShade(q.Normal);
            q.TwoSided = true;
            dst.Add(q);
        }
    }

    /// <summary>The full posed geometry of an entity (relative to its feet), or nothing for kinds without a model.
    /// <paramref name="viewX"/>/<paramref name="viewZ"/>: the camera, for item billboards.</summary>
    /// <param name="viewY">The camera height when known: a camera high above an item (a top-down map) gets the billboard laid
    /// flat instead of edge-on.</param>
    public static void Build(VisionEntity e, List<GeoQuad> dst, double viewX, double viewZ, TextureAtlas? atlas = null, double viewY = double.NaN)
    {
        var kind = ModelKind(e);
        if (kind == "item") { Item(e, dst, viewX, viewZ, viewY); return; }
        var m = Create(kind);
        Pose(m, e);
        var root = Root(m, e);
        Emit(m, root, dst);
        if (kind == "player" && e.HeldItem > 0) HeldItem(m, root, e.HeldItem, dst);
        if (e.Burning) Fire(e, kind, dst, viewX, viewZ);
    }

    /// <summary>Entity width (EntityLiving.setSize) for the fire layers.</summary>
    public static float Width(string kind, int variant) => kind switch
    {
        "spider" => 1.4f, "cavespider" => 0.7f, "pig" or "cow" or "sheep" => 0.9f, "chicken" => 0.3f, "slime" => 0.6f * Math.Max(1, variant),
        "ghast" => 4f, "wolf" => 0.8f, "squid" => 0.95f, "silverfish" => 0.3f, _ => 0.6f,
    };

    /// <summary>
    /// Render.renderEntityOnFire: camera-facing fire quads (tiles 31/47 alternating, every other pair mirrored) 1.4·width wide,
    /// stacked 0.45·scale apart up the entity, each 0.9× narrower and 0.03 nearer the camera.
    /// </summary>
    private static void Fire(VisionEntity e, string kind, List<GeoQuad> dst, double viewX, double viewZ)
    {
        var model = Create(kind);
        float f5 = Width(kind, e.Variant) * 1.4f;
        float f6 = 0.5f, f8 = model.Height / f5, f9 = 0, f10 = 0;
        double dx = viewX - e.X, dz = viewZ - e.Z;
        var rot = Matrix4x4.CreateRotationY((float)Math.Atan2(dx, dz));
        float push = 0.3f - (int)f8 * 0.02f;
        for (int l = 0; f8 > 0 && l < 12; l++)
        {
            bool mirror = (l / 2) % 2 == 0;
            Vector3 P(float x, float y) => Vector3.Transform(new Vector3(x * f5, y * f5, (push + f10) * f5), rot);
            var q = GeoQuad.Make(P(f6, -f9), P(-f6, -f9), P(-f6, 1.4f - f9), P(f6, 1.4f - f9),
                new(mirror ? 0 : 1, 1), new(mirror ? 1 : 0, 1), new(mirror ? 1 : 0, 0), new(mirror ? 0 : 1, 0));
            q.Tex = QuadTex.Fire; q.Tile = (short)(l % 2); q.Pass = RenderPass.Cutout; q.Emissive = true; q.TwoSided = true; q.Flat = 0xff9a20;
            dst.Add(q);
            f8 -= 0.45f; f9 -= 0.45f; f6 *= 0.9f; f10 += 0.03f;
        }
    }

    /// <summary>The held item as a small icon in the right hand (arm space, pointing forward like RenderPlayer's item pose).</summary>
    private static void HeldItem(EntityModel m, Matrix4x4 root, int itemId, List<GeoQuad> dst)
    {
        var arm = m["rarm"];
        var xf = arm.Local * Matrix4x4.CreateScale(F5) * root;
        // A 10×10 px icon in the arm's y-z plane, the hand at its upper-back corner.
        Vector3 a = new(-1, 8, -8), b = new(-1, 18, -8), c = new(-1, 18, 2), d = new(-1, 8, 2);
        var (tex, tile) = ItemIcons.Of(itemId, 0);
        var q = GeoQuad.Make(Vector3.Transform(a, xf), Vector3.Transform(b, xf), Vector3.Transform(c, xf), Vector3.Transform(d, xf),
            new(0, 0), new(0, 1), new(1, 1), new(1, 0));
        q.Tex = tex; q.Tile = (short)tile; q.Pass = RenderPass.Cutout; q.TwoSided = true; q.Shade = 1; q.Flat = ItemIcons.Colour(itemId);
        q.Tint = tex == QuadTex.Terrain ? BlockTextures.For(itemId, 0, Face.North).Tint : 0;
        dst.Add(q);
    }

    /// <summary>RenderItem copies: 1, 2 (count &gt; 1), 3 (&gt; 5), 4 (&gt; 20).</summary>
    public static int Copies(int count) => count > 20 ? 4 : count > 5 ? 3 : count > 1 ? 2 : 1;

    /// <summary>RenderItem.renderItemIn3d(renderType): standard cube, stairs, fence, cactus, piston.</summary>
    public static bool ItemIn3d(int id) => id is > 0 and < 256 && BlockGeometry.RenderType(id) is 0 or 10 or 11 or 13 or 16;

    /// <summary>The bob (0..0.2) and spin (degrees) of a dropped item: sin((age)/10 + hover)·0.1 + 0.1, (age/20 + hover)·57.3.</summary>
    public static (float Bob, float Spin) ItemMotion(VisionEntity e)
    {
        float hover = (float)((e.Seed * 2654435761L & 0xffff) / 65535.0 * Math.PI * 2);
        float age = (float)(e.Age ?? (e.Seed * 37 % 600));
        return (MathF.Sin(age / 10f + hover) * 0.1f + 0.1f, (age / 20f + hover) * (180f / MathF.PI));
    }

    private static void Item(VisionEntity e, List<GeoQuad> dst, double viewX, double viewZ, double viewY = double.NaN)
    {
        int id = e.ItemId, count = Math.Max(1, e.ItemCount);
        var (bob, spin) = ItemMotion(e);
        var rnd = new Random(187 + (int)(e.Seed & 0xffff));
        int n = Copies(count);
        if (ItemIn3d(id))
        {
            var tmp = new List<GeoQuad>();
            var cube = new SparseVoxels().Set(0, 64, 0, id, e.ItemDamage);
            BlockGeometry.Build(cube, 0, 64, 0, tmp);
            for (int c = 0; c < n; c++)
            {
                var jitter = c == 0 ? Vector3.Zero : new Vector3((rnd.NextSingle() * 2 - 1) * 0.2f, (rnd.NextSingle() * 2 - 1) * 0.2f, (rnd.NextSingle() * 2 - 1) * 0.2f);
                var xf = Matrix4x4.CreateTranslation(new Vector3(-0.5f) + jitter) * Matrix4x4.CreateScale(0.25f)
                    * Matrix4x4.CreateRotationY(spin * MathF.PI / 180f) * Matrix4x4.CreateTranslation(0, 0.125f + bob, 0);
                foreach (var q0 in tmp)
                {
                    var q = q0;
                    q.P0 = Vector3.Transform(q.P0, xf); q.P1 = Vector3.Transform(q.P1, xf); q.P2 = Vector3.Transform(q.P2, xf); q.P3 = Vector3.Transform(q.P3, xf);
                    q.Shade = ModelShade(q.Normal);
                    q.LightFace = -1; q.TwoSided = true;
                    dst.Add(q);
                }
            }
            return;
        }
        // Billboard: 0.5 wide, −0.125..0.375 about the centre, turned toward the camera around y (RenderItem 180 − playerViewY).
        var (tex, tile) = ItemIcons.Of(id, e.ItemDamage);
        double dx = viewX - e.X, dz = viewZ - e.Z;
        float ang = (float)Math.Atan2(dx, dz);
        var rot = Matrix4x4.CreateRotationY(ang);
        // Seen from high above (a top-down map): lie flat, facing up, so the icon isn't edge-on.
        if (!double.IsNaN(viewY) && viewY - e.Y > 3 * Math.Max(0.5, Math.Sqrt(dx * dx + dz * dz)))
            rot = Matrix4x4.CreateRotationX(-MathF.PI / 2) * Matrix4x4.CreateTranslation(0, -0.1f, 0.125f) * Matrix4x4.CreateRotationY(MathF.PI);
        for (int c = 0; c < n; c++)
        {
            var off = c == 0 ? Vector3.Zero : new Vector3((rnd.NextSingle() * 2 - 1) * 0.15f, (rnd.NextSingle() * 2 - 1) * 0.15f, (rnd.NextSingle() * 2 - 1) * 0.15f);
            Vector3 P(float x, float y) => Vector3.Transform(new Vector3(x, y, 0) + off, rot) + new Vector3(0, 0.125f + bob, 0);
            var q = GeoQuad.Make(P(-0.25f, 0.375f), P(-0.25f, -0.125f), P(0.25f, -0.125f), P(0.25f, 0.375f), new(0, 0), new(0, 1), new(1, 1), new(1, 0));
            q.Tex = tex; q.Tile = (short)tile; q.Pass = RenderPass.Cutout; q.TwoSided = true; q.Shade = 1; q.Flat = ItemIcons.Colour(id);
            q.Tint = tex == QuadTex.Terrain ? BlockTextures.For(id, e.ItemDamage, Face.North).Tint : 0;
            dst.Add(q);
        }
    }

    /// <summary>Shadow radius (Render.shadowSize) of an entity's model.</summary>
    public static float ShadowSize(VisionEntity e)
    {
        var k = ModelKind(e);
        if (k == "item") return 0.15f;
        return k switch { "spider" => 1f, "cavespider" => 0.7f, "cow" or "sheep" => 0.7f, "chicken" => 0.3f, "slime" => 0.25f * Math.Max(1, e.Variant), "ghast" => 3f, "silverfish" => 0.3f, _ => 0.5f };
    }
}
