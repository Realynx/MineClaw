using System.Security.Cryptography;
using System.Text;
using MineClaw.Agent.Llm;
using MineClaw.Bot.Game;
using MineClaw.Client;

namespace MineClaw.Bot.Tests;

/// <summary>
/// Pins the tool surface the model sees (WP03 refactor): every tool's name, description and input schema, per tier, for a
/// bot on the fake client and one on a real (unconnected) <see cref="MinecraftGameClient"/> (which adds the combat/tree/...
/// tools). One hash per tool: names, schemas and descriptions are in the cached prompt prefix, so they must stay byte-identical.
/// Set WP03_GOLDEN_OUT to a directory to write the dumps out (to diff after a change).
/// </summary>
public class ToolSurfaceGoldenTests
{
    private static List<string> Dump(Agent.Tools.ToolRegistry tools, string label)
    {
        var lines = new List<string>();
        foreach (var tier in new ModelTier?[] { null, ModelTier.Main, ModelTier.Worker, ModelTier.Cheap })
        {
            var defs = tier is { } t ? tools.Definitions(t) : tools.Definitions();
            lines.Add($"{label} {tier?.ToString() ?? "all"}: " + string.Join(",", defs.Select(d => d.Name)));
            if (tier != null) continue;
            foreach (var d in defs.OrderBy(d => d.Name, StringComparer.Ordinal))
            {
                var text = (d.Name + "\n" + d.Description + "\n" + d.InputSchema.GetRawText()).ReplaceLineEndings("\n");
                lines.Add($"{label} {d.Name} {Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..16]}");
            }
        }
        return lines;
    }

    private static List<string> Actual()
    {
        var actual = new List<string>();
        var (host, _) = TestBot.Create();
        using (host) actual.AddRange(Dump(host.Runtime.Tools, "fake"));
        var client = new MinecraftClient(new ClientOptions { Host = "127.0.0.1", Port = 1, Username = "maid_chan", AutoReconnect = false });
        using (var real = BotHost.Create(new MinecraftGameClient(client), TestBot.Options(), null, new NoLlm(), persist: false))
            actual.AddRange(Dump(real.Runtime.Tools, "real"));
        return actual;
    }

    [Fact]
    public void Tool_names_descriptions_and_schemas_match_the_golden_hashes()
    {
        var actual = Actual();
        var outDir = Environment.GetEnvironmentVariable("WP03_GOLDEN_OUT");
        if (outDir is { Length: > 0 }) { File.WriteAllLines(Path.Combine(outDir, "tool-surface.txt"), actual); return; }
        Assert.Equal(Expected, actual);
    }

