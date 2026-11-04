namespace MineClaw.Bot.Tests;

/// <summary>The golden dump of <see cref="BuildingGoldenTests"/> (generated from the pre-refactor code).</summary>
internal static class BuildingGoldenData
{
    public const string Expected = """"""
cat skyblock [<null>] => not reached
cat skyblock [] => not reached
cat skyblock [   ] => not reached
cat skyblock [not reached: cancelled] => not reached (the run stopped first)
cat skyblock [Not Reached] => not reached (the run stopped first)
cat skyblock [waiting on supports] => waiting on supports
cat skyblock [waits for an earlier step] => waiting on supports
cat skyblock [no stand: above my reach (I can't climb)] => no stand: above my reach (I can't climb)
cat skyblock [no stand: I can't walk near it from here] => no stand: I can't walk near it from here
cat skyblock [no support within 6 scaffold blocks of 1,2,3] => no support: over the void past my scaffold limit
cat skyblock [no stand reaches scaffold cell 4,5,6] => no stand for its scaffold
cat skyblock [no stand within reach of a support face] => no stand in reach
cat skyblock [no stand] => no stand in reach
cat skyblock [needs a water bucket to go 6 down] => below the floor: needs a water bucket
cat skyblock [water descent blocked at 1,2,3] => no safe water column
cat skyblock [climbing back up fails] => no safe water column
cat skyblock [no edge with open space for a column] => no safe water column
cat skyblock [it would wall me in] => would wall me in
cat skyblock [I'm not standing on anything] => I'm not standing anywhere walkable
cat skyblock [protected] => protected
cat skyblock [protected by policy] => protected
cat skyblock [someone built there] => someone else's block there
cat skyblock [not mine: someone's] => someone else's block there
cat skyblock [not ours] => not mine: left
cat skyblock [wrong block] => a wrong block there
cat skyblock [missing cobblestone slab (need 40, have 0): ask] => no cobblestone slab on hand
cat skyblock [missing planks] => no planks on hand
cat skyblock [missing #12 thing 1,2,3 here] => no  thing  here on hand
cat skyblock [waits for another lane's step] => waiting on another lane
cat skyblock [needs step #12 (skipped)] => needs a skipped step
cat skyblock [waited too long: nothing ready] => waited too long
cat skyblock [goto: no path] => no path to its stand
cat skyblock [jammed] => failed: jammed
cat skyblock [nothing solid on any side (1,2,3)] => failed: nothing solid on any side
cat skyblock [placed at -1,64,-3 but it fell; retry later] => failed: placed at  but it fell
cat skyblock [xyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyy] => failed: xyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyy
cat skyblock [  waiting on supports  ] => waiting on supports
cat skyblock [#4 failed 12 times] => failed: failed  times
cat skyblock [MISSING Glass] => no MISSING Glass on hand
cat skyblock [Someone] => someone else's block there
cat normal [<null>] => not reached
cat normal [] => not reached
cat normal [   ] => not reached
cat normal [not reached: cancelled] => not reached (the run stopped first)
cat normal [Not Reached] => not reached (the run stopped first)
cat normal [waiting on supports] => waiting on supports
cat normal [waits for an earlier step] => waiting on supports
cat normal [no stand: above my reach (I can't climb)] => no stand: above my reach (I can't climb)
cat normal [no stand: I can't walk near it from here] => no stand: I can't walk near it from here
cat normal [no support within 6 scaffold blocks of 1,2,3] => no support: past my scaffold limit
cat normal [no stand reaches scaffold cell 4,5,6] => no stand for its scaffold
cat normal [no stand within reach of a support face] => no stand in reach
cat normal [no stand] => no stand in reach
cat normal [needs a water bucket to go 6 down] => below the floor: needs a water bucket
cat normal [water descent blocked at 1,2,3] => no safe water column
cat normal [climbing back up fails] => no safe water column
cat normal [no edge with open space for a column] => no safe water column
cat normal [it would wall me in] => would wall me in
cat normal [I'm not standing on anything] => I'm not standing anywhere walkable
cat normal [protected] => protected
cat normal [protected by policy] => protected
cat normal [someone built there] => someone else's block there
cat normal [not mine: someone's] => someone else's block there
cat normal [not ours] => not mine: left
cat normal [wrong block] => a wrong block there
cat normal [missing cobblestone slab (need 40, have 0): ask] => no cobblestone slab on hand
cat normal [missing planks] => no planks on hand
cat normal [missing #12 thing 1,2,3 here] => no  thing  here on hand
cat normal [waits for another lane's step] => waiting on another lane
cat normal [needs step #12 (skipped)] => needs a skipped step
cat normal [waited too long: nothing ready] => waited too long
cat normal [goto: no path] => no path to its stand
cat normal [jammed] => failed: jammed
cat normal [nothing solid on any side (1,2,3)] => failed: nothing solid on any side
cat normal [placed at -1,64,-3 but it fell; retry later] => failed: placed at  but it fell
cat normal [xyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyy] => failed: xyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyy
cat normal [  waiting on supports  ] => waiting on supports
cat normal [#4 failed 12 times] => failed: failed  times
cat normal [MISSING Glass] => no MISSING Glass on hand
cat normal [Someone] => someone else's block there
gate [] => null
gate [  ] => null
gate [yes] => GateReply { Verdict = Approve, By = PoofImaFox, Text = yes, Correction =  }
gate [Yes please] => GateReply { Verdict = Approve, By = PoofImaFox, Text = Yes please, Correction =  }
gate [ok, keep going] => GateReply { Verdict = Approve, By = PoofImaFox, Text = ok, keep going, Correction =  }
gate [looks good!] => GateReply { Verdict = Approve, By = PoofImaFox, Text = looks good!, Correction =  }
gate [that's it thanks] => GateReply { Verdict = Approve, By = PoofImaFox, Text = that's it thanks, Correction =  }
gate [keep going] => GateReply { Verdict = Approve, By = PoofImaFox, Text = keep going, Correction =  }
gate [carry on please] => GateReply { Verdict = Approve, By = PoofImaFox, Text = carry on please, Correction =  }
gate [no] => GateReply { Verdict = Hold, By = PoofImaFox, Text = no, Correction =  }
gate [nope] => GateReply { Verdict = Hold, By = PoofImaFox, Text = nope, Correction =  }
gate [stop] => GateReply { Verdict = Hold, By = PoofImaFox, Text = stop, Correction =  }
gate [wait] => GateReply { Verdict = Hold, By = PoofImaFox, Text = wait, Correction =  }
gate [hold on] => GateReply { Verdict = Hold, By = PoofImaFox, Text = hold on, Correction =  }
gate [not there] => GateReply { Verdict = Hold, By = PoofImaFox, Text = not there, Correction =  }
gate [wrong spot] => GateReply { Verdict = Hold, By = PoofImaFox, Text = wrong spot, Correction =  }
gate [undo] => GateReply { Verdict = Undo, By = PoofImaFox, Text = undo, Correction =  }
gate [undo it] => GateReply { Verdict = Undo, By = PoofImaFox, Text = undo it, Correction =  }
gate [take it all down] => GateReply { Verdict = Undo, By = PoofImaFox, Text = take it all down, Correction =  }
gate [tear it down] => GateReply { Verdict = Undo, By = PoofImaFox, Text = tear it down, Correction =  }
gate [wrong way] => GateReply { Verdict = Correct, By = PoofImaFox, Text = wrong way, Correction = BuildTransform { Quarters = 2, Mirror = False, Dx = 0, Dy = 0, Dz = 0, Anchor = (1, 64, -2), Axis = z, IsNone = False, Text = turned around } }
gate [other direction] => GateReply { Verdict = Correct, By = PoofImaFox, Text = other direction, Correction = BuildTransform { Quarters = 2, Mirror = False, Dx = 0, Dy = 0, Dz = 0, Anchor = (1, 64, -2), Axis = z, IsNone = False, Text = turned around } }
gate [backwards] => GateReply { Verdict = Correct, By = PoofImaFox, Text = backwards, Correction = BuildTransform { Quarters = 2, Mirror = False, Dx = 0, Dy = 0, Dz = 0, Anchor = (1, 64, -2), Axis = z, IsNone = False, Text = turned around } }
gate [reverse it] => GateReply { Verdict = Correct, By = PoofImaFox, Text = reverse it, Correction = BuildTransform { Quarters = 2, Mirror = False, Dx = 0, Dy = 0, Dz = 0, Anchor = (1, 64, -2), Axis = z, IsNone = False, Text = turned around } }
gate [turn it around] => GateReply { Verdict = Correct, By = PoofImaFox, Text = turn it around, Correction = BuildTransform { Quarters = 2, Mirror = False, Dx = 0, Dy = 0, Dz = 0, Anchor = (1, 64, -2), Axis = z, IsNone = False, Text = turned around } }
gate [180] => GateReply { Verdict = Correct, By = PoofImaFox, Text = 180, Correction = BuildTransform { Quarters = 2, Mirror = False, Dx = 0, Dy = 0, Dz = 0, Anchor = (1, 64, -2), Axis = z, IsNone = False, Text = turned around } }
gate [turn it] => GateReply { Verdict = Correct, By = PoofImaFox, Text = turn it, Correction = BuildTransform { Quarters = 1, Mirror = False, Dx = 0, Dy = 0, Dz = 0, Anchor = (1, 64, -2), Axis = z, IsNone = False, Text = turned right } }
gate [turn it left] => GateReply { Verdict = Correct, By = PoofImaFox, Text = turn it left, Correction = BuildTransform { Quarters = 3, Mirror = False, Dx = 0, Dy = 0, Dz = 0, Anchor = (1, 64, -2), Axis = z, IsNone = False, Text = turned left } }
gate [rotate right] => GateReply { Verdict = Correct, By = PoofImaFox, Text = rotate right, Correction = BuildTransform { Quarters = 1, Mirror = False, Dx = 0, Dy = 0, Dz = 0, Anchor = (1, 64, -2), Axis = z, IsNone = False, Text = turned right } }
gate [counterclockwise] => GateReply { Verdict = Correct, By = PoofImaFox, Text = counterclockwise, Correction = BuildTransform { Quarters = 3, Mirror = False, Dx = 0, Dy = 0, Dz = 0, Anchor = (1, 64, -2), Axis = z, IsNone = False, Text = turned left } }
gate [anti-clockwise] => GateReply { Verdict = Correct, By = PoofImaFox, Text = anti-clockwise, Correction = BuildTransform { Quarters = 3, Mirror = False, Dx = 0, Dy = 0, Dz = 0, Anchor = (1, 64, -2), Axis = z, IsNone = False, Text = turned left } }
gate [turn counter-clockwise] => GateReply { Verdict = Correct, By = PoofImaFox, Text = turn counter-clockwise, Correction = BuildTransform { Quarters = 3, Mirror = False, Dx = 0, Dy = 0, Dz = 0, Anchor = (1, 64, -2), Axis = z, IsNone = False, Text = turned left } }
gate [flip it] => GateReply { Verdict = Correct, By = PoofImaFox, Text = flip it, Correction = BuildTransform { Quarters = 0, Mirror = True, Dx = 0, Dy = 0, Dz = 0, Anchor = (1, 64, -2), Axis = z, IsNone = False, Text = mirrored } }
gate [mirrored] => null
gate [mirror it] => GateReply { Verdict = Correct, By = PoofImaFox, Text = mirror it, Correction = BuildTransform { Quarters = 0, Mirror = True, Dx = 0, Dy = 0, Dz = 0, Anchor = (1, 64, -2), Axis = z, IsNone = False, Text = mirrored } }
gate [move 2 east] => GateReply { Verdict = Correct, By = PoofImaFox, Text = move 2 east, Correction = BuildTransform { Quarters = 0, Mirror = False, Dx = 2, Dy = 0, Dz = 0, Anchor = (1, 64, -2), Axis = z, IsNone = False, Text = moved 2,0,0 } }
gate [move it 3 north] => GateReply { Verdict = Correct, By = PoofImaFox, Text = move it 3 north, Correction = BuildTransform { Quarters = 0, Mirror = False, Dx = 0, Dy = 0, Dz = -3, Anchor = (1, 64, -2), Axis = z, IsNone = False, Text = moved 0,0,-3 } }
gate [shift it two west] => GateReply { Verdict = Correct, By = PoofImaFox, Text = shift it two west, Correction = BuildTransform { Quarters = 0, Mirror = False, Dx = -2, Dy = 0, Dz = 0, Anchor = (1, 64, -2), Axis = z, IsNone = False, Text = moved -2,0,0 } }
gate [move a block up] => GateReply { Verdict = Correct, By = PoofImaFox, Text = move a block up, Correction = BuildTransform { Quarters = 0, Mirror = False, Dx = 0, Dy = 1, Dz = 0, Anchor = (1, 64, -2), Axis = z, IsNone = False, Text = moved 0,1,0 } }
gate [put it over 1 down] => GateReply { Verdict = Correct, By = PoofImaFox, Text = put it over 1 down, Correction = BuildTransform { Quarters = 0, Mirror = False, Dx = 0, Dy = -1, Dz = 0, Anchor = (1, 64, -2), Axis = z, IsNone = False, Text = moved 0,-1,0 } }
gate [push the hut five south] => GateReply { Verdict = Correct, By = PoofImaFox, Text = push the hut five south, Correction = BuildTransform { Quarters = 0, Mirror = False, Dx = 0, Dy = 0, Dz = 5, Anchor = (1, 64, -2), Axis = z, IsNone = False, Text = moved 0,0,5 } }
gate [3 blocks further east] => GateReply { Verdict = Correct, By = PoofImaFox, Text = 3 blocks further east, Correction = BuildTransform { Quarters = 0, Mirror = False, Dx = 3, Dy = 0, Dz = 0, Anchor = (1, 64, -2), Axis = z, IsNone = False, Text = moved 3,0,0 } }
gate [2 down] => GateReply { Verdict = Correct, By = PoofImaFox, Text = 2 down, Correction = BuildTransform { Quarters = 0, Mirror = False, Dx = 0, Dy = -2, Dz = 0, Anchor = (1, 64, -2), Axis = z, IsNone = False, Text = moved 0,-2,0 } }
gate [move left] => null
gate [move it right] => null
gate [turn it and move 2 east] => GateReply { Verdict = Correct, By = PoofImaFox, Text = turn it and move 2 east, Correction = BuildTransform { Quarters = 1, Mirror = False, Dx = 2, Dy = 0, Dz = 0, Anchor = (1, 64, -2), Axis = z, IsNone = False, Text = turned right, moved 2,0,0 } }
gate [wrong way and flip it] => GateReply { Verdict = Correct, By = PoofImaFox, Text = wrong way and flip it, Correction = BuildTransform { Quarters = 2, Mirror = True, Dx = 0, Dy = 0, Dz = 0, Anchor = (1, 64, -2), Axis = z, IsNone = False, Text = turned around, mirrored } }
gate [yes but move 1 up] => GateReply { Verdict = Correct, By = PoofImaFox, Text = yes but move 1 up, Correction = BuildTransform { Quarters = 0, Mirror = False, Dx = 0, Dy = 1, Dz = 0, Anchor = (1, 64, -2), Axis = z, IsNone = False, Text = moved 0,1,0 } }
gate [move 99 west] => GateReply { Verdict = Correct, By = PoofImaFox, Text = move 99 west, Correction = BuildTransform { Quarters = 0, Mirror = False, Dx = -99, Dy = 0, Dz = 0, Anchor = (1, 64, -2), Axis = z, IsNone = False, Text = moved -99,0,0 } }
gate [whatever] => null
gate [turn] => GateReply { Verdict = Correct, By = PoofImaFox, Text = turn, Correction = BuildTransform { Quarters = 1, Mirror = False, Dx = 0, Dy = 0, Dz = 0, Anchor = (1, 64, -2), Axis = z, IsNone = False, Text = turned right } }
gate [move it to the east] => GateReply { Verdict = Correct, By = PoofImaFox, Text = move it to the east, Correction = BuildTransform { Quarters = 0, Mirror = False, Dx = 1, Dy = 0, Dz = 0, Anchor = (1, 64, -2), Axis = z, IsNone = False, Text = moved 1,0,0 } }
gate [move 4 more north and 2 east] => GateReply { Verdict = Correct, By = PoofImaFox, Text = move 4 more north and 2 east, Correction = BuildTransform { Quarters = 0, Mirror = False, Dx = 2, Dy = 0, Dz = -4, Anchor = (1, 64, -2), Axis = z, IsNone = False, Text = moved 2,0,-4 } }
q bridge g0 l0 p0 - => First 0 blocks down: the bridge starts here, runs east, 6×1. Keep going? (say 'keep going', or 'wrong way' / 'turn it' / 'flip it' / 'move 2 east')
q bridge g0 l0 p0 t => First 0 blocks down: the bridge starts here, runs east, 6×1, (turned right). Keep going? (say 'keep going', or 'wrong way' / 'turn it' / 'flip it' / 'move 2 east')
q bridge g0 l0 p1 - => First 0 blocks down: the bridge starts here, runs east, away from well toward farm, 6×1. Keep going? (say 'keep going', or 'wrong way' / 'turn it' / 'flip it' / 'move 2 east'; preview /view/buildplan/12)
q bridge g0 l0 p1 t => First 0 blocks down: the bridge starts here, runs east, away from well toward farm, 6×1, (turned right). Keep going? (say 'keep going', or 'wrong way' / 'turn it' / 'flip it' / 'move 2 east'; preview /view/buildplan/12)
q bridge g0 l0 p2 - => First 0 blocks down: the bridge starts here, runs east, 6×1. Keep going? (say 'keep going', or 'wrong way' / 'turn it' / 'flip it' / 'move 2 east')
q bridge g0 l0 p2 t => First 0 blocks down: the bridge starts here, runs east, 6×1, (turned right). Keep going? (say 'keep going', or 'wrong way' / 'turn it' / 'flip it' / 'move 2 east')
q bridge g0 l0 p3 - => First 0 blocks down: the bridge starts here, runs east, 6×1. Keep going? (say 'keep going', or 'wrong way' / 'turn it' / 'flip it' / 'move 2 east')
q bridge g0 l0 p3 t => First 0 blocks down: the bridge starts here, runs east, 6×1, (turned right). Keep going? (say 'keep going', or 'wrong way' / 'turn it' / 'flip it' / 'move 2 east')
q bridge g0 l0 p4 - => First 0 blocks down: the bridge starts here, runs east, 6×1. Keep going? (say 'keep going', or 'wrong way' / 'turn it' / 'flip it' / 'move 2 east')
q bridge g0 l0 p4 t => First 0 blocks down: the bridge starts here, runs east, 6×1, (turned right). Keep going? (say 'keep going', or 'wrong way' / 'turn it' / 'flip it' / 'move 2 east')
q bridge g0 l1 p0 - => First 0 blocks down: the bridge starts by the well, runs east, 6×1, ending at mob_drop_tower. Keep going? (say 'keep going', or 'wrong way' / 'turn it' / 'flip it' / 'move 2 east')
q bridge g0 l1 p0 t => First 0 blocks down: the bridge starts by the well, runs east, 6×1, ending at mob_drop_tower, (turned right). Keep going? (say 'keep going', or 'wrong way' / 'turn it' / 'flip it' / 'move 2 east')
q bridge g0 l1 p1 - => First 0 blocks down: the bridge starts by the well, runs east, away from well toward farm, 6×1, ending at mob_drop_tower. Keep going? (say 'keep going', or 'wrong way' / 'turn it' / 'flip it' / 'move 2 east'; preview /view/buildplan/12)
q bridge g0 l1 p1 t => First 0 blocks down: the bridge starts by the well, runs east, away from well toward farm, 6×1, ending at mob_drop_tower, (turned right). Keep going? (say 'keep going', or 'wrong way' / 'turn it' / 'flip it' / 'move 2 east'; preview /view/buildplan/12)
q bridge g0 l1 p2 - => First 0 blocks down: the bridge starts by the well, runs east, 6×1, ending at mob_drop_tower. Keep going? (say 'keep going', or 'wrong way' / 'turn it' / 'flip it' / 'move 2 east')
q bridge g0 l1 p2 t => First 0 blocks down: the bridge starts by the well, runs east, 6×1, ending at mob_drop_tower, (turned right). Keep going? (say 'keep going', or 'wrong way' / 'turn it' / 'flip it' / 'move 2 east')
q bridge g0 l1 p3 - => First 0 blocks down: the bridge starts by the well, runs east, 6×1, ending at mob_drop_tower. Keep going? (say 'keep going', or 'wrong way' / 'turn it' / 'flip it' / 'move 2 east')
q bridge g0 l1 p3 t => First 0 blocks down: the bridge starts by the well, runs east, 6×1, ending at mob_drop_tower, (turned right). Keep going? (say 'keep going', or 'wrong way' / 'turn it' / 'flip it' / 'move 2 east')
q bridge g0 l1 p4 - => First 0 blocks down: the bridge starts by the well, runs east, 6×1, ending at mob_drop_tower. Keep going? (say 'keep going', or 'wrong way' / 'turn it' / 'flip it' / 'move 2 east')
q bridge g0 l1 p4 t => First 0 blocks down: the bridge starts by the well, runs east, 6×1, ending at mob_drop_tower, (turned right). Keep going? (say 'keep going', or 'wrong way' / 'turn it' / 'flip it' / 'move 2 east')
q bridge g0 l2 p0 - => First 0 blocks down: the bridge starts by the well, runs east, 6×1. Keep going? (say 'keep going', or 'wrong way' / 'turn it' / 'flip it' / 'move 2 east')
q bridge g0 l2 p0 t => First 0 blocks down: the bridge starts by the well, runs east, 6×1, (turned right). Keep going? (say 'keep going', or 'wrong way' / 'turn it' / 'flip it' / 'move 2 east')
q bridge g0 l2 p1 - => First 0 blocks down: the bridge starts by the well, runs east, away from well toward farm, 6×1. Keep going? (say 'keep going', or 'wrong way' / 'turn it' / 'flip it' / 'move 2 east'; preview /view/buildplan/12)
q bridge g0 l2 p1 t => First 0 blocks down: the bridge starts by the well, runs east, away from well toward farm, 6×1, (turned right). Keep going? (say 'keep going', or 'wrong way' / 'turn it' / 'flip it' / 'move 2 east'; preview /view/buildplan/12)
q bridge g0 l2 p2 - => First 0 blocks down: the bridge starts by the well, runs east, 6×1. Keep going? (say 'keep going', or 'wrong way' / 'turn it' / 'flip it' / 'move 2 east')
q bridge g0 l2 p2 t => First 0 blocks down: the bridge starts by the well, runs east, 6×1, (turned right). Keep going? (say 'keep going', or 'wrong way' / 'turn it' / 'flip it' / 'move 2 east')
q bridge g0 l2 p3 - => First 0 blocks down: the bridge starts by the well, runs east, 6×1. Keep going? (say 'keep going', or 'wrong way' / 'turn it' / 'flip it' / 'move 2 east')
q bridge g0 l2 p3 t => First 0 blocks down: the bridge starts by the well, runs east, 6×1, (turned right). Keep going? (say 'keep going', or 'wrong way' / 'turn it' / 'flip it' / 'move 2 east')
q bridge g0 l2 p4 - => First 0 blocks down: the bridge starts by the well, runs east, 6×1. Keep going? (say 'keep going', or 'wrong way' / 'turn it' / 'flip it' / 'move 2 east')
q bridge g0 l2 p4 t => First 0 blocks down: the bridge starts by the well, runs east, 6×1, (turned right). Keep going? (say 'keep going', or 'wrong way' / 'turn it' / 'flip it' / 'move 2 east')
q bridge g3 l0 p0 - => First 3 blocks down: the bridge starts here, runs east, 6×1. Keep going? (say 'keep going', or 'wrong way' / 'turn it' / 'flip it' / 'move 2 east')
q bridge g3 l0 p0 t => First 3 blocks down: the bridge starts here, runs east, 6×1, (turned right). Keep going? (say 'keep going', or 'wrong way' / 'turn it' / 'flip it' / 'move 2 east')
q bridge g3 l0 p1 - => First 3 blocks down: the bridge starts here, runs east, away from well toward farm, 6×1. Keep going? (say 'keep going', or 'wrong way' / 'turn it' / 'flip it' / 'move 2 east'; preview /view/buildplan/12)
q bridge g3 l0 p1 t => First 3 blocks down: the bridge starts here, runs east, away from well toward farm, 6×1, (turned right). Keep going? (say 'keep going', or 'wrong way' / 'turn it' / 'flip it' / 'move 2 east'; preview /view/buildplan/12)
q bridge g3 l0 p2 - => First 3 blocks down: the bridge starts here, runs east, 6×1. Keep going? (say 'keep going', or 'wrong way' / 'turn it' / 'flip it' / 'move 2 east')
q bridge g3 l0 p2 t => First 3 blocks down: the bridge starts here, runs east, 6×1, (turned right). Keep going? (say 'keep going', or 'wrong way' / 'turn it' / 'flip it' / 'move 2 east')
q bridge g3 l0 p3 - => First 3 blocks down: the bridge starts here, runs east, 6×1. Keep going? (say 'keep going', or 'wrong way' / 'turn it' / 'flip it' / 'move 2 east')
q bridge g3 l0 p3 t => First 3 blocks down: the bridge starts here, runs east, 6×1, (turned right). Keep going? (say 'keep going', or 'wrong way' / 'turn it' / 'flip it' / 'move 2 east')
q bridge g3 l0 p4 - => First 3 blocks down: the bridge starts here, runs east, 6×1. Keep going? (say 'keep going', or 'wrong way' / 'turn it' / 'flip it' / 'move 2 east')
q bridge g3 l0 p4 t => First 3 blocks down: the bridge starts here, runs east, 6×1, (turned right). Keep going? (say 'keep going', or 'wrong way' / 'turn it' / 'flip it' / 'move 2 east')
q bridge g3 l1 p0 - => First 3 blocks down: the bridge starts by the well, runs east, 6×1, ending at mob_drop_tower. Keep going? (say 'keep going', or 'wrong way' / 'turn it' / 'flip it' / 'move 2 east')
q bridge g3 l1 p0 t => First 3 blocks down: the bridge starts by the well, runs east, 6×1, ending at mob_drop_tower, (turned right). Keep going? (say 'keep going', or 'wrong way' / 'turn it' / 'flip it' / 'move 2 east')
q bridge g3 l1 p1 - => First 3 blocks down: the bridge starts by the well, runs east, away from well toward farm, 6×1, ending at mob_drop_tower. Keep going? (say 'keep going', or 'wrong way' / 'turn it' / 'flip it' / 'move 2 east'; preview /view/buildplan/12)
q bridge g3 l1 p1 t => First 3 blocks down: the bridge starts by the well, runs east, away from well toward farm, 6×1, ending at mob_drop_tower, (turned right). Keep going? (say 'keep going', or 'wrong way' / 'turn it' / 'flip it' / 'move 2 east'; preview /view/buildplan/12)
q bridge g3 l1 p2 - => First 3 blocks down: the bridge starts by the well, runs east, 6×1, ending at mob_drop_tower. Keep going? (say 'keep going', or 'wrong way' / 'turn it' / 'flip it' / 'move 2 east')
q bridge g3 l1 p2 t => First 3 blocks down: the bridge starts by the well, runs east, 6×1, ending at mob_drop_tower, (turned right). Keep going? (say 'keep going', or 'wrong way' / 'turn it' / 'flip it' / 'move 2 east')
q bridge g3 l1 p3 - => First 3 blocks down: the bridge starts by the well, runs east, 6×1, ending at mob_drop_tower. Keep going? (say 'keep going', or 'wrong way' / 'turn it' / 'flip it' / 'move 2 east')
q bridge g3 l1 p3 t => First 3 blocks down: the bridge starts by the well, runs east, 6×1, ending at mob_drop_tower, (turned right). Keep going? (say 'keep going', or 'wrong way' / 'turn it' / 'flip it' / 'move 2 east')
q bridge g3 l1 p4 - => First 3 blocks down: the bridge starts by the well, runs east, 6×1, ending at mob_drop_tower. Keep going? (say 'keep going', or 'wrong way' / 'turn it' / 'flip it' / 'move 2 east')
q bridge g3 l1 p4 t => First 3 blocks down: the bridge starts by the well, runs east, 6×1, ending at mob_drop_tower, (turned right). Keep going? (say 'keep going', or 'wrong way' / 'turn it' / 'flip it' / 'move 2 east')
q bridge g3 l2 p0 - => First 3 blocks down: the bridge starts by the well, runs east, 6×1. Keep going? (say 'keep going', or 'wrong way' / 'turn it' / 'flip it' / 'move 2 east')
q bridge g3 l2 p0 t => First 3 blocks down: the bridge starts by the well, runs east, 6×1, (turned right). Keep going? (say 'keep going', or 'wrong way' / 'turn it' / 'flip it' / 'move 2 east')
q bridge g3 l2 p1 - => First 3 blocks down: the bridge starts by the well, runs east, away from well toward farm, 6×1. Keep going? (say 'keep going', or 'wrong way' / 'turn it' / 'flip it' / 'move 2 east'; preview /view/buildplan/12)
q bridge g3 l2 p1 t => First 3 blocks down: the bridge starts by the well, runs east, away from well toward farm, 6×1, (turned right). Keep going? (say 'keep going', or 'wrong way' / 'turn it' / 'flip it' / 'move 2 east'; preview /view/buildplan/12)
q bridge g3 l2 p2 - => First 3 blocks down: the bridge starts by the well, runs east, 6×1. Keep going? (say 'keep going', or 'wrong way' / 'turn it' / 'flip it' / 'move 2 east')
q bridge g3 l2 p2 t => First 3 blocks down: the bridge starts by the well, runs east, 6×1, (turned right). Keep going? (say 'keep going', or 'wrong way' / 'turn it' / 'flip it' / 'move 2 east')
q bridge g3 l2 p3 - => First 3 blocks down: the bridge starts by the well, runs east, 6×1. Keep going? (say 'keep going', or 'wrong way' / 'turn it' / 'flip it' / 'move 2 east')
q bridge g3 l2 p3 t => First 3 blocks down: the bridge starts by the well, runs east, 6×1, (turned right). Keep going? (say 'keep going', or 'wrong way' / 'turn it' / 'flip it' / 'move 2 east')
q bridge g3 l2 p4 - => First 3 blocks down: the bridge starts by the well, runs east, 6×1. Keep going? (say 'keep going', or 'wrong way' / 'turn it' / 'flip it' / 'move 2 east')
q bridge g3 l2 p4 t => First 3 blocks down: the bridge starts by the well, runs east, 6×1, (turned right). Keep going? (say 'keep going', or 'wrong way' / 'turn it' / 'flip it' / 'move 2 east')
q bridge g2 l0 p0 - => First 2 blocks down: the bridge starts here, runs west, 6×1. Keep going? (say 'keep going', or 'wrong way' / 'turn it' / 'flip it' / 'move 2 east')
q bridge g2 l0 p0 t => First 2 blocks down: the bridge starts here, runs west, 6×1, (turned right). Keep going? (say 'keep going', or 'wrong way' / 'turn it' / 'flip it' / 'move 2 east')
q bridge g2 l0 p1 - => First 2 blocks down: the bridge starts here, runs west, away from farm toward well, 6×1. Keep going? (say 'keep going', or 'wrong way' / 'turn it' / 'flip it' / 'move 2 east'; preview /view/buildplan/12)
q bridge g2 l0 p1 t => First 2 blocks down: the bridge starts here, runs west, away from farm toward well, 6×1, (turned right). Keep going? (say 'keep going', or 'wrong way' / 'turn it' / 'flip it' / 'move 2 east'; preview /view/buildplan/12)
q bridge g2 l0 p2 - => First 2 blocks down: the bridge starts here, runs west, 6×1. Keep going? (say 'keep going', or 'wrong way' / 'turn it' / 'flip it' / 'move 2 east')
q bridge g2 l0 p2 t => First 2 blocks down: the bridge starts here, runs west, 6×1, (turned right). Keep going? (say 'keep going', or 'wrong way' / 'turn it' / 'flip it' / 'move 2 east')
q bridge g2 l0 p3 - => First 2 blocks down: the bridge starts here, runs west, 6×1. Keep going? (say 'keep going', or 'wrong way' / 'turn it' / 'flip it' / 'move 2 east')
q bridge g2 l0 p3 t => First 2 blocks down: the bridge starts here, runs west, 6×1, (turned right). Keep going? (say 'keep going', or 'wrong way' / 'turn it' / 'flip it' / 'move 2 east')
q bridge g2 l0 p4 - => First 2 blocks down: the bridge starts here, runs west, 6×1. Keep going? (say 'keep going', or 'wrong way' / 'turn it' / 'flip it' / 'move 2 east')
q bridge g2 l0 p4 t => First 2 blocks down: the bridge starts here, runs west, 6×1, (turned right). Keep going? (say 'keep going', or 'wrong way' / 'turn it' / 'flip it' / 'move 2 east')
q bridge g2 l1 p0 - => First 2 blocks down: the bridge starts at mob_drop_tower, runs west, 6×1, ending by the well. Keep going? (say 'keep going', or 'wrong way' / 'turn it' / 'flip it' / 'move 2 east')
q bridge g2 l1 p0 t => First 2 blocks down: the bridge starts at mob_drop_tower, runs west, 6×1, ending by the well, (turned right). Keep going? (say 'keep going', or 'wrong way' / 'turn it' / 'flip it' / 'move 2 east')
q bridge g2 l1 p1 - => First 2 blocks down: the bridge starts at mob_drop_tower, runs west, away from farm toward well, 6×1, ending by the well. Keep going? (say 'keep going', or 'wrong way' / 'turn it' / 'flip it' / 'move 2 east'; preview /view/buildplan/12)
q bridge g2 l1 p1 t => First 2 blocks down: the bridge starts at mob_drop_tower, runs west, away from farm toward well, 6×1, ending by the well, (turned right). Keep going? (say 'keep going', or 'wrong way' / 'turn it' / 'flip it' / 'move 2 east'; preview /view/buildplan/12)
q bridge g2 l1 p2 - => First 2 blocks down: the bridge starts at mob_drop_tower, runs west, 6×1, ending by the well. Keep going? (say 'keep going', or 'wrong way' / 'turn it' / 'flip it' / 'move 2 east')
q bridge g2 l1 p2 t => First 2 blocks down: the bridge starts at mob_drop_tower, runs west, 6×1, ending by the well, (turned right). Keep going? (say 'keep going', or 'wrong way' / 'turn it' / 'flip it' / 'move 2 east')
q bridge g2 l1 p3 - => First 2 blocks down: the bridge starts at mob_drop_tower, runs west, 6×1, ending by the well. Keep going? (say 'keep going', or 'wrong way' / 'turn it' / 'flip it' / 'move 2 east')
q bridge g2 l1 p3 t => First 2 blocks down: the bridge starts at mob_drop_tower, runs west, 6×1, ending by the well, (turned right). Keep going? (say 'keep going', or 'wrong way' / 'turn it' / 'flip it' / 'move 2 east')
q bridge g2 l1 p4 - => First 2 blocks down: the bridge starts at mob_drop_tower, runs west, 6×1, ending by the well. Keep going? (say 'keep going', or 'wrong way' / 'turn it' / 'flip it' / 'move 2 east')
q bridge g2 l1 p4 t => First 2 blocks down: the bridge starts at mob_drop_tower, runs west, 6×1, ending by the well, (turned right). Keep going? (say 'keep going', or 'wrong way' / 'turn it' / 'flip it' / 'move 2 east')
q bridge g2 l2 p0 - => First 2 blocks down: the bridge starts here, runs west, 6×1, ending by the well. Keep going? (say 'keep going', or 'wrong way' / 'turn it' / 'flip it' / 'move 2 east')
q bridge g2 l2 p0 t => First 2 blocks down: the bridge starts here, runs west, 6×1, ending by the well, (turned right). Keep going? (say 'keep going', or 'wrong way' / 'turn it' / 'flip it' / 'move 2 east')
q bridge g2 l2 p1 - => First 2 blocks down: the bridge starts here, runs west, away from farm toward well, 6×1, ending by the well. Keep going? (say 'keep going', or 'wrong way' / 'turn it' / 'flip it' / 'move 2 east'; preview /view/buildplan/12)
q bridge g2 l2 p1 t => First 2 blocks down: the bridge starts here, runs west, away from farm toward well, 6×1, ending by the well, (turned right). Keep going? (say 'keep going', or 'wrong way' / 'turn it' / 'flip it' / 'move 2 east'; preview /view/buildplan/12)
q bridge g2 l2 p2 - => First 2 blocks down: the bridge starts here, runs west, 6×1, ending by the well. Keep going? (say 'keep going', or 'wrong way' / 'turn it' / 'flip it' / 'move 2 east')
q bridge g2 l2 p2 t => First 2 blocks down: the bridge starts here, runs west, 6×1, ending by the well, (turned right). Keep going? (say 'keep going', or 'wrong way' / 'turn it' / 'flip it' / 'move 2 east')
q bridge g2 l2 p3 - => First 2 blocks down: the bridge starts here, runs west, 6×1, ending by the well. Keep going? (say 'keep going', or 'wrong way' / 'turn it' / 'flip it' / 'move 2 east')
q bridge g2 l2 p3 t => First 2 blocks down: the bridge starts here, runs west, 6×1, ending by the well, (turned right). Keep going? (say 'keep going', or 'wrong way' / 'turn it' / 'flip it' / 'move 2 east')
q bridge g2 l2 p4 - => First 2 blocks down: the bridge starts here, runs west, 6×1, ending by the well. Keep going? (say 'keep going', or 'wrong way' / 'turn it' / 'flip it' / 'move 2 east')
q bridge g2 l2 p4 t => First 2 blocks down: the bridge starts here, runs west, 6×1, ending by the well, (turned right). Keep going? (say 'keep going', or 'wrong way' / 'turn it' / 'flip it' / 'move 2 east')
q mob drop tower g0 l0 p0 - => First 0 blocks down: the mob drop tower starts here, runs south, 2×7×5. Keep going? (say 'keep going', or 'wrong way' / 'turn it' / 'flip it' / 'move 2 east')
q mob drop tower g0 l0 p0 t => First 0 blocks down: the mob drop tower starts here, runs south, 2×7×5, (turned right). Keep going? (say 'keep going', or 'wrong way' / 'turn it' / 'flip it' / 'move 2 east')
q mob drop tower g0 l0 p1 - => First 0 blocks down: the mob drop tower starts here, runs south, toward farm, 2×7×5. Keep going? (say 'keep going', or 'wrong way' / 'turn it' / 'flip it' / 'move 2 east'; preview /view/buildplan/12)
q mob drop tower g0 l0 p1 t => First 0 blocks down: the mob drop tower starts here, runs south, toward farm, 2×7×5, (turned right). Keep going? (say 'keep going', or 'wrong way' / 'turn it' / 'flip it' / 'move 2 east'; preview /view/buildplan/12)
q mob drop tower g0 l0 p2 - => First 0 blocks down: the mob drop tower starts here, runs south, 2×7×5. Keep going? (say 'keep going', or 'wrong way' / 'turn it' / 'flip it' / 'move 2 east')
q mob drop tower g0 l0 p2 t => First 0 blocks down: the mob drop tower starts here, runs south, 2×7×5, (turned right). Keep going? (say 'keep going', or 'wrong way' / 'turn it' / 'flip it' / 'move 2 east')
q mob drop tower g0 l0 p3 - => First 0 blocks down: the mob drop tower starts here, runs south, 2×7×5. Keep going? (say 'keep going', or 'wrong way' / 'turn it' / 'flip it' / 'move 2 east')
q mob drop tower g0 l0 p3 t => First 0 blocks down: the mob drop tower starts here, runs south, 2×7×5, (turned right). Keep going? (say 'keep going', or 'wrong way' / 'turn it' / 'flip it' / 'move 2 east')
q mob drop tower g0 l0 p4 - => First 0 blocks down: the mob drop tower starts here, runs south, toward chests, 2×7×5. Keep going? (say 'keep going', or 'wrong way' / 'turn it' / 'flip it' / 'move 2 east')
q mob drop tower g0 l0 p4 t => First 0 blocks down: the mob drop tower starts here, runs south, toward chests, 2×7×5, (turned right). Keep going? (say 'keep going', or 'wrong way' / 'turn it' / 'flip it' / 'move 2 east')
q mob drop tower g0 l1 p0 - => First 0 blocks down: the mob drop tower starts north of the farm, runs south, 2×7×5. Keep going? (say 'keep going', or 'wrong way' / 'turn it' / 'flip it' / 'move 2 east')
q mob drop tower g0 l1 p0 t => First 0 blocks down: the mob drop tower starts north of the farm, runs south, 2×7×5, (turned right). Keep going? (say 'keep going', or 'wrong way' / 'turn it' / 'flip it' / 'move 2 east')
q mob drop tower g0 l1 p1 - => First 0 blocks down: the mob drop tower starts north of the farm, runs south, toward farm, 2×7×5. Keep going? (say 'keep going', or 'wrong way' / 'turn it' / 'flip it' / 'move 2 east'; preview /view/buildplan/12)
q mob drop tower g0 l1 p1 t => First 0 blocks down: the mob drop tower starts north of the farm, runs south, toward farm, 2×7×5, (turned right). Keep going? (say 'keep going', or 'wrong way' / 'turn it' / 'flip it' / 'move 2 east'; preview /view/buildplan/12)
q mob drop tower g0 l1 p2 - => First 0 blocks down: the mob drop tower starts north of the farm, runs south, 2×7×5. Keep going? (say 'keep going', or 'wrong way' / 'turn it' / 'flip it' / 'move 2 east')
q mob drop tower g0 l1 p2 t => First 0 blocks down: the mob drop tower starts north of the farm, runs south, 2×7×5, (turned right). Keep going? (say 'keep going', or 'wrong way' / 'turn it' / 'flip it' / 'move 2 east')
q mob drop tower g0 l1 p3 - => First 0 blocks down: the mob drop tower starts north of the farm, runs south, 2×7×5. Keep going? (say 'keep going', or 'wrong way' / 'turn it' / 'flip it' / 'move 2 east')
q mob drop tower g0 l1 p3 t => First 0 blocks down: the mob drop tower starts north of the farm, runs south, 2×7×5, (turned right). Keep going? (say 'keep going', or 'wrong way' / 'turn it' / 'flip it' / 'move 2 east')
q mob drop tower g0 l1 p4 - => First 0 blocks down: the mob drop tower starts north of the farm, runs south, toward chests, 2×7×5. Keep going? (say 'keep going', or 'wrong way' / 'turn it' / 'flip it' / 'move 2 east')
q mob drop tower g0 l1 p4 t => First 0 blocks down: the mob drop tower starts north of the farm, runs south, toward chests, 2×7×5, (turned right). Keep going? (say 'keep going', or 'wrong way' / 'turn it' / 'flip it' / 'move 2 east')
q mob drop tower g0 l2 p0 - => First 0 blocks down: the mob drop tower starts by the well, runs south, 2×7×5, ending near the bridge. Keep going? (say 'keep going', or 'wrong way' / 'turn it' / 'flip it' / 'move 2 east')
q mob drop tower g0 l2 p0 t => First 0 blocks down: the mob drop tower starts by the well, runs south, 2×7×5, ending near the bridge, (turned right). Keep going? (say 'keep going', or 'wrong way' / 'turn it' / 'flip it' / 'move 2 east')
q mob drop tower g0 l2 p1 - => First 0 blocks down: the mob drop tower starts by the well, runs south, toward farm, 2×7×5, ending near the bridge. Keep going? (say 'keep going', or 'wrong way' / 'turn it' / 'flip it' / 'move 2 east'; preview /view/buildplan/12)
q mob drop tower g0 l2 p1 t => First 0 blocks down: the mob drop tower starts by the well, runs south, toward farm, 2×7×5, ending near the bridge, (turned right). Keep going? (say 'keep going', or 'wrong way' / 'turn it' / 'flip it' / 'move 2 east'; preview /view/buildplan/12)
q mob drop tower g0 l2 p2 - => First 0 blocks down: the mob drop tower starts by the well, runs south, 2×7×5, ending near the bridge. Keep going? (say 'keep going', or 'wrong way' / 'turn it' / 'flip it' / 'move 2 east')
q mob drop tower g0 l2 p2 t => First 0 blocks down: the mob drop tower starts by the well, runs south, 2×7×5, ending near the bridge, (turned right). Keep going? (say 'keep going', or 'wrong way' / 'turn it' / 'flip it' / 'move 2 east')
q mob drop tower g0 l2 p3 - => First 0 blocks down: the mob drop tower starts by the well, runs south, 2×7×5, ending near the bridge. Keep going? (say 'keep going', or 'wrong way' / 'turn it' / 'flip it' / 'move 2 east')
q mob drop tower g0 l2 p3 t => First 0 blocks down: the mob drop tower starts by the well, runs south, 2×7×5, ending near the bridge, (turned right). Keep going? (say 'keep going', or 'wrong way' / 'turn it' / 'flip it' / 'move 2 east')
q mob drop tower g0 l2 p4 - => First 0 blocks down: the mob drop tower starts by the well, runs south, toward chests, 2×7×5, ending near the bridge. Keep going? (say 'keep going', or 'wrong way' / 'turn it' / 'flip it' / 'move 2 east')
q mob drop tower g0 l2 p4 t => First 0 blocks down: the mob drop tower starts by the well, runs south, toward chests, 2×7×5, ending near the bridge, (turned right). Keep going? (say 'keep going', or 'wrong way' / 'turn it' / 'flip it' / 'move 2 east')
q mob drop tower g3 l0 p0 - => First 3 blocks down: the mob drop tower starts here, runs south, 2×7×5. Keep going? (say 'keep going', or 'wrong way' / 'turn it' / 'flip it' / 'move 2 east')
q mob drop tower g3 l0 p0 t => First 3 blocks down: the mob drop tower starts here, runs south, 2×7×5, (turned right). Keep going? (say 'keep going', or 'wrong way' / 'turn it' / 'flip it' / 'move 2 east')
q mob drop tower g3 l0 p1 - => First 3 blocks down: the mob drop tower starts here, runs south, toward farm, 2×7×5. Keep going? (say 'keep going', or 'wrong way' / 'turn it' / 'flip it' / 'move 2 east'; preview /view/buildplan/12)
q mob drop tower g3 l0 p1 t => First 3 blocks down: the mob drop tower starts here, runs south, toward farm, 2×7×5, (turned right). Keep going? (say 'keep going', or 'wrong way' / 'turn it' / 'flip it' / 'move 2 east'; preview /view/buildplan/12)
q mob drop tower g3 l0 p2 - => First 3 blocks down: the mob drop tower starts here, runs south, 2×7×5. Keep going? (say 'keep going', or 'wrong way' / 'turn it' / 'flip it' / 'move 2 east')
q mob drop tower g3 l0 p2 t => First 3 blocks down: the mob drop tower starts here, runs south, 2×7×5, (turned right). Keep going? (say 'keep going', or 'wrong way' / 'turn it' / 'flip it' / 'move 2 east')
q mob drop tower g3 l0 p3 - => First 3 blocks down: the mob drop tower starts here, runs south, 2×7×5. Keep going? (say 'keep going', or 'wrong way' / 'turn it' / 'flip it' / 'move 2 east')
q mob drop tower g3 l0 p3 t => First 3 blocks down: the mob drop tower starts here, runs south, 2×7×5, (turned right). Keep going? (say 'keep going', or 'wrong way' / 'turn it' / 'flip it' / 'move 2 east')
q mob drop tower g3 l0 p4 - => First 3 blocks down: the mob drop tower starts here, runs south, toward chests, 2×7×5. Keep going? (say 'keep going', or 'wrong way' / 'turn it' / 'flip it' / 'move 2 east')
q mob drop tower g3 l0 p4 t => First 3 blocks down: the mob drop tower starts here, runs south, toward chests, 2×7×5, (turned right). Keep going? (say 'keep going', or 'wrong way' / 'turn it' / 'flip it' / 'move 2 east')
q mob drop tower g3 l1 p0 - => First 3 blocks down: the mob drop tower starts north of the farm, runs south, 2×7×5. Keep going? (say 'keep going', or 'wrong way' / 'turn it' / 'flip it' / 'move 2 east')
q mob drop tower g3 l1 p0 t => First 3 blocks down: the mob drop tower starts north of the farm, runs south, 2×7×5, (turned right). Keep going? (say 'keep going', or 'wrong way' / 'turn it' / 'flip it' / 'move 2 east')
q mob drop tower g3 l1 p1 - => First 3 blocks down: the mob drop tower starts north of the farm, runs south, toward farm, 2×7×5. Keep going? (say 'keep going', or 'wrong way' / 'turn it' / 'flip it' / 'move 2 east'; preview /view/buildplan/12)
q mob drop tower g3 l1 p1 t => First 3 blocks down: the mob drop tower starts north of the farm, runs south, toward farm, 2×7×5, (turned right). Keep going? (say 'keep going', or 'wrong way' / 'turn it' / 'flip it' / 'move 2 east'; preview /view/buildplan/12)
q mob drop tower g3 l1 p2 - => First 3 blocks down: the mob drop tower starts north of the farm, runs south, 2×7×5. Keep going? (say 'keep going', or 'wrong way' / 'turn it' / 'flip it' / 'move 2 east')
q mob drop tower g3 l1 p2 t => First 3 blocks down: the mob drop tower starts north of the farm, runs south, 2×7×5, (turned right). Keep going? (say 'keep going', or 'wrong way' / 'turn it' / 'flip it' / 'move 2 east')
q mob drop tower g3 l1 p3 - => First 3 blocks down: the mob drop tower starts north of the farm, runs south, 2×7×5. Keep going? (say 'keep going', or 'wrong way' / 'turn it' / 'flip it' / 'move 2 east')
q mob drop tower g3 l1 p3 t => First 3 blocks down: the mob drop tower starts north of the farm, runs south, 2×7×5, (turned right). Keep going? (say 'keep going', or 'wrong way' / 'turn it' / 'flip it' / 'move 2 east')
q mob drop tower g3 l1 p4 - => First 3 blocks down: the mob drop tower starts north of the farm, runs south, toward chests, 2×7×5. Keep going? (say 'keep going', or 'wrong way' / 'turn it' / 'flip it' / 'move 2 east')
q mob drop tower g3 l1 p4 t => First 3 blocks down: the mob drop tower starts north of the farm, runs south, toward chests, 2×7×5, (turned right). Keep going? (say 'keep going', or 'wrong way' / 'turn it' / 'flip it' / 'move 2 east')
q mob drop tower g3 l2 p0 - => First 3 blocks down: the mob drop tower starts by the well, runs south, 2×7×5, ending near the bridge. Keep going? (say 'keep going', or 'wrong way' / 'turn it' / 'flip it' / 'move 2 east')
q mob drop tower g3 l2 p0 t => First 3 blocks down: the mob drop tower starts by the well, runs south, 2×7×5, ending near the bridge, (turned right). Keep going? (say 'keep going', or 'wrong way' / 'turn it' / 'flip it' / 'move 2 east')
q mob drop tower g3 l2 p1 - => First 3 blocks down: the mob drop tower starts by the well, runs south, toward farm, 2×7×5, ending near the bridge. Keep going? (say 'keep going', or 'wrong way' / 'turn it' / 'flip it' / 'move 2 east'; preview /view/buildplan/12)
q mob drop tower g3 l2 p1 t => First 3 blocks down: the mob drop tower starts by the well, runs south, toward farm, 2×7×5, ending near the bridge, (turned right). Keep going? (say 'keep going', or 'wrong way' / 'turn it' / 'flip it' / 'move 2 east'; preview /view/buildplan/12)
q mob drop tower g3 l2 p2 - => First 3 blocks down: the mob drop tower starts by the well, runs south, 2×7×5, ending near the bridge. Keep going? (say 'keep going', or 'wrong way' / 'turn it' / 'flip it' / 'move 2 east')
q mob drop tower g3 l2 p2 t => First 3 blocks down: the mob drop tower starts by the well, runs south, 2×7×5, ending near the bridge, (turned right). Keep going? (say 'keep going', or 'wrong way' / 'turn it' / 'flip it' / 'move 2 east')
q mob drop tower g3 l2 p3 - => First 3 blocks down: the mob drop tower starts by the well, runs south, 2×7×5, ending near the bridge. Keep going? (say 'keep going', or 'wrong way' / 'turn it' / 'flip it' / 'move 2 east')
q mob drop tower g3 l2 p3 t => First 3 blocks down: the mob drop tower starts by the well, runs south, 2×7×5, ending near the bridge, (turned right). Keep going? (say 'keep going', or 'wrong way' / 'turn it' / 'flip it' / 'move 2 east')
q mob drop tower g3 l2 p4 - => First 3 blocks down: the mob drop tower starts by the well, runs south, toward chests, 2×7×5, ending near the bridge. Keep going? (say 'keep going', or 'wrong way' / 'turn it' / 'flip it' / 'move 2 east')
q mob drop tower g3 l2 p4 t => First 3 blocks down: the mob drop tower starts by the well, runs south, toward chests, 2×7×5, ending near the bridge, (turned right). Keep going? (say 'keep going', or 'wrong way' / 'turn it' / 'flip it' / 'move 2 east')
q mob drop tower g2 l0 p0 - => First 2 blocks down: the mob drop tower starts here, runs north, 2×7×5. Keep going? (say 'keep going', or 'wrong way' / 'turn it' / 'flip it' / 'move 2 east')
q mob drop tower g2 l0 p0 t => First 2 blocks down: the mob drop tower starts here, runs north, 2×7×5, (turned right). Keep going? (say 'keep going', or 'wrong way' / 'turn it' / 'flip it' / 'move 2 east')
q mob drop tower g2 l0 p1 - => First 2 blocks down: the mob drop tower starts here, runs north, toward farm, 2×7×5. Keep going? (say 'keep going', or 'wrong way' / 'turn it' / 'flip it' / 'move 2 east'; preview /view/buildplan/12)
q mob drop tower g2 l0 p1 t => First 2 blocks down: the mob drop tower starts here, runs north, toward farm, 2×7×5, (turned right). Keep going? (say 'keep going', or 'wrong way' / 'turn it' / 'flip it' / 'move 2 east'; preview /view/buildplan/12)
q mob drop tower g2 l0 p2 - => First 2 blocks down: the mob drop tower starts here, runs north, 2×7×5. Keep going? (say 'keep going', or 'wrong way' / 'turn it' / 'flip it' / 'move 2 east')
q mob drop tower g2 l0 p2 t => First 2 blocks down: the mob drop tower starts here, runs north, 2×7×5, (turned right). Keep going? (say 'keep going', or 'wrong way' / 'turn it' / 'flip it' / 'move 2 east')
q mob drop tower g2 l0 p3 - => First 2 blocks down: the mob drop tower starts here, runs north, 2×7×5. Keep going? (say 'keep going', or 'wrong way' / 'turn it' / 'flip it' / 'move 2 east')
q mob drop tower g2 l0 p3 t => First 2 blocks down: the mob drop tower starts here, runs north, 2×7×5, (turned right). Keep going? (say 'keep going', or 'wrong way' / 'turn it' / 'flip it' / 'move 2 east')
q mob drop tower g2 l0 p4 - => First 2 blocks down: the mob drop tower starts here, runs north, away from chests, 2×7×5. Keep going? (say 'keep going', or 'wrong way' / 'turn it' / 'flip it' / 'move 2 east')
q mob drop tower g2 l0 p4 t => First 2 blocks down: the mob drop tower starts here, runs north, away from chests, 2×7×5, (turned right). Keep going? (say 'keep going', or 'wrong way' / 'turn it' / 'flip it' / 'move 2 east')
q mob drop tower g2 l1 p0 - => First 2 blocks down: the mob drop tower starts here, runs north, 2×7×5, ending north of the farm. Keep going? (say 'keep going', or 'wrong way' / 'turn it' / 'flip it' / 'move 2 east')
q mob drop tower g2 l1 p0 t => First 2 blocks down: the mob drop tower starts here, runs north, 2×7×5, ending north of the farm, (turned right). Keep going? (say 'keep going', or 'wrong way' / 'turn it' / 'flip it' / 'move 2 east')
q mob drop tower g2 l1 p1 - => First 2 blocks down: the mob drop tower starts here, runs north, toward farm, 2×7×5, ending north of the farm. Keep going? (say 'keep going', or 'wrong way' / 'turn it' / 'flip it' / 'move 2 east'; preview /view/buildplan/12)
q mob drop tower g2 l1 p1 t => First 2 blocks down: the mob drop tower starts here, runs north, toward farm, 2×7×5, ending north of the farm, (turned right). Keep going? (say 'keep going', or 'wrong way' / 'turn it' / 'flip it' / 'move 2 east'; preview /view/buildplan/12)
q mob drop tower g2 l1 p2 - => First 2 blocks down: the mob drop tower starts here, runs north, 2×7×5, ending north of the farm. Keep going? (say 'keep going', or 'wrong way' / 'turn it' / 'flip it' / 'move 2 east')
q mob drop tower g2 l1 p2 t => First 2 blocks down: the mob drop tower starts here, runs north, 2×7×5, ending north of the farm, (turned right). Keep going? (say 'keep going', or 'wrong way' / 'turn it' / 'flip it' / 'move 2 east')
q mob drop tower g2 l1 p3 - => First 2 blocks down: the mob drop tower starts here, runs north, 2×7×5, ending north of the farm. Keep going? (say 'keep going', or 'wrong way' / 'turn it' / 'flip it' / 'move 2 east')
q mob drop tower g2 l1 p3 t => First 2 blocks down: the mob drop tower starts here, runs north, 2×7×5, ending north of the farm, (turned right). Keep going? (say 'keep going', or 'wrong way' / 'turn it' / 'flip it' / 'move 2 east')
q mob drop tower g2 l1 p4 - => First 2 blocks down: the mob drop tower starts here, runs north, away from chests, 2×7×5, ending north of the farm. Keep going? (say 'keep going', or 'wrong way' / 'turn it' / 'flip it' / 'move 2 east')
q mob drop tower g2 l1 p4 t => First 2 blocks down: the mob drop tower starts here, runs north, away from chests, 2×7×5, ending north of the farm, (turned right). Keep going? (say 'keep going', or 'wrong way' / 'turn it' / 'flip it' / 'move 2 east')
q mob drop tower g2 l2 p0 - => First 2 blocks down: the mob drop tower starts near the bridge, runs north, 2×7×5, ending by the well. Keep going? (say 'keep going', or 'wrong way' / 'turn it' / 'flip it' / 'move 2 east')
q mob drop tower g2 l2 p0 t => First 2 blocks down: the mob drop tower starts near the bridge, runs north, 2×7×5, ending by the well, (turned right). Keep going? (say 'keep going', or 'wrong way' / 'turn it' / 'flip it' / 'move 2 east')
q mob drop tower g2 l2 p1 - => First 2 blocks down: the mob drop tower starts near the bridge, runs north, toward farm, 2×7×5, ending by the well. Keep going? (say 'keep going', or 'wrong way' / 'turn it' / 'flip it' / 'move 2 east'; preview /view/buildplan/12)
q mob drop tower g2 l2 p1 t => First 2 blocks down: the mob drop tower starts near the bridge, runs north, toward farm, 2×7×5, ending by the well, (turned right). Keep going? (say 'keep going', or 'wrong way' / 'turn it' / 'flip it' / 'move 2 east'; preview /view/buildplan/12)
q mob drop tower g2 l2 p2 - => First 2 blocks down: the mob drop tower starts near the bridge, runs north, 2×7×5, ending by the well. Keep going? (say 'keep going', or 'wrong way' / 'turn it' / 'flip it' / 'move 2 east')
q mob drop tower g2 l2 p2 t => First 2 blocks down: the mob drop tower starts near the bridge, runs north, 2×7×5, ending by the well, (turned right). Keep going? (say 'keep going', or 'wrong way' / 'turn it' / 'flip it' / 'move 2 east')
q mob drop tower g2 l2 p3 - => First 2 blocks down: the mob drop tower starts near the bridge, runs north, 2×7×5, ending by the well. Keep going? (say 'keep going', or 'wrong way' / 'turn it' / 'flip it' / 'move 2 east')
q mob drop tower g2 l2 p3 t => First 2 blocks down: the mob drop tower starts near the bridge, runs north, 2×7×5, ending by the well, (turned right). Keep going? (say 'keep going', or 'wrong way' / 'turn it' / 'flip it' / 'move 2 east')
q mob drop tower g2 l2 p4 - => First 2 blocks down: the mob drop tower starts near the bridge, runs north, away from chests, 2×7×5, ending by the well. Keep going? (say 'keep going', or 'wrong way' / 'turn it' / 'flip it' / 'move 2 east')
q mob drop tower g2 l2 p4 t => First 2 blocks down: the mob drop tower starts near the bridge, runs north, away from chests, 2×7×5, ending by the well, (turned right). Keep going? (say 'keep going', or 'wrong way' / 'turn it' / 'flip it' / 'move 2 east')
q dot g0 l0 p0 - => First 0 blocks down: the dot starts here, runs in place, 1×1. Keep going? (say 'keep going', or 'wrong way' / 'turn it' / 'flip it' / 'move 2 east')
q dot g0 l0 p0 t => First 0 blocks down: the dot starts here, runs in place, 1×1, (turned right). Keep going? (say 'keep going', or 'wrong way' / 'turn it' / 'flip it' / 'move 2 east')
q dot g0 l0 p1 - => First 0 blocks down: the dot starts here, runs in place, toward farm, 1×1. Keep going? (say 'keep going', or 'wrong way' / 'turn it' / 'flip it' / 'move 2 east'; preview /view/buildplan/12)
q dot g0 l0 p1 t => First 0 blocks down: the dot starts here, runs in place, toward farm, 1×1, (turned right). Keep going? (say 'keep going', or 'wrong way' / 'turn it' / 'flip it' / 'move 2 east'; preview /view/buildplan/12)
q dot g0 l0 p2 - => First 0 blocks down: the dot starts here, runs in place, toward Mob Drop Tower, 1×1. Keep going? (say 'keep going', or 'wrong way' / 'turn it' / 'flip it' / 'move 2 east')
q dot g0 l0 p2 t => First 0 blocks down: the dot starts here, runs in place, toward Mob Drop Tower, 1×1, (turned right). Keep going? (say 'keep going', or 'wrong way' / 'turn it' / 'flip it' / 'move 2 east')
q dot g0 l0 p3 - => First 0 blocks down: the dot starts here, runs in place, toward hangout, 1×1. Keep going? (say 'keep going', or 'wrong way' / 'turn it' / 'flip it' / 'move 2 east')
q dot g0 l0 p3 t => First 0 blocks down: the dot starts here, runs in place, toward hangout, 1×1, (turned right). Keep going? (say 'keep going', or 'wrong way' / 'turn it' / 'flip it' / 'move 2 east')
q dot g0 l0 p4 - => First 0 blocks down: the dot starts here, runs in place, toward chests, 1×1. Keep going? (say 'keep going', or 'wrong way' / 'turn it' / 'flip it' / 'move 2 east')
q dot g0 l0 p4 t => First 0 blocks down: the dot starts here, runs in place, toward chests, 1×1, (turned right). Keep going? (say 'keep going', or 'wrong way' / 'turn it' / 'flip it' / 'move 2 east')
q dot g0 l1 p0 - => First 0 blocks down: the dot starts at mob_drop_tower, runs in place, 1×1. Keep going? (say 'keep going', or 'wrong way' / 'turn it' / 'flip it' / 'move 2 east')
q dot g0 l1 p0 t => First 0 blocks down: the dot starts at mob_drop_tower, runs in place, 1×1, (turned right). Keep going? (say 'keep going', or 'wrong way' / 'turn it' / 'flip it' / 'move 2 east')
q dot g0 l1 p1 - => First 0 blocks down: the dot starts at mob_drop_tower, runs in place, toward farm, 1×1. Keep going? (say 'keep going', or 'wrong way' / 'turn it' / 'flip it' / 'move 2 east'; preview /view/buildplan/12)
q dot g0 l1 p1 t => First 0 blocks down: the dot starts at mob_drop_tower, runs in place, toward farm, 1×1, (turned right). Keep going? (say 'keep going', or 'wrong way' / 'turn it' / 'flip it' / 'move 2 east'; preview /view/buildplan/12)
q dot g0 l1 p2 - => First 0 blocks down: the dot starts at mob_drop_tower, runs in place, toward Mob Drop Tower, 1×1. Keep going? (say 'keep going', or 'wrong way' / 'turn it' / 'flip it' / 'move 2 east')
q dot g0 l1 p2 t => First 0 blocks down: the dot starts at mob_drop_tower, runs in place, toward Mob Drop Tower, 1×1, (turned right). Keep going? (say 'keep going', or 'wrong way' / 'turn it' / 'flip it' / 'move 2 east')
q dot g0 l1 p3 - => First 0 blocks down: the dot starts at mob_drop_tower, runs in place, toward hangout, 1×1. Keep going? (say 'keep going', or 'wrong way' / 'turn it' / 'flip it' / 'move 2 east')
q dot g0 l1 p3 t => First 0 blocks down: the dot starts at mob_drop_tower, runs in place, toward hangout, 1×1, (turned right). Keep going? (say 'keep going', or 'wrong way' / 'turn it' / 'flip it' / 'move 2 east')
q dot g0 l1 p4 - => First 0 blocks down: the dot starts at mob_drop_tower, runs in place, toward chests, 1×1. Keep going? (say 'keep going', or 'wrong way' / 'turn it' / 'flip it' / 'move 2 east')
q dot g0 l1 p4 t => First 0 blocks down: the dot starts at mob_drop_tower, runs in place, toward chests, 1×1, (turned right). Keep going? (say 'keep going', or 'wrong way' / 'turn it' / 'flip it' / 'move 2 east')
q dot g0 l2 p0 - => First 0 blocks down: the dot starts near the bridge, runs in place, 1×1. Keep going? (say 'keep going', or 'wrong way' / 'turn it' / 'flip it' / 'move 2 east')
q dot g0 l2 p0 t => First 0 blocks down: the dot starts near the bridge, runs in place, 1×1, (turned right). Keep going? (say 'keep going', or 'wrong way' / 'turn it' / 'flip it' / 'move 2 east')
q dot g0 l2 p1 - => First 0 blocks down: the dot starts near the bridge, runs in place, toward farm, 1×1. Keep going? (say 'keep going', or 'wrong way' / 'turn it' / 'flip it' / 'move 2 east'; preview /view/buildplan/12)
q dot g0 l2 p1 t => First 0 blocks down: the dot starts near the bridge, runs in place, toward farm, 1×1, (turned right). Keep going? (say 'keep going', or 'wrong way' / 'turn it' / 'flip it' / 'move 2 east'; preview /view/buildplan/12)
q dot g0 l2 p2 - => First 0 blocks down: the dot starts near the bridge, runs in place, toward Mob Drop Tower, 1×1. Keep going? (say 'keep going', or 'wrong way' / 'turn it' / 'flip it' / 'move 2 east')
q dot g0 l2 p2 t => First 0 blocks down: the dot starts near the bridge, runs in place, toward Mob Drop Tower, 1×1, (turned right). Keep going? (say 'keep going', or 'wrong way' / 'turn it' / 'flip it' / 'move 2 east')
q dot g0 l2 p3 - => First 0 blocks down: the dot starts near the bridge, runs in place, toward hangout, 1×1. Keep going? (say 'keep going', or 'wrong way' / 'turn it' / 'flip it' / 'move 2 east')
q dot g0 l2 p3 t => First 0 blocks down: the dot starts near the bridge, runs in place, toward hangout, 1×1, (turned right). Keep going? (say 'keep going', or 'wrong way' / 'turn it' / 'flip it' / 'move 2 east')
q dot g0 l2 p4 - => First 0 blocks down: the dot starts near the bridge, runs in place, toward chests, 1×1. Keep going? (say 'keep going', or 'wrong way' / 'turn it' / 'flip it' / 'move 2 east')
q dot g0 l2 p4 t => First 0 blocks down: the dot starts near the bridge, runs in place, toward chests, 1×1, (turned right). Keep going? (say 'keep going', or 'wrong way' / 'turn it' / 'flip it' / 'move 2 east')
q dot g1 l0 p0 - => First 1 block down: the dot starts here, runs in place, 1×1. Keep going? (say 'keep going', or 'wrong way' / 'turn it' / 'flip it' / 'move 2 east')
q dot g1 l0 p0 t => First 1 block down: the dot starts here, runs in place, 1×1, (turned right). Keep going? (say 'keep going', or 'wrong way' / 'turn it' / 'flip it' / 'move 2 east')
q dot g1 l0 p1 - => First 1 block down: the dot starts here, runs in place, toward farm, 1×1. Keep going? (say 'keep going', or 'wrong way' / 'turn it' / 'flip it' / 'move 2 east'; preview /view/buildplan/12)
q dot g1 l0 p1 t => First 1 block down: the dot starts here, runs in place, toward farm, 1×1, (turned right). Keep going? (say 'keep going', or 'wrong way' / 'turn it' / 'flip it' / 'move 2 east'; preview /view/buildplan/12)
q dot g1 l0 p2 - => First 1 block down: the dot starts here, runs in place, toward Mob Drop Tower, 1×1. Keep going? (say 'keep going', or 'wrong way' / 'turn it' / 'flip it' / 'move 2 east')
q dot g1 l0 p2 t => First 1 block down: the dot starts here, runs in place, toward Mob Drop Tower, 1×1, (turned right). Keep going? (say 'keep going', or 'wrong way' / 'turn it' / 'flip it' / 'move 2 east')
q dot g1 l0 p3 - => First 1 block down: the dot starts here, runs in place, toward hangout, 1×1. Keep going? (say 'keep going', or 'wrong way' / 'turn it' / 'flip it' / 'move 2 east')
q dot g1 l0 p3 t => First 1 block down: the dot starts here, runs in place, toward hangout, 1×1, (turned right). Keep going? (say 'keep going', or 'wrong way' / 'turn it' / 'flip it' / 'move 2 east')
q dot g1 l0 p4 - => First 1 block down: the dot starts here, runs in place, toward chests, 1×1. Keep going? (say 'keep going', or 'wrong way' / 'turn it' / 'flip it' / 'move 2 east')
q dot g1 l0 p4 t => First 1 block down: the dot starts here, runs in place, toward chests, 1×1, (turned right). Keep going? (say 'keep going', or 'wrong way' / 'turn it' / 'flip it' / 'move 2 east')
q dot g1 l1 p0 - => First 1 block down: the dot starts at mob_drop_tower, runs in place, 1×1. Keep going? (say 'keep going', or 'wrong way' / 'turn it' / 'flip it' / 'move 2 east')
q dot g1 l1 p0 t => First 1 block down: the dot starts at mob_drop_tower, runs in place, 1×1, (turned right). Keep going? (say 'keep going', or 'wrong way' / 'turn it' / 'flip it' / 'move 2 east')
q dot g1 l1 p1 - => First 1 block down: the dot starts at mob_drop_tower, runs in place, toward farm, 1×1. Keep going? (say 'keep going', or 'wrong way' / 'turn it' / 'flip it' / 'move 2 east'; preview /view/buildplan/12)
q dot g1 l1 p1 t => First 1 block down: the dot starts at mob_drop_tower, runs in place, toward farm, 1×1, (turned right). Keep going? (say 'keep going', or 'wrong way' / 'turn it' / 'flip it' / 'move 2 east'; preview /view/buildplan/12)
q dot g1 l1 p2 - => First 1 block down: the dot starts at mob_drop_tower, runs in place, toward Mob Drop Tower, 1×1. Keep going? (say 'keep going', or 'wrong way' / 'turn it' / 'flip it' / 'move 2 east')
q dot g1 l1 p2 t => First 1 block down: the dot starts at mob_drop_tower, runs in place, toward Mob Drop Tower, 1×1, (turned right). Keep going? (say 'keep going', or 'wrong way' / 'turn it' / 'flip it' / 'move 2 east')
q dot g1 l1 p3 - => First 1 block down: the dot starts at mob_drop_tower, runs in place, toward hangout, 1×1. Keep going? (say 'keep going', or 'wrong way' / 'turn it' / 'flip it' / 'move 2 east')
q dot g1 l1 p3 t => First 1 block down: the dot starts at mob_drop_tower, runs in place, toward hangout, 1×1, (turned right). Keep going? (say 'keep going', or 'wrong way' / 'turn it' / 'flip it' / 'move 2 east')
q dot g1 l1 p4 - => First 1 block down: the dot starts at mob_drop_tower, runs in place, toward chests, 1×1. Keep going? (say 'keep going', or 'wrong way' / 'turn it' / 'flip it' / 'move 2 east')
q dot g1 l1 p4 t => First 1 block down: the dot starts at mob_drop_tower, runs in place, toward chests, 1×1, (turned right). Keep going? (say 'keep going', or 'wrong way' / 'turn it' / 'flip it' / 'move 2 east')
q dot g1 l2 p0 - => First 1 block down: the dot starts near the bridge, runs in place, 1×1. Keep going? (say 'keep going', or 'wrong way' / 'turn it' / 'flip it' / 'move 2 east')
q dot g1 l2 p0 t => First 1 block down: the dot starts near the bridge, runs in place, 1×1, (turned right). Keep going? (say 'keep going', or 'wrong way' / 'turn it' / 'flip it' / 'move 2 east')
q dot g1 l2 p1 - => First 1 block down: the dot starts near the bridge, runs in place, toward farm, 1×1. Keep going? (say 'keep going', or 'wrong way' / 'turn it' / 'flip it' / 'move 2 east'; preview /view/buildplan/12)
q dot g1 l2 p1 t => First 1 block down: the dot starts near the bridge, runs in place, toward farm, 1×1, (turned right). Keep going? (say 'keep going', or 'wrong way' / 'turn it' / 'flip it' / 'move 2 east'; preview /view/buildplan/12)
q dot g1 l2 p2 - => First 1 block down: the dot starts near the bridge, runs in place, toward Mob Drop Tower, 1×1. Keep going? (say 'keep going', or 'wrong way' / 'turn it' / 'flip it' / 'move 2 east')
q dot g1 l2 p2 t => First 1 block down: the dot starts near the bridge, runs in place, toward Mob Drop Tower, 1×1, (turned right). Keep going? (say 'keep going', or 'wrong way' / 'turn it' / 'flip it' / 'move 2 east')
q dot g1 l2 p3 - => First 1 block down: the dot starts near the bridge, runs in place, toward hangout, 1×1. Keep going? (say 'keep going', or 'wrong way' / 'turn it' / 'flip it' / 'move 2 east')
q dot g1 l2 p3 t => First 1 block down: the dot starts near the bridge, runs in place, toward hangout, 1×1, (turned right). Keep going? (say 'keep going', or 'wrong way' / 'turn it' / 'flip it' / 'move 2 east')
q dot g1 l2 p4 - => First 1 block down: the dot starts near the bridge, runs in place, toward chests, 1×1. Keep going? (say 'keep going', or 'wrong way' / 'turn it' / 'flip it' / 'move 2 east')
q dot g1 l2 p4 t => First 1 block down: the dot starts near the bridge, runs in place, toward chests, 1×1, (turned right). Keep going? (say 'keep going', or 'wrong way' / 'turn it' / 'flip it' / 'move 2 east')
q dot g1 l0 p0 - => First 1 block down: the dot starts here, runs in place, 1×1. Keep going? (say 'keep going', or 'wrong way' / 'turn it' / 'flip it' / 'move 2 east')
q dot g1 l0 p0 t => First 1 block down: the dot starts here, runs in place, 1×1, (turned right). Keep going? (say 'keep going', or 'wrong way' / 'turn it' / 'flip it' / 'move 2 east')
q dot g1 l0 p1 - => First 1 block down: the dot starts here, runs in place, toward farm, 1×1. Keep going? (say 'keep going', or 'wrong way' / 'turn it' / 'flip it' / 'move 2 east'; preview /view/buildplan/12)
q dot g1 l0 p1 t => First 1 block down: the dot starts here, runs in place, toward farm, 1×1, (turned right). Keep going? (say 'keep going', or 'wrong way' / 'turn it' / 'flip it' / 'move 2 east'; preview /view/buildplan/12)
q dot g1 l0 p2 - => First 1 block down: the dot starts here, runs in place, toward Mob Drop Tower, 1×1. Keep going? (say 'keep going', or 'wrong way' / 'turn it' / 'flip it' / 'move 2 east')
q dot g1 l0 p2 t => First 1 block down: the dot starts here, runs in place, toward Mob Drop Tower, 1×1, (turned right). Keep going? (say 'keep going', or 'wrong way' / 'turn it' / 'flip it' / 'move 2 east')
q dot g1 l0 p3 - => First 1 block down: the dot starts here, runs in place, toward hangout, 1×1. Keep going? (say 'keep going', or 'wrong way' / 'turn it' / 'flip it' / 'move 2 east')
q dot g1 l0 p3 t => First 1 block down: the dot starts here, runs in place, toward hangout, 1×1, (turned right). Keep going? (say 'keep going', or 'wrong way' / 'turn it' / 'flip it' / 'move 2 east')
q dot g1 l0 p4 - => First 1 block down: the dot starts here, runs in place, toward chests, 1×1. Keep going? (say 'keep going', or 'wrong way' / 'turn it' / 'flip it' / 'move 2 east')
q dot g1 l0 p4 t => First 1 block down: the dot starts here, runs in place, toward chests, 1×1, (turned right). Keep going? (say 'keep going', or 'wrong way' / 'turn it' / 'flip it' / 'move 2 east')
q dot g1 l1 p0 - => First 1 block down: the dot starts at mob_drop_tower, runs in place, 1×1. Keep going? (say 'keep going', or 'wrong way' / 'turn it' / 'flip it' / 'move 2 east')
q dot g1 l1 p0 t => First 1 block down: the dot starts at mob_drop_tower, runs in place, 1×1, (turned right). Keep going? (say 'keep going', or 'wrong way' / 'turn it' / 'flip it' / 'move 2 east')
q dot g1 l1 p1 - => First 1 block down: the dot starts at mob_drop_tower, runs in place, toward farm, 1×1. Keep going? (say 'keep going', or 'wrong way' / 'turn it' / 'flip it' / 'move 2 east'; preview /view/buildplan/12)
q dot g1 l1 p1 t => First 1 block down: the dot starts at mob_drop_tower, runs in place, toward farm, 1×1, (turned right). Keep going? (say 'keep going', or 'wrong way' / 'turn it' / 'flip it' / 'move 2 east'; preview /view/buildplan/12)
q dot g1 l1 p2 - => First 1 block down: the dot starts at mob_drop_tower, runs in place, toward Mob Drop Tower, 1×1. Keep going? (say 'keep going', or 'wrong way' / 'turn it' / 'flip it' / 'move 2 east')
q dot g1 l1 p2 t => First 1 block down: the dot starts at mob_drop_tower, runs in place, toward Mob Drop Tower, 1×1, (turned right). Keep going? (say 'keep going', or 'wrong way' / 'turn it' / 'flip it' / 'move 2 east')
q dot g1 l1 p3 - => First 1 block down: the dot starts at mob_drop_tower, runs in place, toward hangout, 1×1. Keep going? (say 'keep going', or 'wrong way' / 'turn it' / 'flip it' / 'move 2 east')
q dot g1 l1 p3 t => First 1 block down: the dot starts at mob_drop_tower, runs in place, toward hangout, 1×1, (turned right). Keep going? (say 'keep going', or 'wrong way' / 'turn it' / 'flip it' / 'move 2 east')
q dot g1 l1 p4 - => First 1 block down: the dot starts at mob_drop_tower, runs in place, toward chests, 1×1. Keep going? (say 'keep going', or 'wrong way' / 'turn it' / 'flip it' / 'move 2 east')
q dot g1 l1 p4 t => First 1 block down: the dot starts at mob_drop_tower, runs in place, toward chests, 1×1, (turned right). Keep going? (say 'keep going', or 'wrong way' / 'turn it' / 'flip it' / 'move 2 east')
q dot g1 l2 p0 - => First 1 block down: the dot starts near the bridge, runs in place, 1×1. Keep going? (say 'keep going', or 'wrong way' / 'turn it' / 'flip it' / 'move 2 east')
q dot g1 l2 p0 t => First 1 block down: the dot starts near the bridge, runs in place, 1×1, (turned right). Keep going? (say 'keep going', or 'wrong way' / 'turn it' / 'flip it' / 'move 2 east')
q dot g1 l2 p1 - => First 1 block down: the dot starts near the bridge, runs in place, toward farm, 1×1. Keep going? (say 'keep going', or 'wrong way' / 'turn it' / 'flip it' / 'move 2 east'; preview /view/buildplan/12)
q dot g1 l2 p1 t => First 1 block down: the dot starts near the bridge, runs in place, toward farm, 1×1, (turned right). Keep going? (say 'keep going', or 'wrong way' / 'turn it' / 'flip it' / 'move 2 east'; preview /view/buildplan/12)
q dot g1 l2 p2 - => First 1 block down: the dot starts near the bridge, runs in place, toward Mob Drop Tower, 1×1. Keep going? (say 'keep going', or 'wrong way' / 'turn it' / 'flip it' / 'move 2 east')
q dot g1 l2 p2 t => First 1 block down: the dot starts near the bridge, runs in place, toward Mob Drop Tower, 1×1, (turned right). Keep going? (say 'keep going', or 'wrong way' / 'turn it' / 'flip it' / 'move 2 east')
q dot g1 l2 p3 - => First 1 block down: the dot starts near the bridge, runs in place, toward hangout, 1×1. Keep going? (say 'keep going', or 'wrong way' / 'turn it' / 'flip it' / 'move 2 east')
q dot g1 l2 p3 t => First 1 block down: the dot starts near the bridge, runs in place, toward hangout, 1×1, (turned right). Keep going? (say 'keep going', or 'wrong way' / 'turn it' / 'flip it' / 'move 2 east')
q dot g1 l2 p4 - => First 1 block down: the dot starts near the bridge, runs in place, toward chests, 1×1. Keep going? (say 'keep going', or 'wrong way' / 'turn it' / 'flip it' / 'move 2 east')
q dot g1 l2 p4 t => First 1 block down: the dot starts near the bridge, runs in place, toward chests, 1×1, (turned right). Keep going? (say 'keep going', or 'wrong way' / 'turn it' / 'flip it' / 'move 2 east')
"""""";
}
