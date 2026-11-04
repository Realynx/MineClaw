/** Deterministic helpers for the mock fixtures (same data on every load, so screenshots are stable). */

export function rng(seed: number): () => number {
  let a = seed >>> 0;
  return () => {
    a = (a + 0x6d2b79f5) >>> 0;
    let t = a;
    t = Math.imul(t ^ (t >>> 15), t | 1);
    t ^= t + Math.imul(t ^ (t >>> 7), t | 61);
    return ((t ^ (t >>> 14)) >>> 0) / 4294967296;
  };
}

export function pick<T>(r: () => number, items: readonly T[]): T {
  return items[Math.floor(r() * items.length) % items.length] as T;
}

export function int(r: () => number, lo: number, hi: number): number {
  return lo + Math.floor(r() * (hi - lo + 1));
}

/** The mock's clock origin: fixtures are laid out relative to page load. */
export const T0 = Date.now();

export function iso(ms: number): string {
  return new Date(ms).toISOString().replace('Z', '+00:00');
}

export function minsAgo(m: number): number {
  return T0 - m * 60_000;
}

export function isoAgo(minutes: number): string {
  return iso(minsAgo(minutes));
}

export function nowIso(): string {
  return iso(Date.now());
}

/** A plain SVG data payload used for picture routes (no Mojang assets; a stylised isometric block sheet). */
export function blockSheetSvg(seed: number, w = 640, h = 400, title = ''): string {
  const r = rng(seed);
  const palette = ['#7c8a3b', '#8b6a3e', '#9aa0a6', '#6b7b8c', '#b8873a', '#5f8f5a', '#a85c3c', '#c9c2a3'];
  const cubes: string[] = [];
  const s = 22;
  const ox = w / 2;
  const oy = 80;
  for (let i = 0; i < 9; i++) {
    for (let j = 0; j < 9; j++) {
      const hgt = Math.max(0, Math.round((Math.sin(i * 0.7 + seed) + Math.cos(j * 0.6)) * 1.6 + r() * 1.5));
      for (let k = 0; k <= hgt; k++) {
        const x = ox + (i - j) * s;
        const y = oy + (i + j) * s * 0.5 - k * s * 0.6;
        const c = k === hgt ? pick(r, palette) : '#6f6a5f';
        cubes.push(
          `<g><path d="M${x} ${y} l${s} ${s * 0.5} l-${s} ${s * 0.5} l-${s} -${s * 0.5}z" fill="${c}"/>` +
            `<path d="M${x - s} ${y + s * 0.5} l${s} ${s * 0.5} v${s * 0.6} l-${s} -${s * 0.5}z" fill="${c}" opacity=".78"/>` +
            `<path d="M${x + s} ${y + s * 0.5} l-${s} ${s * 0.5} v${s * 0.6} l${s} -${s * 0.5}z" fill="${c}" opacity=".62"/></g>`,
        );
      }
    }
  }
  const label = title
    ? `<text x="16" y="${h - 16}" font-family="monospace" font-size="13" fill="#e6e6e6">${title.replace(/[<&]/g, '')}</text>`
    : '';
  return `<svg xmlns="http://www.w3.org/2000/svg" width="${w}" height="${h}" viewBox="0 0 ${w} ${h}"><rect width="100%" height="100%" fill="#1d2430"/>${cubes.join('')}${label}</svg>`;
}

/** The LCS lengths of every pair of suffixes of `a` and `b`. */
function lcsTable(a: string[], b: string[]): number[][] {
  const n = a.length;
  const m = b.length;
  const dp: number[][] = Array.from({ length: n + 1 }, () => new Array<number>(m + 1).fill(0));
  for (let i = n - 1; i >= 0; i--)
    for (let j = m - 1; j >= 0; j--) dp[i]![j] = a[i] === b[j] ? dp[i + 1]![j + 1]! + 1 : Math.max(dp[i + 1]![j]!, dp[i]![j + 1]!);
  return dp;
}

/** Minimal LCS line diff (mock stand-in for the bridge's ScriptDiff). */
export function lineDiff(a: string[], b: string[]): Array<{ op: ' ' | '+' | '-'; text: string; aLine: number | null; bLine: number | null }> {
  const n = a.length;
  const m = b.length;
  const dp = lcsTable(a, b);
  const out: Array<{ op: ' ' | '+' | '-'; text: string; aLine: number | null; bLine: number | null }> = [];
  let i = 0;
  let j = 0;
  while (i < n || j < m) {
    if (i < n && j < m && a[i] === b[j]) {
      out.push({ op: ' ', text: a[i]!, aLine: i + 1, bLine: j + 1 });
      i++;
      j++;
    } else if (j < m && (i >= n || dp[i]![j + 1]! >= dp[i + 1]![j]!)) {
      out.push({ op: '+', text: b[j]!, aLine: null, bLine: j + 1 });
      j++;
    } else {
      out.push({ op: '-', text: a[i]!, aLine: i + 1, bLine: null });
      i++;
    }
  }
  return out;
}
