/**
 * snake_case ⇄ camelCase key conversion between the bridge (`/v1`, snake_case) and the public `/api` (camelCase).
 *
 * Only object KEYS are converted, recursively through arrays and plain objects. Values are never touched.
 * Domain maps whose keys are data rather than field names are carried opaque: conversion stops at the value of any key in
 * `OPAQUE_KEYS` (matched on both the snake and the camel spelling). Examples: script `params`/`args`, a machine `blackboard`,
 * a memory edit's `fields` (keys are the edit schema's field names, e.g. `__reason`), shard `counts` (keys are record kinds).
 */

/** Keys whose VALUE is opaque domain data: its own keys are never converted. */
export const OPAQUE_KEYS: ReadonlySet<string> = new Set([
  'params',
  'args',
  'blackboard',
  'fields',
  'edit_prefill',
  'editPrefill',
  'counts',
  'by_kind',
  'byKind',
  'see',
  'bill',
  'data',
  'ench',
]);

/**
 * The opaque keys for REQUEST bodies (public → bridge): `bill` is opaque only in responses (a blueprint's material map); a
 * Story Mode bill in a request is ordinary fields (`min_skill`).
 */
export const REQUEST_OPAQUE_KEYS: ReadonlySet<string> = new Set([...OPAQUE_KEYS].filter((k) => k !== 'bill'));

export interface CaseOptions {
  /** Replace the default opaque key set. */
  opaque?: ReadonlySet<string>;
}

/** `cache_write1h` → `cacheWrite1h`, `in_flight` → `inFlight`, `__reason` → `__reason` (leading underscores are kept). */
export function snakeToCamel(key: string): string {
  const lead = /^_+/.exec(key)?.[0] ?? '';
  const rest = key.slice(lead.length);
  return lead + rest.replace(/_+([a-z0-9])/gi, (_m, c: string) => c.toUpperCase());
}

/** `inFlight` → `in_flight`, `cacheWrite1h` → `cache_write1h`, `windowS` → `window_s`, `avgMs` → `avg_ms`. */
export function camelToSnake(key: string): string {
  return key
    .replace(/([a-z0-9])([A-Z])/g, '$1_$2')
    .replace(/([A-Z])([A-Z][a-z])/g, '$1_$2')
    .toLowerCase();
}

function isPlainObject(v: unknown): v is Record<string, unknown> {
  if (v === null || typeof v !== 'object') return false;
  const proto = Object.getPrototypeOf(v) as unknown;
  return proto === Object.prototype || proto === null;
}

function convert(value: unknown, mapKey: (k: string) => string, opaque: ReadonlySet<string>): unknown {
  if (Array.isArray(value)) return value.map((v) => convert(v, mapKey, opaque));
  if (!isPlainObject(value)) return value;
  const out: Record<string, unknown> = {};
  for (const [k, v] of Object.entries(value)) {
    out[mapKey(k)] = opaque.has(k) ? v : convert(v, mapKey, opaque);
  }
  return out;
}

/** Deep-converts object keys from snake_case to camelCase (bridge → public). */
export function toCamel<T = unknown>(value: unknown, options: CaseOptions = {}): T {
  return convert(value, snakeToCamel, options.opaque ?? OPAQUE_KEYS) as T;
}

/** Keys whose JSON numbers are 64-bit integers that must survive exactly (world column hashes). */
export const INT64_KEYS: ReadonlySet<string> = new Set(['hash']);

type ReviverContext = { source?: string } | undefined;

/**
 * JSON.parse for bridge responses: numbers under `INT64_KEYS` become their exact source text (a decimal string), using the
 * reviver source-text access of Node ≥ 22 / V8 ≥ 11.4. Everything else parses normally. Nest uses this for every JSON body it
 * reads from the bridge, then applies `toCamel`.
 */
export function parseBridgeJson(text: string, int64Keys: ReadonlySet<string> = INT64_KEYS): unknown {
  // A reviver runs on every value (16x slower on a 2 MB colony state): skip it when no int64 key can be in the text.
  if (!mayHoldKeys(text, int64Keys)) return JSON.parse(text) as unknown;
  const reviver = function (this: unknown, key: string, value: unknown, context?: ReviverContext): unknown {
    if (typeof value === 'number' && int64Keys.has(key)) {
      if (context?.source !== undefined) return context.source;
      if (Number.isSafeInteger(value)) return String(value);
    }
    return value;
  };
  return JSON.parse(text, reviver) as unknown;
}

/**
 * Whether a JSON text could hold any of these keys: the quoted key itself, or any of its characters written as a `\uXXXX`
 * escape (a key spelled with escapes parses to the same name). False means a reviver for these keys would change nothing.
 */
function mayHoldKeys(text: string, keys: ReadonlySet<string>): boolean {
  for (const key of keys) {
    if (text.includes(`"${key}"`)) return true;
    for (const ch of key) {
      // A character with a short escape (`\"`, `\\`, `\/`, control characters) is not worth a precise check.
      if (ch === '"' || ch === '\\' || ch === '/' || ch < ' ') return true;
      const hex = ch.charCodeAt(0).toString(16).padStart(4, '0');
      if (text.includes(`\\u${hex}`) || text.includes(`\\u${hex.toUpperCase()}`)) return true;
    }
  }
  return false;
}

/** snake → camel spellings already worked out (field names repeat in every frame); cleared when it grows past the cap. */
const camelKeys = new Map<string, string>();
const CAMEL_KEYS_CAP = 8192;

function camelKey(key: string): string {
  let camel = camelKeys.get(key);
  if (camel === undefined) {
    camel = snakeToCamel(key);
    if (camelKeys.size >= CAMEL_KEYS_CAP) camelKeys.clear();
    camelKeys.set(key, camel);
  }
  return camel;
}

function convertJson(value: unknown, opaque: ReadonlySet<string>): unknown {
  if (value === null || typeof value !== 'object') return value;
  if (Array.isArray(value)) {
    // A tuple of primitives (a ghost row, a position) has no keys to convert: it is shared, not copied.
    let i = 0;
    while (i < value.length && (value[i] === null || typeof value[i] !== 'object')) i++;
    return i === value.length ? value : value.map((v) => convertJson(v, opaque));
  }
  if (!isPlainObject(value)) return value;
  const out: Record<string, unknown> = {};
  for (const [k, v] of Object.entries(value)) {
    out[camelKey(k)] = opaque.has(k) ? v : convertJson(v, opaque);
  }
  return out;
}

/**
 * `toCamel` for a value fresh from `JSON.parse`/`parseBridgeJson` (the API's bridge replies and live frames): the same keys,
 * values and shapes, but key spellings are cached and arrays holding no object or array are SHARED with the input instead of
 * copied. Only for input that is not used or mutated afterwards.
 */
export function toCamelJson<T = unknown>(value: unknown, options: CaseOptions = {}): T {
  return convertJson(value, options.opaque ?? OPAQUE_KEYS) as T;
}

/** Deep-converts object keys from camelCase to snake_case (public → bridge: query keys and bodies). */
export function toSnake<T = unknown>(value: unknown, options: CaseOptions = {}): T {
  return convert(value, camelToSnake, options.opaque ?? OPAQUE_KEYS) as T;
}
