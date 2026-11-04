import { describe, expect, it } from 'vitest';
import { passwordStrength } from '@/lib/setupFlow';

const PASSWORDS = [
  '',
  'a',
  'abcdefghij',
  'zqxwvrtpmnb',
  'abcdefghijkl',
  'password1234',
  'Password1234!',
  'zqxwvrtpmnbk',
  'lantern orbit pickle seventy',
  'aaaaaaaaaaaaaaaa',
  'fox-the-builder-77',
  'poofimafox rules',
  'Kiwi loves melons 99!',
  'qwertyuiop12',
  'Tr0ub4dor&3xyz',
  'correct horse battery staple',
  '9876543210ab',
  'ÄÖÜäöüßéèêë1',
  '            ',
  'aB3$aB3$aB3$aB3$',
];
const CONTEXTS: Array<ReadonlyArray<string | null | undefined>> = [[], ['fox', null, 'poofimafox'], ['kiwi', undefined, 'ab']];

describe('password strength (golden, pinned before it became tables)', () => {
  it('rates every password the same', () => {
    const out = PASSWORDS.flatMap((pw) => CONTEXTS.map((c) => `${JSON.stringify(pw)} ${JSON.stringify(c)} → ${JSON.stringify(passwordStrength(pw, c))}`));
    expect(out).toMatchSnapshot();
  });
});
