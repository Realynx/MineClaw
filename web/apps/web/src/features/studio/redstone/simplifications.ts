/**
 * What the simulator does differently from the game: the nine simplifications @mineclaw/redstone states (its tick model),
 * in plain words, for the help the redstone panel shows. Keep them in step with the engine's own list.
 */
export const FIDELITY =
  'Logic and timing are exact to the game tick. Circuits that rely on the order of updates inside one game tick are flagged in the checks, not emulated.';

export const SIMPLIFICATIONS: readonly string[] = [
  'Dust settles to the levels it has in the game, but the parts around it then hear of the change in a fixed order. A circuit that depends on the order of updates inside one game tick (locational tricks, zero-tick pulses, some update detectors) can behave differently in the world; the spots the simulator can tell are listed as checks.',
  'A piston’s move takes two game ticks with no positions in between; the moving block is shown where it is going. Mobs, dropped items, sounds and particles are left out.',
  'Slime blocks move as plain blocks, and a check says so.',
  'TNT is primed and removed, and nothing explodes. Dispensers, droppers and note blocks only report that they fired or played, hoppers only whether they are held, and no items move.',
  'Containers, weighted plates, daylight sensors, trapped chests and detector rails give the level you set. Tripwire does nothing. A wooden button or plate is released by you or by its timer.',
  'Your clicks land between two game ticks, where a player’s do in both game versions, so nothing is lost or late.',
  'Nothing ticks at random.',
  'The ground under the plot is solid, carries power like stone, and never moves.',
  'A memory cell starts in whichever state the simulator’s start order gives it. In the world that depends on the order the torches are placed.',
];
