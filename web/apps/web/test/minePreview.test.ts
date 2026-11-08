import type * as THREE from 'three';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { Canvas, EARTH, keyOf, setPlotMode, type V3 } from '@/features/studio/canvas';
import { minePreview, oreFamily, volumeOutline, type PreviewTerrain } from '@/features/studio/mine/minePreview';
import { mineStats } from '@/features/studio/mine/mineStats';
import type { MineTerrain } from '@/features/studio/mine/mineTerrain';
import { oreTable } from '@/features/studio/mine/oreTable';
import { Overlay3D } from '@/features/studio/overlay3d';
import { copy, erase, move, pasteCells } from '@/features/studio/tools';

afterEach(() => setPlotMode({ kind: 'build' }));

function terrain(cells: Array<[number, number, number, number]>, version = '1.12.2'): PreviewTerrain {
  const map = new Map(cells.map(([x, y, z, id]) => [`${x},${y},${z}`, id << 4]));
  return { version, state: (x, y, z) => map.get(`${x},${y},${z}`) ?? 16 };
}

describe('mine route preview', () => {
  it('counts ore exposed by authored cave air without counting the cave as excavation or revealing other cave ore', () => {
    const source = Object.assign(terrain([[5, 20, 5, 0], [6, 20, 5, 15], [30, 20, 30, 0], [31, 20, 30, 56]]), {
      table: oreTable('1.12.2'),
    }) as MineTerrain;
    expect(mineStats(source, []).ores.every((o) => o.exposed === 0)).toBe(true);
    const stats = mineStats(source, [[5, 20, 5]]);
    expect(stats.toMine).toBe(0);
    expect(stats.materials).toEqual([]);
    expect(stats.ores.find((o) => o.ore.id === 'iron')).toMatchObject({ mined: 0, exposed: 1 });
    expect(stats.ores.find((o) => o.ore.id === 'diamond')).toMatchObject({ mined: 0, exposed: 0 });
  });

  it('shows only authored air, never natural caves or unrelated generated ore', () => {
    const source = terrain([
      [5, 20, 5, 0],
      [6, 20, 5, 15],
      [30, 20, 30, 0],
      [31, 20, 30, 56],
    ]);
    expect(minePreview(source, [])).toEqual({ planned: [], veins: [] });
    expect(
      minePreview(source, [
        [5, 20, 5],
        [5, 20, 5],
      ]),
    ).toEqual({ planned: [[5, 20, 5]], veins: [[6, 20, 5]] });
  });

  it('follows the whole touched face-connected family without chaining to another family or diagonal ore', () => {
    const source = terrain([
      [6, 20, 5, 15],
      [7, 20, 5, 15],
      [8, 20, 5, 15],
      [8, 21, 5, 15],
      [9, 21, 5, 16],
      [9, 22, 6, 15],
      [20, 20, 20, 15],
    ]);
    const preview = minePreview(source, [
      [5, 20, 5],
      [6, 20, 5],
    ]);
    expect(preview.veins).toEqual(
      expect.arrayContaining([
        [6, 20, 5],
        [7, 20, 5],
        [8, 20, 5],
        [8, 21, 5],
      ]),
    );
    expect(preview.veins).toHaveLength(4);
  });

  it('shows detectable floor and hazardous vein segments but excludes ore replaced by authored blocks', () => {
    const source = terrain([
      [5, 19, 5, 15], // The walking floor below the authored feet cell.
      [5, 18, 5, 15], // The rest of that vein remains visible too.
      [6, 20, 5, 15],
      [7, 20, 5, 15], // A support block prevents entering this vein.
      [4, 20, 5, 15],
      [3, 20, 5, 10], // Would open lava.
      [5, 20, 6, 15],
      [5, 21, 6, 13], // Would drop gravel.
      [5, 20, 4, 15],
      [5, 20, 3, 15], // Safe branch.
    ]);
    expect(minePreview(source, [[5, 20, 5]], [[6, 20, 5, 4, 0]]).veins).toEqual(
      expect.arrayContaining([
        [5, 20, 4],
        [5, 20, 3],
        [5, 19, 5],
        [5, 18, 5],
        [4, 20, 5],
        [5, 20, 6],
      ]),
    );
    expect(minePreview(source, [[5, 20, 5]], [[6, 20, 5, 4, 0]]).veins).toHaveLength(6);
  });

  it('discovers every ore family directly exposed by the route even when the planned block is ore', () => {
    const source = terrain([
      [5, 20, 5, 15], [6, 20, 5, 16], [7, 20, 5, 16],
      [5, 20, 6, 56], [5, 20, 7, 56], [8, 20, 5, 14],
    ]);
    const preview = minePreview(source, [[5, 20, 5]]);
    expect(preview.veins).toEqual(expect.arrayContaining([[5, 20, 5], [6, 20, 5], [7, 20, 5], [5, 20, 6], [5, 20, 7]]));
    // Gold only borders the far end of the discovered coal vein, not the authored route.
    expect(preview.veins).toHaveLength(5);
  });

  it('continues through lit redstone, distinguishes version-specific ores, and follows across the plot edge', () => {
    const source = terrain([
      [63, 20, 5, 73],
      [64, 20, 5, 74],
      [65, 20, 5, 73],
    ]);
    expect(minePreview(source, [[62, 20, 5]]).veins).toHaveLength(3);
    expect(oreFamily(129, 'b1.8.1')).toBe(0);
    expect(oreFamily(129, '1.12.2')).toBe(129);
    expect(oreFamily(153, '1.12.2')).toBe(153);
  });
});

