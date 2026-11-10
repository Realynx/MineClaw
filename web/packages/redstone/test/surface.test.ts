/**
 * The package's public surface is a contract other code is built against while the engine is being written: its names and
 * signatures change only on purpose.
 */
import { describe, expect, expectTypeOf, it } from 'vitest';
import * as redstone from '../src/index.js';
import type {
  ComponentDef,
  CreateSimulation,
  MirrorMeta,
  RotateMeta,
  Simulation,
  TransformLayout,
  VerifyComponent,
} from '../src/index.js';
import * as testing from '../src/testing.js';

describe('the package surface', () => {
  it('exports the simulator, the orientation helpers, the library and nothing else', () => {
    expect(Object.keys(redstone).sort()).toEqual([
      'GROUND_ID',
      'LIBRARY',
      'createSimulation',
      'mirrorMeta',
      'rotateMeta',
      'transformLayout',
      'verifyComponent',
    ]);
    expect(Object.keys(testing).sort()).toEqual(['FakeSimulation', 'fakeSimulations']);
  });

  it('keeps the signatures the contract gives them', () => {
    expectTypeOf(redstone.createSimulation).toEqualTypeOf<CreateSimulation>();
    expectTypeOf(redstone.rotateMeta).toEqualTypeOf<RotateMeta>();
    expectTypeOf(redstone.mirrorMeta).toEqualTypeOf<MirrorMeta>();
    expectTypeOf(redstone.transformLayout).toEqualTypeOf<TransformLayout>();
    expectTypeOf(redstone.verifyComponent).toEqualTypeOf<VerifyComponent>();
    expectTypeOf(redstone.LIBRARY).toEqualTypeOf<readonly ComponentDef[]>();
    expectTypeOf<testing.FakeSimulation>().toExtend<Simulation>();
    expect(Array.isArray(redstone.LIBRARY)).toBe(true);
  });

  it('reads undisturbed ground as its own id, apart from air', () => {
    expect(redstone.GROUND_ID).toBe(-1);
    expect([(redstone.GROUND_ID << 4) >> 4, (redstone.GROUND_ID << 4) & 15]).toEqual([-1, 0]);
  });
});
