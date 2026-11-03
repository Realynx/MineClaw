import * as THREE from 'three';
import { afterAll, beforeAll, describe, expect, it } from 'vitest';
import { BUBBLE, BubbleLayer, ChatBubbles, TextureCache, bubbleAlpha, cleanText, findSpeaker, holdMs, linePx, stackOffsets, wrapText, type BubbleMessage } from '../src/render/bubbles';

describe('chat bubble text', () => {
  it('keeps short lines whole and strips § codes and extra whitespace', () => {
    expect(wrapText('hi   there')).toEqual(['hi there']);
    expect(cleanText('§ahello §lworld\n')).toBe('hello world');
    expect(wrapText('   ')).toEqual([]);
  });

  it('word-wraps at 32 characters a line', () => {
    const lines = wrapText('the quick brown fox jumps over the lazy dog and keeps running far away');
    expect(lines).toEqual(['the quick brown fox jumps over', 'the lazy dog and keeps running', 'far away']);
    for (const l of lines) expect(l.length).toBeLessThanOrEqual(BUBBLE.wrap);
  });

  it('breaks words longer than a line', () => {
    const lines = wrapText(`go ${'x'.repeat(40)}`);
    expect(lines[0]).toBe(`go ${'x'.repeat(29)}`);
    expect(lines[1]).toBe('x'.repeat(11));
  });

  it('stops at 3 lines with an ellipsis', () => {
    const text = Array.from({ length: 40 }, (_, i) => `word${i}`).join(' ');
    const lines = wrapText(text);
    expect(lines).toHaveLength(3);
    expect(lines[2]!.endsWith('…')).toBe(true);
    expect([...lines[2]!].length).toBeLessThanOrEqual(BUBBLE.wrap);
    // exactly three full lines: no ellipsis
    expect(wrapText('a'.repeat(32) + ' ' + 'b'.repeat(32) + ' ' + 'c'.repeat(32)).at(-1)).toBe('c'.repeat(32));
  });

  it('compact bubbles are one short line', () => {
    expect(wrapText('Hi poofimafox! The generator is steady', BUBBLE.compactWrap, 1)).toEqual(['Hi poofimafox! Th…']);
  });
});

describe('chat bubble timing', () => {
  it('holds 6 s plus 0.5 s per extra line, then fades over 0.5 s', () => {
    expect(holdMs(1)).toBe(6000);
    expect(holdMs(3)).toBe(7000);
    expect(bubbleAlpha(60, 1)).toBeCloseTo(0.5);
    expect(bubbleAlpha(3000, 1)).toBe(1);
    expect(bubbleAlpha(6000, 1)).toBe(1);
    expect(bubbleAlpha(6250, 1)).toBeCloseTo(0.5);
    expect(bubbleAlpha(6500, 1)).toBe(0);
    expect(bubbleAlpha(6500, 3)).toBe(1);
    expect(bubbleAlpha(7250, 3)).toBeCloseTo(0.5);
    expect(bubbleAlpha(-5, 1)).toBe(0);
  });
});

describe('chat bubble stacks', () => {
  it('keeps the last 3 lines per speaker, oldest first', () => {
    const c = new ChatBubbles();
    const dropped: BubbleMessage[] = [];
    c.onDrop = (m) => dropped.push(m);
    for (let i = 1; i <= 4; i++) c.say('Maid_Chan', `line ${i}`, 'said', i * 100);
    expect(c.messages('maid_chan').map((m) => m.text)).toEqual(['line 2', 'line 3', 'line 4']);
    expect(dropped.map((m) => m.text)).toEqual(['line 1']);
  });

  it('ignores the same line heard twice within 2 s', () => {
    const c = new ChatBubbles();
    expect(c.say('Bob', 'hello', 'chat', 0)).not.toBeNull();
    expect(c.say('bob', 'hello', 'chat', 1500)).toBeNull();
    expect(c.say('bob', 'hello', 'chat', 2500)).not.toBeNull();
    expect(c.messages('bob')).toHaveLength(2);
  });

  it('expires each line by its own time', () => {
    const c = new ChatBubbles();
    c.say('a', 'one', 'chat', 0);
    c.say('a', 'two', 'chat', 3000);
    c.prune(6400);
    expect(c.messages('a')).toHaveLength(2);
    c.prune(6600);
    expect(c.messages('a').map((m) => m.text)).toEqual(['two']);
    c.prune(9600);
    expect(c.size).toBe(0);
  });

  it('tracks a bounded number of speakers', () => {
    const c = new ChatBubbles();
    for (let i = 0; i < BUBBLE.maxSpeakers + 5; i++) c.say(`p${i}`, 'hi', 'chat', i);
    expect(c.size).toBe(BUBBLE.maxSpeakers);
    expect(c.messages('p0')).toHaveLength(0);
  });

  it('stacks upward from the base, each a gap above the one below', () => {
    expect(stackOffsets([1, 2, 1.5], 0.5, 0.1)).toEqual([0.5, 1.6, 3.7]);
  });

  it('sizes lines by distance, clamped for readability', () => {
    expect(linePx(2, 'full')).toBe(22);
    expect(linePx(12, 'full')).toBeCloseTo(18.33, 1);
    expect(linePx(200, 'full')).toBe(14);
    expect(linePx(2, 'compact')).toBe(10);
  });
});

