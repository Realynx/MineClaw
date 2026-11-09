import { expect, it } from 'vitest';
import { WorldChestState, WorldEntities } from '../src/index.js';

it('carries a complete open-chest snapshot on full and incremental entity frames while accepting legacy servers', () => {
  const chest = { dim: -1, x: 229, y: 64, z: 381, openCount: 2 };
  const frame = { at: 1000, rev: 1, bots: [], entities: [] };
  expect(WorldEntities.parse(frame).chests).toBeUndefined();
  for (const full of [true, false]) expect(WorldEntities.parse({ ...frame, full, chests: [chest] }).chests).toEqual([chest]);
  expect(WorldEntities.parse({ ...frame, chests: [] }).chests).toEqual([]);
  expect(WorldChestState.safeParse({ ...chest, openCount: 0 }).success).toBe(false);
  expect(WorldChestState.safeParse({ ...chest, x: 229.5 }).success).toBe(false);
});
