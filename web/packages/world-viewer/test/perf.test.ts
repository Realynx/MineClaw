import { describe, expect, it } from 'vitest';
import { ALL_VISIBLE, connects, pairBit, sectionVisibility, visibleSections } from '../src/mesh/visibility';
import { buildStateTable } from '../src/mesh/states';
import { PROFILES, QualityController } from '../src/render/quality';
import { precipitation } from '../src/world/sky';
import { storeWith, type B } from './helpers';

const table = buildStateTable('b1.8.1', null);

describe('cave culling (visibility graph)', () => {
  it('pair bits cover the 15 face pairs exactly once', () => {
    const bits = new Set<number>();
    for (let a = 0; a < 6; a++) for (let b = a + 1; b < 6; b++) bits.add(pairBit(a, b));
    expect(bits.size).toBe(15);
    expect([...bits].reduce((x, y) => x | y, 0)).toBe(ALL_VISIBLE);
    expect(pairBit(3, 1)).toBe(pairBit(1, 3));
  });

  it('a section split by a solid wall connects each side to its own faces only', async () => {
    // Wall at x = 8 through section (0, 4, 0): west half reaches the west face, east half the east face.
    const wall: B[] = [];
    for (let y = 64; y < 80; y++) for (let z = 0; z < 16; z++) wall.push([8, y, z, 1]);
    const store = await storeWith(wall);
    const m = sectionVisibility(store, 0, 0, 4, 0, table);
    expect(connects(m, 4, 5)).toBe(false); // west ↔ east blocked
    expect(connects(m, 4, 1)).toBe(true); // west ↔ up (the west half touches the top)
    expect(connects(m, 5, 2)).toBe(true);
    expect(sectionVisibility(store, 0, 0, 2, 0, table)).toBe(ALL_VISIBLE); // empty
  });

  it('a fully solid section blocks the walk; what lies behind it is not visible', () => {
    // A row of sections along +x; section (2,0,0) is solid rock (mask 0).
    const vis = (cx: number, cy: number) => (cx === 2 && cy === 0 ? 0 : ALL_VISIBLE);
    const seen = visibleSections(0, 0, 0, 6, 1, (cx, cy) => vis(cx, cy));
    expect(seen.has('1,0,0')).toBe(true);
    expect(seen.has('2,0,0')).toBe(true); // the wall itself is drawn
    // Around the wall through other sections it is still reachable (open world), so build a tunnel world instead:
    const tunnel = (cx: number, cy: number, cz: number) => (cz === 0 && cy === 0 ? (cx === 3 ? 0 : ALL_VISIBLE) : 0);
    const t = visibleSections(0, 0, 0, 8, 1, tunnel);
    expect(t.has('2,0,0')).toBe(true);
    expect(t.has('3,0,0')).toBe(true);
    expect(t.has('4,0,0')).toBe(false);
    expect(t.has('5,0,0')).toBe(false);
  });

  it('never walks back toward the camera, and skips sections behind the view direction', () => {
    const seen = visibleSections(0, 0, 0, 4, 1, () => ALL_VISIBLE, { x: 1, y: 0, z: 0 });
    expect(seen.has('3,0,0')).toBe(true);
    expect(seen.has('-3,0,0')).toBe(false);
  });
});

describe('quality controller', () => {
  it('steps resolution down first, then draw distance, and gives them back with headroom', () => {
    const q = new QualityController(PROFILES.software);
    for (let i = 0; i < 20; i++) q.sample(i * 1000, 60);
    expect(q.state.scale).toBeCloseTo(PROFILES.software.minScale);
    expect(q.state.drawFactor).toBeLessThan(1);
    expect(q.drawDistance(128)).toBeGreaterThanOrEqual(PROFILES.software.minDraw);
    for (let i = 20; i < 80; i++) q.sample(i * 1000, 5);
    expect(q.state.drawFactor).toBe(1);
    expect(q.state.scale).toBe(1);
    expect(q.drawDistance(200)).toBe(PROFILES.software.maxDraw);
  });

  it('samples at most once a second', () => {
    const q = new QualityController(PROFILES.gpu);
    expect(q.due(1000)).toBe(true);
    q.sample(1000, 10);
    expect(q.due(1500)).toBe(false);
    expect(q.due(2001)).toBe(true);
  });
});

describe('precipitation by biome', () => {
  it('rain, snow on cold or high ground, nothing in dry biomes', () => {
    expect(precipitation({ temp: 0.8, precip: 'rain' }, 70)).toBe('rain');
    expect(precipitation({ temp: 0.0, precip: 'snow' }, 70)).toBe('snow');
    expect(precipitation({ temp: 0.2, precip: 'rain' }, 160)).toBe('snow'); // extreme hills tops
    expect(precipitation({ temp: 2, precip: 'none' }, 70)).toBe('none');
    expect(precipitation(null, 70)).toBe('rain');
  });
});
