import { describe, expect, it } from 'vitest';
import { ControlCamera } from '@/features/control/controlCamera';

const position = (x: number, y = 64, dim = 0) => ({ x, y, z: 2, dim });

describe('controlled pawn camera', () => {
  it('moves at constant speed between 20 Hz authoritative samples instead of easing to a stop each frame', () => {
    const camera = new ControlCamera();
    for (let at = 0; at <= 150; at += 50) camera.push(position(at / 250), at);
    const positions = [100, 110, 120, 130, 140, 150].map((now) => camera.position(now)!.x);
    positions.slice(1).forEach((x, i) => expect(x - positions[i]!).toBeCloseTo(0.04));
    expect(camera.position(150)?.x).toBeCloseTo(0.3);
  });

  it('preserves server sample spacing when delivery batches several updates together', () => {
    const camera = new ControlCamera();
    for (let step = 0; step <= 4; step++) camera.push(position(step * 0.2), 1000, 1_000_000 + step * 50);
    expect(camera.position(1000)?.x).toBeCloseTo(0.5);
    expect(camera.position(1025)?.x).toBeCloseTo(0.6);
    expect(camera.position(2000)?.x).toBeCloseTo(0.8); // no speculative motion after the final snapshot
  });

  it('snaps across teleports, dimension changes, stale gaps and server clock regressions', () => {
    const camera = new ControlCamera();
    camera.push(position(0), 0, 1000);
    camera.push(position(10), 50, 1050);
    expect(camera.position(50)?.x).toBe(10);
    camera.push(position(11, 70, -1), 100, 1100);
    expect(camera.position(100)).toMatchObject({ x: 11, y: 70, dim: -1 });
    camera.push(position(11.5, 70, -1), 500, 1500);
    expect(camera.position(500)?.x).toBe(11.5);
    camera.push(position(12, 70, -1), 550, 1200);
    expect(camera.position(550)?.x).toBe(12);
    camera.clear();
    expect(camera.position(1000)).toBeNull();
  });
});
