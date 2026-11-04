/**
 * Administration form state, as plain functions (tested in test/administration.test.ts):
 * - the draft: setting key → new value. A key absent = unchanged; `null` = reset (drop the override, back to the base config);
 *   for a secret, a string is the new text ("" = clear it).
 * - validation from the setting's type, bounds and options (the bot validates again, all-or-nothing);
 * - a secret field's display state, which never shows more than the server's "…last4" hint.
 */
import type { AdminSetting, Me, SettingValue } from '@mineclaw/contracts';

export type Draft = Readonly<Record<string, SettingValue>>;

export function hasDraft(draft: Draft, key: string): boolean {
  return Object.prototype.hasOwnProperty.call(draft, key);
}

/** The value a field shows: the draft when edited, else the running value (secrets never have one). */
export function shownValue(s: AdminSetting, draft: Draft): SettingValue {
  if (hasDraft(draft, s.key)) {
    const v = draft[s.key] ?? null;
    // A reset shows the default (the base config's value is not known separately; the bot reports it after the save).
    return v === null && !s.secret ? (s.default ?? null) : v;
  }
  return s.secret ? null : (s.value ?? null);
}

/** The provider a model field follows: the drafted provider, else the running one (anthropic by default). */
export function providerOf(s: AdminSetting, all: readonly AdminSetting[], draft: Draft): string {
  if (!s.providerKey) return 'anthropic';
  const p = all.find((x) => x.key === s.providerKey);
  const v = hasDraft(draft, s.providerKey) ? draft[s.providerKey] : p?.value;
  return typeof v === 'string' && v ? v : 'anthropic';
}

export function isHttpUrl(v: string): boolean {
  try {
    const u = new URL(v);
    return (u.protocol === 'http:' || u.protocol === 'https:') && !!u.hostname;
  } catch {
    return false;
  }
}

/**
 * Client-side check of one drafted value (null = fine). `null` (reset) is always allowed; the server has the final word.
 * `allowedClaude` is the bot's startup 5.5-generation set.
 */
export function validateValue(
  s: AdminSetting,
  v: SettingValue,
  ctx: { provider?: string; allowedClaude?: readonly string[] } = {},
): string | null {
  if (v === null) return null;
  if (!s.editable) return 'read-only';
  const rule = Object.hasOwn(VALUE_RULES, s.type) ? VALUE_RULES[s.type] : undefined;
  return (rule ?? textRule)(s, v, ctx);
}

type ValueRule = (
  s: AdminSetting,
  v: Exclude<SettingValue, null>,
  ctx: { provider?: string; allowedClaude?: readonly string[] },
) => string | null;

/** A number inside the setting's min / max (either may be unset). */
function bounds(s: AdminSetting, n: number): string | null {
  if (s.min !== null && s.min !== undefined && n < s.min) return `at least ${s.min}`;
  if (s.max !== null && s.max !== undefined && n > s.max) return `at most ${s.max}`;
  return null;
}

const isFiniteNumber = (v: unknown): v is number => typeof v === 'number' && Number.isFinite(v);

const textRule: ValueRule = (_s, v) => (typeof v !== 'string' ? 'text' : null);

function modelRule(...[s, v, ctx]: Parameters<ValueRule>): string | null {
  if (typeof v !== 'string' || !v.trim()) return 'pick a model';
  const provider = ctx.provider ?? 'anthropic';
  const allowed = ctx.allowedClaude ?? s.options ?? [];
  if (provider === 'anthropic' && !allowed.includes(v)) return `Claude models must be 5.5-generation: ${allowed.join(', ')}`;
  return null;
}

