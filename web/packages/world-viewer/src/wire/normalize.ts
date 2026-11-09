// Accepts the supervisor's snake_case replies or Nest's camelCase mirror and returns the viewer's types with defaults filled.

import { FLAG_GLOWING, FLAG_INVISIBLE, FLAG_ON_FIRE, FLAG_RIDING, FLAG_SNEAKING, FLAG_SPRINTING, FLAG_USING_ITEM, type BlockEntities, type BlockEntityColumn, type BlockEntityDto, type BotDto, type EntityDelta, type EntityDto, type EntityEvent, type EquipRef, type EquipSlot, type Equipment, type ItemRef, type SignRun, type Mark, type Overlays, type PlanDto, type WorldIndex } from '../types';

const camel = (k: string) => k.replace(/_([a-z0-9])/g, (_, c: string) => c.toUpperCase());

/** Recursively renames snake_case keys to camelCase (arrays and nested objects too). */
export function camelize<T = unknown>(v: unknown): T {
  if (Array.isArray(v)) return v.map((x) => camelize(x)) as T;
  if (v && typeof v === 'object' && Object.getPrototypeOf(v) === Object.prototype) {
    const out: Record<string, unknown> = {};
    for (const [k, x] of Object.entries(v as Record<string, unknown>)) out[camel(k)] = camelize(x);
    return out as T;
  }
  return v as T;
}

const num = (v: unknown, d = 0) => (typeof v === 'number' && Number.isFinite(v) ? v : typeof v === 'string' && v !== '' && Number.isFinite(+v) ? +v : d);

export function normalizeIndex(raw: unknown): WorldIndex {
  const r = camelize<Partial<WorldIndex>>(raw) ?? {};
  return {
    dim: num(r.dim),
    at: r.at,
    columns: (r.columns ?? []).map((c) => ({ cx: num(c.cx), cz: num(c.cz), hash: c.hash ?? 0, seen: num(c.seen), by: c.by ?? null, live: !!c.live })),
    version: r.version ?? null,
    time: r.time == null ? null : num(r.time, NaN),
    timeFrozen: r.timeFrozen === true || (typeof r.time === 'number' && r.time < 0),
    rain: r.rain ?? null,
    thunder: r.thunder ?? null,
  };
}

function bot(b: Partial<BotDto>): BotDto {
  const eq = b.equipment != null ? equipment(b.equipment) : undefined;
  return {
    ...b,
    ...(eq !== undefined ? { equipment: eq } : {}),
    name: b.name ?? '?',
    short: b.short ?? b.name ?? '?',
    main: !!b.main,
    dim: num(b.dim),
    x: num(b.x),
    y: num(b.y),
    z: num(b.z),
    hp: num(b.hp, 20),
    food: num(b.food, 20),
  };
}

export function normalizeEntities(raw: unknown): EntityDelta {
  const r = camelize<Partial<EntityDelta>>(raw) ?? {};
  return {
    ...(typeof r.frame === 'number' ? { frame: r.frame } : {}),
    at: r.at,
    rev: num(r.rev),
    full: r.full ?? true,
    ...(Array.isArray(r.chests) ? { chests: r.chests.filter((c) => c && num(c.openCount) > 0).map((c) => ({ dim: num(c.dim), x: num(c.x), y: num(c.y), z: num(c.z), openCount: num(c.openCount) })) } : {}),
    bots: (r.bots ?? []).map(bot),
    entities: (r.entities ?? []).map((e: Partial<EntityDto>) => entity(e)),
    removed: (r.removed ?? []).filter((p) => Array.isArray(p) && p.length >= 2).map((p) => [num(p[0]), num(p[1])] as [number, number]),
    events: (r.events ?? []).filter((v) => v && typeof v === 'object').map((v: Partial<EntityEvent>) => ({
      ...v,
      id: v.id ?? null,
      kind: String(v.kind ?? ''),
      at: num(v.at),
      x: v.x == null ? null : num(v.x),
      y: v.y == null ? null : num(v.y),
      z: v.z == null ? null : num(v.z),
    })),
  };
}

