/**
 * Shared primitives.
 *
 * Wire rules (see docs/web/api.md §1):
 * - The public `/api` is camelCase JSON. The bridge (`/v1`) and the supervisor emit snake_case; Nest converts with `toCamel`.
 * - C# nullables are serialized with `WhenWritingNull`, so a null field is usually ABSENT: every nullable field is `.nullish()`.
 * - `DateTimeOffset` values are ISO-8601 strings with an offset ("2026-11-02T10:15:30.123+00:00"). A few world routes use unix
 *   milliseconds instead; those fields use `UnixMs`.
 * - Response objects are non-strict (unknown keys are stripped by `parse`, never rejected) so a newer bot can add fields.
 *   Request bodies are strict.
 */
import { z } from 'zod';

/** ISO-8601 date-time with offset, as System.Text.Json writes `DateTimeOffset`. */
export const IsoDateTime = z.string().min(1).describe('ISO-8601 date-time with offset');
export type IsoDateTime = z.infer<typeof IsoDateTime>;

/** Unix epoch milliseconds. */
export const UnixMs = z.number().describe('unix epoch milliseconds');
export type UnixMs = z.infer<typeof UnixMs>;

/**
 * A C# `double` that may be NaN or ±Infinity: the supervisor allows named floating-point literals, which System.Text.Json writes
 * as the JSON strings "NaN", "Infinity", "-Infinity". Use `toNumber` to read one.
 */
export const Double = z.union([z.number(), z.enum(['NaN', 'Infinity', '-Infinity'])]);
export type Double = z.infer<typeof Double>;

/** Reads a `Double` as a JS number (NaN/±Infinity for the named literals). */
export function toNumber(value: Double | null | undefined): number {
  if (value === null || value === undefined) return Number.NaN;
  if (typeof value === 'number') return value;
  return value === 'NaN' ? Number.NaN : value === 'Infinity' ? Number.POSITIVE_INFINITY : Number.NEGATIVE_INFINITY;
}

/** Minecraft dimension id: 0 overworld, -1 nether (1 = end, unused in this world). */
export const DimId = z.number().int();
export type DimId = z.infer<typeof DimId>;

/** Dimension filter used by memory, search and shards. */
export const DimFilter = z.enum(['any', 'overworld', 'nether', 'none']);
export type DimFilter = z.infer<typeof DimFilter>;

/** A block position [x, y, z]. */
export const Vec3i = z.tuple([z.number().int(), z.number().int(), z.number().int()]);
export type Vec3i = z.infer<typeof Vec3i>;

/** A position [x, y, z] (may be fractional). */
export const Vec3 = z.tuple([z.number(), z.number(), z.number()]);
export type Vec3 = z.infer<typeof Vec3>;

/** Any JSON value (script params, blackboards, raw bills): carried opaque, never key-converted (see `OPAQUE_KEYS`). */
export const JsonValue: z.ZodType<unknown> = z.unknown();

/** Query-string integer (query values arrive as strings; Nest coerces). */
export const QInt = z.coerce.number().int();
/** Query-string boolean: "1" | "true" | "0" | "false". */
export const QBool = z
  .union([z.boolean(), z.enum(['1', '0', 'true', 'false'])])
  .transform((v) => v === true || v === '1' || v === 'true');

/** Error body of every public route (4xx/5xx). Never contains stack traces or upstream URLs. */
export const ApiError = z.object({
  error: z.string().describe('human-readable, generic for auth failures'),
  code: z
    .enum([
      'bad_request',
      'unauthorized',
      'forbidden',
      'csrf',
      'not_found',
      'conflict',
      'payload_too_large',
      'rate_limited',
      'locked',
      'upstream_unavailable',
      'upstream_error',
      'busy',
      'internal',
      'step_up_required',
    ])
    .optional(),
  retryAfterMs: z.number().int().nonnegative().optional().describe('set on 429/503 together with a Retry-After header'),
  details: z
    .array(z.object({ path: z.string(), message: z.string() }))
    .optional()
    .describe('validation issues (400)'),
});
export type ApiError = z.infer<typeof ApiError>;

/** Server-side paging envelope. */
export function paged<T extends z.ZodType>(item: T) {
  return z.object({
    total: z.number().int().nonnegative(),
    offset: z.number().int().nonnegative(),
    limit: z.number().int().positive(),
    items: z.array(item),
  });
}

/** Common paging query (offset/limit). Max limit 500 unless a route says otherwise. */
export const PageQuery = z.object({
  offset: QInt.min(0).default(0),
  limit: QInt.min(1).max(500).default(100),
});
export type PageQuery = z.infer<typeof PageQuery>;

/** `{ ok: true }` acknowledgement. */
export const OkResponse = z.object({ ok: z.boolean(), message: z.string().nullish() });
export type OkResponse = z.infer<typeof OkResponse>;

/** GET /api/health. `bridge` reflects a signed ping of the bridge's `/v1/health`. */
export const HealthResponse = z.object({ ok: z.boolean(), version: z.string(), bridge: z.enum(['up', 'down']) });
export type HealthResponse = z.infer<typeof HealthResponse>;

/** No request body / no params. */
export const Empty = z.object({}).strict();
export type Empty = z.infer<typeof Empty>;
