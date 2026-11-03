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
  { role: 'story', label: 'Story Mode', hint: 'run colonies: orders, work, schedules, builds (Admin can too)' },
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
  let pool = 0;
  if (/[a-z]/.test(pw)) pool += 26;
  if (/[A-Z]/.test(pw)) pool += 26;
  if (/\d/.test(pw)) pool += 10;
  if (/[^A-Za-z0-9\s]/.test(pw)) pool += 33;
  if (/\s/.test(pw)) pool += 1;
  const unique = new Set(pw).size;
  // Repeated characters add little: count length as if each distinct character appeared at most ~3 times.
  const effective = Math.min(pw.length, unique * 3);
  let bits = effective * Math.log2(Math.max(pool, 2));

  const lower = pw.toLowerCase();
  const weak: string[] = [];
  const words = [...COMMON, ...context.filter((c): c is string => !!c && c.length >= 3).map((c) => c.toLowerCase())];
  if (words.some((w) => lower.includes(w))) {
    bits -= 20;
    weak.push('avoid your name and common words');
  }
  if (/(.)\1\1/.test(pw)) {
    bits -= 10;
    weak.push('avoid repeated characters');
  }
  if (hasRun(pw)) {
    bits -= 10;
    weak.push('avoid runs like abcd or 1234');
  }

  const score: Strength['score'] = bits < 45 ? 1 : bits < 60 ? 2 : bits < 80 ? 3 : 4;
  const label = ['Too short', 'Weak', 'Fair', 'Good', 'Strong'][score]!;
  const hint =
    weak.length > 0
      ? `${weak[0]![0]!.toUpperCase()}${weak[0]!.slice(1)}.`
      : score >= 4
        ? 'A strong password.'
        : 'Longer is stronger: a few unrelated words work well.';
  return { score, label, hint };
}
