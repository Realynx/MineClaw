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
  const unit = s?.unit && typeof v === 'number' ? ` ${s.unit}` : '';
  return `${String(v)}${unit}`;
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
    if (s.secret) {
      const was = s.secretState?.set ? 'set' : 'not set';
      if (v === null) {
        if (!s.overridden) continue;
        rows.push({ ...base, kind: 'secret-reset', from: was, to: 'base config' });
      } else if (v === '') {
        if (!s.secretState?.set) continue;
        rows.push({ ...base, kind: 'secret-cleared', from: was, to: 'cleared' });
      } else rows.push({ ...base, kind: 'secret-changed', from: was, to: 'changed' });
      continue;
    }
    if (v === null) {
      if (!s.overridden) continue;
      rows.push({ ...base, kind: 'reset', from: formatValue(s.value, s), to: 'base config' });
      continue;
    }
    if (same(v, s.value)) continue;
    rows.push({ ...base, kind: 'change', from: formatValue(s.value, s), to: formatValue(v, s) });
  }
  return rows;
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