describe('authored mine air', () => {
  it('retains a complete tunnel through a seed cave and can undo/redo it without painting other cave cells', () => {
    setPlotMode({
      kind: 'mine',
      terrain: terrain([
        [5, 20, 5, 0],
        [6, 20, 5, 0],
        [8, 20, 5, 0],
      ]),
    });
    const canvas = new Canvas();
    expect(canvas.get(5, 20, 5)).toEqual([0, 0]);
    expect(canvas.authoredState(keyOf(5, 20, 5))).toBe(-1);
    const change = canvas.apply([
      [5, 20, 5, 0, 0],
      [6, 20, 5, 0, 0],
      [7, 20, 5, 0, 0],
    ]);
    expect(change.forward).toHaveLength(3);
    expect(change.backward.every((c) => c[3] === EARTH)).toBe(true);
    expect(canvas.holes()).toEqual([
      [5, 20, 5],
      [6, 20, 5],
      [7, 20, 5],
    ]);
    expect(canvas.apply(change.forward).forward).toEqual([]);
    canvas.apply(change.backward);
    expect(canvas.holes()).toEqual([]);
    expect(canvas.get(5, 20, 5)).toEqual([0, 0]);
    canvas.apply(change.forward);
    const loaded = new Canvas();
    loaded.load(canvas.cells(), canvas.holes());
    expect(loaded.holes()).toEqual(canvas.holes());
    expect(loaded.dug.has(keyOf(8, 20, 5))).toBe(false);
  });

  it('moves and copies an exact mine mask over natural air and authors the full box when carving it', () => {
    setPlotMode({ kind: 'mine', terrain: { state: () => 0 } });
    const canvas = new Canvas();
    const selection = { min: [5, 20, 5] as V3, max: [6, 21, 5] as V3 };
    canvas.apply(erase(canvas, selection, true));
    expect(canvas.holes()).toHaveLength(4);
    const masked = { ...selection, mask: new Set([keyOf(5, 20, 5), keyOf(6, 21, 5)]) };
    const clip = copy(canvas, masked)!;
    expect(clip.cells).toHaveLength(2);
    expect(pasteCells(clip, [20, 20, 20])).toHaveLength(2);
    const change = canvas.apply(move(canvas, masked, [10, 0, 0]).cells);
    expect(canvas.holes()).toEqual(expect.arrayContaining([[15, 20, 5], [16, 21, 5], [5, 21, 5], [6, 20, 5]]));
    expect(canvas.dug.has(keyOf(5, 20, 5))).toBe(false);
    expect(canvas.dug.has(keyOf(6, 21, 5))).toBe(false);
    canvas.apply(change.backward);
    expect(canvas.holes()).toHaveLength(4);
    expect(canvas.dug.has(keyOf(15, 20, 5))).toBe(false);
  });
});

describe('excavation volume wireframes', () => {
  it('keeps exterior edges while removing shared faces and coplanar seams', () => {
    expect(volumeOutline([[0, 0, 0]])).toHaveLength(12 * 6);
    // A 2×2×2 box has twelve outer edges, each two unit segments, with no internal seams.
    const cells: V3[] = [];
    for (let x = 0; x < 2; x++) for (let y = 0; y < 2; y++) for (let z = 0; z < 2; z++) cells.push([x, y, z]);
    const outline = volumeOutline(cells);
    expect(outline).toHaveLength(24 * 6);
    const edges = Array.from({ length: outline.length / 6 }, (_, i) => Array.from(outline.slice(i * 6, i * 6 + 6)));
    expect(new Set(edges.map((edge) => edge.join(','))).size).toBe(edges.length);
    expect(volumeOutline([...cells, ...cells])).toEqual(outline);
    // Two cells touching diagonally retain the shared sharp edge once.
    expect(
      volumeOutline([
        [0, 0, 0],
        [1, 1, 0],
      ]),
    ).toHaveLength(23 * 6);
  });

  it('draws through terrain, reuses buffers, clears previews and releases every GPU resource once', () => {
    setPlotMode({ kind: 'mine', terrain: terrain([]) });
    const overlay = new Overlay3D(64);
    overlay.setMinePreview({
      planned: [
        [1, 20, 1],
        [2, 20, 1],
      ],
      veins: [[3, 20, 1]],
    });
    overlay.setMinePlacement([[4, 20, 1]]);
    const planned = overlay.group.getObjectByName('mine-planned') as THREE.LineSegments<
      THREE.BufferGeometry,
      THREE.LineBasicMaterial
    >;
    const vein = overlay.group.getObjectByName('mine-veins') as typeof planned;
    const ghost = overlay.group.getObjectByName('mine-placement') as typeof planned;
    expect([planned, vein, ghost].every((o) => o.visible && !o.material.depthTest && !o.material.depthWrite)).toBe(
      true,
    );
    expect(new Set([planned, vein, ghost].map((o) => o.material.color.getHex())).size).toBe(3);
    const buffer = planned.geometry.getAttribute('position');
    overlay.setMinePreview({ planned: [[1, 20, 1]], veins: [] });
    expect(planned.geometry.getAttribute('position')).toBe(buffer);
    expect(vein.visible).toBe(false);
    overlay.setMinePreview(null);
    overlay.setMinePlacement(null);
    expect(planned.visible).toBe(false);
    expect(planned.geometry.drawRange.count).toBe(0);
    expect(ghost.visible).toBe(false);
    const resources = new Set<THREE.BufferGeometry | THREE.Material>();
    overlay.group.traverse((o) => {
      const obj = o as THREE.Mesh;
      if (obj.geometry) resources.add(obj.geometry);
      const mats = Array.isArray(obj.material) ? obj.material : [obj.material];
      for (const material of mats) if (material) resources.add(material);
    });
    const disposals = [...resources].map((r) => vi.spyOn(r, 'dispose'));
    overlay.dispose();
    expect(disposals.every((spy) => spy.mock.calls.length === 1)).toBe(true);
  });
});
