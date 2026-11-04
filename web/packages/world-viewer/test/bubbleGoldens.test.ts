// Behaviour pin for the complexity refactor of render/bubbles.ts (docs/dev/complexity.md, WP12): seeded texts through
// wrapText, seeded say / prune sequences through ChatBubbles, seeded crowds through findSpeaker, and a seeded chat over
// bots and players drawn by BubbleLayer.showFor in several views and modes (a stand-in canvas that measures text by its
// length). The report, every sprite's state and the canvas calls are hashed; the digests were taken from the code before
// it was reshaped.

import * as THREE from 'three';
import { afterAll, beforeAll, describe, expect, it } from 'vitest';
import { BubbleLayer, ChatBubbles, findSpeaker, wrapText, type BubbleKind, type BubbleMode, type SpeakerBot, type SpeakerEntity } from '../src/render/bubbles';
import { Digest, rng } from './digest';

const WORDS = ['hi', 'the', 'generator', 'is', 'steady', '§cred§r', 'supercalifragilisticexpialidocious', 'ab', 'x', '\u{1F600}\u{1F600}', 'tab\there', 'new\nline', 'antidisestablishmentarianism-and-more', '   ', 'é中', 'a.b.c.d'];
function randomText(r: () => number): string {
  const n = Math.floor(r() * 30);
  const out: string[] = [];
  for (let i = 0; i < n; i++) out.push(WORDS[Math.floor(r() * WORDS.length)]);
  return out.join(r() < 0.8 ? ' ' : '  ');
}

const NAMES = ['maid_chan', 'Maid_Chan', 'main', 'MAIN', 'poofimafox', ' kiwi ', 'ghost', '', '  ', 'hive_2', 'Hive_2', 'zombie'];
const KINDS: BubbleKind[] = ['chat', 'said', 'whisper', 'viewer'];