/** [id, meta] from [id, meta] / [id] / {id, meta|damage} / a bare id; null for empty. */
export function itemRef(v: unknown): ItemRef {
  if (v == null || v === false) return null;
  if (typeof v === 'number') return v > 0 ? [v, 0] : null;
  if (Array.isArray(v)) return v.length && num(v[0]) > 0 ? [num(v[0]), num(v[1])] : null;
  return typeof v === 'object' ? objectRef(v as Record<string, unknown>) : null;
}

/** {id, meta} / {item, damage}. */
function objectRef(o: Record<string, unknown>): ItemRef {
  const id = num(o.id ?? o.item);
  return id > 0 ? [id, num(o.meta ?? o.damage)] : null;
}

const FLAG_BITS: Array<[string[], number]> = [
  [['onFire', 'burning', 'fire'], FLAG_ON_FIRE],
  [['sneaking', 'crouching'], FLAG_SNEAKING],
  [['riding', 'vehicle'], FLAG_RIDING],
  [['sprinting'], FLAG_SPRINTING],
  [['usingItem'], FLAG_USING_ITEM],
  [['invisible'], FLAG_INVISIBLE],
  [['glowing'], FLAG_GLOWING],
];

function flags(v: unknown): number {
  if (typeof v === 'number') return v;
  if (!v || typeof v !== 'object') return 0;
  const o = v as Record<string, unknown>;
  let f = 0;
  for (const [names, bit] of FLAG_BITS) if (names.some((n) => !!o[n])) f |= bit;
  return f;
}

/** An equipment slot: [id, meta], keeping a dye colour (a third array element, or `color`/`rgb`) when there is one. A trailing
 * flags object (`[id, meta, {glint}]`, `[id, meta, rgb, {glint}]`) is not part of the ref: see `equipGlint`. */
export function equipRef(v: unknown): EquipRef {
  const r = itemRef(v);
  if (!r) return null;
  const c = Array.isArray(v) ? v[2] : v && typeof v === 'object' ? ((v as Record<string, unknown>).color ?? (v as Record<string, unknown>).rgb) : undefined;
  return typeof c === 'number' && Number.isFinite(c) ? [r[0], r[1], c & 0xffffff] : r;
}

/** Whether a wire equipment slot glints: its trailing flags object (or an object form's own `glint`) says so. */
export function equipGlint(v: unknown): boolean {
  if (Array.isArray(v)) {
    const last: unknown = v[v.length - 1];
    return !!last && typeof last === 'object' && (last as Record<string, unknown>).glint === true;
  }
  return !!v && typeof v === 'object' && (v as Record<string, unknown>).glint === true;
}

const SLOT_NAMES: Array<[EquipSlot, string[]]> = [
  ['main', ['main', 'mainhand', 'mainHand', 'hand']],
  ['off', ['off', 'offhand', 'offHand']],
  ['head', ['head', 'helmet']],
  ['chest', ['chest', 'chestplate']],
  ['legs', ['legs', 'leggings']],
  ['feet', ['feet', 'boots']],
];

function equipment(v: unknown): Equipment | null {
  if (!v || typeof v !== 'object') return null;
  const o = v as Record<string, unknown>;
  const e: Equipment = {};
  const glint: Partial<Record<EquipSlot, boolean>> = {};
  let any = false, shiny = false;
  for (const [slot, names] of SLOT_NAMES) {
    const raw = names.map((n) => o[n]).find((x) => x != null);
    const ref = equipRef(raw);
    e[slot] = ref;
    if (ref) any = true;
    if (ref && equipGlint(raw)) shiny = glint[slot] = true;
  }
  if (shiny) e.glint = glint;
  return any ? e : null;
}

type WireEntity = Partial<EntityDto> & Record<string, unknown>;

function entity(e: WireEntity): EntityDto {
  const kind = e.kind ?? 'mob';
  const out: EntityDto = { ...e, kind, dim: num(e.dim), x: num(e.x), y: num(e.y), z: num(e.z) };
  // The fields are read in this order (new keys land in it after the wire's own).
  genericFields(e, out);
  appearanceFields(e, out);
  kindFields(e, out, kind);
  extraFields(e, out);
  if (kind === 'minecart') minecartFields(e, out);
  if (e.pose && typeof e.pose === 'object') out.pose = poseOf(e.pose as Record<string, unknown>);
  return out;
}

