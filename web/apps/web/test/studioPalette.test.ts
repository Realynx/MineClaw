/**
 * The Design Studio's whole-version palette: search by name, spec, id and id:meta, the creative-tab categories, the recent and
 * favourite rows (per version, this browser), and the facing / pairing rules of the new stateful families (beds, double
 * plants, rails, vines, signs, pistons, buttons, chests pairing), the same as the bot's DesignFacing.
 */
import { createDesignerMock, mockDesignerPalette } from '@mineclaw/contracts';
import { beforeEach, describe, expect, it } from 'vitest';
import { Canvas } from '@/features/studio/canvas';
import { familyOf, firstHalfOf, hasFacing, mirrorMeta, partnerOf, placeMeta, rotateMeta } from '@/features/studio/facing';
import {
  categoryCounts,
  entriesOf,
  favouriteKeys,
  inCategory,
  pushRecent,
  recentKeys,
  searchPalette,
  toggleFavourite,
} from '@/features/studio/palette';

const modern = mockDesignerPalette('1.12.2');
const beta = mockDesignerPalette('b1.8.1');

describe('palette search and categories', () => {
  it('finds blocks by words, specs, ids and id:meta', () => {
    expect(searchPalette(modern, 'magenta concrete')[0]!.spec).toBe('concrete:magenta');
    expect(searchPalette(modern, 'stained_glass_pane:lime')[0]!.spec).toBe('stained_glass_pane:lime');
    expect(searchPalette(modern, '251:5')[0]!.spec).toBe('concrete:lime');
    expect(searchPalette(modern, 'spruce door')[0]!.spec).toBe('spruce_door');
    expect(searchPalette(modern, 'bed')[0]!.spec).toBe('bed');
    expect(searchPalette(beta, 'concrete')).toEqual([]);
    expect(searchPalette(beta, '35:7')[0]!.name).toBe('Gray Wool');
    // Name-start matches first, palette order among equals.
    expect(searchPalette(modern, 'oak')[0]!.name).toMatch(/^Oak /);
  });

  it('groups the palette in creative-tab categories, every entry in one', () => {
    const counts = categoryCounts(modern);
    expect(counts.map(([c]) => c)).toEqual(['Building', 'Decoration', 'Colored', 'Nature', 'Redstone', 'Transport', 'Misc']);
    expect(counts.reduce((n, [, k]) => n + k, 0)).toBe(modern.length);
    expect(inCategory(modern, 'Colored').every((e) => e.cat === 'Colored')).toBe(true);
    expect(inCategory(modern, 'Colored').length).toBeGreaterThanOrEqual(16 * 7);
    expect(inCategory(modern, 'Transport').map((e) => e.spec)).toContain('golden_rail');
    expect(inCategory(modern, null)).toHaveLength(modern.length);
    expect(modern.length).toBeGreaterThan(beta.length * 2);
  });
});

describe('recent and favourite rows', () => {
  beforeEach(() => localStorage.clear());
  it('keeps the newest first, at most 12, per version', () => {
    for (const e of modern.slice(0, 14)) pushRecent('1.12.2', e);
    pushRecent('1.12.2', modern[3]!);
    const keys = recentKeys('1.12.2');
    expect(keys).toHaveLength(12);
    expect(keys[0]).toBe(`${modern[3]!.id}:${modern[3]!.meta}`);
    expect(new Set(keys).size).toBe(12);
    expect(recentKeys('b1.8.1')).toEqual([]);
    // Another version's keys don't resolve to entries that palette doesn't have.
    expect(entriesOf(beta, keys).every((e) => beta.includes(e))).toBe(true);
  });
  it('stars and un-stars', () => {
    const red = modern.find((e) => e.spec === 'wool:red')!;
    expect(toggleFavourite('1.12.2', red)).toEqual(['35:14']);
    expect(entriesOf(modern, favouriteKeys('1.12.2'))[0]!.name).toBe('Red Wool');
    expect(toggleFavourite('1.12.2', red)).toEqual([]);
  });
});

