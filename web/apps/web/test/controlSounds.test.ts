import { readFileSync } from 'node:fs';
import path from 'node:path';
import { describe, expect, it } from 'vitest';
import {
  blockSound,
  ECHO_MS,
  Footsteps,
  SoundEchoes,
  soundMaterial,
  STEP_DISTANCE,
  type SoundMaterial,
} from '@/features/control/controlSounds';

describe("sounds of the pawn's own actions", () => {
  it('names a material for each block as the bot side does for breaks', () => {
    const cases: Array<[number, SoundMaterial]> = [
      [1, 'stone'],
      [4, 'stone'],
      [56, 'stone'],
      [5, 'wood'],
      [17, 'wood'],
      [54, 'wood'],
      [2, 'grass'],
      [3, 'grass'],
      [18, 'grass'],
      [12, 'sand'],
      [13, 'gravel'],
      [20, 'glass'],
      [35, 'cloth'],
      [78, 'snow'],
      [80, 'snow'],
    ];
    for (const [id, material] of cases) expect(soundMaterial(id)).toBe(material);
  });

  it("builds events the Controller's player accepts: a sound, a place, a dimension and the time it happened", () => {
    expect(blockSound('hit', 1, 0, 3, 64, -2, 1_700_000_000_000)).toEqual({
      kind: 'sound',
      sound: 'block.stone.hit',
      volume: 0.25,
      pitch: 0.5,
      dim: 0,
      x: 3.5,
      y: 64.5,
      z: -1.5,
      at: 1_700_000_000_000,
    });
    expect(blockSound('break', 17, -1, 0, 70, 0, 5)).toMatchObject({
      sound: 'block.wood.break',
      volume: 1,
      pitch: 0.8,
      dim: -1,
    });
    expect(blockSound('place', 35, 0, 0, 70, 0, 5)).toMatchObject({
      sound: 'block.cloth.place',
      volume: 1,
      pitch: 0.8,
    });
    // A step sounds at the feet, not at the middle of a cell.
    expect(blockSound('step', 2, 0, 10.3, 64, 4.7, 5)).toMatchObject({
      sound: 'block.grass.step',
      volume: 0.15,
      pitch: 1,
      x: 10.3,
      y: 64,
      z: 4.7,
    });
  });

  it('only names sound events the 1.12.2 sound catalogue has', () => {
    const here = path.dirname(new URL(import.meta.url).pathname.replace(/^\/([A-Za-z]:)/, '$1'));
    const catalogue = JSON.parse(
      readFileSync(
        path.join(here, '..', '..', '..', '..', 'third_party', 'minecraft-data', 'pc', '1.12.2', 'sounds.json'),
        'utf8',
      ),
    ) as Array<{ name: string }>;
    const names = new Set(catalogue.map((s) => s.name));
    const materials = new Set([1, 5, 2, 12, 13, 20, 35, 78].map(soundMaterial));
    expect(materials.size).toBe(8);
    for (const id of [1, 5, 2, 12, 13, 20, 35, 78])
      for (const kind of ['hit', 'break', 'place', 'step'] as const)
        expect(names).toContain(blockSound(kind, id, 0, 0, 0, 0, 0).sound);
  });
});

describe('footsteps', () => {
  /** Walks `frames` frames of `stride` blocks along x and returns the frames that made a step. */
  const walk = (f: Footsteps, frames: number, stride: number, onGround = true, sneaking = false, from = 0) =>
    Array.from({ length: frames }, (_, i) => i).filter((i) => f.move(from + i * stride, 0, onGround, sneaking));

  it('steps every 1.67 blocks walked on the ground', () => {
    const f = new Footsteps();
    // 0.15 blocks a frame: the first step once 1.67 blocks are behind (frame 12, 1.8 blocks), then every 1.67 more.
    expect(walk(f, 50, 0.15)).toEqual([12, 23, 34, 45]);
    expect(STEP_DISTANCE).toBeCloseTo(1.6667, 4);
  });

  it('makes no step in the air or while sneaking, and does not count a jump to a new place as walking', () => {
    expect(walk(new Footsteps(), 60, 0.1, false)).toEqual([]);
    expect(walk(new Footsteps(), 60, 0.1, true, true)).toEqual([]);
    const f = new Footsteps();
    expect(f.move(0, 0, true, false)).toBe(false);
    expect(f.move(40, 0, true, false)).toBe(false); // a teleport or a snap
    expect(walk(f, 16, 0.1, true, false, 40)).toEqual([]);
    f.reset();
    expect(f.move(1000, 0, true, false)).toBe(false); // after a reset the first position only sets the start
  });
});

describe('SoundEchoes', () => {
  const sound = (name: string, x: number, at = 0, dim = 0) => ({
    kind: 'sound',
    sound: name,
    dim,
    x: x + 0.5,
    y: 64.5,
    z: 0.5,
    at,
  });

  it('hears a break or place at one cell once, from whichever source came first', () => {
    const echoes = new SoundEchoes();
    const own = sound('block.stone.break', 3);
    expect(echoes.own([own], 0)).toEqual([own]);
    // Another pawn's report of it (another material table, its own time) is the same break.
    expect(echoes.stream([sound('block.wood.break', 3, 500)], 400)).toEqual([]);
    // Not another cell, another dimension, another kind, or what is not a block's break or place.
    const others = [
      sound('block.stone.break', 4),
      sound('block.stone.break', 3, 0, -1),
      sound('block.stone.place', 3),
      sound('block.stone.step', 3),
      { kind: 'hurt', dim: 0, x: 3.5, y: 64.5, z: 0.5, at: 0 },
    ];
    expect(echoes.stream(others, 400)).toEqual(others);
    // The stream first: the pawn's own copy is the echo now.
    const placed = sound('block.stone.place', 4);
    expect(echoes.stream([placed], 600)).toEqual([placed]);
    expect(echoes.stream([placed], 700)).toEqual([placed]); // a frame repeated: the player drops it by its own key
    expect(echoes.own([placed, sound('block.stone.hit', 4)], 800)).toEqual([sound('block.stone.hit', 4)]);
  });

  it('forgets a cell after the echo window', () => {
    const echoes = new SoundEchoes();
    echoes.own([sound('block.stone.break', 3)], 0);
    expect(echoes.stream([sound('block.stone.break', 3)], ECHO_MS - 1)).toEqual([]);
    expect(echoes.stream([sound('block.stone.break', 3)], ECHO_MS)).toHaveLength(1);
    echoes.reset();
    expect(echoes.own([sound('block.stone.break', 3)], ECHO_MS + 1)).toHaveLength(1);
  });
});
