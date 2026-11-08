import { COLORS, GLYPHS } from './visuals';

export const WORK_GLYPHS: Record<string, string> = {
  chop: 'chop',
  mine: 'mine',
  harvest: 'harvest',
  cut: 'cut',
  haul: 'haul',
  hunt: 'hunt',
  build: 'build',
  deconstruct: 'deconstruct',
  attack: 'hunt',
  craft: 'bench',
  cook: 'bench',
  sleep: 'bed',
  rest: 'bed',
  guard: 'drafted',
  flee: 'drafted',
  rescue: 'haul',
  sow: 'harvest',
  farm: 'harvest',
  plant: 'harvest',
  store: 'chest',
  deliver: 'haul',
  smelt: 'bench',
  fish: 'harvest',
  move: 'haul',
  repair: 'build',
};
const IDLE =
  'M8 1.5a6.5 6.5 0 1 1 0 13 6.5 6.5 0 0 1 0-13Zm0 1.5a5 5 0 1 0 0 10A5 5 0 0 0 8 3ZM7.3 4h1.4v3.7l2.5 1.5-.7 1.2L7.3 8.5Z';
const WORK_COLORS: Record<string, string> = {
  guard: '#ff9f43',
  flee: COLORS.danger,
  sleep: '#a5a3eb',
  rest: '#a5a3eb',
  fish: '#65d6cb',
  smelt: '#efa060',
  craft: COLORS.bench,
  cook: '#efa060',
  move: '#8bb5d6',
};

/** The controller map, freecam and Live Hive share the same work symbols. */
export function workAppearance(kind: string): { path: string; color: string } {
  const glyph = WORK_GLYPHS[kind];
  return {
    path: GLYPHS[glyph ?? ''] ?? IDLE,
    color: COLORS.designation[kind] ?? WORK_COLORS[kind] ?? COLORS.designation[glyph ?? ''] ?? '#9ba7b7',
  };
}
