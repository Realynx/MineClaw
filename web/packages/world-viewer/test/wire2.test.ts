import { describe, expect, it } from 'vitest';
import { dyeIndex, itemRef, normalizeBlockEntities, normalizeEntities } from '../src/wire/normalize';
import { FLAG_ON_FIRE, FLAG_RIDING, FLAG_SNEAKING } from '../src/types';
import { BlockEntityStore } from '../src/world/blockEntities';
import { InlineMeshHost } from '../src/mesh/host';
import { createFixtureSource, plaza } from '../src/fixtures/world';
import { EntityStore } from '../src/world/entities';

describe('entity wire additions (tolerant decoder)', () => {
  it('folds the flags object into the bitfield, decodes equipment aliases, head yaw, baby and kind fields', () => {
    const d = normalizeEntities({
      rev: 4,
      full: true,
      bots: [],
      entities: [
        { kind: 'mob', dim: 0, x: 1, y: 2, z: 3, id: 7, type: 54, head_yaw: 30, baby: true, vehicle: 12, flags: 0x01 | 0x02 | 0x04, equipment: { main: [267, 0], head: [306, 3] } },
        { kind: 'mob', dim: 0, x: 1, y: 2, z: 3, id: 13, type: 54, flags: { on_fire: true, sneaking: true, vehicle: true }, equipment: { mainhand: [267, 0], helmet: { id: 306, damage: 3 }, boots: null } },
        { kind: 'item_frame', dim: 0, x: 0, y: 0, z: 0, id: 8, item: 276, damage: 0, rotation: 3, facing: 'north' },
        { kind: 'painting', dim: 0, x: 0, y: 0, z: 0, id: 14, name: 'Kebab', facing: 'west' },
        { kind: 'armor_stand', dim: 0, x: 0, y: 0, z: 0, id: 15, look: 'small,arms', pose: { head: [10, 0, 0], left_arm: [-10, 0, -10] } },
        { kind: 'falling_block', dim: 0, x: 0, y: 0, z: 0, id: 9, block: [12, 0] },
        { kind: 'minecart', dim: 0, x: 0, y: 0, z: 0, id: 10, look: 'chest' },
        { kind: 'mob', dim: 0, x: 0, y: 0, z: 0, id: 11, type: 91, variant: 2, look: 'red,sheared', flags: 3 },
      ],
      events: [{ id: 7, kind: 'swing', at: 1000 }, { kind: 'lightning', at: 1001, x: 5, y: 70, z: 6 }],
    });
    const [zombie, legacy, frame, painting, stand, falling, cart, sheep] = d.entities;
    expect(zombie.flags).toBe(FLAG_ON_FIRE | FLAG_SNEAKING | FLAG_RIDING);
    expect(zombie.headYaw).toBe(30);
    expect(zombie.baby).toBe(true);
    expect(zombie.vehicle).toBe(12);
    expect(zombie.equipment).toEqual({ main: [267, 0], off: null, head: [306, 3], chest: null, legs: null, feet: null });
    // The older shapes still decode: a flags object, slot aliases, item objects.
    expect(legacy.flags).toBe(FLAG_ON_FIRE | FLAG_SNEAKING | FLAG_RIDING);
    expect(legacy.equipment?.head).toEqual([306, 3]);
    expect(painting.motive).toBe('Kebab');
    expect(painting.facing).toBe('west');
    expect(stand.look).toBe('small,arms');
    expect(stand.pose).toEqual({ head: [10, 0, 0], leftArm: [-10, 0, -10] });
    expect(frame.frameItem).toEqual([276, 0]);
    expect(frame.item).toBeNull();
    expect(frame.facing).toBe('north');
    expect(falling.block).toEqual([12, 0]);
    expect(cart.minecartType).toBe('chest');
    expect(sheep.variant).toBe(2);
    expect(sheep.look).toBe('red,sheared');
    expect(sheep.flags).toBe(3);
    expect(d.events).toHaveLength(2);
    expect(d.events![1]).toMatchObject({ kind: 'lightning', x: 5, y: 70, z: 6, id: null });
  });

  it('keeps events until drained', () => {
    const s = new EntityStore(() => 0);
    s.apply(normalizeEntities({ rev: 1, full: true, bots: [], entities: [], events: [{ id: 1, kind: 'hurt', at: 5 }] }));
    s.apply(normalizeEntities({ rev: 2, full: false, bots: [], entities: [], events: [{ id: 1, kind: 'death', at: 6 }] }));
    expect(s.drainEvents().map((e) => e.kind)).toEqual(['hurt', 'death']);
    expect(s.drainEvents()).toEqual([]);
  });

  it('item refs and dye colours accept every spelling', () => {
    expect(itemRef(5)).toEqual([5, 0]);
    expect(itemRef([35, 14])).toEqual([35, 14]);
    expect(itemRef({ id: 35, meta: 2 })).toEqual([35, 2]);
    expect(itemRef(null)).toBeNull();
    expect(dyeIndex('light_blue')).toBe(3);
    expect(dyeIndex('lightBlue')).toBe(3);
    expect(dyeIndex('light_gray')).toBe(8);
    expect(dyeIndex(14)).toBe(14);
    expect(dyeIndex('nope')).toBe(-1);
  });

  it('block entities: sign lines as an array or text1..4, patterns as pairs or objects', () => {
    const r = normalizeBlockEntities({
      columns: [
        { cx: 0, cz: 1, hash: '-12345678901234567', entities: [
          { x: 1, y: 64, z: 17, kind: 'minecraft:sign', text1: 'a', text2: '', text3: 'c', text4: '' },
          { x: 2, y: 64, z: 17, kind: 'banner', color: 'white', patterns: [{ pattern: 'cr', color: 14 }, ['bo', 'black']] },
          { x: 3, y: 64, z: 17, kind: 'skull', skull: 4, rotation: 8, owner: 'Notch' },
        ] },
      ],
    });
    const [sign, banner, skull] = r.columns[0].entities;
    expect(sign.kind).toBe('sign');
    expect(sign.lines).toEqual(['a', '', 'c', '']);
    expect(banner.patterns).toEqual([['cr', 14], ['bo', 'black']]);
    expect(banner.base).toBe('white');
    expect(skull.type).toBe(4);
    expect(skull.owner).toBe('Notch');
  });
});

