import { afterEach, beforeEach, describe, expect, it } from 'vitest';
import { plotMode, setPlotMode } from '@/features/studio/canvas';
import { minePlacementPoint, mineSurfacePoint, modulePlacementHeight, surfaceModuleAt } from '@/features/studio/mine/placementHeight';
import { defaultModule, moduleCells, modulePorts } from '@/features/studio/mine/modules';
import { MineTerrain } from '@/features/studio/mine/mineTerrain';

beforeEach(() => setPlotMode({ kind: 'mine', terrain: MineTerrain.for('12345', '1.12.2') }));
afterEach(() => setPlotMode({ kind: 'build' }));

describe('mine placement height', () => {
  it('starts unconnected access pieces at the surface and extraction at the preferred depth', () => {
    for (const kind of ['stairs', 'spiral'] as const) expect(modulePlacementHeight(defaultModule(kind), 'auto', 11)).toMatchObject({ surface: true });
    for (const kind of ['tunnel', 'branches', 'chamber', 'junction'] as const) expect(modulePlacementHeight(defaultModule(kind), 'auto', 16)).toMatchObject({ surface: false, level: 16 });
  });

  it('continues from the selected descent lower exit while explicit surface or height choices win', () => {
    const selected = { id: 'shaft', at: [30, 70, 30] as [number, number, number], spec: { ...defaultModule('spiral'), depth: 12 } };
    const exit = Math.min(...modulePorts(selected.spec, selected.at).map((port) => port.at[1]));
    expect(modulePlacementHeight(defaultModule('tunnel'), 'auto', 11, selected)).toEqual({ mode: 'auto', surface: false, level: exit });
    expect(modulePlacementHeight(defaultModule('stairs'), 'fixed', 27, selected)).toEqual({ mode: 'fixed', surface: false, level: 27 });
    expect(modulePlacementHeight(defaultModule('tunnel'), 'surface', 11, selected).surface).toBe(true);
  });

  it('uses the component height when a combined closed loop has no open connection ports', () => {
    const tunnel = { ...defaultModule('tunnel'), length: 7, width: 2 };
    const spec = { ...defaultModule('custom'), parts: [
      { spec: { ...tunnel, rotation: 2 }, at: [0, 0, -3] as [number, number, number] },
      { spec: tunnel, at: [0, 0, 3] as [number, number, number] },
      { spec: { ...tunnel, rotation: 1 }, at: [-3, 0, 0] as [number, number, number] },
      { spec: { ...tunnel, rotation: 3 }, at: [3, 0, 0] as [number, number, number] },
    ] };
    const selected = { id: 'loop', spec, at: [30, 25, 30] as [number, number, number] };
    expect(modulePorts(spec, selected.at)).toEqual([]);
    expect(modulePlacementHeight(tunnel, 'auto', 11, selected)).toEqual({ mode: 'auto', surface: false, level: 25 });
  });

  it('hits uneven surface columns, including hill sides, while rejecting rays pointing away or outside the plot', () => {
    const terrain = { surface: (x: number) => x < 20 ? 60 : 75 };
    expect(mineSurfacePoint({ origin: { x: 10.5, y: 100, z: 20.5 }, direction: { x: 0, y: -1, z: 0 } }, terrain)).toEqual([10, 61, 20]);
    expect(mineSurfacePoint({ origin: { x: 19.5, y: 70, z: 20.5 }, direction: { x: 1, y: 0, z: 0 } }, terrain)).toEqual([20, 76, 20]);
    expect(mineSurfacePoint({ origin: { x: 10.5, y: 100, z: 20.5 }, direction: { x: 0, y: 1, z: 0 } }, terrain)).toBeNull();
    expect(mineSurfacePoint({ origin: { x: plotMode().size + 1, y: 100, z: 20.5 }, direction: { x: 0, y: -1, z: 0 } }, terrain)).toBeNull();
  });

  it('rests each rotated access entrance on its actual generated surface instead of the footprint centre', () => {
    const terrain = MineTerrain.for('12345', '1.12.2');
    expect(terrain.surfaceRange().max).toBeGreaterThan(terrain.surfaceRange().min);
    let sawUnevenEntrance = false;
    for (const coordinate of [12, 24, 36, 48]) for (const kind of ['stairs', 'spiral'] as const) for (const rotation of [0, 1, 2, 3]) {
      const spec = { ...defaultModule(kind), rotation, depth: 12 };
      const at = surfaceModuleAt(spec, [coordinate, 11, coordinate], terrain);
      const entry = moduleCells(spec, at).entry;
      expect(entry[1]).toBe(terrain.surface(entry[0], entry[2]) + 1);
      if (terrain.surface(entry[0], entry[2]) !== terrain.surface(at[0], at[2])) sawUnevenEntrance = true;
      expect(moduleCells(spec, at).path[0]).toEqual(entry);
      expect(Math.min(...moduleCells(spec, at).path.map((p) => p[1]))).toBeLessThan(entry[1]);
    }
    expect(sawUnevenEntrance).toBe(true);
  });

  it('projects fixed placement independently of terrain and surface placement onto the actual entrance height', () => {
    const terrain = MineTerrain.for('12345', '1.12.2');
    const spec = defaultModule('spiral');
    const ray = { origin: { x: 30.5, y: 110, z: 30.5 }, direction: { x: 0, y: -1, z: 0 } };
    expect(minePlacementPoint(ray, terrain, spec, { mode: 'fixed', surface: false, level: 16 })).toEqual([30, 16, 30]);
    const at = minePlacementPoint(ray, terrain, spec, { mode: 'auto', surface: true, level: 11 })!;
    const entry = moduleCells(spec, at).entry;
    expect(entry[1]).toBe(terrain.surface(entry[0], entry[2]) + 1);
    expect(at[1]).toBeGreaterThan(50);
  });

  it('uses the expanded mine bounds for surface and fixed placement without scanning the whole plot', () => {
    expect(plotMode().size).toBe(256);
    const terrain = { surface: () => 70 };
    const ray = { origin: { x: 200.5, y: 110, z: 200.5 }, direction: { x: 0, y: -1, z: 0 } };
    expect(mineSurfacePoint(ray, terrain)).toEqual([200, 71, 200]);
    expect(minePlacementPoint(ray, terrain, defaultModule('tunnel'), { mode: 'fixed', surface: false, level: 20 })).toEqual([200, 20, 200]);
  });
});
