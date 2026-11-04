// Behaviour pin for the wire normalizers (docs/dev/complexity.md, WP12): seeded payloads over every field the entity,
// block-entity, overlay and index readers branch on (snake_case and camelCase, numbers as strings, odd shapes), both
// versions' kinds, hashed with their key order and undefined values. Generated from the code before it was reshaped.

import { describe, expect, it } from 'vitest';
import { dyeIndex, equipGlint, equipRef, itemRef, normalizeBlockEntities, normalizeEntities, normalizeIndex, normalizeOverlays } from '../src/wire/normalize';
import { Digest, rng } from './digest';

/** JSON with undefined kept and key order as built. */
function show(v: unknown): string {
  return JSON.stringify(v, (_k, x) => (x === undefined ? '~undef' : typeof x === 'bigint' ? `${x}n` : x));
}

const r = rng(2025);
const pick = <T,>(xs: readonly T[]): T => xs[Math.floor(r() * xs.length)];
const maybe = (p = 0.5) => r() < p;

const ITEMS: unknown[] = [undefined, null, false, 0, 5, -1, '7', [276, 0], [35, 14, 0xff0000], [35, 3, { glint: true }], [298, 0, 0xa06540, { glint: true }], [], [0, 3], { id: 1, meta: 2 }, { item: 3, damage: '4' }, { id: 0 }, { id: 310, color: 123456 }, { id: 311, rgb: 0x123456, glint: true }, 'stone'];
const KINDS = [undefined, 'mob', 'player', 'item', 'painting', 'item_frame', 'falling_block', 'minecart', 'xp_orb', 'arrow', 'boat'];

function equipment(): unknown {
  if (maybe(0.15)) return pick([null, 'x', 7]);
  const o: Record<string, unknown> = {};
  for (const n of ['main', 'mainhand', 'mainHand', 'hand', 'off', 'offhand', 'offHand', 'head', 'helmet', 'chest', 'chestplate', 'legs', 'leggings', 'feet', 'boots']) if (maybe(0.2)) o[maybe(0.3) ? n.replace(/([A-Z])/g, '_$1').toLowerCase() : n] = pick(ITEMS);
  return o;
}

function flags(): unknown {
  if (maybe(0.4)) return Math.floor(r() * 256);
  const o: Record<string, unknown> = {};
  for (const n of ['onFire', 'on_fire', 'burning', 'fire', 'sneaking', 'crouching', 'riding', 'vehicle', 'sprinting', 'usingItem', 'using_item', 'invisible', 'glowing']) if (maybe(0.25)) o[n] = pick([true, false, 1, 0, 'yes']);
  return maybe(0.1) ? null : o;
}

function entity(): Record<string, unknown> {
  const e: Record<string, unknown> = {};
  const kind = pick(KINDS);
  if (kind !== undefined) e.kind = kind;
  const num = () => pick([Math.round(r() * 2000 - 1000) / 8, `${Math.round(r() * 100)}`, '', 'x', null, undefined, NaN]);
  for (const k of ['id', 'dim', 'x', 'y', 'z', 'yaw', 'pitch']) if (maybe(0.85)) e[k] = num();
  const set = (camel: string, v: unknown) => {
    if (v === undefined) return;
    const snake = camel.replace(/([A-Z])/g, '_$1').toLowerCase();
    e[maybe(0.3) ? snake : camel] = v;
  };
  if (maybe(0.4)) set('flags', flags());
  if (maybe(0.4)) set('equipment', equipment());
  if (maybe(0.3)) set('headYaw', num());
  if (maybe(0.3)) set('baby', pick([true, false, 0, 1, null]));
  if (maybe(0.3)) set('variant', pick(['red', 'cat:2', 3, null]));
  if (maybe(0.3)) set('look', pick(['brown', 'tamed,collar=14', 5, null, 'chest,tnt']));
  if (maybe(0.3)) set('vehicle', pick([true, false, { id: 3 }, null]));
  if (maybe(0.3)) set('motive', pick(['Kebab', null, 4]));
  if (maybe(0.3)) set('name', pick(['Alice', 'Bob', null]));
  if (maybe(0.4)) set('item', pick(ITEMS));
  if (maybe(0.3)) set('damage', num());
  if (maybe(0.3)) set('rotation', pick([null, 3, '5', 'x']));
  if (maybe(0.3)) set('block', pick(ITEMS));
  if (maybe(0.3)) set('carried', pick(ITEMS));
  if (maybe(0.3)) set('glint', pick([true, false, 1, null]));
  if (maybe(0.3)) set('arrows', pick([3, -2, 2.7, '4', null, 'x']));
  if (maybe(0.3)) set('shoulders', pick([{ left: 'parrot:1', right: null }, { left: 3, right: 'parrot' }, null, 'p', {}]));
  if (maybe(0.3)) set('minecartType', pick(['chest', 2, null]));
  if (maybe(0.3)) set('type', pick(['furnace', 90, null, 'pig']));
  if (maybe(0.3)) set('pose', pick([{ head: [1, 2, 3], body: [1, '2', 'x'], leftArm: [1, 2], right_leg: 'n' }, null, [1, 2, 3], {}]));
  if (maybe(0.2)) set('count', 3);
  return e;
}