/** The client-side rule per setting type; any other type takes text. */
const VALUE_RULES: Record<string, ValueRule> = {
  bool: (_s, v) => (typeof v === 'boolean' ? null : 'on or off'),
  int: (s, v) => {
    if (!isFiniteNumber(v)) return 'a number';
    return Number.isInteger(v) ? bounds(s, v) : 'a whole number';
  },
  number: (s, v) => (isFiniteNumber(v) ? bounds(s, v) : 'a number'),
  enum: (s, v) =>
    typeof v === 'string' && (s.options ?? []).includes(v)
      ? null
      : `one of ${(s.options ?? []).map((o) => o || '(none)').join(', ')}`,
  url: (_s, v) => {
    if (typeof v !== 'string') return 'a URL';
    return v === '' || isHttpUrl(v) ? null : 'an http:// or https:// URL';
  },
  list: (_s, v) => {
    if (!Array.isArray(v)) return 'a list';
    return v.some((x) => !x.trim()) ? 'no empty entries' : null;
  },
  model: modelRule,
  secret: (_s, v) => {
    if (typeof v !== 'string') return 'text';
    return v.length > 400 ? 'at most 400 characters' : null;
  },
};

/** An editable, untouched model field whose provider is drafted. */
function providerSwitched(s: AdminSetting, draft: Draft): boolean {
  if (s.type !== 'model' || !s.providerKey || !s.editable) return false;
  return hasDraft(draft, s.providerKey) && !hasDraft(draft, s.key);
}

/** Every drafted key's client-side error. */
export function validateDraft(
  all: readonly AdminSetting[],
  draft: Draft,
  allowedClaude: readonly string[],
): Record<string, string> {
  const out: Record<string, string> = {};
  for (const [key, v] of Object.entries(draft)) {
    const s = all.find((x) => x.key === key);
    if (!s) {
      out[key] = 'unknown setting';
      continue;
    }
    const err = validateValue(s, v, { provider: providerOf(s, all, draft), allowedClaude });
    if (err) out[key] = err;
  }
  // A provider switch revalidates its model even when the model itself was not touched.
  for (const s of all) {
    if (!providerSwitched(s, draft)) continue;
    const err = validateValue(s, s.value ?? null, { provider: providerOf(s, all, draft), allowedClaude });
    if (err) out[s.key] = `${err} (the provider changed: pick a model)`;
  }
  return out;
}

/** Server 400 `details` → errors by setting key (`path` is a key, or `changes.N.…` for the Nth change). */
export function serverErrors(
  details: ReadonlyArray<{ path: string; message: string }> | undefined,
  sentKeys: readonly string[],
): Record<string, string> {
  const out: Record<string, string> = {};
  for (const d of details ?? []) {
    const m = /^changes\.(\d+)/.exec(d.path);
    const key = m ? sentKeys[Number(m[1])] : d.path;
    if (key) out[key] = out[key] ? `${out[key]}; ${d.message}` : d.message;
  }
  return out;
}

export type SecretMode = 'set' | 'unset' | 'replacing' | 'clearing' | 'reset';

export interface SecretView {
  mode: SecretMode;
  /** "Set — ends in ab12", "Not set", "New key entered", "Will be cleared", "Back to the base config". */
  text: string;
}

/** What a write-only secret field shows. It never reveals more than the server's hint. */
export function secretView(s: AdminSetting, draft: Draft): SecretView {
  if (hasDraft(draft, s.key)) {
    const v = draft[s.key];
    if (v === null) return { mode: 'reset', text: 'Back to the base config on save' };
    if (v === '') return { mode: 'clearing', text: 'Will be cleared on save' };
    return { mode: 'replacing', text: 'New value entered (hidden)' };
  }
  const st = s.secretState;
  if (st?.set) {
    const tail = st.hint ? st.hint.replace(/^…/, '') : null;
    return { mode: 'set', text: tail ? `Set — ends in ${tail}` : 'Set' };
  }
  return { mode: 'unset', text: 'Not set' };
}

/** A step-up younger than its expiry (the server is the judge; this only avoids a needless 403 round trip). */
export function stepUpFresh(me: Pick<Me, 'stepUpUntil'> | null | undefined, now = Date.now()): boolean {
  const until = me?.stepUpUntil ? Date.parse(me.stepUpUntil) : NaN;
  return Number.isFinite(until) && until - 5_000 > now;
}

/** Parses a list field's text (one per line or comma separated). */
export function parseList(text: string): string[] {
  return text
    .split(/[\n,]/)
    .map((x) => x.trim())
    .filter((x) => x.length > 0);
}

/** Parses a number field's text; '' or junk → NaN (validation reports it). */
export function parseNumber(text: string): number {
  return text.trim() === '' ? Number.NaN : Number(text);
}
