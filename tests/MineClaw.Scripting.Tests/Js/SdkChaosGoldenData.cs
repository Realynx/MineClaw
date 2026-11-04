namespace MineClaw.Scripting.Tests.Js;

/// <summary>Golden lines of <see cref="SdkChaosGoldenTests"/>: "scenario#seed calls trace-hash outcome", made before the WP09 SDK refactor.</summary>
internal static class SdkChaosGoldenData
{
    public const string Expected = """"
place.single#1 1 B13CAC74EA17FD1B {"ok":true,"msg":"","n":0}
place.single#2 1 B13CAC74EA17FD1B {"ok":false,"msg":"no known chest","n":0}
place.single#3 1 B13CAC74EA17FD1B {"ok":false,"msg":"","n":0}
place.batch#1 0 E3B0C44298FC1C14 {"ok":true,"msg":"batch: 0/4 placed"}
place.batch#2 9 ECD5B3907EAE0333 {"ok":true,"msg":"batch: 3/4 placed"}
place.batch#3 0 E3B0C44298FC1C14 {"ok":true,"msg":"batch: 0/4 placed"}
place.turned#1 0 E3B0C44298FC1C14 {"ok":true,"msg":"batch: 0/3 placed"}
place.turned#2 9 E416AD70E2245932 {"ok":true,"msg":"batch: 2/3 placed"}
place.turned#3 0 E3B0C44298FC1C14 {"ok":true,"msg":"batch: 0/3 placed"}
place.far#1 1 17E8A3FE29EDF517 {"ok":true,"msg":"batch: 0/2 placed"}
place.far#2 9 BDDD15A5170907CF {"ok":true,"msg":"batch: 2/2 placed"}
place.far#3 1 17E8A3FE29EDF517 {"ok":true,"msg":"batch: 0/2 placed"}
pour.water#1 4 3F7AFB987C0DEA17 {"ok":true,"msg":"batch: 1/3 poured"}
pour.water#2 0 E3B0C44298FC1C14 {"ok":true,"msg":"batch: 1/3 poured; no bucket filled: no water source to fill a bucket from"}
pour.water#3 1 84BAD9F7A2A82AF2 {"ok":true,"msg":"batch: 1/3 poured; no bucket filled: failed"}
pour.lava#1 0 E3B0C44298FC1C14 {"ok":true,"msg":"batch: 1/1 poured"}
pour.lava#2 0 E3B0C44298FC1C14 {"ok":true,"msg":"batch: 1/1 poured"}
pour.lava#3 0 E3B0C44298FC1C14 {"ok":true,"msg":"batch: 1/1 poured"}
pour.nosource#1 2 ABEEB1BD6978E4A1 {"ok":true,"msg":"batch: 0/2 poured"}
pour.nosource#2 0 E3B0C44298FC1C14 {"ok":true,"msg":"batch: 0/2 poured; no bucket filled: no water source to fill a bucket from"}
pour.nosource#3 0 E3B0C44298FC1C14 {"ok":true,"msg":"batch: 0/2 poured; no bucket filled: no water source to fill a bucket from"}
escape.pit#1 2 972973C4F9FAC599 {"ok":false,"stuck":true,"msg":"stuck in a pit at 0,65,0 (unreachable): help is on its way"}
escape.pit#2 2 7179EF3C37881941 {"ok":false,"stuck":true,"msg":"stuck in a pit at 0,65,0 (in reach): help is on its way"}
escape.pit#3 2 972973C4F9FAC599 {"ok":false,"stuck":true,"msg":"stuck in a pit at 0,65,0: help is on its way"}
escape.climb#1 3 57D6A8BC0ACD5F73 {"ok":false,"stuck":true,"msg":"couldn't climb out below home at 0,65,0 (didn't appear)"}
escape.climb#2 2 F6D6C7FFAE7A666B {"ok":false,"stuck":true,"msg":"couldn't climb out below home at 0,65,0 (in reach)"}
escape.climb#3 2 A4B33B31A1BE8FD0 {"ok":false,"stuck":true,"msg":"couldn't climb out below home at 0,65,0"}
escape.flat#1 0 E3B0C44298FC1C14 {"ok":true,"out":false,"msg":"not stuck"}
escape.flat#2 0 E3B0C44298FC1C14 {"ok":true,"out":false,"msg":"not stuck"}
escape.flat#3 0 E3B0C44298FC1C14 {"ok":true,"out":false,"msg":"not stuck"}
go.pit#1 1 7CF638CFF25AA132 {"ok":true,"msg":"","n":0}
go.pit#2 1 7CF638CFF25AA132 {"ok":false,"msg":"no known chest","n":0}
go.pit#3 1 7CF638CFF25AA132 {"ok":false,"msg":"","n":0}
go.high#1 1 CB6A78761A070821 {"ok":true,"msg":"","n":0}
go.high#2 1 CB6A78761A070821 {"ok":false,"msg":"no known chest","n":0}
go.high#3 1 CB6A78761A070821 {"ok":false,"msg":"","n":0}
dig.reach#1 1 1CB34047811DBC4D {"ok":true,"msg":"","n":0}
dig.reach#2 1 1CB34047811DBC4D {"ok":false,"msg":"no known chest","n":0}
dig.reach#3 1 1CB34047811DBC4D {"ok":false,"msg":"","n":0}
dig.plain#1 1 C9D3DF2FAEFDCA4A {"ok":true,"msg":"","n":0}
dig.plain#2 1 C9D3DF2FAEFDCA4A {"ok":false,"msg":"no known chest","n":0}
dig.plain#3 1 C9D3DF2FAEFDCA4A {"ok":false,"msg":"","n":0}
ready#1 1 3AE0CE28326B276F [{"ok":true},{"ok":true}]
ready#2 1 3AE0CE28326B276F [{"ok":true},{"ok":true}]
ready#3 1 3AE0CE28326B276F [{"ok":true},{"ok":true}]
till#1 3 437A08ACAEA46FFB {"ok":false,"msg":"didn't appear","n":0}
till#2 1 0416CE8F12A375E6 {"ok":false,"msg":"no known chest","n":0}
till#3 1 0416CE8F12A375E6 {"ok":false,"msg":"","n":0}
idle#1 3 B2E5C1524ECC7543 {"ok":true,"msg":"stargazing"}
idle#2 1 69F78896A990B3E9 {"ok":false,"msg":"no known chest","n":0}
idle#3 1 69F78896A990B3E9 {"ok":false,"msg":"","n":0}
fetch#1 2 95F6BB2CB9D93AB3 {"ok":false,"msg":"unreachable","n":0}
fetch#2 3 256E44E2B70A5B45 {"ok":false,"msg":"couldn't get closer","n":0}
fetch#3 1 1BF079620ABD1561 {"ok":false,"msg":"","n":0}
work.kinds#1 5 115D5EDA6014562F ["equip={\"ok\":false,\"msg\":\"unreachable\",\"n\":0}","look={\"ok\":false,\"msg\":\"no skill 'peek_container'; use find_skill\",\"n\":0}","dump={\"ok\":false,\"msg\":\"didn't appear\",\"n\":0}","escape={\"ok\":false,\"stuck\":true,\"msg\":\"no way home (got 3): help is on its way\"}","other={\"ok\":true}"]
work.kinds#2 4 4D111147C5C1A0B5 ["equip={\"ok\":false,\"msg\":\"no known chest\",\"n\":0}","look={\"ok\":false,\"msg\":\"no skill 'peek_container'; use find_skill\",\"n\":0}","dump={\"ok\":false,\"msg\":\"in reach\",\"n\":0}","escape={\"ok\":false,\"stuck\":true,\"msg\":\"no way home (claimed): help is on its way\"}","other={\"ok\":true}"]
work.kinds#3 3 AC86689E6EE9A9AE ["equip={\"ok\":false,\"msg\":\"\",\"n\":0}","look={\"ok\":false,\"msg\":\"no skill 'peek_container'; use find_skill\",\"n\":0}","dump={\"ok\":false,\"msg\":\"\",\"n\":0}","escape={\"ok\":true,\"msg\":\"in reach\",\"n\":0}","other={\"ok\":true}"]
forEach#1 3 7E0496EF113B0273 {"ok":true,"n":1,"failed":3,"taken":0,"msg":"1/4 done, 3 failed (last: didn't appear)"}
forEach#2 3 7E0496EF113B0273 {"ok":false,"n":0,"failed":4,"taken":0,"msg":"0/4 done, 4 failed (last: couldn't get closer)"}
forEach#3 3 7E0496EF113B0273 {"ok":true,"n":1,"failed":3,"taken":0,"msg":"1/4 done, 3 failed (last: false)"}
forEach.given#1 2 957641983C97211B {"ok":true,"n":1,"failed":1,"taken":0,"msg":"1/2 done, 1 failed (last: unreachable)"}
forEach.given#2 2 957641983C97211B {"ok":false,"n":0,"failed":2,"taken":0,"msg":"0/2 done, 2 failed (last: in reach)"}
forEach.given#3 2 957641983C97211B {"ok":false,"n":0,"failed":2,"taken":0,"msg":"0/2 done, 2 failed (last: false)"}
forEach.none#1 0 E3B0C44298FC1C14 {"ok":true,"n":0,"failed":0,"taken":0,"msg":"exhausted: no targets; nearest beyond: log 1,2,3 (40 m)","exhausted":true,"beyond":[{"name":"log","x":1,"y":2,"z":3,"d":40}]}
forEach.none#2 0 E3B0C44298FC1C14 {"ok":true,"n":0,"failed":0,"taken":0,"msg":"exhausted: no targets; nearest beyond: log 1,2,3 (40 m)","exhausted":true,"beyond":[{"name":"log","x":1,"y":2,"z":3,"d":40}]}
forEach.none#3 0 E3B0C44298FC1C14 {"ok":true,"n":0,"failed":0,"taken":0,"msg":"exhausted: no targets; nearest beyond: log 1,2,3 (40 m)","exhausted":true,"beyond":[{"name":"log","x":1,"y":2,"z":3,"d":40}]}
gather.default#1 5 0B3F439F6DD0B6CE {"ok":false,"n":0,"have":0,"msg":"cobblestone 0/4 (source: got 3)"}
gather.default#2 5 B63EE8F1654EC5DB {"ok":false,"n":0,"have":3,"msg":"cobblestone 3/4 (chests: no known chest)"}
gather.default#3 5 0B3F439F6DD0B6CE {"ok":false,"n":0,"have":0,"msg":"cobblestone 0/4"}
gather.routes#1 6 F7757B7CF1BA2B16 {"ok":false,"n":0,"have":1,"msg":"dirt 1/3 (mine: got 3; gen: in reach)"}
gather.routes#2 7 EAFA0C9731AEE7B3 {"ok":false,"n":0,"have":2,"msg":"dirt 2/3 (chests: no known chest; gen: from my eyes)"}
gather.routes#3 0 E3B0C44298FC1C14 {"ok":true,"n":0,"have":3,"msg":"have 3 dirt"}
gather.stash#1 2 A1F806AA08A7BB91 {"ok":false,"n":0,"have":0,"msg":"cobblestone 0/5 (gen: unreachable)"}
gather.stash#2 2 70F55874AB693607 {"ok":false,"n":0,"have":3,"msg":"cobblestone 0/5: in reach","full":true}
gather.stash#3 1 3DAE1FEA112E2239 {"ok":false,"n":0,"have":0,"msg":"cobblestone 0/5"}
gather.fn#1 3 E4802C5BF97F8FC0 {"ok":false,"n":0,"have":0,"msg":"log 0/2 (fn: didn't appear)"}
gather.fn#2 3 E4802C5BF97F8FC0 {"ok":false,"n":0,"have":0,"msg":"log 0/2 (fn: couldn't get closer)"}
gather.fn#3 3 E4802C5BF97F8FC0 {"ok":false,"n":0,"have":0,"msg":"log 0/2"}
scout#1 5 150C10244960870F {"ok":false,"found":[],"d":32,"msg":"stuck 32 m out: got 3"}
scout#2 4 009B495A0FCD572C {"ok":false,"found":[],"d":16,"msg":"stuck 16 m out: claimed"}
scout#3 6 F98AE43DCA1F913F {"ok":false,"found":[],"d":48,"msg":"no cow within 48 m that way (195 deg)"}
ensure#1 7 893384BDC2769240 FAILED failed: script t at line 2 (ensure: no pickaxe (not in inventory or chests, can't craft)): 7 acts, 0s
ensure#2 7 893384BDC2769240 FAILED failed: script t at line 2 (ensure: no pickaxe (not in inventory or chests, can't craft)): 7 acts, 0s
ensure#3 7 893384BDC2769240 FAILED failed: script t at line 2 (ensure: no pickaxe (not in inventory or chests, can't craft)): 7 acts, 0s
quota#1 2 8B4495FD0FB8154A {"ok":false,"n":1,"msg":"1/5: unreachable"}
quota#2 1 85FEB9D1AEFA3D65 {"ok":false,"n":0,"msg":"0/5: no known chest"}
quota#3 1 85FEB9D1AEFA3D65 {"ok":false,"n":0,"msg":"0/5: stopped"}
patrol#1 8 2B467E9B7CD822A7 {"ok":true,"n":2,"msg":"reached 2 point(s) (last miss: can't reach)"}
patrol#2 8 2B467E9B7CD822A7 {"ok":true,"n":2,"msg":"reached 2 point(s) (last miss: unreachable)"}
patrol#3 6 D9AC962EFE285701 {"ok":true,"n":3,"msg":"reached 3 point(s) (last miss: got 3)"}
stash#1 2 C01B82656D117D7C [{"ok":true,"n":0,"msg":""},{"ok":false,"n":0,"msg":"unreachable"},{"ok":true,"n":34,"msg":"34 free"}]
stash#2 2 C01B82656D117D7C [{"ok":false,"full":true,"need":2,"n":0,"msg":"no known chest"},{"ok":false,"n":0,"msg":"in reach"},{"ok":true,"n":34,"msg":"34 free"}]
stash#3 2 C01B82656D117D7C [{"ok":false,"n":0,"msg":""},{"ok":false,"n":0,"msg":""},{"ok":true,"n":34,"msg":"34 free"}]
handTo#1 6 CE4718112D61DBEC {"ok":false,"n":0,"msg":"in reach"}
handTo#2 1 378A66123F75AA3C {"ok":false,"n":0,"msg":"no known chest"}
handTo#3 1 378A66123F75AA3C {"ok":false,"n":0,"msg":""}
chopTrees#1 0 E3B0C44298FC1C14 0
chopTrees#2 0 E3B0C44298FC1C14 0
chopTrees#3 0 E3B0C44298FC1C14 0
guardArea#1 0 E3B0C44298FC1C14 0
guardArea#2 0 E3B0C44298FC1C14 0
guardArea#3 0 E3B0C44298FC1C14 0
"""";
}