describe('speakers', () => {
  const bots = [
    { name: 'maid_chan', dim: 0, x: 1, y: 64, z: 2, dto: { short: 'main' } },
    { name: 'maid_chan_109', dim: 0, x: 5, y: 64, z: 5, dto: { short: '109' } },
  ];
  const ents = [
    { key: '0:300', kind: 'player', name: 'poofimafox', dim: 0, x: -2, y: 65, z: 12 },
    { key: '0:301', kind: 'mob', name: 'kiwi', dim: 0, x: 0, y: 0, z: 0 },
    { key: '0:302', kind: 'player', name: 'maid_chan', dim: 0, x: 9, y: 9, z: 9 },
  ];

  it('maps bots by name or short key and players by username, any case', () => {
    expect(findSpeaker('MAID_CHAN', bots, ents)).toMatchObject({ key: 'maid_chan', bot: true, x: 1 });
    expect(findSpeaker('109', bots, ents)).toMatchObject({ key: 'maid_chan_109', bot: true });
    expect(findSpeaker('main', bots, ents)).toMatchObject({ key: 'maid_chan' });
    expect(findSpeaker('PoofImaFox', bots, ents)).toMatchObject({ key: '0:300', bot: false, z: 12 });
  });

  it('prefers the bot over a player of the same name; mobs and strangers are nobody', () => {
    expect(findSpeaker('maid_chan', bots, ents)?.bot).toBe(true);
    expect(findSpeaker('kiwi', bots, ents)).toBeNull();
    expect(findSpeaker('nobody', bots, ents)).toBeNull();
    expect(findSpeaker(' ', bots, ents)).toBeNull();
  });
});

describe('bubble textures', () => {
  it('reference-counts and keeps only a few idle ones (LRU)', () => {
    const disposed: string[] = [];
    const cache = new TextureCache((key) => ({ tex: { dispose: () => disposed.push(key) }, w: 1, h: 1, refs: 0 }), 2);
    for (const k of ['a', 'b', 'c']) cache.acquire(k);
    expect(cache.size).toBe(3);
    cache.release('a');
    cache.release('b');
    expect(disposed).toEqual([]);
    cache.acquire('a'); // a used again: b is the least recent idle one
    cache.release('a');
    cache.release('c');
    expect(disposed).toEqual(['b']);
    expect(cache.size).toBe(2);
    cache.acquire('c');
    expect(disposed).toEqual(['b']); // reused, not redrawn
  });
});

describe('bubble layer', () => {
  // Node has no canvas: a stand-in that measures nothing (the layer only needs sizes).
  const g = globalThis as unknown as { document?: unknown };
  let had: unknown;
  beforeAll(() => {
    had = g.document;
    g.document = { createElement: () => ({ width: 0, height: 0, getContext: () => null }) };
  });
  afterAll(() => {
    g.document = had;
  });

  const camera = () => {
    const c = new THREE.PerspectiveCamera(70, 16 / 10, 0.05, 400);
    c.position.set(0, 66, 8);
    c.lookAt(0, 65, 0);
    c.updateMatrixWorld();
    return c;
  };
  const bot = { name: 'maid_chan', dim: 0, x: 0, y: 64, z: 0, dto: { short: 'main' } };
  const view = (over: Partial<Parameters<BubbleLayer['showFor']>[0]> = {}) => ({ dim: 0, camera: camera(), height: 800, pxScale: 1, mode: 'full' as const, bots: true, players: true, ...over });

  it('draws a stack over the right body, above its label, on top of terrain', () => {
    const l = new BubbleLayer();
    l.say('Maid_Chan', 'Hi poofimafox!', 'said', 0);
    l.say('maid_chan', 'The generator is steady.', 'said', 100);
    l.update(1000, [bot], []);
    l.showFor({ ...view(), labelLift: () => 0.5 }, 1);
    const shown = l.shownIn(1);
    expect(shown).toHaveLength(1);
    expect(shown[0]).toMatchObject({ speaker: 'maid_chan', key: 'maid_chan', stacked: 2, lines: ['The generator is steady.'] });
    // The anchor is the head of the bot, centred in this view.
    expect(Math.abs(shown[0]!.x - 640)).toBeLessThan(2);
    const sprites = l.group.children.filter((c) => c.visible) as THREE.Sprite[];
    expect(sprites).toHaveLength(2);
    for (const s of sprites) {
      expect((s.material as THREE.SpriteMaterial).depthTest).toBe(false);
      expect(s.center.y).toBeLessThan(0); // lifted in screen space, above the label
    }
  });

  it('shows nothing for speakers off screen, in another dimension, unknown or with their layer off', () => {
    const l = new BubbleLayer();
    l.say('maid_chan', 'hello', 'said', 0);
    l.say('poofimafox', 'hey', 'chat', 0);
    l.say('ghost', 'boo', 'chat', 0);
    l.update(1000, [{ ...bot, x: 0, z: 40 }], [{ key: '0:300', kind: 'player', name: 'poofimafox', dim: -1, x: 0, y: 64, z: 0 }]);
    l.showFor(view(), 1);
    expect(l.shownIn(1)).toHaveLength(0); // the bot is behind the camera, the player in the nether
    l.update(1000, [bot], []);
    l.showFor(view({ bots: false }), 1);
    expect(l.shownIn(1)).toHaveLength(0);
    l.showFor(view({ mode: 'off' }), 1);
    expect(l.group.visible).toBe(false);
  });

  it('thumbnails get one compact bubble; lines fade and leave', () => {
    const l = new BubbleLayer();
    l.say('maid_chan', 'one', 'said', 0);
    l.say('maid_chan', 'two', 'said', 10);
    l.update(1000, [bot], []);
    l.showFor(view({ mode: 'compact' }), 2);
    expect(l.shownIn(2)[0]).toMatchObject({ stacked: 1, lines: ['two'] });
    l.update(6700, [bot], []);
    l.showFor(view(), 1);
    expect(l.shownIn(1)).toHaveLength(0);
    expect(l.model.size).toBe(0);
  });
});
