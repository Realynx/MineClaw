import { describe, expect, it } from 'vitest';
import { keyAction } from '@/features/story/tools';

/** Every key code the Story map reacts to, plus neighbours that must stay unbound. */
const CODES = [
  ...'ABCDEFGHIJKLMNOPQRSTUVWXYZ'.split('').map((c) => `Key${c}`),
  ...'0123456789'.split('').map((d) => `Digit${d}`),
  ...'0123456789'.split('').map((d) => `Numpad${d}`),
  'Space',
  'Escape',
  'Enter',
  'Tab',
  'BracketLeft',
  'BracketRight',
  'Equal',
  'Minus',
  'NumpadAdd',
  'NumpadSubtract',
  'Slash',
  'Home',
  'End',
  'PageUp',
  'PageDown',
  'ArrowUp',
  'ArrowDown',
  'ArrowLeft',
  'ArrowRight',
  'F11',
  'Backquote',
];

const MODS = ['shiftKey', 'ctrlKey', 'metaKey', 'altKey'] as const;
const FLAGS = ['buildTool', 'heightTool', 'materialTool', 'speedKeys'] as const;

/** One letter per set bit, e.g. 0b0101 over MODS → "S·M·" style tags ("-" when none). */
function tag(bits: number, names: readonly string[]): string {
  const on = names.filter((_, i) => bits & (1 << i)).map((n) => n.replace(/Key$/, ''));
  return on.length ? on.join('+') : '-';
}

describe('keyAction (golden: every key × modifier × tool state)', () => {
  it('maps every key exactly as before', () => {
    const lines: string[] = [];
    for (let f = 0; f < 1 << FLAGS.length; f++) {
      const args = FLAGS.map((_, i) => !!(f & (1 << i))) as [boolean, boolean, boolean, boolean];
      lines.push(`# ${tag(f, FLAGS)}`);
      for (const code of CODES) {
        const out: string[] = [];
        for (let m = 0; m < 1 << MODS.length; m++) {
          const e: Parameters<typeof keyAction>[0] = { code, key: code };
          MODS.forEach((k, i) => {
            if (m & (1 << i)) e[k] = true;
          });
          const a = keyAction(e, ...args);
          if (a) out.push(`${tag(m, MODS)}=${a}`);
        }
        if (out.length) lines.push(`${code}: ${out.join(' ')}`);
      }
    }
    expect(lines.join('\n')).toMatchSnapshot();
  });
});
