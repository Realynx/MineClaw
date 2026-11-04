/**
 * Builds a plausible value that satisfies a zod 4 schema (mock bridge, for routes without a contracts fixture). It walks
 * `schema._zod.def`; strings get key-aware samples (`at`/`…At` → ISO time, `name`/`bot` → a bot name), numbers respect
 * min/max, arrays get two items. Output is the PUBLIC (camelCase) shape; the mock bridge converts it with `toSnake`.
 */
import type { z } from 'zod';

interface Def {
  type: string;
  [k: string]: unknown;
}
interface Internals {
  def: Def;
  bag?: Record<string, unknown>;
}
type Schema = { _zod: Internals };

const ISO = '2026-11-02T10:00:00+00:00';
const BOT_NAMES = ['maid_chan', 'maid_chan_67'];

const KNOWN_PATTERNS: Record<string, string> = {
  '^\\d{6}$': '123456',
  '^-?\\d+$': '-7046029254386353131',
  '^[A-Za-z0-9_-]{8,64}$': 'r-8f14e45fceea',
  '^[A-Za-z,]*$': 'feed,now',
  '^#[0-9a-fA-F]{6}$': '#4f8cff',
  [/^-?\d+,-?\d+(;-?\d+,-?\d+){0,63}$/.source]: '0,0;1,0',
};

function inner(s: Schema): Def {
  return s._zod.def;
}

interface Limits {
  minLength?: number;
  maxLength?: number;
  patterns: RegExp[];
  prefix?: string;
  min?: number;
  max?: number;
  exclusiveMin?: number;
  exclusiveMax?: number;
  int: boolean;
}

type CheckDef = Record<string, unknown>;

/** How each zod 4 check narrows the limits. */
const CHECKS: Record<string, (out: Limits, d: CheckDef) => void> = {
  min_length: (out, d) => {
    out.minLength = Math.max(out.minLength ?? 0, d.minimum as number);
  },
  max_length: (out, d) => {
    out.maxLength = Math.min(out.maxLength ?? Infinity, d.maximum as number);
  },
  length_equals: (out, d) => {
    out.minLength = out.maxLength = d.length as number;
  },
  string_format: (out, d) => {
    if (d.format === 'starts_with') out.prefix = d.prefix as string;
    else if (d.pattern instanceof RegExp) out.patterns.push(d.pattern);
  },
  greater_than: (out, d) => {
    if (d.inclusive) out.min = Math.max(out.min ?? -Infinity, d.value as number);
    else out.exclusiveMin = d.value as number;
  },
  less_than: (out, d) => {
    if (d.inclusive) out.max = Math.min(out.max ?? Infinity, d.value as number);
    else out.exclusiveMax = d.value as number;
  },
  number_format: (out) => {
    out.int = true;
  },
};

/** An own entry of a lookup table (never an inherited `toString` & co). */
function own<T>(table: Record<string, T>, key: unknown): T | undefined {
  return typeof key === 'string' && Object.hasOwn(table, key) ? table[key] : undefined;
}

/** zod 4 keeps refinements in `def.checks` (each with its own `_zod.def`). */
function limitsOf(s: Schema): Limits {
  const out: Limits = { patterns: [], int: false };
  const checks = (s._zod.def.checks as Array<{ _zod: { def: CheckDef } }> | undefined) ?? [];
  for (const c of checks) {
    const d = c._zod.def;
    own(CHECKS, d.check)?.(out, d);
  }
  return out;
}

const TOTP_URI = 'otpauth://totp/MineClaw:fox?secret=JBSWY3DPEHPK3PXP&issuer=MineClaw';

/** A fixed sample for a known pattern or the TOTP URI prefix; undefined otherwise. */
function fixedSample(lim: Limits): string | undefined {
  for (const p of lim.patterns) {
    const known = KNOWN_PATTERNS[p.source];
    if (known !== undefined) return known;
  }
  return lim.prefix === 'otpauth://totp/' ? TOTP_URI : undefined;
}

const isOneOf = (k: string, ...names: string[]) => names.includes(k);

/** Key-aware samples, first match wins (`k` is the lower-cased key). */
const KEY_SAMPLES: Array<[(k: string) => boolean, (key: string, i: number) => string]> = [
  [(k) => k === 'at' || k.endsWith('at') || k === 'since' || k === 'until' || k.endsWith('time'), () => ISO],
  [(k) => isOneOf(k, 'bot', 'body', 'brain', 'owner', 'by', 'who'), (_key, i) => BOT_NAMES[i % 2]!],
  [(k) => k === 'name', (_key, i) => (i === 0 ? 'cobble_loop' : 'wheat_farm')],
  [(k) => k === 'id' || k.endsWith('id'), (_key, i) => String(1 + i)],
  [(k) => k === 'version', () => 'b1.8.1'],
  [
    (k) => k.includes('hash') || k.includes('sha'),
    () => 'e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855',
  ],
  [(k) => isOneOf(k, 'text', 'summary', 'message', 'note', 'description'), (key, i) => `sample ${key} ${i + 1}`],
];

