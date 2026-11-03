import { ROUTES, ROUTE_IDS, SSE_EVENTS, ViewMeta, type RouteDef } from '@mineclaw/contracts';
import { describe, expect, it } from 'vitest';
import type { z } from 'zod';
import { generate } from '../src/mock/generate.js';

const schemas: Array<[string, z.ZodType]> = [];
for (const id of ROUTE_IDS) {
  const r: RouteDef = ROUTES[id];
  if (r.response) schemas.push([`route ${id}`, r.response]);
}
for (const [name, s] of Object.entries(SSE_EVENTS)) schemas.push([`sse ${name}`, s as z.ZodType]);
schemas.push(['ViewMeta', ViewMeta]);

describe('schema sample generator', () => {
  it.each(schemas)('%s', (_name, schema) => {
    const value = generate(schema);
    const r = schema.safeParse(value);
    if (!r.success) throw new Error(JSON.stringify(r.error.issues.slice(0, 5), null, 2));
    expect(r.success).toBe(true);
  });
});