function genericFields(e: WireEntity, out: EntityDto): void {
  if (e.flags != null) out.flags = flags(e.flags);
  if (e.equipment != null) out.equipment = equipment(e.equipment);
  if (e.headYaw != null) out.headYaw = num(e.headYaw);
  if (e.baby != null) out.baby = !!e.baby;
}

/** `look` is the appearance string; an older string `variant` is read as one. A boolean `vehicle` is the riding flag. */
function appearanceFields(e: WireEntity, out: EntityDto): void {
  if (typeof e.variant === 'string') {
    out.look = (e.look as string | undefined) ?? e.variant;
    out.variant = null;
  }
  if (e.look != null) out.look = String(e.look);
  if (typeof e.vehicle === 'boolean') {
    if (e.vehicle) out.flags = (out.flags ?? 0) | FLAG_RIDING;
    out.vehicle = null;
  }
}

/** Kind-specific fields that share names with the generic ones. */
function kindFields(e: WireEntity, out: EntityDto, kind: string): void {
  if (kind === 'painting') out.motive = (e.motive as string | undefined) ?? e.name ?? null;
  if (kind === 'item_frame') {
    out.frameItem = frameItemOf(e);
    out.item = null;
    out.rotation = e.rotation == null ? 0 : num(e.rotation);
  }
  if (kind === 'falling_block' && e.block != null) out.block = itemRef(e.block);
}

/** An item frame's item: a ref as it comes ([id, meta] / {id, meta}), or a bare id with its `damage`. */
function frameItemOf(e: WireEntity): ItemRef {
  const it = e.item;
  if (Array.isArray(it) || (it && typeof it === 'object')) return itemRef(it);
  return itemRef(it != null ? [num(it), num(e.damage)] : null);
}

function extraFields(e: WireEntity, out: EntityDto): void {
  if (e.carried != null) out.carried = itemRef(e.carried);
  if (e.glint != null) out.glint = !!e.glint;
  if (e.arrows != null) out.arrows = Math.max(0, Math.floor(num(e.arrows)));
  if (e.shoulders && typeof e.shoulders === 'object') out.shoulders = shouldersOf(e.shoulders as Record<string, unknown>);
}

function shouldersOf(s: Record<string, unknown>): NonNullable<EntityDto['shoulders']> {
  return { left: typeof s.left === 'string' ? s.left : null, right: typeof s.right === 'string' ? s.right : null };
}

/** A minecart's type: its own field, a string `type`, or the first word of its look. */
function minecartFields(e: WireEntity, out: EntityDto): void {
  const named = typeof e.type === 'string' ? e.type : null;
  const t = e.minecartType ?? named ?? (out.look ? out.look.split(',')[0] : null);
  out.minecartType = (t as string | number | null) ?? null;
  if (named !== null) out.type = null;
}

/** Part rotations: the [x, y, z] triples only. */
function poseOf(pose: Record<string, unknown>): NonNullable<EntityDto['pose']> {
  const p: NonNullable<EntityDto['pose']> = {};
  for (const [k, v] of Object.entries(pose)) if (Array.isArray(v) && v.length >= 3) p[k as keyof typeof p] = [num(v[0]), num(v[1]), num(v[2])];
  return p;
}

const DYES = ['white', 'orange', 'magenta', 'light_blue', 'yellow', 'lime', 'pink', 'gray', 'silver', 'cyan', 'purple', 'blue', 'brown', 'green', 'red', 'black'];

/** A dye colour as 0..15 from a number or a name ("light_blue", "lightBlue", "silver"/"light_gray"); -1 = unknown. */
export function dyeIndex(v: unknown): number {
  if (typeof v === 'number') return v >= 0 && v < 16 ? Math.floor(v) : -1;
  if (typeof v !== 'string') return -1;
  const n = v.replace(/^minecraft:/, '').replace(/([a-z])([A-Z])/g, '$1_$2').toLowerCase().replace('light_gray', 'silver');
  return DYES.indexOf(n);
}

