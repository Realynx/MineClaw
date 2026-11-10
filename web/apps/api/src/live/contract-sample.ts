/**
 * What a contract check (dev `validate`/`validateResponses`) looks at: the payload with every bulk array of tuples (a colony's
 * 47k ghost rows, block lists) cut to its first rows. Everything else is checked in full; the tuples' shape is checked on the
 * rows kept, which is what costs zod tens of ms per frame otherwise. Never mutates the payload; returns it unchanged when
 * nothing was cut.
 */
export const SAMPLED_ROWS = 64;

export function contractSample(value: unknown, keep = SAMPLED_ROWS): unknown {
  if (value === null || typeof value !== 'object') return value;
  if (Array.isArray(value)) {
    const rows = value.length > keep && Array.isArray(value[0]) ? value.slice(0, keep) : value;
    let changed = rows !== value;
    const out = rows.map((v: unknown) => {
      const s = contractSample(v, keep);
      if (s !== v) changed = true;
      return s;
    });
    return changed ? out : value;
  }
  let out: Record<string, unknown> | undefined;
  for (const [k, v] of Object.entries(value)) {
    const s = contractSample(v, keep);
    if (s !== v) (out ??= { ...value })[k] = s;
  }
  return out ?? value;
}