describe('new families: facing, rotation, mirror, pairing', () => {
  it('knows the families the bot knows', () => {
    expect(familyOf(26)).toBe('bed');
    expect(familyOf(175)).toBe('tall');
    expect(familyOf(193)).toBe('door');
    expect(familyOf(240)).toBe('horizontal');
    expect(familyOf(227)).toBe('facing6');
    for (const id of [26, 66, 106, 63, 176, 33, 154, 77, 69, 235]) expect(hasFacing(id)).toBe(true);
    expect(hasFacing(251)).toBe(false);
  });

  it('places beds along the look, pistons and glazed terracotta toward the player, buttons on their wall', () => {
    expect(placeMeta(26, 0, 'north', 1, '1.12.2')).toBe(2); // the head away from the player
    expect(placeMeta(33, 1, 'north', 3, '1.12.2')).toBe(3); // a piston facing the player (south)
    expect(placeMeta(235, 0, 'east', 1, '1.12.2')).toBe(1); // front to the west, toward the player
    expect(placeMeta(77, 1, 'north', 3, '1.12.2')).toBe(3); // on the south face of a block: faces south
    expect(placeMeta(198, 1, 'north', 1, '1.12.2')).toBe(1); // an end rod on a top face stands up
    expect(placeMeta(66, 0, 'east', 1, '1.12.2')).toBe(1); // a rail along the look
    expect(placeMeta(63, 0, 'north', 1, 'b1.8.1')).toBe(0); // a sign facing the player
    expect(placeMeta(170, 0, 'north', 4, '1.12.2')).toBe(4); // hay along x
  });

  it('turns and mirrors like the bot', () => {
    expect(rotateMeta(26, 0, 1, '1.12.2')).toBe(1);
    expect(rotateMeta(26, 8, 1, '1.12.2')).toBe(9);
    expect(rotateMeta(66, 2, 1, 'b1.8.1')).toBe(5);
    expect(rotateMeta(66, 6, 1, 'b1.8.1')).toBe(7);
    expect(rotateMeta(27, 13, 1, '1.12.2')).toBe(11);
    expect(rotateMeta(106, 1, 1, '1.12.2')).toBe(2);
    expect(rotateMeta(63, 0, 1, 'b1.8.1')).toBe(4);
    expect(rotateMeta(69, 5, 1, 'b1.8.1')).toBe(6);
    expect(rotateMeta(155, 3, 1, '1.12.2')).toBe(4);
    expect(rotateMeta(54, 5, 1, '1.12.2')).toBe(3);
    expect(mirrorMeta(63, 4, 'x')).toBe(12);
    expect(mirrorMeta(66, 6, 'x')).toBe(7);
    expect(mirrorMeta(66, 6, 'z')).toBe(9);
    expect(mirrorMeta(106, 2, 'x')).toBe(8);
  });

  it('keeps beds, doors and double plants whole and pairs chests on the canvas', () => {
    expect(partnerOf(26, 1)).toEqual([-1, 0, 0, 9]);
    expect(firstHalfOf(26, 9)).toEqual([1, 0, 0]);
    const c = new Canvas();
    c.apply([[10, 0, 10, 26, 0]]);
    expect(c.get(10, 0, 11)).toEqual([26, 8]); // the head, one step south
    c.apply([[10, 0, 11, 1, 0]]); // replacing the head takes the foot
    expect(c.get(10, 0, 10)).toEqual([0, 0]);
    c.apply([[20, 0, 20, 175, 4]]);
    expect(c.get(20, 1, 20)).toEqual([175, 12]);
    c.apply([[30, 0, 30, 193, 1]]);
    expect(c.get(30, 1, 30)).toEqual([193, 9]);
    c.apply([[40, 0, 40, 54, 2]]);
    c.apply([[41, 0, 40, 54, 5]]);
    expect(c.get(41, 0, 40)).toEqual([54, 2]); // the second chest pairs, facing north like the first
  });
});

describe('the mock studio versions', () => {
  it('switches an empty canvas, checks and converts a full one, and saves the version', () => {
    const m = createDesignerMock(500);
    const s = m.create({ version: '1.12.2' }, 'poofimafox');
    expect(s.version).toBe('1.12.2');
    expect(s.palette.length).toBeGreaterThan(250);
    m.canvas(s.id, { ops: [{ kind: 'place', cells: [[1, 0, 1, 251, 2], [2, 0, 1, 4, 0], [3, 0, 1, 5, 1]] }] });
    const check = m.version(s.id, { version: 'b1.8.1' });
    expect(check.changed).toBe(false);
    expect(check.report!.missing.map((x) => x.name)).toEqual(['Magenta Concrete']);
    const conv = m.version(s.id, { version: 'b1.8.1', apply: true });
    expect(conv.session!.version).toBe('b1.8.1');
    expect(conv.session!.blocks.map((b) => b[3]).sort()).toEqual([4, 5]);
    const saved = m.save(s.id, { name: 'hut' }, 'poofimafox');
    expect(saved.text).toContain('(b1.8.1)');
    expect(m.get(s.id).revisions[0]!.version).toBe('b1.8.1');
  });
});