const textOf = (l: unknown) => (l == null ? '' : String(l));
const TEXT_KEYS = ['text1', 'text2', 'text3', 'text4'];

/** Sign lines as `lines` or text1..text4. */
function signLines(b: Record<string, unknown>): string[] {
  return Array.isArray(b.lines) ? (b.lines as unknown[]).map(textOf) : TEXT_KEYS.map((k) => textOf(b[k]));
}

/** Banner patterns as [code, colour] pairs or {pattern, color} objects. */
function bannerPatterns(b: Record<string, unknown>): Array<[string, number | string]> | null {
  if (!Array.isArray(b.patterns)) return null;
  return (b.patterns as unknown[]).map((p) => {
    if (Array.isArray(p)) return [String(p[0]), p[1] as number | string];
    const o = p as Record<string, unknown>;
    return [String(o.pattern ?? ''), o.color as number | string];
  }) as Array<[string, number | string]>;
}

/** A styled sign's four lines of runs (objects only, text as a string). */
function signRich(b: Record<string, unknown>): SignRun[][] | null {
  if (!Array.isArray(b.rich)) return null;
  return (b.rich as unknown[]).slice(0, 4).map((l) =>
    Array.isArray(l) ? (l as unknown[]).filter((r) => !!r && typeof r === 'object').map((r) => ({ ...(r as SignRun), text: String((r as SignRun).text ?? '') })) : [],
  );
}

function blockEntity(b: Record<string, unknown>): BlockEntityDto {
  const lines = signLines(b);
  const patterns = bannerPatterns(b);
  const kind = String(b.kind ?? '').replace(/^minecraft:/, '');
  const rich = signRich(b);
  return {
    ...b,
    ...(rich ? { rich } : {}),
    x: num(b.x),
    y: num(b.y),
    z: num(b.z),
    kind,
    lines: lines.some((l) => l) ? lines.slice(0, 4) : null,
    patterns,
    // The wire names the skull type `skull` and a banner's base colour `color`.
    type: (b.type as number | string | undefined) ?? (b.skull as number | string | undefined) ?? null,
    base: bannerBase(b, kind),
  };
}

function bannerBase(b: Record<string, unknown>, kind: string): number | string | null {
  const color = kind === 'banner' ? (b.color as number | string | undefined) : undefined;
  return (b.base as number | string | undefined) ?? color ?? null;
}

/** `api/world/blockentities` (snake_case or camelCase; sign lines as `lines` or text1..text4; patterns as pairs or objects). */
export function normalizeBlockEntities(raw: unknown): BlockEntities {
  const r = camelize<Partial<BlockEntities> & { dim?: number }>(raw) ?? { columns: [] };
  return {
    dim: r.dim,
    columns: (r.columns ?? []).map((c: Partial<BlockEntityColumn>) => ({
      cx: num(c.cx),
      cz: num(c.cz),
      hash: c.hash ?? 0,
      entities: (c.entities ?? []).map((e) => blockEntity(e as Record<string, unknown>)),
    })),
  };
}

function mark(m: Partial<Mark>): Mark {
  const min = (m.min ?? [0, 0, 0]).map((v) => num(v)) as Mark['min'];
  const max = (m.max ?? min).map((v) => num(v)) as Mark['max'];
  return { kind: m.kind ?? '', text: m.text ?? null, min, max, owner: m.owner ?? null };
}

export function normalizeOverlays(raw: unknown): Overlays {
  const r = camelize<Partial<Overlays>>(raw) ?? {};
  return {
    at: r.at,
    dim: num(r.dim),
    claims: (r.claims ?? []).map(mark),
    places: (r.places ?? []).map(mark),
    points: (r.points ?? []).map(mark),
    plans: (r.plans ?? []).map((p: Partial<PlanDto>) => ({ id: p.id ?? 0, name: p.name ?? '', status: p.status ?? null, part: p.part ?? null, blocks: (p.blocks ?? []) as PlanDto['blocks'] })),
    dangers: (r.dangers ?? []).map(mark),
    notes: (r.notes ?? []).map((n) => (typeof n === 'string' ? n : JSON.stringify(n))),
  };
}