describe('block entity store', () => {
  it('fetches the columns that arrived, hands them to the worker and re-meshes their sections', async () => {
    const src = createFixtureSource({ version: '1.12.2', radius: 3, now: () => 0 });
    const host = new InlineMeshHost();
    await host.configure('1.12.2', null, false);
    const p = plaza('1.12.2').filter((b) => b.be);
    const cols = [...new Set(p.map((b) => `${b.x >> 4},${b.z >> 4}`))].map((k) => k.split(',').map(Number) as [number, number]);
    await host.apply(await src.chunks(0, cols));
    const dirty: Array<[number, number, number]> = [];
    const store = new BlockEntityStore(src, host, (_d, list) => dirty.push(...list));
    store.want(0, cols);
    store.tick(1000);
    await new Promise((r) => setTimeout(r, 20));
    expect(store.revision).toBe(1);
    const all = [...store.all(0)];
    expect(all.find((b) => b.kind === 'sign')?.lines?.[0]).toBe('MineClaw');
    const bed = p.find((b) => b.id === 26)!;
    expect(host.engine.store.blockEntityAt(0, bed.x, bed.y, bed.z)?.color).toBe(14);
    expect(dirty.length).toBeGreaterThan(0);
    // Same hash again: nothing changes.
    store.want(0, cols);
    store.tick(2000);
    await new Promise((r) => setTimeout(r, 20));
    expect(store.revision).toBe(1);
  });
});
