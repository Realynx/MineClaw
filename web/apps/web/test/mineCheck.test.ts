/**
 * MineModuleController.check, which a placement preview and a component drag call at every pointer step: it gives the
 * answer `add` and `move` would, without working out the whole component again at each step.
 */
import { MOCK_DESIGNER_PALETTE } from '@mineclaw/contracts';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { inside, plotMode, setPlotMode, type V3 } from '@/features/studio/canvas';
import { MineModuleController } from '@/features/studio/mine/MineModuleController';
import * as modules from '@/features/studio/mine/modules';
import { Studio, type StudioApi } from '@/features/studio/studio';

vi.mock('@/features/studio/mine/modules', async (original) => {
  const m = await original<typeof modules>();
  return { ...m, moduleCells: vi.fn(m.moduleCells) };
});

const api: StudioApi = {
  canvas: () => Promise.resolve({ rev: 1, applied: 1 }),
  message: () => Promise.reject(new Error('unused')),
  save: () => Promise.reject(new Error('unused')),
};
const studio = () =>
  new Studio(
    {
      id: 9,
      owner: 'test',
      model: 'main',
      designId: null,
      name: 'mine',
      rev: 0,
      blocks: [],
      anchor: null,
      messages: [],
      costUsd: 0,
      calls: 0,
      version: '1.12.2',
      palette: MOCK_DESIGNER_PALETTE,
      plot: { size: 64, height: 128, depth: 0 },
      revisions: [],
      kind: 'mine',
      seed: '12345',
    },
    api,
    60_000,
  );

afterEach(() => setPlotMode({ kind: 'build' }));

describe('checking where a component may stand', () => {
  it('answers as add would at every step, and works the component out once for all of them', () => {
    const s = studio();
    const c = new MineModuleController(s, null);
    const spec = { ...modules.defaultModule('branches'), length: 48, branchLength: 12, spacing: 3 };
    const cells = vi.mocked(modules.moduleCells);
    // The rule as add applies it, cell by cell at the real position.
    const expected = (at: V3): string | null => {
      const placed = cells.getMockImplementation()!(spec, at).cells;
      if (placed.some(([x, y, z]) => !inside(x, y, z) || y < 1)) return 'off';
      if (placed.some(([x, y, z]) => s.terrain!.state(x, y, z) >> 4 === 7)) return 'bedrock';
      return null;
    };
    const kind = (reason: string | null) =>
      reason === null ? null : reason.includes('bedrock') ? 'bedrock' : reason.includes('plot') ? 'off' : reason;
    const steps: V3[] = [];
    for (let x = 0; x < plotMode().size; x += 3) steps.push([x, 20, 32]);
    for (let y = -2; y <= 8; y++) steps.push([32, y, 32]);
    cells.mockClear();
    const answers = steps.map((at) => kind(c.check(spec, at)));
    expect(cells).toHaveBeenCalledTimes(1);
    expect(answers).toEqual(steps.map(expected));
    // Every kind of answer was met on the way.
    expect(new Set(answers)).toEqual(new Set([null, 'off', 'bedrock']));
    expect(c.check(spec, [32.5, 20, 32])).toBe('Place layouts on whole block coordinates.');
    c.dispose();
    s.dispose();
  });
});
