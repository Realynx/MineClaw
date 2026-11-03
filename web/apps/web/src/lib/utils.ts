import { toNumber, type Double } from '@mineclaw/contracts';

/** Joins class names, skipping falsy values. */
export function cn(...parts: Array<string | false | null | undefined | 0>): string {
  return parts.filter(Boolean).join(' ');
}

const rtf = new Intl.RelativeTimeFormat('en', { numeric: 'auto', style: 'short' });

/** Milliseconds from an ISO string, unix ms number, or null. */
export function toMs(t: string | number | null | undefined): number | null {
  if (t === null || t === undefined || t === '') return null;
  if (typeof t === 'number') return t;
  const ms = Date.parse(t);
  return Number.isNaN(ms) ? null : ms;
}

/** "12 s ago", "3 min ago", "yesterday". */
export function ago(t: string | number | null | undefined, now = Date.now()): string {
  const ms = toMs(t);
  if (ms === null) return '—';
  const s = Math.round((ms - now) / 1000);
  const abs = Math.abs(s);
  if (abs < 5) return 'just now';
  if (abs < 60) return rtf.format(s, 'second');
  if (abs < 3600) return rtf.format(Math.round(s / 60), 'minute');
  if (abs < 86400) return rtf.format(Math.round(s / 3600), 'hour');
  if (abs < 86400 * 30) return rtf.format(Math.round(s / 86400), 'day');
  return new Date(ms).toLocaleDateString();
}

const dtf = new Intl.DateTimeFormat(undefined, {
  year: 'numeric',
  month: 'short',
  day: 'numeric',
  hour: '2-digit',
  minute: '2-digit',
});
const tf = new Intl.DateTimeFormat(undefined, { hour: '2-digit', minute: '2-digit', second: '2-digit' });

export function dateTime(t: string | number | null | undefined): string {
  const ms = toMs(t);
  return ms === null ? '—' : dtf.format(ms);
}

export function clock(t: string | number | null | undefined): string {
  const ms = toMs(t);
  return ms === null ? '—' : tf.format(ms);
}

/** "1.2 s", "3 min 4 s", "2 h 5 min". */
export function duration(seconds: number | null | undefined): string {
  if (seconds === null || seconds === undefined || !Number.isFinite(seconds)) return '—';
  if (seconds < 10) return `${seconds.toFixed(1)} s`;
  if (seconds < 60) return `${Math.round(seconds)} s`;
  const m = Math.floor(seconds / 60);
  if (m < 60) return `${m} min ${Math.round(seconds % 60)} s`;
  const h = Math.floor(m / 60);
  return `${h} h ${m % 60} min`;
}

const nf = new Intl.NumberFormat('en');
const cf = new Intl.NumberFormat('en', { notation: 'compact', maximumFractionDigits: 1 });

export function num(n: number | null | undefined): string {
  return n === null || n === undefined || !Number.isFinite(n) ? '—' : nf.format(n);
}

export function compact(n: number | null | undefined): string {
  return n === null || n === undefined || !Number.isFinite(n) ? '—' : cf.format(n);
}

export function usd(n: number | null | undefined): string {
  if (n === null || n === undefined || !Number.isFinite(n)) return '—';
  return n < 0.01 && n > 0 ? `$${n.toFixed(4)}` : `$${n.toFixed(2)}`;
}

export function bytes(n: number | null | undefined): string {
  if (n === null || n === undefined || !Number.isFinite(n)) return '—';
  const units = ['B', 'KB', 'MB', 'GB'];
  let v = n;
  let i = 0;
  while (v >= 1024 && i < units.length - 1) {
    v /= 1024;
    i++;
  }
  return `${v < 10 && i > 0 ? v.toFixed(1) : Math.round(v)} ${units[i]}`;
}

/** Percent text from 0..1 (a Double that may be NaN). */
export function pct(v: Double | number | null | undefined, digits = 0): string {
  const n = typeof v === 'number' ? v : toNumber(v);
  return Number.isFinite(n) ? `${(n * 100).toFixed(digits)}%` : '—';
}

export function dbl(v: Double | null | undefined): number {
  return toNumber(v);
}

export function fixed(v: Double | number | null | undefined, digits = 2): string {
  const n = typeof v === 'number' ? v : toNumber(v);
  return Number.isFinite(n) ? n.toFixed(digits) : '—';
}

export function dimName(dim: number | string | null | undefined): string {
  if (dim === -1 || dim === '-1' || dim === 'nether') return 'Nether';
  if (dim === 1 || dim === '1' || dim === 'end') return 'End';
  return 'Overworld';
}

export function clamp(v: number, lo: number, hi: number): number {
  return Math.min(hi, Math.max(lo, v));
}

/** A 32-hex request id (matches ChatRequest.requestId). */
export function newRequestId(): string {
  const c = globalThis.crypto;
  if (c && 'randomUUID' in c) return c.randomUUID().replace(/-/g, '');
  let s = '';
  for (let i = 0; i < 32; i++) s += Math.floor(Math.random() * 16).toString(16);
  return s;
}

export async function copyText(text: string): Promise<boolean> {
  try {
    await navigator.clipboard.writeText(text);
    return true;
  } catch {
    return false;
  }
}

/** "1 hit", "12 hits". */
export function plural(n: number, one: string, many = `${one}s`): string {
  return `${num(n)} ${n === 1 ? one : many}`;
}
