/**
 * Golden runs of the zone painter (`paintZones` / `eraseZones`) and the irrigation planner (`irrigationPlan`): seeded random
 * paints, extends and erases over a small map, and plans over random zones and soils. They pin the exact results (zones, rows,
 * ids, names, merge / split / shrink lists, removed cells, channel cells, notes) so the code can be restructured safely.
 * `STORY_GOLDEN_DUMP=<file>` writes the trace.
 */
import { createHash } from 'node:crypto';
import { writeFileSync } from 'node:fs';
import { describe, expect, it } from 'vitest';
import { eraseZones, irrigationPlan, paintZones, rectCells, rowsOf, zoneCellSet, type ZoneLike } from '../src/index.js';

/** A seeded generator (mulberry32). */
function seeded(seed: number): () => number {
  let a = seed >>> 0;
  return () => {
    a = (a + 0x6d2b79f5) >>> 0;
    let t = a;
    t = Math.imul(t ^ (t >>> 15), t | 1);
    t ^= t + Math.imul(t ^ (t >>> 7), t | 61);
    return ((t ^ (t >>> 14)) >>> 0) / 4294967296;
  };
}

const KINDS = ['farm', 'farm', 'chop', 'mine', 'home', 'forbidden', 'fishing'];
const PLANTS = ['wheat', 'carrot', null];

function digest(trace: string[], name: string): string {
  if (process.env.STORY_GOLDEN_DUMP) writeFileSync(`${process.env.STORY_GOLDEN_DUMP}.${name}`, trace.join('\n'));
  return createHash('sha256').update(trace.join('\n')).digest('hex');
}

const show = (zones: readonly ZoneLike[]) =>
  zones
    .map(
      (z) =>
        `${z.id}:${z.kind}:${z.plant ?? '-'}:${z.name ?? '-'}:${z.area.join(',')}:${JSON.stringify(z.rows ?? null)}:${z.cells ?? '-'}`,
    )
    .join(' | ');

describe('zone painter (golden)', () => {
  it('paints, extends and erases exactly as before', () => {
    const rnd = seeded(1234);
    const int = (n: number) => Math.floor(rnd() * n);
    let zones: ZoneLike[] = [];
    let next = 1;
    const newId = () => next++;
    const trace: string[] = [];
    for (let step = 0; step < 400; step++) {
      const x1 = int(24) - 4,
        z1 = int(24) - 4;
      const rect = [x1, z1, x1 + int(7) - 1, z1 + int(7) - 1] as const;
      const roll = rnd();
      try {
        if (roll < 0.25 && zones.length) {
          const kind = rnd() < 0.3 ? null : rnd() < 0.2 ? 'any' : zones[int(zones.length)]!.kind;
          const out = eraseZones(zones, kind, rect, newId);
          zones = out.zones;
          trace.push(
            `erase ${kind} ${rect.join(',')} → ${JSON.stringify(out.result)} removed ${[...out.removedCells].sort((a, b) => a - b).join(',')}`,
          );
        } else {
          const kind = KINDS[int(KINDS.length)]!;
          const plant = PLANTS[int(PLANTS.length)] ?? null;
          const into =
            rnd() < 0.15 && zones.length
              ? rnd() < 0.1
                ? 'nope'
                : zones[int(zones.length)]!.id
              : rnd() < 0.05
                ? null
                : undefined;
          const cells = rnd() < 0.15 ? new Set([...rectCells(...rect)].filter(() => rnd() < 0.6)) : null;
          const out = paintZones(
            zones,
            { kind, plant, rect, y: [60 + int(4), 64 + int(4)], into, cells },
            (k, p) => ({
              id: newId(),
              kind: k,
              plant: p,
              name: rnd() < 0.5 ? `Zone ${next}` : null,
              area: [0, 0, 0, 0, 0, 0],
            }),
            newId,
          );
          zones = out.zones;
          trace.push(
            `paint ${kind}/${plant} ${rect.join(',')} into ${String(into)} cells ${cells?.size ?? '-'} → ${JSON.stringify(out.result)} removed ${[...out.removedCells].sort((a, b) => a - b).join(',')}`,
          );
        }
      } catch (e) {
        trace.push(`refused ${(e as Error).message} ${(e as { status?: number }).status}`);
      }
      trace.push(`  ${show(zones)}`);
    }
    expect(trace.filter((l) => l.startsWith('refused')).length).toBeGreaterThan(0);
    expect(digest(trace, 'zones')).toMatchSnapshot();
  });

  it('plans irrigation exactly as before', () => {
    const rnd = seeded(77);
    const int = (n: number) => Math.floor(rnd() * n);
    const trace: string[] = [];
    for (let step = 0; step < 300; step++) {
      const w = 1 + int(26),
        d = 1 + int(26);
      const x0 = int(20) - 10,
        z0 = int(20) - 10;
      const cells = [...rectCells(x0, z0, x0 + w - 1, z0 + d - 1)].filter(() => rnd() < 0.92);
      if (!cells.length) continue;
      const zone: ZoneLike = {
        id: step,
        kind: 'farm',
        area: [x0, 63, z0, x0 + w - 1, 63, z0 + d - 1],
        rows: rowsOf(cells),
      };
      zoneCellSet(zone);
      const cane = rnd() < 0.3;
      const salt = int(1000);
      const allWet = rnd() < 0.1;
      const h = (x: number, y: number, z: number) => Math.abs((x * 73856093) ^ (y * 19349663) ^ (z * 83492791) ^ salt);
      const plan = irrigationPlan(
        zone,
        cane,
        (x, z) => (h(x, 0, z) % 11 === 0 ? null : 62 + (h(x, 1, z) % 2)),
        (x, y, z) => allWet || h(x, y, z) % 5 === 0,
        (x, y, z) => [2, 3, 8, 9, 1, 12, 60][h(x, y + 7, z) % 7]!,
      );
      trace.push(`${w}x${d}@${x0},${z0} cane ${cane} → ${JSON.stringify(plan)}`);
    }
    expect(digest(trace, 'irrigation')).toMatchSnapshot();
  });
});
