// Story Mode's bodies in the world: bots (pawns, hive bodies) keep their full model when a top-down map looks at them from far
// up, follow the storey cut like the blocks, and can drop the viewer's own name labels (the map draws its own).
import { readFileSync } from 'node:fs';
import { join } from 'node:path';
import * as THREE from 'three';
import { describe, expect, it } from 'vitest';
import type { AssetBundle, Pack1122 } from '../src/assets/bundle';
import { BOT_DETAIL_MIN, EntityLayer, type EntityShowOptions } from '../src/render/entityLayer';
import type { BotView, EntityView } from '../src/world/entities';
import type { EntityDto } from '../src/types';

// Labels draw on a canvas: a stub document is enough here (node).
if (typeof document === 'undefined') (globalThis as Record<string, unknown>).document = { createElement: () => ({ width: 0, height: 0, getContext: () => null }) };

const pack = JSON.parse(readFileSync(join(__dirname, '..', 'generated', '1.12.2', 'pack.json'), 'utf8')) as Pack1122;

function layer(): EntityLayer {
  const l = new EntityLayer();
  l.setBundle({ version: '1.12.2', pack, blocks: {}, items: {}, entity: {}, anim: null, env: {} } as unknown as AssetBundle);
  l.detailDistance = 24; // the software profile's "Auto"
  return l;
}
const bot = (name: string, x: number, y = 64): BotView => ({ name, dim: 0, x, y, z: 0, yaw: 0, dto: { name, short: name, main: false, dim: 0, x, y, z: 0, hp: 20, food: 20 } });
const zombie = (id: number, x: number, y = 64): EntityView => {
  const dto = { kind: 'mob', dim: 0, x, y, z: 0, id, type: 54 } as EntityDto;
  return { key: `0:${id}`, id, dim: 0, kind: 'mob', name: null, type: 54, item: 0, count: 1, x, y, z: 0, yaw: 0, ageTicks: 0, flags: 0, dto };
};
/** Two frames (the first poses), then the view. */
function show(l: EntityLayer, bots: BotView[], ents: EntityView[], cam: THREE.Vector3, opts: Partial<EntityShowOptions> = {}): void {
  for (let f = 0; f < 2; f++) {
    l.update(bots, ents, 1000 + f * 16, new Set());
    l.showFor(0, cam, { bots: true, entities: true, labels: true, ...opts });
  }
}

describe('bodies on a top-down map', () => {
  it('a bot keeps its model out to BOT_DETAIL_MIN apparent blocks; a mob there is a box', () => {
    const l = layer();
    // 60 blocks overhead: past the 24-block model distance, inside the bots' floor.
    show(l, [bot('maid_s1_a', 0)], [zombie(7, 1)], new THREE.Vector3(0, 124, 0));
    expect(l.detailOf('maid_s1_a')).toBe(true);
    expect(l.detailOf('0:7')).toBe(false);
    expect(l.botModels).toBe(1);
    show(l, [bot('maid_s1_a', 0)], [], new THREE.Vector3(0, 64 + BOT_DETAIL_MIN + 20, 0));
    expect(l.detailOf('maid_s1_a')).toBe(false);
    expect(l.botModels).toBe(0);
  });

  it('a detailed-model distance of 0 still means boxes for everyone', () => {
    const l = layer();
    l.detailDistance = 0;
    show(l, [bot('maid_s1_a', 0)], [], new THREE.Vector3(0, 70, 4));
    expect(l.detailOf('maid_s1_a')).toBe(false);
  });

  it('the storey cut hides bodies on the levels above it, bots and mobs alike', () => {
    const l = layer();
    const cut = { x: 0, y: 70, z: 0, r: 512, dimBelow: 66 };
    show(l, [bot('low', 0, 65), bot('roof', 2, 70)], [zombie(1, 1, 71), zombie(2, 3, 64)], new THREE.Vector3(0, 120, 0), { cut });
    expect(l.detailOf('low')).toBe(true);
    expect(l.detailOf('roof')).toBeNull();
    expect(l.detailOf('0:1')).toBeNull();
    expect(l.detailOf('0:2')).not.toBeNull();
    expect(l.instances).toBe(2);
  });

  it('bot name labels can be left to the app', () => {
    const l = layer();
    const labels = () => {
      let n = 0;
      l.group.traverse((o) => {
        if ((o as THREE.Sprite).isSprite && o.visible && o.parent?.visible !== false) n++;
      });
      return n;
    };
    show(l, [bot('maid_s1_a', 0)], [], new THREE.Vector3(0, 70, 4));
    expect(labels()).toBe(1);
    show(l, [bot('maid_s1_a', 0)], [], new THREE.Vector3(0, 70, 4), { botLabels: false });
    expect(labels()).toBe(0);
  });
});
