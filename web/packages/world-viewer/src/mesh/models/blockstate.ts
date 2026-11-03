// A 1.12.2 blockstate file (assets/minecraft/blockstates/*.json), our port of the C# BlockStateFile (ModelBlockDefinition):
// `variants` (a property string such as "facing=east,half=bottom,shape=straight", or "normal", → one model or a weighted
// list of which the first is taken, so pictures are stable) or `multipart` (every case whose `when` holds applies, with `OR`
// lists and "a|b" alternatives). Variants match by properties, not by string: a variant applies when none of its properties
// contradicts the state, and the one matching the most wins (ties: file order), so a property the state doesn't carry never
// hides a variant.

export interface VariantRef {
  model: string;
  x: number;
  y: number;
  uvlock: boolean;
}

type Props = Record<string, string>;
type Cond = Record<string, string[]>;

interface RawRef {
  model?: string;
  x?: number;
  y?: number;
  uvlock?: boolean;
}

const one = (r: RawRef): VariantRef => ({ model: r.model ?? 'missingno', x: r.x ?? 0, y: r.y ?? 0, uvlock: r.uvlock === true });
const first = (v: RawRef | RawRef[] | undefined): VariantRef | null => (Array.isArray(v) ? (v.length ? one(v[0]) : null) : v ? one(v) : null);

export function parseProps(key: string): Props {
  const d: Props = {};
  for (const part of key.split(',')) {
    const i = part.indexOf('=');
    if (i > 0) d[part.slice(0, i)] = part.slice(i + 1);
  }
  return d;
}

function condition(e: Record<string, unknown>): Cond {
  const d: Cond = {};
  for (const [k, v] of Object.entries(e)) d[k] = String(v).toLowerCase().split('|');
  return d;
}

export class BlockStateFile {
  private readonly variants: Array<{ key: string; props: Props; model: VariantRef }> = [];
  private readonly parts: Array<{ when: Cond[] | null; apply: VariantRef | null }> = [];

  get isMultipart(): boolean {
    return this.parts.length > 0;
  }

  static parse(json: unknown): BlockStateFile {
    const f = new BlockStateFile();
    const r = json as { variants?: Record<string, RawRef | RawRef[]>; multipart?: Array<{ when?: Record<string, unknown> & { OR?: Array<Record<string, unknown>> }; apply?: RawRef | RawRef[] }> };
    if (r.variants)
      for (const [key, v] of Object.entries(r.variants)) {
        const m = first(v);
        if (m) f.variants.push({ key, props: parseProps(key), model: m });
      }
    if (r.multipart)
      for (const part of r.multipart) {
        let when: Cond[] | null = null;
        if (part.when) when = part.when.OR ? part.when.OR.map(condition) : [condition(part.when)];
        f.parts.push({ when, apply: first(part.apply) });
      }
    return f;
  }

  /** The models that draw a state (one for variants, every applying case for multipart). `key` = an exact variant key to
   * prefer ("all" for seamless double slabs). */
  select(state: Props, key?: string | null): VariantRef[] {
    const out: VariantRef[] = [];
    if (this.parts.length) {
      for (const { when, apply } of this.parts) {
        if (!apply) continue;
        if (!when || when.some((c) => Object.entries(c).every(([k, vs]) => state[k] != null && vs.includes(state[k])))) out.push(apply);
      }
      return out;
    }
    if (key != null) {
      const v = this.variants.find((x) => x.key === key);
      if (v) return [v.model];
    }
    let best = -1;
    let pick: VariantRef | null = null;
    for (const v of this.variants) {
      let score = 0, ok = true;
      for (const [k, val] of Object.entries(v.props)) {
        const have = state[k];
        if (have == null) continue;
        if (have !== val) {
          ok = false;
          break;
        }
        score++;
      }
      if (ok && score > best) {
        best = score;
        pick = v.model;
      }
    }
    if (pick) out.push(pick);
    else if (this.variants.length) out.push(this.variants[0].model);
    return out;
  }
}
