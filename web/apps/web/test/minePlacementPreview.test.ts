import { afterEach, describe, expect, it, vi } from 'vitest';
import type * as THREE from 'three';
import { setPlotMode } from '@/features/studio/canvas';
import * as modules from '@/features/studio/mine/modules';
import * as outlines from '@/features/studio/mine/minePreview';
import { MAX_PLACEMENT_OUTLINE_CELLS, modulePlacementPreview } from '@/features/studio/mine/placementPreview';
import { Overlay3D } from '@/features/studio/overlay3d';

afterEach(() => { vi.restoreAllMocks(); setPlotMode({ kind: 'build' }); });

describe('cached mine placement previews', () => {
  it('builds a small outline once per configuration and reuses it while positions change', () => {
    const geometry = vi.spyOn(modules, 'moduleCells'), outline = vi.spyOn(outlines, 'volumeOutline');
    const spec = { ...modules.defaultModule('tunnel'), length: 24, rotation: 1 };
    const prepared = modulePlacementPreview(spec);
    expect(prepared.simplified).toBe(false);
    expect(prepared.cells).toBe(48);
    expect(prepared.bounds).toEqual({ min: [0, 0, -11], max: [0, 1, 12] });
    for (let i = 0; i < 100; i++) expect(modulePlacementPreview(spec)).toBe(prepared);
    expect(geometry).toHaveBeenCalledTimes(1);
    expect(outline).toHaveBeenCalledTimes(1);
    expect(modulePlacementPreview({ ...spec, length: 25 })).not.toBe(prepared);
    expect(geometry).toHaveBeenCalledTimes(2);
  });

  it('uses twelve bounds edges for large pieces without running the voxel outline builder', () => {
    const geometry = vi.spyOn(modules, 'moduleCells'), outline = vi.spyOn(outlines, 'volumeOutline');
    const spec = { ...modules.defaultModule('chamber'), length: 192, width: 96, height: 6 };
    const prepared = modulePlacementPreview(spec);
    expect(prepared.cells).toBe(110_592);
    expect(prepared.cells).toBeGreaterThan(MAX_PLACEMENT_OUTLINE_CELLS);
    expect(prepared.simplified).toBe(true);
    expect(prepared.positions).toHaveLength(12 * 6);
    for (let i = 0; i < 100; i++) expect(modulePlacementPreview(spec).positions).toBe(prepared.positions);
    expect(geometry).toHaveBeenCalledTimes(1);
    expect(outline).not.toHaveBeenCalled();
  });

  it('does not retraverse a custom group when translating its cached connection ports', () => {
    const parts = vi.fn(() => [{ spec: modules.defaultModule('tunnel'), at: [0, 0, 0] as [number, number, number] }, { spec: modules.defaultModule('chamber'), at: [12, 0, 0] as [number, number, number] }]);
    const spec = { ...modules.defaultModule('custom'), get parts() { return parts(); } };
    const local = modules.moduleLocalPorts(spec);
    expect(local.length).toBeGreaterThan(0);
    const reads = parts.mock.calls.length;
    for (let i = 0; i < 100; i++) {
      const translated = modules.modulePorts(spec, [i, 20, i]);
      expect(translated[0]!.at).toEqual([local[0]!.at[0] + i, local[0]!.at[1] + 20, local[0]!.at[2] + i]);
    }
    expect(parts).toHaveBeenCalledTimes(reads);
    expect(modules.moduleLocalPorts(spec)).toBe(local);
  });

  it('moves a cached outline without rewriting or reallocating its GPU buffer', () => {
    setPlotMode({ kind: 'mine', terrain: { state: () => 16 } });
    const overlay = new Overlay3D(256);
    const prepared = modulePlacementPreview(modules.defaultModule('tunnel'));
    overlay.setMinePlacementGeometry(prepared.positions, [20, 30, 40]);
    const ghost = overlay.group.getObjectByName('mine-placement') as THREE.LineSegments;
    const attribute = ghost.geometry.getAttribute('position') as THREE.BufferAttribute;
    const version = attribute.version, geometry = ghost.geometry;
    for (let i = 0; i < 100; i++) overlay.setMinePlacementGeometry(prepared.positions, [i, 30, 40]);
    expect(ghost.position.toArray()).toEqual([99, 30, 40]);
    expect(ghost.geometry).toBe(geometry);
    expect(ghost.geometry.getAttribute('position')).toBe(attribute);
    expect(attribute.version).toBe(version);
    overlay.setMinePlacementGeometry(null);
    expect(ghost.visible).toBe(false);
    overlay.setMinePlacementGeometry(prepared.positions, [10, 20, 30]);
    expect(ghost.visible).toBe(true);
    expect(attribute.version).toBe(version);
    overlay.dispose();
  });
});
