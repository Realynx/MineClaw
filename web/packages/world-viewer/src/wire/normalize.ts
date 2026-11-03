// Accepts the supervisor's snake_case replies or Nest's camelCase mirror and returns the viewer's types with defaults filled.

import type { BotDto, EntityDelta, EntityDto, Mark, Overlays, PlanDto, WorldIndex } from '../types';

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
    time: r.time ?? null,
    rain: r.rain ?? null,
    thunder: r.thunder ?? null,
  };
}

function bot(b: Partial<BotDto>): BotDto {
  return {
    ...b,
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
    at: r.at,
    rev: num(r.rev),
    full: r.full ?? true,
    bots: (r.bots ?? []).map(bot),
    entities: (r.entities ?? []).map((e: Partial<EntityDto>) => ({ ...e, kind: e.kind ?? 'mob', dim: num(e.dim), x: num(e.x), y: num(e.y), z: num(e.z) })),
    removed: (r.removed ?? []).filter((p) => Array.isArray(p) && p.length >= 2).map((p) => [num(p[0]), num(p[1])] as [number, number]),
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
