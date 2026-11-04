// The wire's per-kind appearance, decoded tolerantly: the legacy numeric `variant` (the Mob16 value: slime size, sheep
// colour | 0x10 sheared, pig saddle, wolf flags, cat/rabbit/parrot/villager type…) and the `look` string of comma-separated
// tokens ("red,sheared", "creamy,white_dots,tame,saddle,armor=iron", "tame,sitting,collar=red", "powered,fusing",
// "size=4", "profession=librarian"). Tokens are lower-cased with '-' and ' ' as '_'; "key=value" (or "key:value") tokens
// are also looked up by key. Looks and poses ask for what they
// understand and ignore the rest, so new tokens never break anything.

const DYES = ['white', 'orange', 'magenta', 'light_blue', 'yellow', 'lime', 'pink', 'gray', 'silver', 'cyan', 'purple', 'blue', 'brown', 'green', 'red', 'black'];
const DYE_ALIASES: Record<string, string> = { light_gray: 'silver', lightgray: 'silver', grey: 'gray', lightblue: 'light_blue', light_grey: 'silver' };

export function dyeOf(s: string | undefined): number {
  if (s == null) return -1;
  if (/^\d+$/.test(s)) {
    const n = Number(s);
    return n >= 0 && n < 16 ? n : -1;
  }
  const n = s.replace(/^minecraft:/, '');
  return DYES.indexOf(DYE_ALIASES[n] ?? n);
}

/** A string variant and the look as tokens: lower case, '-' and whitespace as '_', no "minecraft:" prefix. */
function variantTokens(raw: number | string | null, look: string | null | undefined): string[] {
  const text = [typeof raw === 'string' ? raw : '', look ?? ''].filter(Boolean).join(',');
  return text
    .split(/[,;|]/)
    .map((t) => t.trim().toLowerCase().replace(/[\s-]+/g, '_').replace(/^minecraft:/, ''))
    .filter(Boolean);
}

export class Variant {
  /** The numeric variant: the wire number, or the first bare-number token of a string; 0 when none. */
  readonly num: number;
  readonly tokens: readonly string[];
  private readonly kv: Record<string, string> = {};

  private constructor(readonly raw: number | string | null, look?: string | null) {
    if (typeof raw === 'number' && !look) {
      this.num = Number.isFinite(raw) ? raw : 0;
      this.tokens = [];
      return;
    }
    const toks = variantTokens(raw, look);
    const num = this.readTokens(toks);
    this.num = typeof raw === 'number' && Number.isFinite(raw) ? raw : num;
    this.tokens = toks;
  }

  /** Files the "key=value" / "key:value" tokens by key; the first bare-number token (0 when none). */
  private readTokens(toks: readonly string[]): number {
    let num = 0, found = false;
    for (const t of toks) {
      const i = t.search(/[:=]/);
      if (i > 0) this.kv[t.slice(0, i)] = t.slice(i + 1);
      else if (!found && /^-?\d+$/.test(t)) {
        num = Number(t);
        found = true;
      }
    }
    return num;
  }

  static readonly NONE = new Variant(null);

  /** From the numeric `variant` (or an older string variant) and the `look` tokens. */
  static of(v: number | string | null | undefined, look?: string | null): Variant {
    if ((v == null || v === '') && !look) return Variant.NONE;
    return new Variant(v ?? null, look);
  }

  /** Tokens are present (the numeric Mob16 bits then don't apply: the look says it all). */
  get isString(): boolean {
    return this.tokens.length > 0;
  }

  /** Any of the tokens is present (bare, or as a key). */
  has(...names: string[]): boolean {
    return names.some((n) => this.tokens.includes(n) || n in this.kv);
  }

  /** The value of a "key:value" token. */
  get(key: string): string | undefined {
    return this.kv[key];
  }

  /** A number from "key:value", else `fallback`. */
  numOf(key: string, fallback: number): number {
    const v = this.kv[key];
    return v != null && v !== '' && Number.isFinite(Number(v)) ? Number(v) : fallback;
  }

  /** The index in `names` of the first token (or key value of `key`) that is one of them; −1 when none. */
  pick(names: readonly string[], key?: string): number {
    if (key && this.kv[key] != null) {
      const k = this.kv[key];
      const i = names.indexOf(k);
      if (i >= 0) return i;
      if (/^\d+$/.test(k)) return Number(k) < names.length ? Number(k) : -1;
    }
    for (const t of this.tokens) {
      const i = names.indexOf(t);
      if (i >= 0) return i;
    }
    return -1;
  }

  /** A dye colour 0..15 from "key:colour", else the first bare dye token; −1 when none. */
  dye(key?: string): number {
    if (key && this.kv[key] != null) return dyeOf(this.kv[key]);
    for (const t of this.tokens) {
      const d = dyeOf(t);
      if (d >= 0 && !/^\d+$/.test(t)) return d;
    }
    return -1;
  }
}
