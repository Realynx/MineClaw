/**
 * First-run setup helpers (the `/setup` page): the password strength hint and the role presets. The server enforces the
 * real rules (contracts `SetupRequest`); this only guides the person typing.
 */
import { PASSWORD_MIN_LENGTH, type Role } from '@mineclaw/contracts';
import { api } from './api/client';
import { qk } from './api/query';

export const setupKey = qk('setup.status');

/** Whether the app is in first-run setup mode (no users yet). Errors count as "no": the login page stays usable. */
export async function setupNeeded(): Promise<boolean> {
  try {
    return (await api('setup.status')).needed;
  } catch {
    return false;
  }
}

export const ROLE_INFO: ReadonlyArray<{ role: Role; label: string; hint: string }> = [
  { role: 'admin', label: 'Admin', hint: 'memory edits, rebuilds, backups, shard import, the audit log' },
  { role: 'chat', label: 'Chat', hint: 'talk to the bots as the linked player' },
  { role: 'commands', label: 'Commands', hint: 'your chat lines may arm command grants (with Chat)' },
  { role: 'story', label: 'Controller', hint: 'run colonies: orders, work, schedules, builds (Admin can too)' },
  { role: 'design', label: 'Design Studio', hint: 'co-design builds with a model and save them (Admin can too)' },
  { role: 'viewer', label: 'Viewer', hint: 'read everything (every account can)' },
];

/** The first account gets everything; each role can be unticked. */
export const FIRST_ACCOUNT_ROLES: readonly Role[] = ['admin', 'chat', 'commands', 'story', 'viewer'];

export interface Strength {
  /** 0 = too short (refused), 1 weak, 2 fair, 3 good, 4 strong. */
  score: 0 | 1 | 2 | 3 | 4;
  label: string;
  hint: string;
}

const COMMON = ['password', 'passwort', 'qwerty', 'letmein', 'welcome', 'minecraft', 'mineclaw', 'admin', 'iloveyou', 'dragon', 'monkey'];
const SEQUENCES = ['abcdefghijklmnopqrstuvwxyz', 'qwertyuiopasdfghjklzxcvbnm', '01234567890'];

function hasRun(pw: string): boolean {
  const lower = pw.toLowerCase();
  for (const seq of SEQUENCES) {
    for (let i = 0; i + 4 <= seq.length; i++) {
      const part = seq.slice(i, i + 4);
      if (lower.includes(part) || lower.includes([...part].reverse().join(''))) return true;
    }
  }
  return false;
}

/**
 * A rough strength estimate: length × character variety, minus common words, the username or player, repeats and
 * keyboard runs. Long passphrases score well without symbols.
 */
export function passwordStrength(pw: string, context: ReadonlyArray<string | null | undefined> = []): Strength {
  if (pw.length < PASSWORD_MIN_LENGTH) {
    const left = PASSWORD_MIN_LENGTH - pw.length;
    return { score: 0, label: 'Too short', hint: `${left} more character${left === 1 ? '' : 's'} (at least ${PASSWORD_MIN_LENGTH})` };
  }
  const unique = new Set(pw).size;
  // Repeated characters add little: count length as if each distinct character appeared at most ~3 times.
  const effective = Math.min(pw.length, unique * 3);
  let bits = effective * Math.log2(Math.max(poolSize(pw), 2));
  const weak: string[] = [];
  for (const w of weaknesses(pw, context)) {
    bits -= w.bits;
    weak.push(w.hint);
  }
  const score = scoreOf(bits);
  const label = ['Too short', 'Weak', 'Fair', 'Good', 'Strong'][score]!;
  return { score, label, hint: hintOf(weak, score) };
}

/** Character classes and how many characters each adds to the guessing pool. */
const CHAR_POOLS: ReadonlyArray<[RegExp, number]> = [
  [/[a-z]/, 26],
  [/[A-Z]/, 26],
  [/\d/, 10],
  [/[^A-Za-z0-9\s]/, 33],
  [/\s/, 1],
];

function poolSize(pw: string): number {
  let pool = 0;
  for (const [re, n] of CHAR_POOLS) if (re.test(pw)) pool += n;
  return pool;
}

/** What weakens a password: common words or the user's names, repeated characters, keyboard runs (bits off, hint). */
function weaknesses(pw: string, context: ReadonlyArray<string | null | undefined>): Array<{ bits: number; hint: string }> {
  const lower = pw.toLowerCase();
  const words = [...COMMON, ...context.filter((c): c is string => !!c && c.length >= 3).map((c) => c.toLowerCase())];
  const checks: Array<[boolean, number, string]> = [
    [words.some((w) => lower.includes(w)), 20, 'avoid your name and common words'],
    [/(.)\1\1/.test(pw), 10, 'avoid repeated characters'],
    [hasRun(pw), 10, 'avoid runs like abcd or 1234'],
  ];
  return checks.filter(([hit]) => hit).map(([, bits, hint]) => ({ bits, hint }));
}

function scoreOf(bits: number): Strength['score'] {
  if (bits < 45) return 1;
  if (bits < 60) return 2;
  return bits < 80 ? 3 : 4;
}

function hintOf(weak: readonly string[], score: Strength['score']): string {
  if (weak.length > 0) return `${weak[0]![0]!.toUpperCase()}${weak[0]!.slice(1)}.`;
  return score >= 4 ? 'A strong password.' : 'Longer is stronger: a few unrelated words work well.';
}
