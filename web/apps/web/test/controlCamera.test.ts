import { describe, expect, it } from 'vitest';
import { ControlCamera } from '@/features/control/controlCamera';
import { advanceControlBody, type ControlBody, type ControlCollision } from '@/features/control/controlMotion';
import { idleInput } from '@/features/control/playerInput';

const position = (x: number, y = 64, dim = 0) => ({ x, y, z: 2, dim, onGround: true, food: 20 });
const floor: ControlCollision = () => ({ loaded: true, boxes: [{ minX: -100, maxX: 100, minY: 63, maxY: 64, minZ: -100, maxZ: 100 }] });
const walking = { ...idleInput(-90), forward: 1 };
const body = (): ControlBody => ({ ...position(0), vx: 0, vy: 0, vz: 0, onGround: true, height: 1.8, jumpCooldown: 0 });

describe('controlled pawn prediction', () => {
  it('moves on the first render after a key press without any server movement reply', () => {
    const camera = new ControlCamera();
    camera.push(position(0), 0);
    camera.position(0, idleInput(), floor);
    camera.setInput(walking, 0);
    expect(camera.position(16)?.x).toBeGreaterThan(0.015);
    expect(camera.position(32)?.x).toBeGreaterThan(0.04);
  });

  it.each([[80, false], [100, false], [120, false], [120, true]] as const)('keeps steady movement smooth with %ims delayed20Hz authority and Speed3 sprint=%s', (lag, sprint) => {
    const camera = new ControlCamera(), server = body();
    const input = { ...walking, sprint }, multiplier = sprint ? 1.3 : 1;
    camera.push(position(0), 0); camera.position(0, idleInput(), floor); camera.setInput(input, 0);
    const pending: Array<{ at: number; state: ReturnType<typeof position> }> = [];
    const rendered: number[] = [];
    for (let now = 10; now <= 3000; now += 10) {
      advanceControlBody(server, input, 10, floor, 20, 1.6);
      if (now % 50 === 0) pending.push({ at: now, state: { ...position(server.x), z: server.z } });
      while (pending[0] && pending[0].at + lag <= now) {
        const update = pending.shift()!;
        camera.push(update.state, now, 1_000_000 + update.at);
      }
      const x = camera.position(now, input, floor)!.x;
      if (now >= 2000) rendered.push(x);
    }
    const speed = (rendered.at(-1)! - rendered[0]!) / ((rendered.length - 1) * 0.01);
    expect(speed).toBeGreaterThan(6.5 * multiplier);
    expect(speed).toBeLessThan(7.3 * multiplier);
    const steps = rendered.slice(1).map((x, i) => x - rendered[i]!);
    expect(Math.min(...steps)).toBeGreaterThan(0.055 * multiplier);
    expect(Math.max(...steps)).toBeLessThan(0.085 * multiplier);
    expect(Math.abs(camera.position(3000)!.x - server.x)).toBeLessThan(0.5 * multiplier);
  });

  it('settles to a stopped body while inventory is open and resumes from that position without old lead', () => {
    const camera = new ControlCamera();
    camera.push(position(0), 0); camera.position(0, idleInput(), floor); camera.setInput(walking, 0);
    camera.position(150);
    camera.setInput(idleInput(-90), 150, true);
    for (let now = 200; now <= 1000; now += 50) { camera.push(position(0.25), now); camera.position(now + 10); }
    expect(camera.position(1010)!.x).toBeCloseTo(0.25, 2);
    camera.setInput(walking, 1010);
    expect(camera.position(1026)!.x).toBeGreaterThan(0.265);
  });

  it('bounds lead, resets stale movement and clears motion on blur', () => {
    const camera = new ControlCamera();
    camera.push(position(0), 0); camera.position(0, idleInput(), floor); camera.setInput(walking, 0);
    for (let now = 10; now <= 340; now += 10) camera.position(now);
    expect(camera.position(340)!.x).toBeLessThanOrEqual(1);
    expect(camera.position(350)).toMatchObject({ x: 0 });
    camera.push(position(5), 400); camera.setInput(walking, 400); camera.position(450, walking, floor);
    camera.setInput(idleInput(-90), 450, true);
    const stopped = camera.position(450)!;
    expect(camera.position(500)).toEqual(stopped);
  });

  it('reconciles an actual lateral correction gradually and snaps teleports, dimensions and clock resets', () => {
    const camera = new ControlCamera();
    camera.push(position(0), 0, 1000); camera.position(0, idleInput(), floor);
    camera.push(position(0.5), 50, 1050);
    const corrected = camera.position(66)!.x;
    expect(corrected).toBeGreaterThan(0); expect(corrected).toBeLessThan(0.5);
    camera.push(position(10), 100, 1100); expect(camera.position(100)?.x).toBe(10);
    camera.push(position(11, 70, -1), 150, 1150); expect(camera.position(150)).toMatchObject({ x: 11, y: 70, dim: -1 });
    camera.push(position(12, 70, -1), 200, 900); expect(camera.position(200)?.x).toBe(12);
    camera.clear(); expect(camera.position(1000)).toBeNull();
  });

  it('uses authoritative movement while nearby terrain is not loaded', () => {
    const camera = new ControlCamera(), unknown: ControlCollision = () => ({ loaded: false, boxes: [] });
    camera.push(position(0), 0); camera.position(0, walking, unknown);
    expect(camera.position(50)?.x).toBe(0);
    camera.push(position(0.2), 50);
    expect(camera.position(66)?.x).toBe(0.2);
  });
});