function keySample(key: string, i: number): string {
  const k = key.toLowerCase();
  const rule = KEY_SAMPLES.find(([matches]) => matches(k));
  return rule ? rule[1](key, i) : `${key || 'value'}${i ? ` ${i + 1}` : ''}`;
}

/** The prefix, padded to the minimum length, cut to the maximum. */
function fitString(v: string, lim: Limits): string {
  const prefix = lim.prefix;
  if (prefix && !v.startsWith(prefix)) v = prefix + v;
  const min = lim.minLength ?? 0;
  const max = lim.maxLength;
  while (v.length < min) v += 'x';
  if (max !== undefined && v.length > max) v = v.slice(0, max);
  return v;
}

function sampleString(key: string, s: Schema, i: number): string {
  const lim = limitsOf(s);
  return fixedSample(lim) ?? fitString(keySample(key, i), lim);
}

function sampleNumber(s: Schema, i: number): number {
  const lim = limitsOf(s);
  let v = 1 + i;
  if (lim.min !== undefined && v < lim.min) v = lim.min;
  if (lim.exclusiveMin !== undefined && v <= lim.exclusiveMin) v = lim.exclusiveMin + 1;
  v = capNumber(v, lim);
  return lim.int ? Math.round(v) : v;
}

function capNumber(v: number, lim: Limits): number {
  if (lim.max !== undefined && v > lim.max) v = lim.max;
  if (lim.exclusiveMax !== undefined && v >= lim.exclusiveMax) v = lim.exclusiveMax - (lim.int ? 1 : 0.5);
  return v;
}

/** One `generate` step: the schema, its def, the key it sits under, the depth and the item index. */
interface Gen {
  s: Schema;
  def: Def;
  key: string;
  depth: number;
  i: number;
}

const deeper = (g: Gen, schema: unknown, key = g.key, i = g.i) => generate(schema as z.ZodType, key, g.depth + 1, i);

const unwrap = (g: Gen) => deeper(g, g.def.innerType);

function genEnum(g: Gen): unknown {
  const values = Object.values(g.def.entries as Record<string, string | number>);
  return values[g.i % values.length];
}

function genArray(g: Gen): unknown {
  const lim = limitsOf(g.s);
  // Recursive schemas (the hive tree, loot categories) bottom out in empty lists.
  if (g.depth > 10 && !(lim.minLength ?? 0)) return [];
  const n = Math.max(lim.minLength ?? 0, Math.min(2, lim.maxLength ?? 2));
  const singular = g.key.endsWith('s') ? g.key.slice(0, -1) : g.key;
  return Array.from({ length: n }, (_, j) => deeper(g, g.def.element, singular, j));
}

function genObject(g: Gen): unknown {
  const out: Record<string, unknown> = {};
  for (const [k, v] of Object.entries(g.def.shape as Record<string, z.ZodType>)) {
    const value = deeper(g, v, k);
    if (value !== undefined) out[k] = value;
  }
  return out;
}

function genRecord(g: Gen): unknown {
  const k = generate(g.def.keyType as z.ZodType, 'key', g.depth + 1, 0);
  return { [String(k)]: deeper(g, g.def.valueType) };
}

/** A generator per zod 4 def type; a type not listed generates undefined. */
const GENERATORS: Record<string, (g: Gen) => unknown> = {
  string: (g) => sampleString(g.key, g.s, g.i),
  number: (g) => sampleNumber(g.s, g.i),
  int: (g) => sampleNumber(g.s, g.i),
  bigint: () => 1,
  boolean: (g) => g.i % 2 === 0,
  null: () => null,
  undefined: () => undefined,
  void: () => undefined,
  any: () => ({}),
  unknown: () => ({}),
  date: () => ISO,
  literal: (g) => (g.def.values as unknown[])[0],
  enum: genEnum,
  optional: unwrap,
  nullable: unwrap,
  default: unwrap,
  prefault: unwrap,
  catch: unwrap,
  readonly: unwrap,
  nonoptional: unwrap,
  pipe: (g) => deeper(g, g.def.in),
  lazy: (g) => deeper(g, (g.def.getter as () => z.ZodType)()),
  // Prefer a non-string option for Double-like unions (number | "NaN"), and an object for discriminated unions.
  union: (g) => deeper(g, (g.def.options as z.ZodType[])[0]),
  intersection: (g) => ({ ...(deeper(g, g.def.left) as object), ...(deeper(g, g.def.right) as object) }),
  array: genArray,
  tuple: (g) => (g.def.items as z.ZodType[]).map((item, j) => deeper(g, item, g.key, j)),
  record: genRecord,
  object: genObject,
};

export function generate(schema: z.ZodType, key = '', depth = 0, i = 0): unknown {
  const s = schema as unknown as Schema;
  const def = inner(s);
  if (depth > 24) return undefined;
  return own(GENERATORS, def.type)?.({ s, def, key, depth, i });
}
