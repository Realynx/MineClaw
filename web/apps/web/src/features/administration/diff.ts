/**
 * The "Review changes" diff: what a save would send, one row per drafted key, with secrets shown only as changed / cleared.
 */
import type { AdminSetting, SettingChange, SettingValue } from '@mineclaw/contracts';
import { hasDraft, type Draft } from './fieldState';

export type DiffKind = 'change' | 'reset' | 'secret-changed' | 'secret-cleared' | 'secret-reset';

export interface DiffRow {
  key: string;
  label: string;
  group: string;
  section: string;
  apply: AdminSetting['apply'];
  kind: DiffKind;
  from: string;
  to: string;
}

/** A value as one line of text ("(empty)", "on/off", lists comma separated, "(inherit)" for null). */
export function formatValue(v: SettingValue | undefined, s?: Pick<AdminSetting, 'type' | 'unit' | 'nullable'>): string {
  if (v === undefined || v === null) return s?.nullable ? '(inherit)' : '(unset)';
  if (typeof v === 'boolean') return v ? 'on' : 'off';
  if (Array.isArray(v)) return v.length ? v.join(', ') : '(none)';
  if (v === '') return '(empty)';
  return `${String(v)}${unitOf(v, s)}`;
}

/** " ms" & co after a number when the setting has a unit. */
function unitOf(v: SettingValue, s?: Pick<AdminSetting, 'unit'>): string {
  return s?.unit && typeof v === 'number' ? ` ${s.unit}` : '';
}

function same(a: SettingValue | undefined, b: SettingValue | undefined): boolean {
  return JSON.stringify(a ?? null) === JSON.stringify(b ?? null);
}

/** The drafted keys that really change something (a value equal to the running one, not overridden, is dropped). */
export function buildDiff(all: readonly AdminSetting[], draft: Draft): DiffRow[] {
  const rows: DiffRow[] = [];
  for (const s of all) {
    if (!hasDraft(draft, s.key)) continue;
    const v = draft[s.key] ?? null;
    const base = { key: s.key, label: s.label, group: s.group, section: s.section, apply: s.apply };
    const change = s.secret ? secretChange(s, v) : valueChange(s, v);
    if (change) rows.push({ ...base, ...change });
  }
  return rows;
}

type Change = Pick<DiffRow, 'kind' | 'from' | 'to'> | null;

/** A secret shows only set / changed / cleared; a reset of a secret without an override, or clearing an unset one, is no change. */
function secretChange(s: AdminSetting, v: SettingValue): Change {
  const set = !!s.secretState?.set;
  const was = set ? 'set' : 'not set';
  if (v === null) return s.overridden ? { kind: 'secret-reset', from: was, to: 'base config' } : null;
  if (v === '') return set ? { kind: 'secret-cleared', from: was, to: 'cleared' } : null;
  return { kind: 'secret-changed', from: was, to: 'changed' };
}

/** A reset of an overridden value, or a value different from the running one. */
function valueChange(s: AdminSetting, v: SettingValue): Change {
  if (v === null) return s.overridden ? { kind: 'reset', from: formatValue(s.value, s), to: 'base config' } : null;
  if (same(v, s.value)) return null;
  return { kind: 'change', from: formatValue(s.value, s), to: formatValue(v, s) };
}

/** The save body's changes, in diff order. */
export function toChanges(rows: readonly DiffRow[], draft: Draft): SettingChange[] {
  return rows.map((r) => ({ key: r.key, value: draft[r.key] ?? null }));
}

/** The draft minus the keys a save took (the rest stay for the user to fix). */
export function withoutKeys(draft: Draft, keys: readonly string[]): Draft {
  const out: Record<string, SettingValue> = { ...draft };
  for (const k of keys) delete out[k];
  return out;
}

export const MIN_REASON = 3;

export function reasonError(reason: string): string | null {
  const t = reason.trim();
  if (t.length < MIN_REASON) return `Say why (at least ${MIN_REASON} characters)`;
  if (t.length > 300) return 'At most 300 characters';
  return null;
}