describe('bubble goldens', () => {
  it('wrapText', () => {
    const d = new Digest();
    const r = rng(31);
    for (let k = 0; k < 4000; k++) {
      const text = randomText(r);
      d.add(JSON.stringify(wrapText(text)));
      d.add(JSON.stringify(wrapText(text, 1 + Math.floor(r() * 40), Math.floor(r() * 5))));
    }
    expect(d.hex()).toBe('257a2a6e86c120b56d116e4797ecb1b2');
  });

  it('ChatBubbles say / prune', () => {
    const d = new Digest();
    const r = rng(32);
    const b = new ChatBubbles();
    let base = -1;
    b.onDrop = (m) => d.add(`drop ${m.id - base} ${m.speaker}`);
    let now = 0;
    for (let k = 0; k < 3000; k++) {
      now += Math.floor(r() * 900);
      const name = r() < 0.3 ? `p${Math.floor(r() * 60)}` : NAMES[Math.floor(r() * NAMES.length)];
      const text = r() < 0.4 ? ['hello', 'hello', 'Hello', '§ahello'][Math.floor(r() * 4)] : randomText(r);
      const m = b.say(name, text, KINDS[Math.floor(r() * 4)], now);
      if (m && base < 0) base = m.id - 1;
      d.add(m ? `${m.id - base}|${m.speaker}|${m.text}|${m.lines.join('/')}|${m.kind}|${m.at}` : 'null');
      if (k % 13 === 0) b.prune(now);
      if (k % 97 === 0) d.add([...b.speakers()].map((s) => `${s}:${b.messages(s).length}`).join(','));
      if (k === 2000) b.clear();
    }
    b.onDrop = null;
    for (let k = 0; k < 20; k++) b.say(`q${k % 5}`, `line ${k}`, 'chat', now + k);
    d.add(String(b.size));
    expect(d.hex()).toBe('84329c410aaf37ebd462bcba573d926a');
  });

  it('findSpeaker', () => {
    const d = new Digest();
    const r = rng(33);
    for (let k = 0; k < 3000; k++) {
      const bots: SpeakerBot[] = [];
      for (let j = Math.floor(r() * 4); j > 0; j--)
        bots.push({ name: NAMES[Math.floor(r() * NAMES.length)], dim: j % 2, x: j, y: 64, z: -j, dto: { short: r() < 0.5 ? NAMES[Math.floor(r() * NAMES.length)] : r() < 0.5 ? null : undefined } });
      const ents: SpeakerEntity[] = [];
      for (let j = Math.floor(r() * 4); j > 0; j--)
        ents.push({ key: `0:${j}`, kind: r() < 0.7 ? 'player' : 'mob', name: r() < 0.8 ? NAMES[Math.floor(r() * NAMES.length)] : null, dim: 0, x: j * 2, y: 70, z: j });
      d.add(JSON.stringify(findSpeaker(NAMES[Math.floor(r() * NAMES.length)], bots, ents)));
    }
    expect(d.hex()).toBe('7138444cb97905b796619167625519d8');
  });

  describe('BubbleLayer.showFor', () => {
    const g = globalThis as unknown as { document?: unknown };
    let had: unknown;
    let calls: string[] = [];
    beforeAll(() => {
      had = g.document;
      const ctx = new Proxy({} as Record<string, unknown>, {
        get: (t, p: string) => {
          if (p === 'measureText') return (s: string) => ({ width: [...s].length * 11.3 });
          if (p in t) return t[p];
          return (...a: unknown[]) => calls.push(`${p}(${a.map((x) => (typeof x === 'number' ? x.toFixed(3) : String(x))).join(',')})`);
        },
        set: (t, p: string, v) => {
          t[p] = v;
          calls.push(`${p}=${String(v)}`);
          return true;
        },
      });
      g.document = { createElement: () => ({ width: 0, height: 0, getContext: () => ctx }) };
    });
    afterAll(() => {
      g.document = had;
    });

    it('views, modes and stacks', () => {
      const d = new Digest();
      const r = rng(34);
      const l = new BubbleLayer();
      const texIds = new Map<THREE.Texture, number>();
      const tex = (t: THREE.Texture | null) => {
        if (!t) return -1;
        if (!texIds.has(t)) texIds.set(t, texIds.size);
        return texIds.get(t);
      };
      const bots: SpeakerBot[] = [
        { name: 'maid_chan', dim: 0, x: 0, y: 64, z: 0, dto: { short: 'main' } },
        { name: 'hive_2', dim: 0, x: 6, y: 66, z: -12, dto: {} },
        { name: 'deep', dim: -1, x: 1, y: 30, z: 1, dto: { short: null } },
      ];
      const ents: SpeakerEntity[] = [
        { key: '0:7', kind: 'player', name: 'poofimafox', dim: 0, x: -5, y: 64, z: -3 },
        { key: '0:8', kind: 'player', name: 'kiwi', dim: 0, x: 30, y: 64, z: -2 },
        { key: '0:9', kind: 'zombie', name: 'zed', dim: 0, x: 2, y: 64, z: -2 },
      ];
      const speakers = ['maid_chan', 'main', 'hive_2', 'deep', 'poofimafox', 'kiwi', 'zed', 'nobody'];
      const modes: BubbleMode[] = ['full', 'full', 'compact', 'off'];
      let now = 0;
      for (let frame = 0; frame < 400; frame++) {
        now += Math.floor(r() * 400);
        for (let j = Math.floor(r() * 3); j > 0; j--) l.say(speakers[Math.floor(r() * speakers.length)], randomText(r) || 'ok', KINDS[Math.floor(r() * 4)], now - Math.floor(r() * 200));
        l.update(now, bots, ents);
        for (let view = 0; view < 3; view++) {
          const cam = new THREE.PerspectiveCamera(50 + r() * 40, 0.8 + r(), 0.05, 400);
          cam.position.set((r() - 0.5) * 30, 64 + r() * 30, 10 + r() * 30);
          cam.lookAt((r() - 0.5) * 10, 64, (r() - 0.5) * 10);
          const lift = r();
          l.showFor(
            {
              dim: r() < 0.9 ? 0 : -1,
              camera: cam,
              height: 400 + Math.floor(r() * 800),
              pxScale: r() < 0.1 ? 0 : 0.5 + r() * 2,
              mode: modes[Math.floor(r() * modes.length)],
              bots: r() < 0.85,
              players: r() < 0.85,
              labelLift: r() < 0.3 ? undefined : r() < 0.5 ? () => lift : (key, bot) => (bot ? lift : key.length * 0.01),
            },
            view,
          );
          d.add(JSON.stringify(l.shownIn(view)));
          d.add(String(l.group.visible));
          for (const c of l.group.children as THREE.Sprite[]) {
            const m = c.material as THREE.SpriteMaterial;
            d.add(`${c.visible}|${c.position.toArray()}|${c.scale.toArray()}|${c.center.toArray()}|${m.opacity}|${tex(m.map)}|${m.version}`);
          }
        }
        d.add(String(l.textures));
        if (frame % 50 === 0) {
          d.add(calls.join(';'));
          calls = [];
        }
        if (frame === 300) l.clear();
      }
      d.add(calls.join(';'));
      l.dispose();
      expect(d.hex()).toBe('6959bc2257997d3483fb3d8c3bba9f60');
    });
  });
});