function bot(): Record<string, unknown> {
  const b: Record<string, unknown> = {};
  if (maybe(0.9)) b.name = pick(['Alice', 'Bob', 'Cy']);
  if (maybe(0.5)) b.short = pick(['main', '67']);
  if (maybe(0.5)) b.main = pick([true, false, 1, null]);
  for (const k of ['dim', 'x', 'y', 'z', 'hp', 'food']) if (maybe(0.8)) b[k] = pick([3, '4', null, 'x', 19.5]);
  if (maybe(0.4)) b.equipment = equipment();
  if (maybe(0.3)) b[maybe(0.5) ? 'pose_age' : 'poseAge'] = 3;
  return b;
}

function blockEntity(): Record<string, unknown> {
  const b: Record<string, unknown> = {};
  for (const k of ['x', 'y', 'z']) if (maybe(0.9)) b[k] = pick([3, '-4', null, 'x']);
  if (maybe(0.9)) b.kind = pick(['sign', 'minecraft:sign', 'banner', 'minecraft:banner', 'skull', 'mob_spawner', 'bed', 'flower_pot', null]);
  if (maybe(0.4)) b.lines = pick([['a', null, 3], [], ['', '', '', '', 'e'], 'x']);
  for (const k of ['text1', 'text2', 'text3', 'text4']) if (maybe(0.3)) b[k] = pick(['hi', null, 4, '']);
  if (maybe(0.4)) b.patterns = pick([[['bs', 3], { pattern: 'cr', color: 'red' }, { color: 2 }], [], 'x', null]);
  if (maybe(0.3)) b.rich = pick([[[{ text: 'a', color: 'red' }, null, 'x', { bold: true }], 'y', [{ text: 3 }], [], [], []], null, 'r']);
  if (maybe(0.3)) b.type = pick([3, 'wither', null]);
  if (maybe(0.3)) b.skull = pick([1, 'creeper']);
  if (maybe(0.3)) b.base = pick([4, 'blue', null]);
  if (maybe(0.3)) b.color = pick([14, 'lightBlue']);
  if (maybe(0.3)) b.mob = pick(['zombie', null]);
  if (maybe(0.2)) b.rotation = 7;
  return b;
}

describe('wire golden', () => {
  it('pins the normalizers over seeded payloads', () => {
    const d = new Digest();
    for (let i = 0; i < 600; i++) {
      const payload: Record<string, unknown> = {};
      if (maybe(0.7)) payload.rev = pick([3, '9', null]);
      if (maybe(0.3)) payload.frame = pick([4, '5']);
      if (maybe(0.5)) payload.full = pick([true, false]);
      if (maybe(0.5)) payload.at = 1000 + i;
      if (maybe(0.9)) payload.bots = Array.from({ length: Math.floor(r() * 3) }, bot);
      if (maybe(0.9)) payload.entities = Array.from({ length: Math.floor(r() * 6) }, entity);
      if (maybe(0.4)) payload.removed = [[1, 2], [3], 'x', ['4', '5', 6]];
      if (maybe(0.4)) payload.events = [{ kind: 'lightning', at: '5', x: 1, y: null }, null, 'e', { id: 4, kind: 3, z: '2' }, {}];
      d.add(show(normalizeEntities(payload)));
    }
    for (let i = 0; i < 300; i++) {
      const cols = Array.from({ length: Math.floor(r() * 3) }, () => ({ cx: pick([1, '2', null]), cz: -3, hash: pick([5, undefined]), entities: Array.from({ length: Math.floor(r() * 4) }, blockEntity) }));
      d.add(show(normalizeBlockEntities(maybe(0.1) ? null : { dim: pick([0, -1, undefined]), columns: maybe(0.9) ? cols : undefined })));
    }
    for (const v of ITEMS) d.add(show([itemRef(v), equipRef(v), equipGlint(v)]));
    for (const v of [0, 15, 16, -1, 3.7, 'white', 'minecraft:light_blue', 'lightBlue', 'light_gray', 'silver', 'LIGHT_BLUE', 'nope', null, true]) d.add(show(dyeIndex(v)));
    for (let i = 0; i < 40; i++) {
      const mark = () => ({ kind: pick(['claim', undefined]), text: pick(['t', null, undefined]), min: pick([[1, '2', 3], undefined]), max: pick([[4, 5, 6], undefined]), owner: pick(['A', undefined]) });
      d.add(show(normalizeOverlays(maybe(0.1) ? null : { at: 3, dim: pick(['-1', 0]), claims: [mark(), mark()], places: [mark()], points: maybe() ? [mark()] : undefined, plans: [{ id: 3, name: 'p', blocks: [[1, 2, 3, 4, 0]] }, {}], dangers: [mark()], notes: ['n', { a: 1 }, 3] })));
      d.add(show(normalizeIndex(maybe(0.1) ? null : { dim: '0', at: 4, columns: [{ cx: '1', cz: 2, hash: '99', seen: 5, by: 'A', live: 1 }, {}], version: pick(['1.12.2', undefined]), time: pick([null, -6000, '400', 'x', undefined]), time_frozen: pick([true, undefined]), rain: pick([0.5, undefined]), thunder: pick([null, 1]) })));
    }
    expect(d.hex()).toBe('ab78fdf2028f0962acd6addde36d5055');
  });
});
