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
  const reviver = function (this: unknown, key: string, value: unknown, context?: ReviverContext): unknown {
    if (typeof value === 'number' && int64Keys.has(key)) {
      if (context?.source !== undefined) return context.source;
      if (Number.isSafeInteger(value)) return String(value);
    }
    return value;
  };
  return JSON.parse(text, reviver) as unknown;
}

/** Deep-converts object keys from camelCase to snake_case (public → bridge: query keys and bodies). */
export function toSnake<T = unknown>(value: unknown, options: CaseOptions = {}): T {
  return convert(value, camelToSnake, options.opaque ?? OPAQUE_KEYS) as T;
}
