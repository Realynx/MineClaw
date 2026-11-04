namespace MineClaw.Scripting.Tests;

/// <summary>The golden dump of <see cref="StoryScanGoldenTests"/> (generated from the pre-refactor code).</summary>
internal static class StoryScanGoldenData
{
    public const string Expected = """
@ -6,69,-6
  pit  {"feet":"-6,69,-6","pit":false,"feet_y":69}
  home {"feet":"-6,69,-6","pit":false,"feet_y":69}
  clmb {"feet":"-6,69,-6","pit":true,"feet_y":69,"climb":true,"approach":"2,66,-6","need":null,"stair":null,"pillar":true,"block":null,"blocks":0,"gather":[],"toward":"9,67,0"}
  reach 0,4,0 {"within":true,"block":null,"blocks":0}
  reach 1,7,0 {"within":false,"block":null,"blocks":0,"stand":"-6,69,-6","steps":3,"gather":[]}
  reach 0,12,1 {"within":false,"block":null,"blocks":0,"stand":"-6,69,-6","steps":8,"gather":[]}
  reach 2,30,-1 {"within":false,"block":null,"blocks":0,"why":"no ground to stand on below or beside it within 24 blocks"}
  reach 0,2,0 {"within":true,"block":null,"blocks":0}
  reach 3,6,0 {"within":false,"block":null,"blocks":0,"stand":"-3,67,-6","steps":3,"gather":["-5,66,-9","-5,66,-7","-5,66,-3","-4,66,-8","-4,66,-5","-3,66,-8","-3,66,-7","-2,66,-6","-1,66,-9","-1,66,-3","0,66,-5"]}
  reach -2,9,2 {"within":false,"block":null,"blocks":0,"stand":"-6,69,-6","steps":6,"gather":[]}
@ -6,65,-4
  pit  {"feet":"-6,65,-4","pit":false,"feet_y":65}
  home {"feet":"-6,65,-4","pit":false,"feet_y":65}
  clmb {"feet":"-6,65,-4","pit":true,"feet_y":65,"climb":true,"approach":"1,65,-3","need":0,"stair":{"dig":["-5,66,-4"],"to":"-5,66,-4"},"pillar":true,"block":null,"blocks":0,"gather":["-7,65,-4","-6,65,-3"],"toward":"9,67,0"}
  reach 0,4,0 {"within":true,"block":null,"blocks":0}
  reach 1,7,0 {"within":false,"block":null,"blocks":0,"stand":"-5,67,-4","steps":0,"gather":["-8,66,-6","-5,66,-7","-5,66,-3","-5,66,-2","-4,66,-5","-3,66,-7","-3,66,-2","-3,66,-1","-2,66,-6","-2,66,-1"]}
  reach 0,12,1 {"within":false,"block":null,"blocks":0,"stand":"-4,71,-2","steps":2,"gather":[]}
  reach 2,30,-1 {"within":false,"block":null,"blocks":0,"stand":"-6,69,-6","steps":22,"gather":[]}
  reach 0,2,0 {"within":true,"block":null,"blocks":0}
  reach 3,6,0 {"within":false,"block":null,"blocks":0,"stand":"-4,67,-5","steps":0,"gather":["-7,66,-8","-5,66,-7","-5,66,-3","-5,66,-2","-4,66,-8","-3,66,-8","-3,66,-7","-3,66,-2","-2,66,-6","-1,66,-3","-1,66,-2"]}
  reach -2,9,2 {"within":false,"block":null,"blocks":0,"stand":"-9,69,-1","steps":1,"gather":[]}
@ -6,65,-2
  pit  {"feet":"-6,65,-2","pit":false,"feet_y":65}
  home {"feet":"-6,65,-2","pit":false,"feet_y":65}
  clmb {"feet":"-6,65,-2","pit":true,"feet_y":65,"climb":true,"approach":"2,65,-3","need":0,"stair":{"dig":["-5,66,-2"],"to":"-5,66,-2"},"pillar":true,"block":null,"blocks":0,"gather":["-5,66,-2","-5,65,-2","-6,65,-1","-6,65,-3"],"toward":"9,67,0"}
  reach 0,4,0 {"within":true,"block":null,"blocks":0}
  reach 1,7,0 {"within":false,"block":null,"blocks":0,"stand":"-5,67,-2","steps":0,"gather":["-5,66,-3","-4,66,-5","-4,66,0","-4,66,1","-3,66,-2","-3,66,-1","-3,66,1","-2,66,-1","-2,66,0"]}
  reach 0,12,1 {"within":false,"block":null,"blocks":0,"stand":"-4,71,-2","steps":2,"gather":[]}
  reach 2,30,-1 {"within":false,"block":null,"blocks":0,"stand":"-4,71,-2","steps":20,"gather":[]}
  reach 0,2,0 {"within":true,"block":null,"blocks":0}
  reach 3,6,0 {"within":false,"block":null,"blocks":0,"stand":"-3,68,-2","steps":0,"gather":["-4,67,-3","-4,67,-2"]}
  reach -2,9,2 {"within":false,"block":null,"blocks":0,"stand":"-9,69,-1","steps":1,"gather":[]}
@ -6,65,0
  pit  {"feet":"-6,65,0","pit":false,"feet_y":65}
  home {"feet":"-6,65,0","pit":false,"feet_y":65}
  clmb {"feet":"-6,65,0","pit":true,"feet_y":65,"climb":true,"approach":"2,66,0","need":0,"stair":null,"pillar":true,"block":null,"blocks":0,"gather":["-6,65,-1"],"toward":"9,67,0"}
  reach 0,4,0 {"within":true,"block":null,"blocks":0}
  reach 1,7,0 {"within":false,"block":null,"blocks":0,"stand":"-4,71,-2","steps":0,"gather":[]}
  reach 0,12,1 {"within":false,"block":null,"blocks":0,"stand":"-6,67,2","steps":6,"gather":["-9,66,-1","-9,66,2","-8,66,2","-8,66,5","-7,66,4","-6,66,3","-6,66,5","-4,66,0","-4,66,1","-4,66,5","-3,66,-1","-3,66,1"]}
  reach 2,30,-1 {"within":false,"block":null,"blocks":0,"stand":"-4,71,-2","steps":20,"gather":[]}
  reach 0,2,0 {"within":true,"block":null,"blocks":0}
  reach 3,6,0 {"within":false,"block":null,"blocks":0,"stand":"-3,66,0","steps":0,"gather":["-6,65,-3","-6,65,-1","-5,65,-2","-5,65,3","-4,65,1","-3,65,-2","-2,65,-3","-2,65,2","-1,65,0","0,65,-3","0,65,-2","0,65,0","0,65,2"]}
  reach -2,9,2 {"within":false,"block":null,"blocks":0,"stand":"-8,67,2","steps":2,"gather":["-11,66,0","-11,66,2","-10,66,0","-10,66,1","-10,66,5","-9,66,-1","-9,66,2","-8,66,5","-7,66,4","-6,66,2","-6,66,3","-6,66,5"]}
@ -6,67,2
  pit  {"feet":"-6,67,2","pit":false,"feet_y":67}
  home {"feet":"-6,67,2","pit":false,"feet_y":67}
  clmb {"feet":"-6,67,2","pit":true,"feet_y":67,"climb":true,"approach":"0,66,0","need":null,"stair":null,"pillar":true,"block":null,"blocks":0,"gather":[],"toward":"9,67,0"}
  reach 0,4,0 {"within":true,"block":null,"blocks":0}
  reach 1,7,0 {"within":false,"block":null,"blocks":0,"stand":"-4,70,4","steps":0,"gather":["-7,69,5"]}
  reach 0,12,1 {"within":false,"block":null,"blocks":0,"stand":"-7,70,5","steps":5,"gather":["-4,69,4"]}
  reach 2,30,-1 {"within":false,"block":null,"blocks":0,"stand":"-3,72,3","steps":21,"gather":[]}
  reach 0,2,0 {"within":true,"block":null,"blocks":0}
  reach 3,6,0 {"within":false,"block":null,"blocks":0,"stand":"-3,72,3","steps":0,"gather":[]}
  reach -2,9,2 {"within":false,"block":null,"blocks":0,"stand":"-7,70,5","steps":2,"gather":["-4,69,4"]}
@ -6,66,4
  pit  {"feet":"-6,66,4","pit":false,"feet_y":66}
  home {"feet":"-6,66,4","pit":false,"feet_y":66}
  clmb {"feet":"-6,66,4","pit":true,"feet_y":66,"climb":true,"approach":"-2,67,0","need":0,"stair":null,"pillar":true,"block":null,"blocks":0,"gather":["-7,66,4","-6,66,5","-6,66,3"],"toward":"9,67,0"}
  reach 0,4,0 {"within":true,"block":null,"blocks":0}
  reach 1,7,0 {"within":false,"block":null,"blocks":0,"stand":"-4,70,4","steps":0,"gather":["-7,69,5"]}
  reach 0,12,1 {"within":false,"block":null,"blocks":0,"stand":"-7,70,5","steps":4,"gather":["-4,69,4"]}
  reach 2,30,-1 {"within":false,"block":null,"blocks":0,"stand":"-3,72,3","steps":20,"gather":[]}
  reach 0,2,0 {"within":true,"block":null,"blocks":0}
  reach 3,6,0 {"within":false,"block":null,"blocks":0,"stand":"-4,70,4","steps":0,"gather":["-7,69,5"]}
  reach -2,9,2 {"within":false,"block":null,"blocks":0,"stand":"-7,70,5","steps":1,"gather":["-4,69,4"]}
@ -6,65,6
  pit  {"feet":"-6,65,6","pit":false,"feet_y":65}
  home {"feet":"-6,65,6","pit":false,"feet_y":65}
  clmb {"feet":"-6,65,6","pit":true,"feet_y":65,"climb":true,"approach":"-1,66,5","need":0,"stair":{"dig":["-5,66,6"],"to":"-5,66,6"},"pillar":true,"block":null,"blocks":0,"gather":["-6,66,5","-6,65,7","-6,65,5"],"toward":"9,67,0"}
  reach 0,4,0 {"within":true,"block":null,"blocks":0}
  reach 1,7,0 {"within":false,"block":null,"blocks":0,"stand":"-5,67,6","steps":0,"gather":["-8,66,5","-8,66,7","-8,66,9","-7,66,4","-6,66,3","-6,66,5","-4,66,5","-3,66,9","-2,66,3"]}
  reach 0,12,1 {"within":false,"block":null,"blocks":0,"stand":"-7,70,5","steps":3,"gather":["-4,69,4"]}
  reach 2,30,-1 {"within":false,"block":null,"blocks":0,"stand":"-3,72,4","steps":19,"gather":[]}
  reach 0,2,0 {"within":true,"block":null,"blocks":0}
  reach 3,6,0 {"within":false,"block":null,"blocks":0,"stand":"-3,66,6","steps":0,"gather":["-6,65,4","-6,65,5","-6,65,7","-5,65,3","-5,65,8","-5,65,9","-3,65,5","-3,65,7","-3,65,8","-2,65,6","-2,65,7","-2,65,9","-1,65,4","-1,65,9","0,65,5","0,65,7"]}
  reach -2,9,2 {"within":false,"block":null,"blocks":0,"stand":"-9,70,9","steps":0,"gather":[]}
@ -4,66,-6
  pit  {"feet":"-4,66,-6","pit":false,"feet_y":66}
  home {"feet":"-4,66,-6","pit":false,"feet_y":66}
  clmb {"feet":"-4,66,-6","pit":true,"feet_y":66,"climb":true,"approach":"4,67,-6","need":0,"stair":null,"pillar":true,"block":null,"blocks":0,"gather":["-4,66,-5"],"toward":"9,67,0"}
  reach 0,4,0 {"within":true,"block":null,"blocks":0}
  reach 1,7,0 {"within":false,"block":null,"blocks":0,"stand":"-3,67,-6","steps":1,"gather":["-5,66,-9","-5,66,-7","-5,66,-3","-4,66,-8","-4,66,-5","-3,66,-8","-3,66,-7","-2,66,-6","-1,66,-9","-1,66,-3","0,66,-5"]}
  reach 0,12,1 {"within":false,"block":null,"blocks":0,"stand":"-6,69,-6","steps":5,"gather":[]}
  reach 2,30,-1 {"within":false,"block":null,"blocks":0,"stand":"0,71,-7","steps":21,"gather":[]}
  reach 0,2,0 {"within":true,"block":null,"blocks":0}
  reach 3,6,0 {"within":false,"block":null,"blocks":0,"stand":"-1,68,-6","steps":0,"gather":["-4,67,-3","1,67,-5"]}
  reach -2,9,2 {"within":false,"block":null,"blocks":0,"stand":"-4,71,-2","steps":1,"gather":[]}
@ -4,66,-4
  pit  {"feet":"-4,66,-4","pit":false,"feet_y":66}
  home {"feet":"-4,66,-4","pit":false,"feet_y":66}
  clmb {"feet":"-4,66,-4","pit":true,"feet_y":66,"climb":true,"approach":"4,66,-4","need":0,"stair":{"dig":["-4,67,-3"],"to":"-4,67,-3"},"pillar":true,"block":null,"blocks":0,"gather":["-4,67,-3","-4,66,-5"],"toward":"9,67,0"}
  reach 0,4,0 {"within":true,"block":null,"blocks":0}
  reach 1,7,0 {"within":false,"block":null,"blocks":0,"stand":"-4,71,-2","steps":0,"gather":[]}
  reach 0,12,1 {"within":false,"block":null,"blocks":0,"stand":"-4,71,-2","steps":3,"gather":[]}
  reach 2,30,-1 {"within":false,"block":null,"blocks":0,"stand":"0,71,-7","steps":22,"gather":[]}
  reach 0,2,0 {"within":true,"block":null,"blocks":0}
  reach 3,6,0 {"within":false,"block":null,"blocks":0,"stand":"0,68,-3","steps":0,"gather":["-1,67,-6","1,67,-5","2,67,-1"]}
  reach -2,9,2 {"within":false,"block":null,"blocks":0,"stand":"-4,71,-2","steps":0,"gather":[]}
@ -4,71,-2
  pit  {"feet":"-4,71,-2","pit":false,"feet_y":71}
  home {"feet":"-4,71,-2","pit":false,"feet_y":71}
  clmb {"feet":"-4,71,-2","pit":true,"feet_y":71,"climb":true,"approach":"4,67,-2","need":null,"stair":null,"pillar":true,"block":null,"blocks":0,"gather":[],"toward":"9,67,0"}
  reach 0,4,0 {"within":true,"block":null,"blocks":0}
  reach 1,7,0 {"within":false,"block":null,"blocks":0,"stand":"-4,71,-2","steps":3,"gather":[]}
  reach 0,12,1 {"within":false,"block":null,"blocks":0,"stand":"-4,71,-2","steps":8,"gather":[]}
  reach 2,30,-1 {"within":false,"block":null,"blocks":0,"why":"no ground to stand on below or beside it within 24 blocks"}
  reach 0,2,0 {"within":true,"block":null,"blocks":0}
  reach 3,6,0 {"within":false,"block":null,"blocks":0,"stand":"-1,67,-2","steps":5,"gather":["-4,66,-5","-4,66,0","-4,66,1","-3,66,-2","-3,66,-1","-3,66,1","-2,66,-1","-2,66,0","-1,66,-3","0,66,-5","0,66,-1","1,66,-2","1,66,-1","2,66,-5","2,66,-1"]}
  reach -2,9,2 {"within":false,"block":null,"blocks":0,"stand":"-4,71,-2","steps":6,"gather":[]}
@ -4,67,0
  pit  {"feet":"-4,67,0","pit":false,"feet_y":67}
  home {"feet":"-4,67,0","pit":false,"feet_y":67}
  clmb {"feet":"-4,67,0","pit":true,"feet_y":67,"climb":true,"approach":"4,66,-1","need":null,"stair":null,"pillar":true,"block":null,"blocks":0,"gather":[],"toward":"9,67,0"}
  reach 0,4,0 {"within":true,"block":null,"blocks":0}
  reach 1,7,0 {"within":false,"block":null,"blocks":0,"stand":"-4,71,-2","steps":0,"gather":[]}
  reach 0,12,1 {"within":false,"block":null,"blocks":0,"stand":"-3,72,3","steps":3,"gather":[]}
  reach 2,30,-1 {"within":false,"block":null,"blocks":0,"stand":"-4,71,-2","steps":22,"gather":[]}
  reach 0,2,0 {"within":true,"block":null,"blocks":0}
  reach 3,6,0 {"within":false,"block":null,"blocks":0,"stand":"-1,66,0","steps":2,"gather":["-4,65,1","-3,65,-2","-3,65,0","-2,65,-3","-2,65,2","0,65,-3","0,65,-2","0,65,0","0,65,2","1,65,0","1,65,2","1,65,3"]}
  reach -2,9,2 {"within":false,"block":null,"blocks":0,"stand":"-4,70,4","steps":3,"gather":["-7,69,5"]}
@ -4,67,2
  pit  {"feet":"-4,67,2","pit":false,"feet_y":67}
  home {"feet":"-4,67,2","pit":false,"feet_y":67}
  clmb {"feet":"-4,67,2","pit":true,"feet_y":67,"climb":true,"approach":"2,66,0","need":null,"stair":null,"pillar":true,"block":null,"blocks":0,"gather":[],"toward":"9,67,0"}
  reach 0,4,0 {"within":true,"block":null,"blocks":0}
  reach 1,7,0 {"within":false,"block":null,"blocks":0,"stand":"-3,72,3","steps":0,"gather":[]}
  reach 0,12,1 {"within":false,"block":null,"blocks":0,"stand":"-3,72,3","steps":3,"gather":[]}
  reach 2,30,-1 {"within":false,"block":null,"blocks":0,"stand":"-3,72,3","steps":21,"gather":[]}
  reach 0,2,0 {"within":true,"block":null,"blocks":0}
  reach 3,6,0 {"within":false,"block":null,"blocks":0,"stand":"-2,72,3","steps":0,"gather":[]}
  reach -2,9,2 {"within":false,"block":null,"blocks":0,"stand":"-7,70,5","steps":2,"gather":["-4,69,4"]}
@ -4,70,4
  pit  {"feet":"-4,70,4","pit":false,"feet_y":70}
  home {"feet":"-4,70,4","pit":false,"feet_y":70}
  clmb {"feet":"-4,70,4","pit":true,"feet_y":70,"climb":true,"approach":"0,66,0","need":1,"stair":null,"pillar":true,"block":null,"blocks":0,"gather":[],"toward":"9,67,0"}
  reach 0,4,0 {"within":true,"block":null,"blocks":0}
  reach 1,7,0 {"within":false,"block":null,"blocks":0,"stand":"-3,72,4","steps":0,"gather":[]}
  reach 0,12,1 {"within":false,"block":null,"blocks":0,"stand":"-3,72,4","steps":6,"gather":[]}
  reach 2,30,-1 {"within":false,"block":null,"blocks":0,"stand":"-2,72,3","steps":23,"gather":[]}
  reach 0,2,0 {"within":true,"block":null,"blocks":0}
  reach 3,6,0 {"within":false,"block":null,"blocks":0,"stand":"-1,72,4","steps":0,"gather":[]}
  reach -2,9,2 {"within":false,"block":null,"blocks":0,"stand":"-7,70,5","steps":5,"gather":[]}
@ -4,65,6
  pit  {"feet":"-4,65,6","pit":false,"feet_y":65}
  home {"feet":"-4,65,6","pit":false,"feet_y":65}
  clmb {"feet":"-4,65,6","pit":true,"feet_y":65,"climb":true,"approach":"0,66,0","need":0,"stair":{"dig":["-4,66,5"],"to":"-4,66,5"},"pillar":true,"block":null,"blocks":0,"gather":["-4,66,5"],"toward":"9,67,0"}
  reach 0,4,0 {"within":true,"block":null,"blocks":0}
  reach 1,7,0 {"within":false,"block":null,"blocks":0,"stand":"-4,68,7","steps":0,"gather":[]}
  reach 0,12,1 {"within":false,"block":null,"blocks":0,"stand":"-3,72,6","steps":1,"gather":[]}
  reach 2,30,-1 {"within":false,"block":null,"blocks":0,"stand":"-2,72,5","steps":18,"gather":[]}
  reach 0,2,0 {"within":true,"block":null,"blocks":0}
  reach 3,6,0 {"within":false,"block":null,"blocks":0,"stand":"0,68,6","steps":0,"gather":["2,67,4"]}
  reach -2,9,2 {"within":false,"block":null,"blocks":0,"stand":"-4,71,9","steps":0,"gather":["-2,70,8","-2,70,10"]}
@ -2,67,-6
  pit  {"feet":"-2,67,-6","pit":false,"feet_y":67}
  home {"feet":"-2,67,-6","pit":false,"feet_y":67}
  clmb {"feet":"-2,67,-6","pit":true,"feet_y":67,"climb":true,"approach":"6,65,-6","need":0,"stair":null,"pillar":true,"block":null,"blocks":0,"gather":["-1,67,-6"],"toward":"9,67,0"}
  reach 0,4,0 {"within":true,"block":null,"blocks":0}
  reach 1,7,0 {"within":false,"block":null,"blocks":0,"stand":"0,71,-7","steps":0,"gather":[]}
  reach 0,12,1 {"within":false,"block":null,"blocks":0,"stand":"0,71,-7","steps":5,"gather":[]}
  reach 2,30,-1 {"within":false,"block":null,"blocks":0,"stand":"0,71,-7","steps":21,"gather":[]}
  reach 0,2,0 {"within":true,"block":null,"blocks":0}
  reach 3,6,0 {"within":false,"block":null,"blocks":0,"stand":"0,71,-7","steps":0,"gather":[]}
  reach -2,9,2 {"within":false,"block":null,"blocks":0,"stand":"-4,71,-2","steps":1,"gather":[]}
@ -2,65,-4
  pit  {"feet":"-2,65,-4","pit":false,"feet_y":65}
  home {"feet":"-2,65,-4","pit":false,"feet_y":65}
  clmb {"feet":"-2,65,-4","pit":true,"feet_y":65,"climb":true,"approach":"6,66,-4","need":0,"stair":{"dig":["-2,66,-5"],"to":"-2,66,-5"},"pillar":true,"block":null,"blocks":0,"gather":["-2,65,-3"],"toward":"9,67,0"}
  reach 0,4,0 {"within":true,"block":null,"blocks":0}
  reach 1,7,0 {"within":false,"block":null,"blocks":0,"stand":"0,68,-3","steps":0,"gather":["-1,67,-6","1,67,-5","2,67,-1"]}
  reach 0,12,1 {"within":false,"block":null,"blocks":0,"stand":"-4,71,-2","steps":2,"gather":[]}
  reach 2,30,-1 {"within":false,"block":null,"blocks":0,"stand":"0,71,-7","steps":20,"gather":[]}
  reach 0,2,0 {"within":true,"block":null,"blocks":0}
  reach 3,6,0 {"within":false,"block":null,"blocks":0,"stand":"0,67,-5","steps":0,"gather":["-3,66,-8","-3,66,-7","-3,66,-2","-2,66,-6","-1,66,-3","-1,66,-2","1,66,-7","1,66,-2","2,66,-8","2,66,-7","2,66,-5","3,66,-7"]}
  reach -2,9,2 {"within":false,"block":null,"blocks":0,"stand":"-4,71,-2","steps":0,"gather":[]}
@ -2,65,-2
  pit  {"feet":"-2,65,-2","pit":false,"feet_y":65}
  home {"feet":"-2,65,-2","pit":false,"feet_y":65}
  clmb {"feet":"-2,65,-2","pit":true,"feet_y":65,"climb":true,"approach":"6,66,-4","need":0,"stair":{"dig":["-1,66,-2"],"to":"-1,66,-2"},"pillar":true,"block":null,"blocks":0,"gather":["-1,66,-2","-2,66,-1","-3,65,-2","-2,65,-3"],"toward":"9,67,0"}
  reach 0,4,0 {"within":true,"block":null,"blocks":0}
  reach 1,7,0 {"within":false,"block":null,"blocks":0,"stand":"-1,67,-2","steps":0,"gather":["-4,66,-5","-4,66,0","-4,66,1","-3,66,-2","-3,66,-1","-3,66,1","-2,66,-1","-2,66,0","-1,66,-3","0,66,-5","0,66,-1","1,66,-2","1,66,-1","2,66,-5","2,66,-1"]}
  reach 0,12,1 {"within":false,"block":null,"blocks":0,"stand":"-4,71,-2","steps":2,"gather":[]}
  reach 2,30,-1 {"within":false,"block":null,"blocks":0,"stand":"0,68,-3","steps":22,"gather":["-1,67,-6","1,67,-5","2,67,-1"]}
  reach 0,2,0 {"within":true,"block":null,"blocks":0}
  reach 3,6,0 {"within":false,"block":null,"blocks":0,"stand":"1,67,-2","steps":0,"gather":["-2,66,-1","-2,66,0","-1,66,-3","-1,66,-2","0,66,-5","0,66,-1","1,66,-1","2,66,-5","2,66,-1","3,66,-1","3,66,0","4,66,-2","4,66,1"]}
  reach -2,9,2 {"within":false,"block":null,"blocks":0,"stand":"-4,71,-2","steps":0,"gather":[]}
@ -2,67,0
  pit  {"feet":"-2,67,0","pit":false,"feet_y":67}
  home {"feet":"-2,67,0","pit":false,"feet_y":67}
  clmb {"feet":"-2,67,0","pit":true,"feet_y":67,"climb":true,"approach":"4,66,-1","need":null,"stair":null,"pillar":true,"block":null,"blocks":0,"gather":[],"toward":"9,67,0"}
  reach 0,4,0 {"within":true,"block":null,"blocks":0}
  reach 1,7,0 {"within":false,"block":null,"blocks":0,"stand":"-1,66,0","steps":3,"gather":["-4,65,1","-3,65,-2","-3,65,0","-2,65,-3","-2,65,2","0,65,-3","0,65,-2","0,65,0","0,65,2","1,65,0","1,65,2","1,65,3"]}
  reach 0,12,1 {"within":false,"block":null,"blocks":0,"stand":"-3,72,3","steps":3,"gather":[]}
  reach 2,30,-1 {"within":false,"block":null,"blocks":0,"why":"no ground to stand on below or beside it within 24 blocks"}
  reach 0,2,0 {"within":true,"block":null,"blocks":0}
  reach 3,6,0 {"within":false,"block":null,"blocks":0,"stand":"2,68,-1","steps":1,"gather":[]}
  reach -2,9,2 {"within":false,"block":null,"blocks":0,"stand":"-3,72,3","steps":0,"gather":[]}
@ -2,67,2
  pit  {"feet":"-2,67,2","pit":false,"feet_y":67}
  home {"feet":"-2,67,2","pit":false,"feet_y":67}
  clmb {"feet":"-2,67,2","pit":true,"feet_y":67,"climb":true,"approach":"3,67,0","need":4,"stair":null,"pillar":true,"block":null,"blocks":0,"gather":[],"toward":"9,67,0"}
  reach 0,4,0 {"within":true,"block":null,"blocks":0}
  reach 1,7,0 {"within":false,"block":null,"blocks":0,"stand":"-2,72,3","steps":0,"gather":[]}
  reach 0,12,1 {"within":false,"block":null,"blocks":0,"stand":"-2,72,3","steps":2,"gather":[]}
  reach 2,30,-1 {"within":false,"block":null,"blocks":0,"stand":"-1,72,3","steps":21,"gather":[]}
  reach 0,2,0 {"within":true,"block":null,"blocks":0}
  reach 3,6,0 {"within":false,"block":null,"blocks":0,"stand":"0,72,3","steps":0,"gather":[]}
  reach -2,9,2 {"within":false,"block":null,"blocks":0,"stand":"-3,72,3","steps":0,"gather":[]}
@ -2,65,4
  pit  {"feet":"-2,65,4","pit":true,"feet_y":65,"cave":true,"exit":"-1,65,2","stair":{"dig":["-3,66,4"],"to":"-3,66,4"},"pillar":true,"block":null,"blocks":0,"gather":["-2,66,3","-1,65,4"]}
  home {"feet":"-2,65,4","pit":true,"feet_y":65,"cave":true,"exit":"-1,65,2","stair":{"dig":["-2,66,3"],"to":"-2,66,3"},"pillar":true,"block":null,"blocks":0,"gather":["-2,66,3","-1,65,4"],"toward":"9,67,0"}
  clmb {"feet":"-2,65,4","pit":true,"feet_y":65,"cave":true,"climb":true,"approach":"9,65,0","stair":{"dig":["-2,66,3"],"to":"-2,66,3"},"pillar":true,"block":null,"blocks":0,"gather":["-2,66,3","-1,65,4"],"toward":"9,67,0"}
  reach 0,4,0 {"within":true,"block":null,"blocks":0}
  reach 1,7,0 {"within":false,"block":null,"blocks":0,"stand":"-2,67,2","steps":1,"gather":["-4,66,0","-4,66,1","-4,66,5","-3,66,-1","-3,66,1","-2,66,-1","-2,66,0","-2,66,3","0,66,-1","0,66,2","0,66,3","0,66,4","1,66,-1","1,66,4","1,66,5"]}
  reach 0,12,1 {"within":false,"block":null,"blocks":0,"stand":"-2,72,5","steps":0,"gather":[]}
  reach 2,30,-1 {"within":false,"block":null,"blocks":0,"stand":"0,72,3","steps":18,"gather":[]}
  reach 0,2,0 {"within":true,"block":null,"blocks":0}
  reach 3,6,0 {"within":false,"block":null,"blocks":0,"stand":"1,67,4","steps":0,"gather":["-2,66,2","-2,66,3","0,66,2","0,66,3","0,66,4","1,66,5","3,66,2","3,66,6","4,66,1","4,66,6","4,66,7"]}
  reach -2,9,2 {"within":false,"block":null,"blocks":0,"stand":"-3,72,5","steps":0,"gather":[]}
@ -2,66,6
  pit  {"feet":"-2,66,6","pit":true,"feet_y":66,"cave":true,"exit":"-4,65,6","stair":null,"pillar":true,"block":null,"blocks":0,"gather":[]}
  home {"feet":"-2,66,6","pit":true,"feet_y":66,"cave":true,"exit":"-4,65,6","stair":null,"pillar":true,"block":null,"blocks":0,"gather":[],"toward":"9,67,0"}
  clmb {"feet":"-2,66,6","pit":true,"feet_y":66,"cave":true,"climb":true,"approach":"9,65,0","stair":null,"pillar":true,"block":null,"blocks":0,"gather":[],"toward":"9,67,0"}
  reach 0,4,0 {"within":true,"block":null,"blocks":0}
  reach 1,7,0 {"within":false,"block":null,"blocks":0,"stand":"-1,72,6","steps":0,"gather":[]}
  reach 0,12,1 {"within":false,"block":null,"blocks":0,"stand":"-2,72,7","steps":1,"gather":[]}
  reach 2,30,-1 {"within":false,"block":null,"blocks":0,"stand":"0,72,5","steps":19,"gather":[]}
  reach 0,2,0 {"within":true,"block":null,"blocks":0}
  reach 3,6,0 {"within":false,"block":null,"blocks":0,"why":"no ground to stand on below or beside it within 24 blocks"}
  reach -2,9,2 {"within":false,"block":null,"blocks":0,"stand":"-4,71,9","steps":0,"gather":["-2,70,8","-2,70,10"]}
@ 0,66,-6
  pit  {"feet":"0,66,-6","pit":false,"feet_y":66}
  home {"feet":"0,66,-6","pit":false,"feet_y":66}
  clmb {"feet":"0,66,-6","pit":true,"feet_y":66,"climb":true,"approach":"5,67,-4","need":0,"stair":{"dig":["-1,67,-6"],"to":"-1,67,-6"},"pillar":true,"block":null,"blocks":0,"gather":["-1,67,-6","0,66,-5"],"toward":"9,67,0"}
  reach 0,4,0 {"within":true,"block":null,"blocks":0}
  reach 1,7,0 {"within":false,"block":null,"blocks":0,"stand":"0,71,-7","steps":0,"gather":[]}
  reach 0,12,1 {"within":false,"block":null,"blocks":0,"stand":"0,71,-7","steps":3,"gather":[]}
  reach 2,30,-1 {"within":false,"block":null,"blocks":0,"stand":"0,71,-7","steps":21,"gather":[]}
  reach 0,2,0 {"within":true,"block":null,"blocks":0}
  reach 3,6,0 {"within":false,"block":null,"blocks":0,"stand":"3,67,-6","steps":0,"gather":["0,66,-5","1,66,-7","2,66,-8","2,66,-7","2,66,-5","3,66,-7","4,66,-6","5,66,-8","5,66,-7"]}
  reach -2,9,2 {"within":false,"block":null,"blocks":0,"stand":"-4,71,-2","steps":1,"gather":[]}
@ 0,66,-4
  pit  {"feet":"0,66,-4","pit":false,"feet_y":66}
  home {"feet":"0,66,-4","pit":false,"feet_y":66}
  clmb {"feet":"0,66,-4","pit":true,"feet_y":66,"climb":true,"approach":"7,66,-3","need":0,"stair":{"dig":["0,67,-3"],"to":"0,67,-3"},"pillar":true,"block":null,"blocks":0,"gather":["0,66,-5"],"toward":"9,67,0"}
  reach 0,4,0 {"within":true,"block":null,"blocks":0}
  reach 1,7,0 {"within":false,"block":null,"blocks":0,"stand":"1,69,-5","steps":0,"gather":["0,68,-7","4,68,-8"]}
  reach 0,12,1 {"within":false,"block":null,"blocks":0,"stand":"0,68,-3","steps":5,"gather":["-1,67,-6","1,67,-5","2,67,-1"]}
  reach 2,30,-1 {"within":false,"block":null,"blocks":0,"stand":"3,70,-5","steps":22,"gather":["4,69,-3","5,69,-2","6,69,-2"]}
  reach 0,2,0 {"within":true,"block":null,"blocks":0}
  reach 3,6,0 {"within":false,"block":null,"blocks":0,"stand":"3,70,-5","steps":0,"gather":["4,69,-3","5,69,-2","6,69,-2"]}
  reach -2,9,2 {"within":false,"block":null,"blocks":0,"stand":"-4,71,-2","steps":0,"gather":[]}
@ 0,66,-2
  pit  {"feet":"0,66,-2","pit":false,"feet_y":66}
  home {"feet":"0,66,-2","pit":false,"feet_y":66}
  clmb {"feet":"0,66,-2","pit":true,"feet_y":66,"climb":true,"approach":"6,67,-3","need":0,"stair":{"dig":["0,67,-3"],"to":"0,67,-3"},"pillar":true,"block":null,"blocks":0,"gather":["1,66,-2","-1,66,-2","0,66,-1"],"toward":"9,67,0"}
  reach 0,4,0 {"within":true,"block":null,"blocks":0}
  reach 1,7,0 {"within":false,"block":null,"blocks":0,"stand":"1,67,-2","steps":1,"gather":["-2,66,-1","-2,66,0","-1,66,-3","-1,66,-2","0,66,-5","0,66,-1","1,66,-1","2,66,-5","2,66,-1","3,66,-1","3,66,0","4,66,-2","4,66,1"]}
  reach 0,12,1 {"within":false,"block":null,"blocks":0,"stand":"0,67,-1","steps":6,"gather":["-3,66,-2","-3,66,-1","-3,66,1","-2,66,-1","-2,66,0","-2,66,2","-1,66,-3","-1,66,-2","0,66,2","1,66,-2","1,66,-1","2,66,-1","3,66,-1","3,66,0","3,66,2"]}
  reach 2,30,-1 {"within":false,"block":null,"blocks":0,"stand":"3,70,-5","steps":22,"gather":["4,69,-3","5,69,-2","6,69,-2"]}
  reach 0,2,0 {"within":true,"block":null,"blocks":0}
  reach 3,6,0 {"within":false,"block":null,"blocks":0,"stand":"2,68,-1","steps":0,"gather":[]}
  reach -2,9,2 {"within":false,"block":null,"blocks":0,"stand":"-4,71,-2","steps":1,"gather":[]}
@ 0,66,0
  pit  {"feet":"0,66,0","pit":false,"feet_y":66}
  home {"feet":"0,66,0","pit":false,"feet_y":66}
  clmb {"feet":"0,66,0","pit":true,"feet_y":66,"climb":true,"approach":"4,66,-1","need":0,"stair":null,"pillar":true,"block":null,"blocks":0,"gather":["0,66,-1"],"toward":"9,67,0"}
  reach 0,4,0 {"within":true,"block":null,"blocks":0}
  reach 1,7,0 {"within":false,"block":null,"blocks":0,"stand":"2,68,-1","steps":1,"gather":[]}
  reach 0,12,1 {"within":false,"block":null,"blocks":0,"stand":"-1,72,3","steps":2,"gather":[]}
  reach 2,30,-1 {"within":false,"block":null,"blocks":0,"stand":"2,68,-1","steps":23,"gather":[]}
  reach 0,2,0 {"within":true,"block":null,"blocks":0}
  reach 3,6,0 {"within":false,"block":null,"blocks":0,"stand":"3,67,0","steps":0,"gather":["0,66,-1","0,66,2","0,66,3","1,66,-2","1,66,-1","2,66,-1","3,66,-1","3,66,2","4,66,-2","4,66,1","5,66,-2","6,66,-2","6,66,2"]}
  reach -2,9,2 {"within":false,"block":null,"blocks":0,"stand":"-3,72,3","steps":0,"gather":[]}
@ 0,67,2
  pit  {"feet":"0,67,2","pit":false,"feet_y":67}
  home {"feet":"0,67,2","pit":false,"feet_y":67}
  clmb {"feet":"0,67,2","pit":true,"feet_y":67,"climb":true,"approach":"4,66,-1","need":4,"stair":null,"pillar":true,"block":null,"blocks":0,"gather":[],"toward":"9,67,0"}
  reach 0,4,0 {"within":true,"block":null,"blocks":0}
  reach 1,7,0 {"within":false,"block":null,"blocks":0,"stand":"0,72,3","steps":0,"gather":[]}
  reach 0,12,1 {"within":false,"block":null,"blocks":0,"stand":"0,72,3","steps":2,"gather":[]}
  reach 2,30,-1 {"within":false,"block":null,"blocks":0,"stand":"1,72,3","steps":21,"gather":[]}
  reach 0,2,0 {"within":true,"block":null,"blocks":0}
  reach 3,6,0 {"within":false,"block":null,"blocks":0,"stand":"2,72,3","steps":0,"gather":[]}
  reach -2,9,2 {"within":false,"block":null,"blocks":0,"stand":"-2,72,4","steps":0,"gather":[]}
@ 0,70,4
  pit  {"feet":"0,70,4","pit":true,"feet_y":70,"need":1,"stair":null,"pillar":false,"block":null,"blocks":0,"gather":[]}
  home {"feet":"0,70,4","pit":true,"feet_y":70,"need":1,"stair":null,"pillar":false,"block":null,"blocks":0,"gather":[],"toward":"9,67,0"}
  clmb {"feet":"0,70,4","pit":true,"feet_y":70,"climb":true,"need":1,"stair":null,"pillar":false,"block":null,"blocks":0,"gather":[],"toward":"9,67,0"}
  reach 0,4,0 {"within":true,"block":null,"blocks":0}
  reach 1,7,0 {"within":false,"block":null,"blocks":0,"stand":"1,72,4","steps":0,"gather":[]}
  reach 0,12,1 {"within":false,"block":null,"blocks":0,"stand":"0,72,5","steps":5,"gather":[]}
  reach 2,30,-1 {"within":false,"block":null,"blocks":0,"stand":"2,72,3","steps":23,"gather":[]}
  reach 0,2,0 {"within":true,"block":null,"blocks":0}
  reach 3,6,0 {"within":false,"block":null,"blocks":0,"stand":"3,72,4","steps":0,"gather":[]}
  reach -2,9,2 {"within":false,"block":null,"blocks":0,"stand":"-2,72,6","steps":2,"gather":[]}
@ 0,68,6
  pit  {"feet":"0,68,6","pit":true,"feet_y":68,"cave":true,"exit":"4,67,6","stair":{"dig":["0,69,5"],"to":"0,69,5"},"pillar":true,"block":null,"blocks":0,"gather":["0,69,5"]}
  home {"feet":"0,68,6","pit":true,"feet_y":68,"cave":true,"exit":"4,67,6","stair":{"dig":["0,69,5"],"to":"0,69,5"},"pillar":true,"block":null,"blocks":0,"gather":["0,69,5"],"toward":"9,67,0"}
  clmb {"feet":"0,68,6","pit":true,"feet_y":68,"cave":true,"climb":true,"approach":"9,65,0","stair":{"dig":["0,69,5"],"to":"0,69,5"},"pillar":true,"block":null,"blocks":0,"gather":["0,69,5"],"toward":"9,67,0"}
  reach 0,4,0 {"within":true,"block":null,"blocks":0}
  reach 1,7,0 {"within":false,"block":null,"blocks":0,"stand":"1,72,6","steps":0,"gather":[]}
  reach 0,12,1 {"within":false,"block":null,"blocks":0,"stand":"0,72,7","steps":3,"gather":[]}
  reach 2,30,-1 {"within":false,"block":null,"blocks":0,"stand":"2,72,5","steps":21,"gather":[]}
  reach 0,2,0 {"within":true,"block":null,"blocks":0}
  reach 3,6,0 {"within":false,"block":null,"blocks":0,"stand":"3,72,6","steps":0,"gather":[]}
  reach -2,9,2 {"within":false,"block":null,"blocks":0,"stand":"-2,72,8","steps":0,"gather":[]}
@ 2,66,-6
  pit  {"feet":"2,66,-6","pit":false,"feet_y":66}
  home {"feet":"2,66,-6","pit":false,"feet_y":66}
  clmb {"feet":"2,66,-6","pit":true,"feet_y":66,"climb":true,"approach":"6,67,-3","need":0,"stair":null,"pillar":true,"block":null,"blocks":0,"gather":["2,66,-5","2,66,-7"],"toward":"9,67,0"}
  reach 0,4,0 {"within":true,"block":null,"blocks":0}
  reach 1,7,0 {"within":false,"block":null,"blocks":0,"stand":"3,70,-5","steps":0,"gather":["4,69,-3","5,69,-2","6,69,-2"]}
  reach 0,12,1 {"within":false,"block":null,"blocks":0,"stand":"3,70,-5","steps":4,"gather":["4,69,-3","5,69,-2","6,69,-2"]}
  reach 2,30,-1 {"within":false,"block":null,"blocks":0,"stand":"2,71,-9","steps":22,"gather":["0,70,-7"]}
  reach 0,2,0 {"within":true,"block":null,"blocks":0}
  reach 3,6,0 {"within":false,"block":null,"blocks":0,"stand":"5,67,-6","steps":0,"gather":["2,66,-8","2,66,-7","2,66,-5","3,66,-7","4,66,-6","5,66,-8","5,66,-7","7,66,-8","7,66,-4","8,66,-8","8,66,-7"]}
  reach -2,9,2 {"within":false,"block":null,"blocks":0,"stand":"1,69,-5","steps":2,"gather":["0,68,-7","4,68,-8"]}
@ 2,65,-4
  pit  {"feet":"2,65,-4","pit":false,"feet_y":65}
  home {"feet":"2,65,-4","pit":false,"feet_y":65}
  clmb {"feet":"2,65,-4","pit":true,"feet_y":65,"climb":true,"approach":"7,68,-1","need":0,"stair":{"dig":["2,66,-5"],"to":"2,66,-5"},"pillar":true,"block":null,"blocks":0,"gather":["2,66,-5","3,65,-4"],"toward":"9,67,0"}
  reach 0,4,0 {"within":true,"block":null,"blocks":0}
  reach 1,7,0 {"within":false,"block":null,"blocks":0,"stand":"3,70,-5","steps":0,"gather":["4,69,-3","5,69,-2","6,69,-2"]}
  reach 0,12,1 {"within":false,"block":null,"blocks":0,"stand":"3,70,-5","steps":3,"gather":["4,69,-3","5,69,-2","6,69,-2"]}
  reach 2,30,-1 {"within":false,"block":null,"blocks":0,"stand":"3,70,-5","steps":21,"gather":["4,69,-3","5,69,-2","6,69,-2"]}
  reach 0,2,0 {"within":true,"block":null,"blocks":0}
  reach 3,6,0 {"within":false,"block":null,"blocks":0,"stand":"5,67,-4","steps":0,"gather":["2,66,-7","2,66,-5","2,66,-1","3,66,-7","3,66,-1","4,66,-6","4,66,-2","5,66,-7","5,66,-2","6,66,-2","7,66,-4","7,66,-2","8,66,-7","8,66,-1"]}
  reach -2,9,2 {"within":false,"block":null,"blocks":0,"stand":"0,68,-3","steps":2,"gather":["-1,67,-6","1,67,-5","2,67,-1"]}
@ 2,65,-2
  pit  {"feet":"2,65,-2","pit":false,"feet_y":65}
  home {"feet":"2,65,-2","pit":false,"feet_y":65}
  clmb {"feet":"2,65,-2","pit":true,"feet_y":65,"climb":true,"approach":"7,68,-1","need":0,"stair":{"dig":["1,66,-2"],"to":"1,66,-2"},"pillar":true,"block":null,"blocks":0,"gather":["1,66,-2","2,66,-1","3,65,-2"],"toward":"9,67,0"}
  reach 0,4,0 {"within":true,"block":null,"blocks":0}
  reach 1,7,0 {"within":false,"block":null,"blocks":0,"stand":"2,68,-1","steps":0,"gather":[]}
  reach 0,12,1 {"within":false,"block":null,"blocks":0,"stand":"2,68,-1","steps":4,"gather":[]}
  reach 2,30,-1 {"within":false,"block":null,"blocks":0,"stand":"4,70,-3","steps":20,"gather":["3,69,-5","5,69,-2","6,69,-2"]}
  reach 0,2,0 {"within":true,"block":null,"blocks":0}
  reach 3,6,0 {"within":false,"block":null,"blocks":0,"stand":"5,70,-2","steps":0,"gather":["3,69,-5","4,69,-3","4,69,1","6,69,-2"]}
  reach -2,9,2 {"within":false,"block":null,"blocks":0,"stand":"2,68,-1","steps":2,"gather":[]}
@ 2,66,0
  pit  {"feet":"2,66,0","pit":false,"feet_y":66}
  home {"feet":"2,66,0","pit":false,"feet_y":66}
  clmb {"feet":"2,66,0","pit":true,"feet_y":66,"climb":true,"approach":"6,66,-4","need":0,"stair":{"dig":["2,67,-1"],"to":"2,67,-1"},"pillar":true,"block":null,"blocks":0,"gather":["2,67,-1","3,66,0","2,66,-1"],"toward":"9,67,0"}
  reach 0,4,0 {"within":true,"block":null,"blocks":0}
  reach 1,7,0 {"within":false,"block":null,"blocks":0,"stand":"4,69,0","steps":0,"gather":["6,68,0"]}
  reach 0,12,1 {"within":false,"block":null,"blocks":0,"stand":"1,72,3","steps":2,"gather":[]}
  reach 2,30,-1 {"within":false,"block":null,"blocks":0,"stand":"5,71,-1","steps":21,"gather":[]}
  reach 0,2,0 {"within":true,"block":null,"blocks":0}
  reach 3,6,0 {"within":false,"block":null,"blocks":0,"stand":"4,69,0","steps":0,"gather":["6,68,0"]}
  reach -2,9,2 {"within":false,"block":null,"blocks":0,"stand":"-1,72,3","steps":0,"gather":[]}
@ 2,65,2
  pit  {"feet":"2,65,2","pit":false,"feet_y":65}
  home {"feet":"2,65,2","pit":false,"feet_y":65}
  clmb {"feet":"2,65,2","pit":true,"feet_y":65,"climb":true,"approach":"4,66,-1","need":0,"stair":{"dig":["3,66,2"],"to":"3,66,2"},"pillar":true,"block":null,"blocks":0,"gather":["3,66,2","1,65,2"],"toward":"9,67,0"}
  reach 0,4,0 {"within":true,"block":null,"blocks":0}
  reach 1,7,0 {"within":false,"block":null,"blocks":0,"stand":"3,67,2","steps":0,"gather":["0,66,-1","0,66,2","0,66,3","0,66,4","1,66,-1","1,66,4","1,66,5","2,66,-1","3,66,-1","3,66,0","4,66,1","5,66,4","6,66,2","6,66,5"]}
  reach 0,12,1 {"within":false,"block":null,"blocks":0,"stand":"2,72,3","steps":0,"gather":[]}
  reach 2,30,-1 {"within":false,"block":null,"blocks":0,"stand":"3,72,3","steps":19,"gather":[]}
  reach 0,2,0 {"within":true,"block":null,"blocks":0}
  reach 3,6,0 {"within":false,"block":null,"blocks":0,"stand":"5,69,2","steps":0,"gather":["4,68,0","6,68,0"]}
  reach -2,9,2 {"within":false,"block":null,"blocks":0,"stand":"0,72,4","steps":0,"gather":[]}
@ 2,71,4
  pit  {"feet":"2,72,4","pit":false,"feet_y":72}
  home {"feet":"2,72,4","pit":false,"feet_y":72}
  clmb {"feet":"2,72,4","pit":true,"feet_y":72,"climb":true,"approach":"3,72,3","need":null,"stair":null,"pillar":true,"block":null,"blocks":0,"gather":[],"toward":"9,67,0"}
  reach 0,4,0 {"within":true,"block":null,"blocks":0}
  reach 1,7,0 {"within":false,"block":null,"blocks":0,"stand":"3,72,4","steps":1,"gather":[]}
  reach 0,12,1 {"within":false,"block":null,"blocks":0,"stand":"2,72,5","steps":6,"gather":[]}
  reach 2,30,-1 {"within":false,"block":null,"blocks":0,"why":"no ground to stand on below or beside it within 24 blocks"}
  reach 0,2,0 {"within":true,"block":null,"blocks":0}
  reach 3,6,0 {"within":false,"block":null,"blocks":0,"stand":"3,72,3","steps":1,"gather":[]}
  reach -2,9,2 {"within":false,"block":null,"blocks":0,"stand":"0,72,6","steps":3,"gather":[]}
@ 2,66,6
  pit  {"feet":"2,66,6","pit":true,"feet_y":66,"cave":true,"exit":"4,67,6","stair":null,"pillar":true,"block":null,"blocks":0,"gather":["3,66,6"]}
  home {"feet":"2,66,6","pit":true,"feet_y":66,"cave":true,"exit":"4,67,6","stair":null,"pillar":true,"block":null,"blocks":0,"gather":["3,66,6"],"toward":"9,67,0"}
  clmb {"feet":"2,66,6","pit":true,"feet_y":66,"cave":true,"climb":true,"approach":"9,65,0","stair":null,"pillar":true,"block":null,"blocks":0,"gather":["3,66,6"],"toward":"9,67,0"}
  reach 0,4,0 {"within":true,"block":null,"blocks":0}
  reach 1,7,0 {"within":false,"block":null,"blocks":0,"stand":"3,72,6","steps":0,"gather":[]}
  reach 0,12,1 {"within":false,"block":null,"blocks":0,"stand":"2,72,7","steps":1,"gather":[]}
  reach 2,30,-1 {"within":false,"block":null,"blocks":0,"stand":"3,72,4","steps":20,"gather":[]}
  reach 0,2,0 {"within":true,"block":null,"blocks":0}
  reach 3,6,0 {"within":false,"block":null,"blocks":0,"stand":"5,69,6","steps":0,"gather":["3,68,8"]}
  reach -2,9,2 {"within":false,"block":null,"blocks":0,"stand":"0,72,8","steps":0,"gather":[]}
@ 4,67,-6
  pit  {"feet":"4,67,-6","pit":false,"feet_y":67}
  home {"feet":"4,67,-6","pit":false,"feet_y":67}
  clmb {"feet":"4,67,-6","pit":true,"feet_y":67,"climb":true,"approach":"7,67,-2","need":null,"stair":null,"pillar":true,"block":null,"blocks":0,"gather":[],"toward":"9,67,0"}
  reach 0,4,0 {"within":true,"block":null,"blocks":0}
  reach 1,7,0 {"within":false,"block":null,"blocks":0,"stand":"3,70,-5","steps":0,"gather":["4,69,-3","5,69,-2","6,69,-2"]}
  reach 0,12,1 {"within":false,"block":null,"blocks":0,"stand":"3,70,-5","steps":5,"gather":["4,69,-3","5,69,-2","6,69,-2"]}
  reach 2,30,-1 {"within":false,"block":null,"blocks":0,"stand":"4,69,-8","steps":24,"gather":["1,68,-5","3,68,-9"]}
  reach 0,2,0 {"within":true,"block":null,"blocks":0}
  reach 3,6,0 {"within":false,"block":null,"blocks":0,"stand":"7,68,-4","steps":1,"gather":["10,67,-4"]}
  reach -2,9,2 {"within":false,"block":null,"blocks":0,"stand":"3,70,-5","steps":2,"gather":["4,69,-3","5,69,-2","6,69,-2"]}
@ 4,66,-4
  pit  {"feet":"4,66,-4","pit":false,"feet_y":66}
  home {"feet":"4,66,-4","pit":false,"feet_y":66}
  clmb {"feet":"4,66,-4","pit":true,"feet_y":66,"climb":true,"approach":"8,66,0","need":0,"stair":{"dig":["4,68,-3","4,67,-3"],"to":"4,67,-3"},"pillar":true,"block":null,"blocks":0,"gather":[],"toward":"9,67,0"}
  reach 0,4,0 {"within":true,"block":null,"blocks":0}
  reach 1,7,0 {"within":false,"block":null,"blocks":0,"stand":"4,70,-3","steps":0,"gather":["3,69,-5","5,69,-2","6,69,-2"]}
  reach 0,12,1 {"within":false,"block":null,"blocks":0,"stand":"4,70,-3","steps":3,"gather":["3,69,-5","5,69,-2","6,69,-2"]}
  reach 2,30,-1 {"within":false,"block":null,"blocks":0,"stand":"4,70,-3","steps":23,"gather":["3,69,-5","5,69,-2","6,69,-2"]}
  reach 0,2,0 {"within":true,"block":null,"blocks":0}
  reach 3,6,0 {"within":false,"block":null,"blocks":0,"stand":"7,68,-4","steps":0,"gather":["10,67,-4"]}
  reach -2,9,2 {"within":false,"block":null,"blocks":0,"stand":"4,70,-3","steps":1,"gather":["3,69,-5","5,69,-2","6,69,-2"]}
@ 4,67,-2
  pit  {"feet":"4,67,-2","pit":false,"feet_y":67}
  home {"feet":"4,67,-2","pit":false,"feet_y":67}
  clmb {"feet":"4,67,-2","pit":true,"feet_y":67,"climb":true,"approach":"7,67,-2","need":2,"stair":{"dig":["5,69,-2","5,68,-2"],"to":"5,68,-2"},"pillar":true,"block":null,"blocks":0,"gather":[],"toward":"9,67,0"}
  reach 0,4,0 {"within":true,"block":null,"blocks":0}
  reach 1,7,0 {"within":false,"block":null,"blocks":0,"stand":"5,70,-2","steps":0,"gather":["3,69,-5","4,69,-3","4,69,1","6,69,-2"]}
  reach 0,12,1 {"within":false,"block":null,"blocks":0,"stand":"5,71,-1","steps":4,"gather":[]}
  reach 2,30,-1 {"within":false,"block":null,"blocks":0,"stand":"5,71,-1","steps":22,"gather":[]}
  reach 0,2,0 {"within":true,"block":null,"blocks":0}
  reach 3,6,0 {"within":false,"block":null,"blocks":0,"stand":"6,70,-2","steps":0,"gather":["3,69,-5","4,69,-3","4,69,1","5,69,-2"]}
  reach -2,9,2 {"within":false,"block":null,"blocks":0,"stand":"4,70,1","steps":2,"gather":["2,69,4","5,69,-2","6,69,-2"]}
@ 4,69,0
  pit  {"feet":"4,69,0","pit":false,"feet_y":69}
  home {"feet":"4,69,0","pit":false,"feet_y":69}
  clmb {"feet":"4,69,0","pit":true,"feet_y":69,"climb":true,"approach":"9,65,0","need":0,"stair":null,"pillar":true,"block":null,"blocks":0,"gather":["4,69,1"],"toward":"9,67,0"}
  reach 0,4,0 {"within":true,"block":null,"blocks":0}
  reach 1,7,0 {"within":false,"block":null,"blocks":0,"stand":"5,71,-1","steps":1,"gather":[]}
  reach 0,12,1 {"within":false,"block":null,"blocks":0,"stand":"3,72,3","steps":5,"gather":[]}
  reach 2,30,-1 {"within":false,"block":null,"blocks":0,"stand":"5,71,-1","steps":24,"gather":[]}
  reach 0,2,0 {"within":true,"block":null,"blocks":0}
  reach 3,6,0 {"within":false,"block":null,"blocks":0,"stand":"5,71,-1","steps":0,"gather":[]}
  reach -2,9,2 {"within":false,"block":null,"blocks":0,"stand":"1,72,3","steps":2,"gather":[]}
@ 4,66,2
  pit  {"feet":"4,66,2","pit":false,"feet_y":66}
  home {"feet":"4,66,2","pit":false,"feet_y":66}
  clmb {"feet":"4,66,2","pit":true,"feet_y":66,"climb":true,"approach":"8,66,0","need":0,"stair":{"dig":["4,68,1","4,67,1"],"to":"4,67,1"},"pillar":true,"block":null,"blocks":0,"gather":["3,66,2","4,66,1"],"toward":"9,67,0"}
  reach 0,4,0 {"within":true,"block":null,"blocks":0}
  reach 1,7,0 {"within":false,"block":null,"blocks":0,"stand":"5,69,2","steps":0,"gather":["4,68,0","6,68,0"]}
  reach 0,12,1 {"within":false,"block":null,"blocks":0,"stand":"3,72,3","steps":2,"gather":[]}
  reach 2,30,-1 {"within":false,"block":null,"blocks":0,"stand":"5,71,-1","steps":21,"gather":[]}
  reach 0,2,0 {"within":true,"block":null,"blocks":0}
  reach 3,6,0 {"within":false,"block":null,"blocks":0,"stand":"7,70,2","steps":0,"gather":["4,69,1"]}
  reach -2,9,2 {"within":false,"block":null,"blocks":0,"stand":"2,72,4","steps":0,"gather":[]}
@ 4,65,4
  pit  {"feet":"4,65,4","pit":false,"feet_y":65}
  home {"feet":"4,65,4","pit":false,"feet_y":65}
  clmb {"feet":"4,65,4","pit":true,"feet_y":65,"climb":true,"approach":"8,66,0","need":0,"stair":{"dig":["5,66,4"],"to":"5,66,4"},"pillar":true,"block":null,"blocks":0,"gather":["5,66,4","3,65,4"],"toward":"9,67,0"}
  reach 0,4,0 {"within":true,"block":null,"blocks":0}
  reach 1,7,0 {"within":false,"block":null,"blocks":0,"stand":"5,67,4","steps":0,"gather":["3,66,2","3,66,6","4,66,1","4,66,6","4,66,7","6,66,2","6,66,5","7,66,1","7,66,4","8,66,1","8,66,4"]}
  reach 0,12,1 {"within":false,"block":null,"blocks":0,"stand":"3,72,4","steps":1,"gather":[]}
  reach 2,30,-1 {"within":false,"block":null,"blocks":0,"stand":"7,70,2","steps":21,"gather":["4,69,1"]}
  reach 0,2,0 {"within":true,"block":null,"blocks":0}
  reach 3,6,0 {"within":false,"block":null,"blocks":0,"stand":"7,67,4","steps":0,"gather":["4,66,1","4,66,6","4,66,7","5,66,4","6,66,2","6,66,5","7,66,1","8,66,1","8,66,4","9,66,6","10,66,3","10,66,4"]}
  reach -2,9,2 {"within":false,"block":null,"blocks":0,"stand":"2,72,6","steps":0,"gather":[]}
@ 4,67,6
  pit  {"feet":"4,67,6","pit":false,"feet_y":67}
  home {"feet":"4,67,6","pit":false,"feet_y":67}
  clmb {"feet":"4,67,6","pit":true,"feet_y":67,"climb":true,"approach":"8,66,3","need":1,"stair":{"dig":["5,68,6"],"to":"5,68,6"},"pillar":true,"block":null,"blocks":0,"gather":["5,68,6"],"toward":"9,67,0"}
  reach 0,4,0 {"within":true,"block":null,"blocks":0}
  reach 1,7,0 {"within":false,"block":null,"blocks":0,"stand":"5,69,6","steps":0,"gather":["3,68,8"]}
  reach 0,12,1 {"within":false,"block":null,"blocks":0,"stand":"3,72,6","steps":3,"gather":[]}
  reach 2,30,-1 {"within":false,"block":null,"blocks":0,"stand":"5,70,5","steps":23,"gather":["2,69,4"]}
  reach 0,2,0 {"within":true,"block":null,"blocks":0}
  reach 3,6,0 {"within":false,"block":null,"blocks":0,"stand":"5,70,5","steps":0,"gather":["2,69,4"]}
  reach -2,9,2 {"within":false,"block":null,"blocks":0,"stand":"2,72,8","steps":0,"gather":[]}
@ 6,65,-6
  pit  {"feet":"6,65,-6","pit":false,"feet_y":65}
  home {"feet":"6,65,-6","pit":false,"feet_y":65}
  clmb {"feet":"6,65,-6","pit":true,"feet_y":65,"climb":true,"approach":"8,66,0","need":0,"stair":{"dig":["5,66,-6"],"to":"5,66,-6"},"pillar":true,"block":null,"blocks":0,"gather":["6,65,-5","6,65,-7"],"toward":"9,67,0"}
  reach 0,4,0 {"within":true,"block":null,"blocks":0}
  reach 1,7,0 {"within":false,"block":null,"blocks":0,"stand":"7,68,-4","steps":0,"gather":["10,67,-4"]}
  reach 0,12,1 {"within":false,"block":null,"blocks":0,"stand":"4,70,-3","steps":4,"gather":["3,69,-5","5,69,-2","6,69,-2"]}
  reach 2,30,-1 {"within":false,"block":null,"blocks":0,"stand":"8,67,-7","steps":23,"gather":["5,66,-8","5,66,-7","7,66,-8","7,66,-4","8,66,-8","9,66,-7","9,66,-6","9,66,-5","10,66,-9","11,66,-10","11,66,-7","11,66,-4"]}
  reach 0,2,0 {"within":true,"block":null,"blocks":0}
  reach 3,6,0 {"within":false,"block":null,"blocks":0,"stand":"9,67,-6","steps":0,"gather":["7,66,-8","7,66,-4","8,66,-8","8,66,-7","9,66,-7","9,66,-5","10,66,-9","11,66,-7","11,66,-4","12,66,-9","12,66,-6"]}
  reach -2,9,2 {"within":false,"block":null,"blocks":0,"stand":"3,70,-5","steps":0,"gather":["4,69,-3","5,69,-2","6,69,-2"]}
@ 6,66,-4
  pit  {"feet":"6,66,-4","pit":false,"feet_y":66}
  home {"feet":"6,66,-4","pit":false,"feet_y":66}
  clmb {"feet":"6,66,-4","pit":true,"feet_y":66,"climb":true,"approach":"9,65,0","need":0,"stair":{"dig":["7,67,-4"],"to":"7,67,-4"},"pillar":true,"block":null,"blocks":0,"gather":["7,67,-4","7,66,-4"],"toward":"9,67,0"}
  reach 0,4,0 {"within":true,"block":null,"blocks":0}
  reach 1,7,0 {"within":false,"block":null,"blocks":0,"stand":"7,68,-4","steps":0,"gather":["10,67,-4"]}
  reach 0,12,1 {"within":false,"block":null,"blocks":0,"stand":"5,71,-1","steps":3,"gather":[]}
  reach 2,30,-1 {"within":false,"block":null,"blocks":0,"stand":"7,68,-4","steps":24,"gather":["10,67,-4"]}
  reach 0,2,0 {"within":true,"block":null,"blocks":0}
  reach 3,6,0 {"within":false,"block":null,"blocks":0,"stand":"9,67,-4","steps":0,"gather":["6,66,-2","7,66,-4","7,66,-2","8,66,-7","8,66,-1","9,66,-7","9,66,-6","9,66,-5","10,66,-2","11,66,-7","11,66,-4","12,66,-6"]}
  reach -2,9,2 {"within":false,"block":null,"blocks":0,"stand":"5,71,-1","steps":0,"gather":[]}
@ 6,70,-2
  pit  {"feet":"6,70,-2","pit":false,"feet_y":70}
  home {"feet":"6,70,-2","pit":false,"feet_y":70}
  clmb {"feet":"6,70,-2","pit":true,"feet_y":70,"climb":true,"approach":"9,65,0","need":null,"stair":null,"pillar":true,"block":null,"blocks":0,"gather":[],"toward":"9,67,0"}
  reach 0,4,0 {"within":true,"block":null,"blocks":0}
  reach 1,7,0 {"within":false,"block":null,"blocks":0,"stand":"5,71,-1","steps":2,"gather":[]}
  reach 0,12,1 {"within":false,"block":null,"blocks":0,"stand":"5,71,-1","steps":7,"gather":[]}
  reach 2,30,-1 {"within":false,"block":null,"blocks":0,"why":"no ground to stand on below or beside it within 24 blocks"}
  reach 0,2,0 {"within":true,"block":null,"blocks":0}
  reach 3,6,0 {"within":false,"block":null,"blocks":0,"stand":"9,70,-1","steps":2,"gather":[]}
  reach -2,9,2 {"within":false,"block":null,"blocks":0,"stand":"5,71,-1","steps":4,"gather":[]}
@ 6,69,0
  pit  {"feet":"6,69,0","pit":false,"feet_y":69}
  home {"feet":"6,69,0","pit":false,"feet_y":69}
  clmb {"feet":"6,69,0","pit":true,"feet_y":69,"climb":true,"approach":"9,65,0","need":null,"stair":null,"pillar":true,"block":null,"blocks":0,"gather":[],"toward":"9,67,0"}
  reach 0,4,0 {"within":true,"block":null,"blocks":0}
  reach 1,7,0 {"within":false,"block":null,"blocks":0,"stand":"5,71,-1","steps":1,"gather":[]}
  reach 0,12,1 {"within":false,"block":null,"blocks":0,"stand":"5,71,-1","steps":6,"gather":[]}
  reach 2,30,-1 {"within":false,"block":null,"blocks":0,"why":"no ground to stand on below or beside it within 24 blocks"}
  reach 0,2,0 {"within":true,"block":null,"blocks":0}
  reach 3,6,0 {"within":false,"block":null,"blocks":0,"stand":"8,70,1","steps":1,"gather":["5,69,-2","6,69,-2"]}
  reach -2,9,2 {"within":false,"block":null,"blocks":0,"stand":"3,72,3","steps":2,"gather":[]}
@ 6,67,2
  pit  {"feet":"6,67,2","pit":false,"feet_y":67}
  home {"feet":"6,67,2","pit":false,"feet_y":67}
  clmb {"feet":"6,67,2","pit":true,"feet_y":67,"climb":true,"approach":"9,65,0","need":1,"stair":{"dig":["5,68,2"],"to":"5,68,2"},"pillar":true,"block":null,"blocks":0,"gather":[],"toward":"9,67,0"}
  reach 0,4,0 {"within":true,"block":null,"blocks":0}
  reach 1,7,0 {"within":false,"block":null,"blocks":0,"stand":"7,70,2","steps":0,"gather":["4,69,1"]}
  reach 0,12,1 {"within":false,"block":null,"blocks":0,"stand":"7,70,2","steps":5,"gather":["4,69,1"]}
  reach 2,30,-1 {"within":false,"block":null,"blocks":0,"stand":"8,70,1","steps":22,"gather":["5,69,-2","6,69,-2"]}
  reach 0,2,0 {"within":true,"block":null,"blocks":0}
  reach 3,6,0 {"within":false,"block":null,"blocks":0,"stand":"9,70,2","steps":0,"gather":["11,69,5"]}
  reach -2,9,2 {"within":false,"block":null,"blocks":0,"stand":"3,72,3","steps":0,"gather":[]}
@ 6,67,4
  pit  {"feet":"6,67,4","pit":false,"feet_y":67}
  home {"feet":"6,67,4","pit":false,"feet_y":67}
  clmb {"feet":"6,67,4","pit":true,"feet_y":67,"climb":true,"approach":"9,65,0","need":null,"stair":null,"pillar":true,"block":null,"blocks":0,"gather":[],"toward":"9,67,0"}
  reach 0,4,0 {"within":true,"block":null,"blocks":0}
  reach 1,7,0 {"within":false,"block":null,"blocks":0,"stand":"7,70,3","steps":0,"gather":["4,69,1"]}
  reach 0,12,1 {"within":false,"block":null,"blocks":0,"stand":"5,70,5","steps":5,"gather":["2,69,4"]}
  reach 2,30,-1 {"within":false,"block":null,"blocks":0,"stand":"8,70,3","steps":22,"gather":["11,69,5"]}
  reach 0,2,0 {"within":true,"block":null,"blocks":0}
  reach 3,6,0 {"within":false,"block":null,"blocks":0,"stand":"8,70,3","steps":0,"gather":["11,69,5"]}
  reach -2,9,2 {"within":false,"block":null,"blocks":0,"stand":"3,72,5","steps":0,"gather":[]}
@ 6,65,6
  pit  {"feet":"6,65,6","pit":false,"feet_y":65}
  home {"feet":"6,65,6","pit":false,"feet_y":65}
  clmb {"feet":"6,65,6","pit":true,"feet_y":65,"climb":true,"approach":"9,67,3","need":0,"stair":{"dig":["6,66,5"],"to":"6,66,5"},"pillar":true,"block":null,"blocks":0,"gather":["6,66,5","6,65,7"],"toward":"9,67,0"}
  reach 0,4,0 {"within":true,"block":null,"blocks":0}
  reach 1,7,0 {"within":false,"block":null,"blocks":0,"stand":"5,70,5","steps":0,"gather":["2,69,4"]}
  reach 0,12,1 {"within":false,"block":null,"blocks":0,"stand":"5,70,5","steps":3,"gather":["2,69,4"]}
  reach 2,30,-1 {"within":false,"block":null,"blocks":0,"stand":"7,70,3","steps":21,"gather":["4,69,1"]}
  reach 0,2,0 {"within":true,"block":null,"blocks":0}
  reach 3,6,0 {"within":false,"block":null,"blocks":0,"stand":"9,67,6","steps":0,"gather":["6,66,5","6,66,9","7,66,4","8,66,4","8,66,8","9,66,9","10,66,3","10,66,4","10,66,9","11,66,5","11,66,9","12,66,7","12,66,9"]}
  reach -2,9,2 {"within":false,"block":null,"blocks":0,"stand":"3,72,7","steps":0,"gather":[]}
@ -12,67,-3
  pit  {"feet":"-12,67,-3","pit":false,"feet_y":67}
  home {"feet":"-12,67,-3","pit":false,"feet_y":67}
  clmb {"feet":"-12,67,-3","pit":true,"feet_y":67,"climb":true,"approach":"-6,66,-1","need":2,"stair":{"dig":["-11,69,-3","-11,68,-3"],"to":"-11,68,-3"},"pillar":false,"block":null,"blocks":0,"gather":[],"toward":"9,67,0"}
  reach 0,4,0 {"within":true,"block":null,"blocks":0}
  reach 1,7,0 {"within":false,"block":null,"blocks":0,"stand":"-11,70,-3","steps":0,"gather":[]}
  reach 0,12,1 {"within":false,"block":null,"blocks":0,"stand":"-11,70,-3","steps":5,"gather":[]}
  reach 2,30,-1 {"within":false,"block":null,"blocks":0,"stand":"-11,70,-3","steps":23,"gather":[]}
  reach 0,2,0 {"within":true,"block":null,"blocks":0}
  reach 3,6,0 {"within":false,"block":null,"blocks":0,"stand":"-11,70,-3","steps":0,"gather":[]}
  reach -2,9,2 {"within":false,"block":null,"blocks":0,"stand":"-14,70,1","steps":2,"gather":[]}
@ -11,70,-3
  pit  {"feet":"-11,70,-3","pit":false,"feet_y":70}
  home {"feet":"-11,70,-3","pit":false,"feet_y":70}
  clmb {"feet":"-11,70,-3","pit":true,"feet_y":70,"climb":true,"approach":"-5,67,-3","need":null,"stair":null,"pillar":true,"block":null,"blocks":0,"gather":[],"toward":"9,67,0"}
  reach 0,4,0 {"within":true,"block":null,"blocks":0}
  reach 1,7,0 {"within":false,"block":null,"blocks":0,"stand":"-11,70,-3","steps":3,"gather":[]}
  reach 0,12,1 {"within":false,"block":null,"blocks":0,"stand":"-11,70,-3","steps":8,"gather":[]}
  reach 2,30,-1 {"within":false,"block":null,"blocks":0,"why":"no ground to stand on below or beside it within 24 blocks"}
  reach 0,2,0 {"within":true,"block":null,"blocks":0}
  reach 3,6,0 {"within":false,"block":null,"blocks":0,"stand":"-9,69,-1","steps":3,"gather":[]}
  reach -2,9,2 {"within":false,"block":null,"blocks":0,"stand":"-14,70,1","steps":5,"gather":[]}
@ -13,65,-2
  pit  {"feet":"-13,65,-2","pit":false,"feet_y":65}
  home {"feet":"-13,65,-2","pit":false,"feet_y":65}
  clmb {"feet":"-13,65,-2","pit":true,"feet_y":65,"climb":true,"approach":"-8,67,0","need":0,"stair":{"dig":["-12,66,-2"],"to":"-12,66,-2"},"pillar":true,"block":null,"blocks":0,"gather":["-12,66,-2","-14,66,-2","-13,66,-3","-12,65,-2","-13,65,-1"],"toward":"9,67,0"}
  reach 0,4,0 {"within":true,"block":null,"blocks":0}
  reach 1,7,0 {"within":false,"block":null,"blocks":0,"stand":"-12,67,-2","steps":0,"gather":["-14,66,-2","-13,66,-3","-12,66,0","-11,66,0","-10,66,0","-10,66,1","-9,66,-1"]}
  reach 0,12,1 {"within":false,"block":null,"blocks":0,"stand":"-14,70,1","steps":3,"gather":[]}
  reach 2,30,-1 {"within":false,"block":null,"blocks":0,"stand":"-11,70,-3","steps":20,"gather":[]}
  reach 0,2,0 {"within":true,"block":null,"blocks":0}
  reach 3,6,0 {"within":false,"block":null,"blocks":0,"stand":"-11,70,-3","steps":0,"gather":[]}
  reach -2,9,2 {"within":false,"block":null,"blocks":0,"stand":"-14,70,1","steps":0,"gather":[]}
@ 8,65,2
  pit  {"feet":"8,65,2","pit":false,"feet_y":65}
  home {"feet":"8,65,2","pit":false,"feet_y":65}
  clmb {"feet":"8,65,2","pit":true,"feet_y":65,"climb":true,"need":0,"stair":{"dig":["8,67,2"],"to":"8,66,3"},"pillar":false,"block":null,"blocks":0,"gather":["8,66,1","7,65,2"],"toward":"9,67,0"}
  reach 0,4,0 {"within":true,"block":null,"blocks":0}
  reach 1,7,0 {"within":false,"block":null,"blocks":0,"stand":"9,70,2","steps":0,"gather":["11,69,5"]}
  reach 0,12,1 {"within":false,"block":null,"blocks":0,"stand":"8,70,3","steps":2,"gather":["11,69,5"]}
  reach 2,30,-1 {"within":false,"block":null,"blocks":0,"stand":"9,70,1","steps":21,"gather":["6,69,-2"]}
  reach 0,2,0 {"within":true,"block":null,"blocks":0}
  reach 3,6,0 {"within":false,"block":null,"blocks":0,"stand":"11,68,2","steps":0,"gather":["9,67,2","13,67,4"]}
  reach -2,9,2 {"within":false,"block":null,"blocks":0,"stand":"5,70,5","steps":0,"gather":["2,69,4"]}
@ 9,68,2
  pit  {"feet":"9,68,2","pit":false,"feet_y":68}
  home {"feet":"9,68,2","pit":false,"feet_y":68}
  clmb {"feet":"9,68,2","pit":true,"feet_y":68,"climb":true,"approach":"9,65,0","need":1,"stair":{"dig":["8,69,2"],"to":"8,69,2"},"pillar":false,"block":null,"blocks":0,"gather":["8,68,2"],"toward":"9,67,0"}
  reach 0,4,0 {"within":true,"block":null,"blocks":0}
  reach 1,7,0 {"within":false,"block":null,"blocks":0,"stand":"9,70,1","steps":1,"gather":["6,69,-2"]}
  reach 0,12,1 {"within":false,"block":null,"blocks":0,"stand":"9,70,3","steps":5,"gather":["11,69,5"]}
  reach 2,30,-1 {"within":false,"block":null,"blocks":0,"stand":"9,70,1","steps":24,"gather":["6,69,-2"]}
  reach 0,2,0 {"within":true,"block":null,"blocks":0}
  reach 3,6,0 {"within":false,"block":null,"blocks":0,"stand":"13,70,2","steps":0,"gather":["11,69,5"]}
  reach -2,9,2 {"within":false,"block":null,"blocks":0,"stand":"7,70,3","steps":3,"gather":["4,69,1"]}
@ 7,66,3
  pit  {"feet":"7,66,3","pit":false,"feet_y":66}
  home {"feet":"7,66,3","pit":false,"feet_y":66}
  clmb {"feet":"7,66,3","pit":true,"feet_y":66,"climb":true,"approach":"9,65,0","need":0,"stair":{"dig":["7,68,2","7,67,2"],"to":"7,67,2"},"pillar":true,"block":null,"blocks":0,"gather":["7,66,4"],"toward":"9,67,0"}
  reach 0,4,0 {"within":true,"block":null,"blocks":0}
  reach 1,7,0 {"within":false,"block":null,"blocks":0,"stand":"8,70,3","steps":0,"gather":["11,69,5"]}
  reach 0,12,1 {"within":false,"block":null,"blocks":0,"stand":"7,70,3","steps":4,"gather":["4,69,1"]}
  reach 2,30,-1 {"within":false,"block":null,"blocks":0,"stand":"9,70,2","steps":21,"gather":["11,69,5"]}
  reach 0,2,0 {"within":true,"block":null,"blocks":0}
  reach 3,6,0 {"within":false,"block":null,"blocks":0,"stand":"10,67,3","steps":0,"gather":["7,66,0","7,66,1","7,66,4","8,66,1","8,66,4","9,66,6","10,66,4","11,66,5","12,66,1","13,66,0","13,66,1","13,66,5"]}
  reach -2,9,2 {"within":false,"block":null,"blocks":0,"stand":"5,70,5","steps":0,"gather":["2,69,4"]}
@ -6,65,-11
  pit  {"feet":"-6,66,-11","pit":false,"feet_y":66}
  home {"feet":"-6,66,-11","pit":false,"feet_y":66}
  clmb {"feet":"-6,66,-11","pit":true,"feet_y":66,"climb":true,"approach":"2,66,-11","need":0,"stair":null,"pillar":true,"block":null,"blocks":0,"gather":["-5,66,-11"],"toward":"9,67,0"}
  reach 0,4,0 {"within":true,"block":null,"blocks":0}
  reach 1,7,0 {"within":false,"block":null,"blocks":0,"stand":"-5,67,-11","steps":0,"gather":["-8,66,-8","-7,66,-13","-7,66,-10","-7,66,-9","-7,66,-8","-6,66,-14","-5,66,-13","-5,66,-12","-5,66,-9","-4,66,-10","-4,66,-8","-3,66,-13","-3,66,-11","-3,66,-10","-3,66,-8","-2,66,-14"]}
  reach 0,12,1 {"within":false,"block":null,"blocks":0,"stand":"-4,69,-10","steps":4,"gather":[]}
  reach 2,30,-1 {"within":false,"block":null,"blocks":0,"stand":"-4,71,-13","steps":20,"gather":[]}
  reach 0,2,0 {"within":true,"block":null,"blocks":0}
  reach 3,6,0 {"within":false,"block":null,"blocks":0,"stand":"-3,67,-11","steps":0,"gather":["-6,66,-14","-5,66,-13","-5,66,-12","-5,66,-11","-5,66,-9","-4,66,-10","-4,66,-8","-3,66,-13","-3,66,-10","-3,66,-8","-2,66,-14","-1,66,-9","0,66,-12"]}
  reach -2,9,2 {"within":false,"block":null,"blocks":0,"stand":"-7,69,-7","steps":1,"gather":["-6,68,-6","-4,68,-10"]}
@ -5,66,-11
  pit  {"feet":"-5,67,-11","pit":false,"feet_y":67}
  home {"feet":"-5,67,-11","pit":false,"feet_y":67}
  clmb {"feet":"-5,67,-11","pit":true,"feet_y":67,"climb":true,"approach":"3,66,-11","need":0,"stair":null,"pillar":true,"block":null,"blocks":0,"gather":["-4,67,-11"],"toward":"9,67,0"}
  reach 0,4,0 {"within":true,"block":null,"blocks":0}
  reach 1,7,0 {"within":false,"block":null,"blocks":0,"stand":"-4,68,-11","steps":0,"gather":["-6,67,-14","-4,67,-10","-3,67,-10","-2,67,-11"]}
  reach 0,12,1 {"within":false,"block":null,"blocks":0,"stand":"-4,69,-10","steps":5,"gather":[]}
  reach 2,30,-1 {"within":false,"block":null,"blocks":0,"stand":"-4,71,-13","steps":21,"gather":[]}
  reach 0,2,0 {"within":true,"block":null,"blocks":0}
  reach 3,6,0 {"within":false,"block":null,"blocks":0,"stand":"-2,68,-11","steps":0,"gather":["-4,67,-11","-4,67,-10","-3,67,-10","1,67,-13"]}
  reach -2,9,2 {"within":false,"block":null,"blocks":0,"stand":"-7,69,-7","steps":2,"gather":["-6,68,-6","-4,68,-10"]}
@ -7,67,-10
  pit  {"feet":"-7,67,-10","pit":false,"feet_y":67}
  home {"feet":"-7,67,-10","pit":false,"feet_y":67}
  clmb {"feet":"-7,67,-10","pit":true,"feet_y":67,"climb":true,"approach":"1,65,-11","need":null,"stair":null,"pillar":true,"block":null,"blocks":0,"gather":[],"toward":"9,67,0"}
  reach 0,4,0 {"within":true,"block":null,"blocks":0}
  reach 1,7,0 {"within":false,"block":null,"blocks":0,"stand":"-4,69,-10","steps":1,"gather":[]}
  reach 0,12,1 {"within":false,"block":null,"blocks":0,"stand":"-7,69,-7","steps":6,"gather":["-6,68,-6","-4,68,-10"]}
  reach 2,30,-1 {"within":false,"block":null,"blocks":0,"stand":"-4,71,-13","steps":22,"gather":[]}
  reach 0,2,0 {"within":true,"block":null,"blocks":0}
  reach 3,6,0 {"within":false,"block":null,"blocks":0,"stand":"-4,69,-10","steps":0,"gather":[]}
  reach -2,9,2 {"within":false,"block":null,"blocks":0,"stand":"-7,69,-7","steps":3,"gather":["-6,68,-6","-4,68,-10"]}
@ -1,65,-5
  pit  {"feet":"-1,65,-5","pit":false,"feet_y":65}
  home {"feet":"-1,65,-5","pit":false,"feet_y":65}
  clmb {"feet":"-1,65,-5","pit":true,"feet_y":65,"climb":true,"approach":"6,67,-3","need":1,"stair":{"dig":["0,66,-5"],"to":"0,66,-5"},"pillar":true,"block":null,"blocks":0,"gather":["0,66,-5","-1,65,-6"],"toward":"9,67,0"}
  reach 0,4,0 {"within":true,"block":null,"blocks":0}
  reach 1,7,0 {"within":false,"block":null,"blocks":0,"stand":"0,67,-5","steps":0,"gather":["-3,66,-8","-3,66,-7","-3,66,-2","-2,66,-6","-1,66,-3","-1,66,-2","1,66,-7","1,66,-2","2,66,-8","2,66,-7","2,66,-5","3,66,-7"]}
  reach 0,12,1 {"within":false,"block":null,"blocks":0,"stand":"1,69,-5","steps":4,"gather":["0,68,-7","4,68,-8"]}
  reach 2,30,-1 {"within":false,"block":null,"blocks":0,"stand":"0,71,-7","steps":20,"gather":[]}
  reach 0,2,0 {"within":true,"block":null,"blocks":0}
  reach 3,6,0 {"within":false,"block":null,"blocks":0,"stand":"2,67,-5","steps":0,"gather":["-1,66,-3","-1,66,-2","0,66,-5","1,66,-7","1,66,-2","2,66,-8","2,66,-7","3,66,-7","4,66,-6","4,66,-2","5,66,-8","5,66,-7","5,66,-2"]}
  reach -2,9,2 {"within":false,"block":null,"blocks":0,"stand":"-4,71,-2","steps":0,"gather":[]}
@ 0,67,-5
  pit  {"feet":"0,67,-5","pit":false,"feet_y":67}
  home {"feet":"0,67,-5","pit":false,"feet_y":67}
  clmb {"feet":"0,67,-5","pit":true,"feet_y":67,"climb":true,"approach":"6,67,-3","need":1,"stair":{"dig":["1,68,-5"],"to":"1,68,-5"},"pillar":true,"block":null,"blocks":0,"gather":["1,68,-5","1,67,-5"],"toward":"9,67,0"}
  reach 0,4,0 {"within":true,"block":null,"blocks":0}
  reach 1,7,0 {"within":false,"block":null,"blocks":0,"stand":"1,69,-5","steps":0,"gather":["0,68,-7","4,68,-8"]}
  reach 0,12,1 {"within":false,"block":null,"blocks":0,"stand":"1,69,-5","steps":6,"gather":["0,68,-7","4,68,-8"]}
  reach 2,30,-1 {"within":false,"block":null,"blocks":0,"stand":"0,71,-7","steps":22,"gather":[]}
  reach 0,2,0 {"within":true,"block":null,"blocks":0}
  reach 3,6,0 {"within":false,"block":null,"blocks":0,"stand":"3,70,-5","steps":0,"gather":["4,69,-3","5,69,-2","6,69,-2"]}
  reach -2,9,2 {"within":false,"block":null,"blocks":0,"stand":"-4,71,-2","steps":1,"gather":[]}
@ -2,65,-4
  pit  {"feet":"-2,65,-4","pit":false,"feet_y":65}
  home {"feet":"-2,65,-4","pit":false,"feet_y":65}
  clmb {"feet":"-2,65,-4","pit":true,"feet_y":65,"climb":true,"approach":"6,66,-4","need":0,"stair":{"dig":["-2,66,-5"],"to":"-2,66,-5"},"pillar":true,"block":null,"blocks":0,"gather":["-2,65,-3"],"toward":"9,67,0"}
  reach 0,4,0 {"within":true,"block":null,"blocks":0}
  reach 1,7,0 {"within":false,"block":null,"blocks":0,"stand":"0,68,-3","steps":0,"gather":["-1,67,-6","1,67,-5","2,67,-1"]}
  reach 0,12,1 {"within":false,"block":null,"blocks":0,"stand":"-4,71,-2","steps":2,"gather":[]}
  reach 2,30,-1 {"within":false,"block":null,"blocks":0,"stand":"0,71,-7","steps":20,"gather":[]}
  reach 0,2,0 {"within":true,"block":null,"blocks":0}
  reach 3,6,0 {"within":false,"block":null,"blocks":0,"stand":"0,67,-5","steps":0,"gather":["-3,66,-8","-3,66,-7","-3,66,-2","-2,66,-6","-1,66,-3","-1,66,-2","1,66,-7","1,66,-2","2,66,-8","2,66,-7","2,66,-5","3,66,-7"]}
  reach -2,9,2 {"within":false,"block":null,"blocks":0,"stand":"-4,71,-2","steps":0,"gather":[]}
@ -11,67,10
  pit  {"feet":"-11,67,10","pit":false,"feet_y":67}
  home {"feet":"-11,67,10","pit":false,"feet_y":67}
  clmb {"feet":"-11,67,10","pit":true,"feet_y":67,"climb":true,"approach":"-3,65,10","need":1,"stair":{"dig":["-11,69,10","-11,68,9"],"to":"-11,68,9"},"pillar":false,"block":null,"blocks":0,"gather":["-11,68,9"],"toward":"9,67,0"}
  reach 0,4,0 {"within":true,"block":null,"blocks":0}
  reach 1,7,0 {"within":false,"block":null,"blocks":0,"stand":"-10,72,10","steps":0,"gather":[]}
  reach 0,12,1 {"within":false,"block":null,"blocks":0,"stand":"-11,72,11","steps":2,"gather":[]}
  reach 2,30,-1 {"within":false,"block":null,"blocks":0,"stand":"-10,72,9","steps":21,"gather":[]}
  reach 0,2,0 {"within":true,"block":null,"blocks":0}
  reach 3,6,0 {"within":false,"block":null,"blocks":0,"stand":"-9,70,9","steps":0,"gather":[]}
  reach -2,9,2 {"within":false,"block":null,"blocks":0,"stand":"-12,72,11","steps":0,"gather":[]}
@ -10,67,10
  pit  {"feet":"-10,67,10","pit":false,"feet_y":67}
  home {"feet":"-10,67,10","pit":false,"feet_y":67}
  clmb {"feet":"-10,67,10","pit":true,"feet_y":67,"climb":true,"approach":"-4,65,8","need":0,"stair":{"dig":["-11,69,10","-11,68,10"],"to":"-11,68,10"},"pillar":true,"block":null,"blocks":0,"gather":["-11,68,10","-9,67,10","-11,67,10"],"toward":"9,67,0"}
  reach 0,4,0 {"within":true,"block":null,"blocks":0}
  reach 1,7,0 {"within":false,"block":null,"blocks":0,"stand":"-10,72,9","steps":0,"gather":[]}
  reach 0,12,1 {"within":false,"block":null,"blocks":0,"stand":"-10,72,11","steps":2,"gather":[]}
  reach 2,30,-1 {"within":false,"block":null,"blocks":0,"stand":"-10,72,9","steps":21,"gather":[]}
  reach 0,2,0 {"within":true,"block":null,"blocks":0}
  reach 3,6,0 {"within":false,"block":null,"blocks":0,"stand":"-9,70,9","steps":0,"gather":[]}
  reach -2,9,2 {"within":false,"block":null,"blocks":0,"stand":"-12,72,11","steps":0,"gather":[]}
@ -12,65,11
  pit  {"feet":"-12,65,11","pit":false,"feet_y":65}
  home {"feet":"-12,65,11","pit":false,"feet_y":65}
  clmb {"feet":"-12,65,11","pit":true,"feet_y":65,"climb":true,"approach":"-5,66,10","need":0,"stair":{"dig":["-12,66,12"],"to":"-12,66,12"},"pillar":true,"block":null,"blocks":0,"gather":["-13,66,11","-12,66,12","-12,65,10"],"toward":"9,67,0"}
  reach 0,4,0 {"within":true,"block":null,"blocks":0}
  reach 1,7,0 {"within":false,"block":null,"blocks":0,"stand":"-11,69,12","steps":0,"gather":["-11,68,9","-9,68,9"]}
  reach 0,12,1 {"within":false,"block":null,"blocks":0,"stand":"-12,72,11","steps":1,"gather":[]}
  reach 2,30,-1 {"within":false,"block":null,"blocks":0,"stand":"-10,72,10","steps":18,"gather":[]}
  reach 0,2,0 {"within":true,"block":null,"blocks":0}
  reach 3,6,0 {"within":false,"block":null,"blocks":0,"stand":"-9,67,11","steps":0,"gather":["-12,66,12","-12,66,14","-11,66,9","-11,66,13","-11,66,14","-10,66,9","-10,66,10","-9,66,9","-8,66,9","-8,66,12","-7,66,14","-6,66,10","-6,66,11","-6,66,12","-6,66,14"]}
  reach -2,9,2 {"within":false,"block":null,"blocks":0,"stand":"-12,72,11","steps":0,"gather":[]}
@ 7,65,-6
  pit  {"feet":"7,65,-6","pit":false,"feet_y":65}
  home {"feet":"7,65,-6","pit":false,"feet_y":65}
  clmb {"feet":"7,65,-6","pit":true,"feet_y":65,"climb":true,"approach":"8,67,-1","need":0,"stair":null,"pillar":true,"block":null,"blocks":0,"gather":["8,65,-6","7,65,-7"],"toward":"9,67,0"}
  reach 0,4,0 {"within":true,"block":null,"blocks":0}
  reach 1,7,0 {"within":false,"block":null,"blocks":0,"stand":"7,68,-4","steps":0,"gather":["10,67,-4"]}
  reach 0,12,1 {"within":false,"block":null,"blocks":0,"stand":"7,68,-4","steps":5,"gather":["10,67,-4"]}
  reach 2,30,-1 {"within":false,"block":null,"blocks":0,"stand":"9,67,-7","steps":23,"gather":["7,66,-8","7,66,-4","8,66,-8","8,66,-7","9,66,-6","9,66,-5","10,66,-9","11,66,-10","11,66,-7","11,66,-4","12,66,-9","12,66,-6"]}
  reach 0,2,0 {"within":true,"block":null,"blocks":0}
  reach 3,6,0 {"within":false,"block":null,"blocks":0,"stand":"10,66,-6","steps":0,"gather":["7,65,-7","7,65,-3","8,65,-9","8,65,-6","8,65,-4","9,65,-5","9,65,-4","10,65,-7","10,65,-4","11,65,-8","11,65,-6","11,65,-5","11,65,-3","13,65,-8"]}
  reach -2,9,2 {"within":false,"block":null,"blocks":0,"stand":"4,70,-3","steps":0,"gather":["3,69,-5","5,69,-2","6,69,-2"]}
@ 8,66,-6
  pit  {"feet":"8,66,-6","pit":false,"feet_y":66}
  home {"feet":"8,66,-6","pit":false,"feet_y":66}
  clmb {"feet":"8,66,-6","pit":true,"feet_y":66,"climb":true,"approach":"8,66,0","need":0,"stair":null,"pillar":true,"block":null,"blocks":0,"gather":["9,66,-6","8,66,-7"],"toward":"9,67,0"}
  reach 0,4,0 {"within":true,"block":null,"blocks":0}
  reach 1,7,0 {"within":false,"block":null,"blocks":0,"stand":"9,67,-6","steps":1,"gather":["7,66,-8","7,66,-4","8,66,-8","8,66,-7","9,66,-7","9,66,-5","10,66,-9","11,66,-7","11,66,-4","12,66,-9","12,66,-6"]}
  reach 0,12,1 {"within":false,"block":null,"blocks":0,"stand":"7,68,-4","steps":6,"gather":["10,67,-4"]}
  reach 2,30,-1 {"within":false,"block":null,"blocks":0,"stand":"10,68,-9","steps":24,"gather":["11,67,-10","13,67,-6"]}
  reach 0,2,0 {"within":true,"block":null,"blocks":0}
  reach 3,6,0 {"within":false,"block":null,"blocks":0,"stand":"12,69,-5","steps":0,"gather":[]}
  reach -2,9,2 {"within":false,"block":null,"blocks":0,"stand":"4,70,-3","steps":1,"gather":["3,69,-5","5,69,-2","6,69,-2"]}
@ 6,66,-5
  pit  {"feet":"6,66,-5","pit":false,"feet_y":66}
  home {"feet":"6,66,-5","pit":false,"feet_y":66}
  clmb {"feet":"6,66,-5","pit":true,"feet_y":66,"climb":true,"approach":"9,65,0","need":null,"stair":null,"pillar":true,"block":null,"blocks":0,"gather":[],"toward":"9,67,0"}
  reach 0,4,0 {"within":true,"block":null,"blocks":0}
  reach 1,7,0 {"within":false,"block":null,"blocks":0,"stand":"7,68,-4","steps":1,"gather":["10,67,-4"]}
  reach 0,12,1 {"within":false,"block":null,"blocks":0,"stand":"4,70,-3","steps":4,"gather":["3,69,-5","5,69,-2","6,69,-2"]}
  reach 2,30,-1 {"within":false,"block":null,"blocks":0,"stand":"7,68,-4","steps":24,"gather":["10,67,-4"]}
  reach 0,2,0 {"within":true,"block":null,"blocks":0}
  reach 3,6,0 {"within":false,"block":null,"blocks":0,"stand":"9,67,-5","steps":0,"gather":["6,66,-2","7,66,-8","7,66,-4","7,66,-2","8,66,-8","8,66,-7","9,66,-7","9,66,-6","10,66,-2","11,66,-7","11,66,-4","12,66,-6"]}
  reach -2,9,2 {"within":false,"block":null,"blocks":0,"stand":"4,70,-3","steps":0,"gather":["3,69,-5","5,69,-2","6,69,-2"]}
@ 0,68,6
  pit  {"feet":"0,68,6","pit":true,"feet_y":68,"cave":true,"exit":"4,67,6","stair":{"dig":["0,69,5"],"to":"0,69,5"},"pillar":true,"block":null,"blocks":0,"gather":["0,69,5"]}
  home {"feet":"0,68,6","pit":true,"feet_y":68,"cave":true,"exit":"4,67,6","stair":{"dig":["0,69,5"],"to":"0,69,5"},"pillar":true,"block":null,"blocks":0,"gather":["0,69,5"],"toward":"9,67,0"}
  clmb {"feet":"0,68,6","pit":true,"feet_y":68,"cave":true,"climb":true,"approach":"9,65,0","stair":{"dig":["0,69,5"],"to":"0,69,5"},"pillar":true,"block":null,"blocks":0,"gather":["0,69,5"],"toward":"9,67,0"}
  reach 0,4,0 {"within":true,"block":null,"blocks":0}
  reach 1,7,0 {"within":false,"block":null,"blocks":0,"stand":"1,72,6","steps":0,"gather":[]}
  reach 0,12,1 {"within":false,"block":null,"blocks":0,"stand":"0,72,7","steps":3,"gather":[]}
  reach 2,30,-1 {"within":false,"block":null,"blocks":0,"stand":"2,72,5","steps":21,"gather":[]}
  reach 0,2,0 {"within":true,"block":null,"blocks":0}
  reach 3,6,0 {"within":false,"block":null,"blocks":0,"stand":"3,72,6","steps":0,"gather":[]}
  reach -2,9,2 {"within":false,"block":null,"blocks":0,"stand":"-2,72,8","steps":0,"gather":[]}
@ 1,67,6
  pit  {"feet":"1,67,6","pit":true,"feet_y":67,"cave":true,"exit":"4,67,6","stair":null,"pillar":true,"block":null,"blocks":0,"gather":["0,67,6"]}
  home {"feet":"1,67,6","pit":true,"feet_y":67,"cave":true,"exit":"4,67,6","stair":null,"pillar":true,"block":null,"blocks":0,"gather":["0,67,6"],"toward":"9,67,0"}
  clmb {"feet":"1,67,6","pit":true,"feet_y":67,"cave":true,"climb":true,"approach":"9,65,0","stair":null,"pillar":true,"block":null,"blocks":0,"gather":["0,67,6"],"toward":"9,67,0"}
  reach 0,4,0 {"within":true,"block":null,"blocks":0}
  reach 1,7,0 {"within":false,"block":null,"blocks":0,"stand":"2,72,6","steps":0,"gather":[]}
  reach 0,12,1 {"within":false,"block":null,"blocks":0,"stand":"1,72,7","steps":2,"gather":[]}
  reach 2,30,-1 {"within":false,"block":null,"blocks":0,"stand":"3,72,5","steps":20,"gather":[]}
  reach 0,2,0 {"within":true,"block":null,"blocks":0}
  reach 3,6,0 {"within":false,"block":null,"blocks":0,"stand":"3,72,5","steps":0,"gather":[]}
  reach -2,9,2 {"within":false,"block":null,"blocks":0,"stand":"-1,72,8","steps":0,"gather":[]}
@ -1,65,7
  pit  {"feet":"-1,65,7","pit":true,"feet_y":65,"cave":true,"exit":"-1,65,10","stair":{"dig":["0,66,7"],"to":"0,66,7"},"pillar":true,"block":null,"blocks":0,"gather":["0,65,7","-2,65,7"]}
  home {"feet":"-1,65,7","pit":true,"feet_y":65,"cave":true,"exit":"-1,65,10","stair":{"dig":["0,66,7"],"to":"0,66,7"},"pillar":true,"block":null,"blocks":0,"gather":["0,65,7","-2,65,7"],"toward":"9,67,0"}
  clmb {"feet":"-1,65,7","pit":true,"feet_y":65,"cave":true,"climb":true,"approach":"9,65,0","stair":{"dig":["0,66,7"],"to":"0,66,7"},"pillar":true,"block":null,"blocks":0,"gather":["0,65,7","-2,65,7"],"toward":"9,67,0"}
  reach 0,4,0 {"within":true,"block":null,"blocks":0}
  reach 1,7,0 {"within":false,"block":null,"blocks":0,"why":"no ground to stand on below or beside it within 24 blocks"}
  reach 0,12,1 {"within":false,"block":null,"blocks":0,"stand":"-1,72,8","steps":0,"gather":[]}
  reach 2,30,-1 {"within":false,"block":null,"blocks":0,"stand":"1,72,6","steps":18,"gather":[]}
  reach 0,2,0 {"within":true,"block":null,"blocks":0}
  reach 3,6,0 {"within":false,"block":null,"blocks":0,"stand":"1,67,6","steps":0,"gather":["-2,66,3","0,66,3","0,66,4","0,66,8","1,66,4","1,66,5","1,66,8","3,66,6","3,66,8","4,66,6","4,66,7"]}
  reach -2,9,2 {"within":false,"block":null,"blocks":0,"stand":"-3,72,9","steps":0,"gather":[]}
all BDB42033E7DCB62D
""";
}
