import { expect, it, vi } from 'vitest';
import { InlineMeshHost } from '../src/mesh/host';
import type { SectionMesh } from '../src/mesh/mesher';
import { WorldCache } from '../src/world/cache';
import { SectionManager } from '../src/render/sections';
import { createChunkMaterials, createChunkUniforms } from '../src/render/chunkMaterial';

function rig(columns: number[]) {
  const cache = new WorldCache();
  cache.applyStored(
    0,
    columns.map((cx) => ({ cx, cz: 0, hash: '1', seen: 1, by: null, live: true, height: 16, missing: false })),
    0,
  );
  const host = new InlineMeshHost();
  const requests: Array<{ cells: Array<[number, number, number]>; complete(): void }> = [];
  vi.spyOn(host, 'mesh').mockImplementation(
    (_dim, cells) =>
      new Promise<SectionMesh[]>((resolve) => {
        requests.push({
          cells,
          complete: () =>
            resolve(cells.map(([cx, sy, cz]) => ({ cx, sy, cz, solid: null, translucent: null, quads: 0 }))),
        });
      }),
  );
  const sections = new SectionManager(host, cache, createChunkMaterials(createChunkUniforms()));
  sections.batch = 1;
  const maintain = () => sections.maintain([{ x: 8, y: 8, z: 8, distance: 64 }], 0);
  return { sections, requests, maintain };
}

it('meshes farther sections while the closest section is dirtied during every mesh request', async () => {
  const { sections, requests, maintain } = rig([0, 1, 2]);
  maintain();
  for (let n = 0; n < 4; n++) {
    await vi.waitFor(() => expect(requests.length).toBeGreaterThan(n));
    sections.markDirty([[0, 0, 0]]);
    if (n === 3) sections.hold = true;
    requests[n].complete();
  }
  await vi.waitFor(() => expect(sections.busy).toBe(false));
  expect(requests.slice(0, 4).map((r) => r.cells[0][0])).toEqual([0, 1, 0, 2]);
  expect(sections.builds.has('2,0,0')).toBe(true);
  sections.dispose();
});

it('shows a newer completed mesh while retaining the rebuild for changes that arrived in flight', async () => {
  const { sections, requests, maintain } = rig([0]);
  maintain();
  requests[0].complete();
  await vi.waitFor(() => expect(sections.busy).toBe(false));
  sections.markDirty([[0, 0, 0]]);
  maintain();
  sections.markDirty([[0, 0, 0]]);
  sections.hold = true;
  requests[1].complete();
  await vi.waitFor(() => expect(sections.busy).toBe(false));
  expect(sections.builds.get('0,0,0')).toBe(2);
  expect(sections.queued).toBe(1);
  sections.hold = false;
  maintain();
  requests[2].complete();
  await vi.waitFor(() => expect(sections.busy).toBe(false));
  expect(sections.builds.get('0,0,0')).toBe(3);
  expect(sections.queued).toBe(0);
  sections.dispose();
});
