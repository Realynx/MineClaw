import { readFileSync } from 'node:fs';
import path from 'node:path';
import { describe, expect, it } from 'vitest';
import { scanCollision } from '@mineclaw/world-viewer/testing';
import { advanceControlBody, type ControlBody, type ControlCollision } from '@/features/control/controlMotion';
import { idleInput } from '@/features/control/playerInput';

/**
 * The prediction against the C# client: tests/fixtures/control-parity/<version>-<case>.json hold what MineClaw.World's
 * PlayerPhysics (b1.8.1) and Physics1122 (1.12.2) do, tick by tick, and ControlParityTests checks the C# side against the
 * same files. This test only reads them.
 */
interface Fixture {
  version: 'b1.8.1' | '1.12.2';
  case: string;
  cells: Array<[number, number, number, number, number]>;
  start: { x: number; y: number; z: number; yaw: number; vx: number; vy: number; vz: number; onGround: boolean };
  ticks: Array<{
    input: {
      forward: number;
      strafe: number;
      jump: boolean;
      sneak: boolean;
      sprint: boolean;
      yaw: number;
      pitch: number;
    };
    x: number;
    y: number;
    z: number;
    vx: number;
    vy: number;
    vz: number;
    onGround: boolean;
  }>;
}

const here = path.dirname(new URL(import.meta.url).pathname.replace(/^\/([A-Za-z]:)/, '$1'));
const folder = path.join(here, '..', '..', '..', '..', 'tests', 'fixtures', 'control-parity');
const fixture = (version: string, name: string) =>
  JSON.parse(readFileSync(path.join(folder, `${version}-${name}.json`), 'utf8')) as Fixture;

/** The C# side's 1e-9 is for the same arithmetic; the browser uses doubles where the client uses floats and a sine table. */
const TOLERANCE = 0.005;
const CASES = ['walk', 'sprint-jump', 'walk-diagonal', 'sneak-diagonal', 'ledge', 'ladder', 'shallow-water'];

/** The fixture's world as the takeover's collision cache answers it: everything not listed is air, all of it loaded. */
function world(f: Fixture): ControlCollision {
  const cells = new Map(f.cells.map(([x, y, z, id, meta]) => [`${x},${y},${z}`, { id, meta }]));
  const air = { id: 0, meta: 0 };
  const read = (x: number, y: number, z: number) => cells.get(`${x},${y},${z}`) ?? air;
  return (box) => scanCollision(read, box, f.version);
}

/** Steps the prediction 50 ms per fixture tick and returns the largest position error and the first tick it was seen. */
function replay(f: Fixture) {
  const query = world(f);
  const body: ControlBody = { ...f.start, dim: 0, height: 1.8, jumpCooldown: 0 };
  let worst = 0,
    at = 0;
  const grounded: boolean[] = [];
  f.ticks.forEach((t, i) => {
    const input = { ...idleInput(), ...t.input };
    expect(advanceControlBody(body, input, 50, query, { version: f.version })).toBe(true);
    const error = Math.max(Math.abs(body.x - t.x), Math.abs(body.y - t.y), Math.abs(body.z - t.z));
    if (error > worst) {
      worst = error;
      at = i + 1;
    }
    grounded.push(body.onGround === t.onGround);
  });
  return { worst, at, grounded };
}

describe('the takeover prediction matches the C# client tick by tick', () => {
  it.each(['b1.8.1', '1.12.2'].flatMap((v) => CASES.map((c) => [v, c] as const)))('%s %s', (version, name) => {
    const f = fixture(version, name);
    expect(f.ticks.length).toBeGreaterThanOrEqual(20);
    expect(f.ticks.length).toBeLessThanOrEqual(40);
    const { worst, at, grounded } = replay(f);
    expect({ worst: worst < TOLERANCE ? 'within' : worst, at: worst < TOLERANCE ? 0 : at }).toEqual({
      worst: 'within',
      at: 0,
    });
    expect(grounded.every(Boolean)).toBe(true);
  });
});
