import { describe, expect, it } from 'vitest';
import { anchorLift, bodyLook, hologramCells, TOKEN_PX } from '@/features/story/bodyLook';

describe('a body on the Story map: its model, or the 2D token', () => {
  const at = (o: Partial<Parameters<typeof bodyLook>[0]>) => bodyLook({ detail: true, pxPerBlock: 18, feetY: 65, cutY: null, ...o });

  it('the model when the viewer drew it and it is big enough to read', () => {
    expect(at({})).toBe('model');
    expect(at({ pxPerBlock: TOKEN_PX })).toBe('model');
  });

  it('the token when zoomed far out (a body is a few pixels)', () => {
    expect(at({ pxPerBlock: TOKEN_PX - 0.5 })).toBe('token');
    expect(at({ pxPerBlock: 3 })).toBe('token');
  });

  it('the token when the viewer has no model for it: not in the world, beyond the entity distance, or a far box', () => {
    expect(at({ detail: null })).toBe('token');
    expect(at({ detail: false })).toBe('token');
  });

  it('a body on a storey above the level cut is cut away like the blocks', () => {
    expect(at({ feetY: 70, cutY: 70 })).toBe('cut');
    expect(at({ feetY: 72.4, cutY: 70 })).toBe('cut');
    expect(at({ feetY: 69, cutY: 70 })).toBe('model');
    expect(at({ feetY: 69.995, cutY: 70 })).toBe('cut'); // standing on the cut level's floor (float noise)
  });

  it('the overlay anchors a model at its middle and a token at its feet', () => {
    expect(anchorLift('model')).toBeGreaterThan(anchorLift('token'));
  });
});

describe('queued builds as hologram cells', () => {
  it('maps ghosts to states by their build, drops done ones, and changes revision only with the queue', () => {
    const st = {
      builds: [
        { id: 1, kind: 'wall', label: 'wall', status: 'active', blocksTotal: 3, blocksDone: 1 },
        { id: 2, kind: 'floor', label: 'floor', status: 'blocked', blocksTotal: 1, blocksDone: 0 },
      ],
      ghosts: {
        rev: 7,
        blocks: [
          [0, 64, 0, 4, 0, 1, 'todo'],
          [1, 64, 0, 4, 0, 1, 'leased'],
          [2, 64, 0, 4, 0, 1, 'done'],
          [3, 63, 0, 5, 0, 2, 'todo'],
          [4, 63, 0, 0, 0, 1, 'unsupported'],
        ],
      },
    } as unknown as Parameters<typeof hologramCells>[0];
    const a = hologramCells(st);
    expect(a.cells.map((c) => [c.x, c.state])).toEqual([
      [0, 'todo'],
      [1, 'active'],
      [3, 'waiting'], // its build lacks materials
      [4, 'blocked'],
    ]);
    expect(a.cells[0]).toMatchObject({ y: 64, z: 0, id: 4, meta: 0, build: 1 });
    expect(hologramCells(st).rev).toBe(a.rev);
    const paused = { ...st!, builds: [{ ...st!.builds![0]!, status: 'paused' }, st!.builds![1]!] } as typeof st;
    expect(hologramCells(paused).rev).not.toBe(a.rev);
    expect(hologramCells(undefined)).toEqual({ cells: [], rev: '0|0|' });
  });
});
