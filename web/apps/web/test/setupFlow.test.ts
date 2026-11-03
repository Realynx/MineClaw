import { PASSWORD_MIN_LENGTH, SetupRequest } from '@mineclaw/contracts';
import { describe, expect, it } from 'vitest';
import { FIRST_ACCOUNT_ROLES, passwordStrength } from '@/lib/setupFlow';

describe('passwordStrength', () => {
  it('refuses anything under the minimum length and says how much is missing', () => {
    const s = passwordStrength('a'.repeat(PASSWORD_MIN_LENGTH - 3));
    expect(s.score).toBe(0);
    expect(s.hint).toMatch(/^3 more characters/);
  });

  it('ranks long passphrases high and short single-class ones low', () => {
    expect(passwordStrength('lantern orbit pickle seventy').score).toBe(4);
    expect(passwordStrength('zqxwvrtpmnbk').score).toBeLessThanOrEqual(2);
    expect(passwordStrength('Tr0ub4dor&3x!').score).toBeGreaterThanOrEqual(3);
  });

  it('penalises common words, the username or player, repeats and runs', () => {
    const base = passwordStrength('violet harbor kite').score;
    expect(passwordStrength('minecraft harbor kite').score).toBeLessThan(base);
    expect(passwordStrength('poofimafox harbor', ['fox', 'poofimafox']).hint).toMatch(/name/);
    expect(passwordStrength('aaaaaaaaaaaaaaaa').score).toBe(1);
    expect(passwordStrength('abcd1234wxyz').hint).toMatch(/runs/);
  });
});

describe('the setup form defaults satisfy the contract', () => {
  it('preset roles + a valid account parse as a SetupRequest (username normalised to lower case)', () => {
    const r = SetupRequest.parse({ username: ' Fox ', password: 'x'.repeat(PASSWORD_MIN_LENGTH), player: 'poofimafox', roles: [...FIRST_ACCOUNT_ROLES] });
    expect(r.username).toBe('fox');
    expect(SetupRequest.safeParse({ username: 'fox', password: 'short', roles: ['admin'] }).success).toBe(false);
  });
});