    /// <summary>Generated from the pre-refactor code (WP03).</summary>
    private static readonly string[] Expected =
    [
        "fake all: stop,remember_waypoint,mark_structure,describe_structure,remember_note,recall,recall_detail,forget,list_memory,where_is,identity,save_routine,run_routine,ask_player,standing_order,reflect_self,run_command,delegate,amend_task,ask_memory,say,activity,status,inventory,look,window,goto,follow,mine,harvest_generator,collect_drops,craft,smelt,collect_furnace,deposit,withdraw,organize_chest,place,dig,use_block,eat,chop_tree,fish,place_turned,acquire,equip,face,escape_dig,clear_dig,dig_here,place_here,clear_temporary,ask_stand,watch_demo,act,find_skill,skill,design,build,blueprint,script,see,do",
        "fake acquire 4B46011A8047BAAF",
        "fake act 1BA285CCCBFEF603",
        "fake activity 18EE0AA639868F61",
        "fake amend_task 9F38A19FE434015B",
        "fake ask_memory 04319FFB0B80E49B",
        "fake ask_player 0A6D6E3E40EC9601",
        "fake ask_stand 8277B3B56DCABF32",
        "fake blueprint C03FB94D335FFA41",
        "fake build 04A92DD69D86ADF5",
        "fake chop_tree EFAC330C669F8EF1",
        "fake clear_dig 751257EA757103F5",
        "fake clear_temporary F23796438E7A0E69",
        "fake collect_drops B86180B1A068B8E7",
        "fake collect_furnace 4CA7F287AA901331",
        "fake craft E9B7296EB5195028",
        "fake delegate D793BCB882B3F26C",
        "fake deposit C3CD41D1C9777DC7",
        "fake describe_structure 1FFAFC662DFB3F82",
        "fake design 24457C7223BAFEF5",
        "fake dig EA150A0C89548BAB",
        "fake dig_here DFC379F30DB5A664",
        "fake do 0D3CAD0B7F412A47",
        "fake eat 565E1C34C2B9E24A",
        "fake equip 767FD6058FDF2144",
        "fake escape_dig 35FB925490BECAFD",
        "fake face 0FD7E32F52DBFC37",
        "fake find_skill F1550F612BC0D4D3",
        "fake fish A7EA59ABFC2C1371",
        "fake follow B867359B33F7BCD5",
        "fake forget 0F8F9A611928FB78",
        "fake goto AB27C58717A3ED33",
        "fake harvest_generator 2BE22D528BD5ABFA",
        "fake identity 1C7545AEA58A5B49",
        "fake inventory C0DB831CD067C2B2",
        "fake list_memory 1E13ACEC1472129A",
        "fake look F9C32908C4DE9484",
        "fake mark_structure A8B0040F0751856A",
        "fake mine 971B818C318384AE",
        "fake organize_chest 0B09CD45DBA7F361",
        "fake place 68221B5FE1C0D129",
        "fake place_here FDF3816820B31806",
        "fake place_turned BB7D6F1C4EDCDFDE",
        "fake recall BF25D6DF3001FE96",
        "fake recall_detail DDFDC32D3F178FF9",
        "fake reflect_self E37552F7AE8CF71A",
        "fake remember_note BB0E3B9B6CF2558F",
        "fake remember_waypoint 33A7AECDFC1EC868",
        "fake run_command 882DE5AEB2331A4C",
        "fake run_routine 20940BC752A0AD08",
        "fake save_routine A994D1F6DDB7FAFF",
        "fake say D142B0BA6B958ABB",
        "fake script 9D878F9971CFAC30",
        "fake see F1E2250F1B7D5654",
        "fake skill A4DE0E3C2A0DFAE9",
        "fake smelt 49F64632D9D29656",
        "fake standing_order 3CEA6D5B0AC22DFE",
        "fake status 71ED84A2E459F946",
        "fake stop 8BF6000859C0318B",
        "fake use_block A2041DA14685835C",
        "fake watch_demo 98193487B8FA3C2E",
        "fake where_is 9215C721EB08FC42",
        "fake window 8933996305DFE389",
        "fake withdraw E29870B9F3B0EF7D",
        "fake Main: stop,remember_waypoint,mark_structure,describe_structure,remember_note,recall_detail,forget,list_memory,where_is,identity,run_routine,ask_player,standing_order,reflect_self,run_command,delegate,amend_task,ask_memory,say,status,inventory,look,ask_stand,watch_demo,design,script,see,do",
        "fake Worker: remember_waypoint,recall,where_is,save_routine,run_routine,status,inventory,look,window,goto,mine,harvest_generator,collect_drops,craft,smelt,collect_furnace,deposit,withdraw,organize_chest,place,dig,use_block,eat,chop_tree,find_skill,skill,build,script,see",
        "fake Cheap: stop,remember_waypoint,remember_note,recall,recall_detail,list_memory,where_is,identity,reflect_self,activity,status,inventory,look",
        "real all: stop,remember_waypoint,mark_structure,describe_structure,remember_note,recall,recall_detail,forget,list_memory,where_is,identity,save_routine,run_routine,ask_player,standing_order,reflect_self,run_command,delegate,amend_task,ask_memory,say,activity,status,inventory,look,window,goto,follow,mine,harvest_generator,collect_drops,craft,smelt,collect_furnace,deposit,withdraw,organize_chest,place,dig,use_block,eat,chop_tree,fish,place_turned,acquire,equip,face,escape_dig,clear_dig,dig_here,place_here,clear_temporary,ask_stand,watch_demo,spar,stop_spar,defend,guard,attack,combat_status,act,find_skill,skill,design,build,blueprint,script,see,do",
        "real acquire 4B46011A8047BAAF",
        "real act 1BA285CCCBFEF603",
        "real activity 18EE0AA639868F61",
        "real amend_task 9F38A19FE434015B",
        "real ask_memory 04319FFB0B80E49B",
        "real ask_player 0A6D6E3E40EC9601",
        "real ask_stand 8277B3B56DCABF32",
        "real attack 871C435DE863A8C5",
        "real blueprint C03FB94D335FFA41",
        "real build 04A92DD69D86ADF5",
        "real chop_tree EFAC330C669F8EF1",
        "real clear_dig 751257EA757103F5",
        "real clear_temporary F23796438E7A0E69",
        "real collect_drops B86180B1A068B8E7",
        "real collect_furnace 4CA7F287AA901331",
        "real combat_status F0B22334B3F58643",
        "real craft E9B7296EB5195028",
        "real defend 6F1EB02052CBD7E0",
        "real delegate D793BCB882B3F26C",
        "real deposit C3CD41D1C9777DC7",
        "real describe_structure 1FFAFC662DFB3F82",
        "real design 24457C7223BAFEF5",
        "real dig EA150A0C89548BAB",
        "real dig_here DFC379F30DB5A664",
        "real do 0D3CAD0B7F412A47",
        "real eat 565E1C34C2B9E24A",
        "real equip 767FD6058FDF2144",
        "real escape_dig 35FB925490BECAFD",
        "real face 0FD7E32F52DBFC37",
        "real find_skill F1550F612BC0D4D3",
        "real fish A7EA59ABFC2C1371",
        "real follow B867359B33F7BCD5",
        "real forget 0F8F9A611928FB78",
        "real goto AB27C58717A3ED33",
        "real guard E536F203010E7E02",
        "real harvest_generator 2BE22D528BD5ABFA",
        "real identity 1C7545AEA58A5B49",
        "real inventory 3C0917893E5B6B4C",
        "real list_memory 1E13ACEC1472129A",
        "real look F9C32908C4DE9484",
        "real mark_structure A8B0040F0751856A",
        "real mine 971B818C318384AE",
        "real organize_chest 0B09CD45DBA7F361",
        "real place 68221B5FE1C0D129",
        "real place_here FDF3816820B31806",
        "real place_turned BB7D6F1C4EDCDFDE",
        "real recall BF25D6DF3001FE96",
        "real recall_detail DDFDC32D3F178FF9",
        "real reflect_self E37552F7AE8CF71A",
        "real remember_note BB0E3B9B6CF2558F",
        "real remember_waypoint 33A7AECDFC1EC868",
        "real run_command 882DE5AEB2331A4C",
        "real run_routine 20940BC752A0AD08",
        "real save_routine A994D1F6DDB7FAFF",
        "real say D142B0BA6B958ABB",
        "real script 9D878F9971CFAC30",
        "real see F1E2250F1B7D5654",
        "real skill A4DE0E3C2A0DFAE9",
        "real smelt 49F64632D9D29656",
        "real spar D27A068000C5CFEA",
        "real standing_order 3CEA6D5B0AC22DFE",
        "real status 71ED84A2E459F946",
        "real stop 8BF6000859C0318B",
        "real stop_spar 65CAA297CD95E0D0",
        "real use_block A2041DA14685835C",
        "real watch_demo 98193487B8FA3C2E",
        "real where_is 9215C721EB08FC42",
        "real window 8933996305DFE389",
        "real withdraw E29870B9F3B0EF7D",
        "real Main: stop,remember_waypoint,mark_structure,describe_structure,remember_note,recall_detail,forget,list_memory,where_is,identity,run_routine,ask_player,standing_order,reflect_self,run_command,delegate,amend_task,ask_memory,say,status,inventory,look,ask_stand,watch_demo,design,script,see,do",
        "real Worker: remember_waypoint,recall,where_is,save_routine,run_routine,status,inventory,look,window,goto,mine,harvest_generator,collect_drops,craft,smelt,collect_furnace,deposit,withdraw,organize_chest,place,dig,use_block,eat,chop_tree,spar,stop_spar,defend,guard,attack,combat_status,find_skill,skill,build,script,see",
        "real Cheap: stop,remember_waypoint,remember_note,recall,recall_detail,list_memory,where_is,identity,reflect_self,activity,status,inventory,look,combat_status",
    ];
}
