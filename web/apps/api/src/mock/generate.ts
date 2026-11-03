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

/** zod 4 keeps refinements in `def.checks` (each with its own `_zod.def`). */
function limitsOf(s: Schema): Limits {
  const out: Limits = { patterns: [], int: false };
  const checks = (s._zod.def.checks as Array<{ _zod: { def: Record<string, unknown> } }> | undefined) ?? [];
  for (const c of checks) {
    const d = c._zod.def;
    switch (d.check) {
      case 'min_length':
        out.minLength = Math.max(out.minLength ?? 0, d.minimum as number);
        break;
      case 'max_length':
        out.maxLength = Math.min(out.maxLength ?? Infinity, d.maximum as number);
        break;
      case 'length_equals':
        out.minLength = out.maxLength = d.length as number;
        break;
      case 'string_format':
        if (d.format === 'starts_with') out.prefix = d.prefix as string;
        else if (d.pattern instanceof RegExp) out.patterns.push(d.pattern);
        break;
      case 'greater_than':
        if (d.inclusive) out.min = Math.max(out.min ?? -Infinity, d.value as number);
        else out.exclusiveMin = d.value as number;
        break;
      case 'less_than':
        if (d.inclusive) out.max = Math.min(out.max ?? Infinity, d.value as number);
        else out.exclusiveMax = d.value as number;
        break;
      case 'number_format':
        out.int = true;
        break;
    }
  }
  return out;
}

function sampleString(key: string, s: Schema, i: number): string {
  const lim = limitsOf(s);
  for (const p of lim.patterns) {
    const known = KNOWN_PATTERNS[p.source];
    if (known !== undefined) return known;
  }
  if (lim.prefix === 'otpauth://totp/') return 'otpauth://totp/MineClaw:fox?secret=JBSWY3DPEHPK3PXP&issuer=MineClaw';
  const k = key.toLowerCase();
  let v: string;
  if (k === 'at' || k.endsWith('at') || k === 'since' || k === 'until' || k.endsWith('time')) v = ISO;
  else if (k === 'bot' || k === 'body' || k === 'brain' || k === 'owner' || k === 'by' || k === 'who')
    v = BOT_NAMES[i % 2]!;
  else if (k === 'name') v = i === 0 ? 'cobble_loop' : 'wheat_farm';
  else if (k === 'id' || k.endsWith('id')) v = String(1 + i);
  else if (k === 'version') v = 'b1.8.1';
  else if (k.includes('hash') || k.includes('sha'))
    v = 'e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855';
  else if (k === 'text' || k === 'summary' || k === 'message' || k === 'note' || k === 'description')
    v = `sample ${key} ${i + 1}`;
  else v = `${key || 'value'}${i ? ` ${i + 1}` : ''}`;
  const prefix = lim.prefix;
  if (prefix && !v.startsWith(prefix)) v = prefix + v;
  const min = lim.minLength ?? 0;
  const max = lim.maxLength;
  while (v.length < min) v += 'x';
  if (max !== undefined && v.length > max) v = v.slice(0, max);
  return v;
}

function sampleNumber(s: Schema, i: number): number {
  const lim = limitsOf(s);
  let v = 1 + i;
  if (lim.min !== undefined && v < lim.min) v = lim.min;
  if (lim.exclusiveMin !== undefined && v <= lim.exclusiveMin) v = lim.exclusiveMin + 1;
  if (lim.max !== undefined && v > lim.max) v = lim.max;
  if (lim.exclusiveMax !== undefined && v >= lim.exclusiveMax) v = lim.exclusiveMax - (lim.int ? 1 : 0.5);
  return lim.int ? Math.round(v) : v;
}

export function generate(schema: z.ZodType, key = '', depth = 0, i = 0): unknown {
  const s = schema as unknown as Schema;
  const def = inner(s);
  if (depth > 24) return undefined;
  switch (def.type) {
    case 'string':
      return sampleString(key, s, i);
    case 'number':
    case 'int':
      return sampleNumber(s, i);
    case 'bigint':
      return 1;
    case 'boolean':
      return i % 2 === 0;
    case 'null':
      return null;
    case 'undefined':
    case 'void':
      return undefined;
    case 'any':
    case 'unknown':
      return {};
    case 'date':
      return ISO;
    case 'literal':
      return (def.values as unknown[])[0];
    case 'enum': {
      const entries = def.entries as Record<string, string | number>;
      const values = Object.values(entries);
      return values[i % values.length];
    }
    case 'optional':
    case 'nullable':
    case 'default':
    case 'prefault':
    case 'catch':
    case 'readonly':
    case 'nonoptional':
      return generate(def.innerType as z.ZodType, key, depth + 1, i);
    case 'pipe':
      return generate(def.in as z.ZodType, key, depth + 1, i);
    case 'lazy':
      return generate((def.getter as () => z.ZodType)(), key, depth + 1, i);
    case 'union': {
      const options = def.options as z.ZodType[];
      // Prefer a non-string option for Double-like unions (number | "NaN"), and an object for discriminated unions.
      return generate(options[0]!, key, depth + 1, i);
    }
    case 'intersection':
      return {
        ...(generate(def.left as z.ZodType, key, depth + 1, i) as object),
        ...(generate(def.right as z.ZodType, key, depth + 1, i) as object),
      };
    case 'array': {
      const lim = limitsOf(s);
      // Recursive schemas (the hive tree, loot categories) bottom out in empty lists.
      if (depth > 10 && !(lim.minLength ?? 0)) return [];
      const n = Math.max(lim.minLength ?? 0, Math.min(2, lim.maxLength ?? 2));
      const singular = key.endsWith('s') ? key.slice(0, -1) : key;
      return Array.from({ length: n }, (_, j) => generate(def.element as z.ZodType, singular, depth + 1, j));
    }
    case 'tuple':
      return (def.items as z.ZodType[]).map((item, j) => generate(item, key, depth + 1, j));
    case 'record': {
      const k = generate(def.keyType as z.ZodType, 'key', depth + 1, 0);
      return { [String(k)]: generate(def.valueType as z.ZodType, key, depth + 1, i) };
    }
    case 'object': {
      const shape = def.shape as Record<string, z.ZodType>;
      const out: Record<string, unknown> = {};
      for (const [k, v] of Object.entries(shape)) {
        const value = generate(v, k, depth + 1, i);
        if (value !== undefined) out[k] = value;
      }
      return out;
    }
    default:
      return undefined;
  }
}
