using MineClaw.Data;
using MineClaw.World;

namespace MineClaw.Scripting.Tests.Js;

/// <summary>
/// Pins <c>find(query, r, limit)</c> (ScriptSession.FindAll) over blocks, every entity word, mob types, players, dropped items,
/// remembered places, radius and limit. A refactor must leave the output unchanged.
/// </summary>
public sealed class FindGoldenTests
{
    [Fact]
    public async Task Find_matches_the_golden_answers()
    {
        using var rig = Rig.Js();
        rig.World.SetBlock(4, 65, 4, Blocks.Log, 0);
        rig.World.SetBlock(-2, 65, 0, Blocks.Log, 2);
        rig.World.SetBlock(2, 65, -2, Blocks.Crops, 3);
        rig.World.SetBlock(3, 65, -2, Blocks.Crops, 7);
        rig.World.SetBlock(-5, 65, -5, Blocks.Cobblestone, 0);
        rig.World.Entities.Spawn(new Entity(77, EntityKind.Mob, new Vec3d(2.5, 65, 0.5), TypeId: MobTypes.Zombie));
        rig.World.Entities.Spawn(new Entity(78, EntityKind.Mob, new Vec3d(5.5, 65, 0.5), TypeId: MobTypes.Cow));
        rig.World.Entities.Spawn(new Entity(76, EntityKind.Mob, new Vec3d(-8.5, 65, 3.5), TypeId: MobTypes.Skeleton));
        rig.World.Entities.Spawn(new Entity(79, EntityKind.Player, new Vec3d(-3.5, 65, 0.5), Name: "kiwi"));
        rig.World.Entities.Spawn(new Entity(80, EntityKind.Player, new Vec3d(9.5, 65, 9.5), Name: "Robin"));
        rig.World.Entities.Spawn(new Entity(81, EntityKind.Item, new Vec3d(1.5, 65, 1.5), Item: new ItemStack((short)Blocks.Cobblestone, 7, 0)));
        rig.World.Entities.Spawn(new Entity(82, EntityKind.Item, new Vec3d(-1.5, 65, 6.5), Item: new ItemStack(30000, 1, 0)));
        var s = await rig.Run("""
            const qs = [['log'], ['wheat'], ['zombie'], ['hostile'], ['Hostiles'], ['monster'], ['mob'], ['mobs'], ['player'], ['players'],
              ['kiwi'], ['robin'], ['item'], ['drops'], ['home'], ['diamond_ore', { r: 8 }], ['cobblestone'], ['cobblestone', { r: 3 }],
              ['mob', { limit: 1 }], [' log '], ['skeleton', { r: 5 }], ['cow'], ['nothing'], ['']]
            return qs.map(([q, o]) => q + '=' + JSON.stringify(find(q, o))).join('\n')
            """);
        Assert.True(s.Ok, s.Outcome);
        if (Environment.GetEnvironmentVariable("WP09_GOLDEN_OUT") is { Length: > 0 } path) File.WriteAllText(path, s.Result!.ReplaceLineEndings("\n"));
        Assert.Equal(Expected.ReplaceLineEndings("\n"), s.Result!.ReplaceLineEndings("\n"));
    }

    private const string Expected = """"
log=[{"x":-2,"y":65,"z":0,"name":"log","d":2.1,"kind":"block","meta":2},{"x":4,"y":65,"z":4,"name":"log","d":5.7,"kind":"block","meta":0}]
wheat=[{"x":2,"y":65,"z":-2,"name":"wheat","d":2.9,"kind":"block","meta":3},{"x":3,"y":65,"z":-2,"name":"wheat","d":3.6,"kind":"block","meta":7}]
zombie=[{"x":2,"y":65,"z":0,"name":"zombie","d":2,"kind":"mob"}]
hostile=[{"x":2,"y":65,"z":0,"name":"zombie","d":2,"kind":"mob"},{"x":-9,"y":65,"z":3,"name":"skeleton","d":9.5,"kind":"mob"}]
Hostiles=[{"x":2,"y":65,"z":0,"name":"zombie","d":2,"kind":"mob"},{"x":-9,"y":65,"z":3,"name":"skeleton","d":9.5,"kind":"mob"}]
monster=[{"x":2,"y":65,"z":0,"name":"zombie","d":2,"kind":"mob"},{"x":-9,"y":65,"z":3,"name":"skeleton","d":9.5,"kind":"mob"}]
mob=[{"x":2,"y":65,"z":0,"name":"zombie","d":2,"kind":"mob"},{"x":5,"y":65,"z":0,"name":"cow","d":5,"kind":"mob"},{"x":-9,"y":65,"z":3,"name":"skeleton","d":9.5,"kind":"mob"}]
mobs=[{"x":2,"y":65,"z":0,"name":"zombie","d":2,"kind":"mob"},{"x":5,"y":65,"z":0,"name":"cow","d":5,"kind":"mob"},{"x":-9,"y":65,"z":3,"name":"skeleton","d":9.5,"kind":"mob"}]
player=[{"x":-4,"y":65,"z":0,"name":"kiwi","d":4,"kind":"player"},{"x":9,"y":65,"z":9,"name":"Robin","d":12.7,"kind":"player"}]
players=[{"x":-4,"y":65,"z":0,"name":"kiwi","d":4,"kind":"player"},{"x":9,"y":65,"z":9,"name":"Robin","d":12.7,"kind":"player"}]
kiwi=[{"x":-4,"y":65,"z":0,"name":"kiwi","d":4,"kind":"player"}]
robin=[{"x":9,"y":65,"z":9,"name":"Robin","d":12.7,"kind":"player"}]
item=[{"x":1,"y":65,"z":1,"name":"cobblestone","d":1.4,"kind":"item","count":7},{"x":-2,"y":65,"z":6,"name":"item","d":6.3,"kind":"item","count":1}]
drops=[{"x":1,"y":65,"z":1,"name":"cobblestone","d":1.4,"kind":"item","count":7},{"x":-2,"y":65,"z":6,"name":"item","d":6.3,"kind":"item","count":1}]
home=[{"x":5,"y":65,"z":5,"name":"home","d":7.1,"kind":"place"}]
diamond_ore=[]
cobblestone=[{"x":-5,"y":65,"z":-5,"name":"cobblestone","d":7.1,"kind":"block","meta":0}]
cobblestone=[]
mob=[{"x":2,"y":65,"z":0,"name":"zombie","d":2,"kind":"mob"}]
 log =[{"x":-2,"y":65,"z":0,"name":"log","d":2.1,"kind":"block","meta":2},{"x":4,"y":65,"z":4,"name":"log","d":5.7,"kind":"block","meta":0}]
skeleton=[]
cow=[{"x":5,"y":65,"z":0,"name":"cow","d":5,"kind":"mob"}]
nothing=[]
=[]
"""";
}
