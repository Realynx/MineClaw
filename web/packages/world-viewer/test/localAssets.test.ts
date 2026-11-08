import { describe, expect, it } from 'vitest';
import { deflateRawSync } from 'node:zlib';
import { readArchive, resourcePath } from '../src/assets/archive';
import { resolveSound, soundDistance, type SoundListener } from '../src/audio/controllerAudio';

function zip(name: string, payload: Uint8Array, compressed = false): Blob {
  const filename = new TextEncoder().encode(name),
    bytes = compressed ? deflateRawSync(payload) : payload;
  const header = new Uint8Array(30 + filename.length),
    h = new DataView(header.buffer);
  h.setUint32(0, 0x04034b50, true);
  h.setUint16(8, compressed ? 8 : 0, true);
  h.setUint32(18, bytes.length, true);
  h.setUint32(22, payload.length, true);
  h.setUint16(26, filename.length, true);
  header.set(filename, 30);
  const central = new Uint8Array(46 + filename.length),
    c = new DataView(central.buffer);
  c.setUint32(0, 0x02014b50, true);
  c.setUint16(10, compressed ? 8 : 0, true);
  c.setUint32(20, bytes.length, true);
  c.setUint32(24, payload.length, true);
  c.setUint16(28, filename.length, true);
  central.set(filename, 46);
  const end = new Uint8Array(22),
    e = new DataView(end.buffer);
  e.setUint32(0, 0x06054b50, true);
  e.setUint16(8, 1, true);
  e.setUint16(10, 1, true);
  e.setUint32(12, central.length, true);
  e.setUint32(16, header.length + bytes.length, true);
  return new Blob([header, new Uint8Array(bytes), central, end]);
}
describe('local game archives', () => {
  it.each([false, true])('reads game resources (deflated=%s)', async (compressed) => {
    const path = 'assets/minecraft/blockstates/stone.json',
      data = new TextEncoder().encode('{"variants":{}}');
    const resources = await readArchive(zip(path, data, compressed));
    expect(await resources.get(path)?.text()).toBe('{"variants":{}}');
  });
  it('skips executable classes and path traversal', async () => {
    for (const path of [
      'net/minecraft/client/Main.class',
      '../terrain.png',
      'assets/minecraft/textures/../secret.png',
    ]) {
      expect(resourcePath(path)).toBe(false);
      expect((await readArchive(zip(path, new Uint8Array([1, 2])))).size).toBe(0);
    }
  });
  it('rejects malformed archives and oversized expanded resources', async () => {
    await expect(readArchive(new Blob(['not a jar']))).rejects.toThrow('valid JAR');
    const data = new Uint8Array(await zip('terrain.png', new Uint8Array([1])).arrayBuffer());
    const central = 30 + 'terrain.png'.length + 1;
    new DataView(data.buffer).setUint32(central + 24, 20 * 1024 * 1024, true);
    await expect(readArchive(new Blob([data]))).rejects.toThrow('oversized');
  });
});
describe('controller sound', () => {
  const listener: SoundListener = {
    dim: 0,
    version: '1.12.2',
    position: [0, 100, 0],
    forward: [0, 0, -1],
    up: [0, 1, 0],
    topDown: true,
  };
  it('uses horizontal distance in top view and full distance in freecam', () => {
    expect(soundDistance([3, 0, 4], listener)).toBe(5);
    expect(soundDistance([3, 0, 4], { ...listener, topDown: false })).toBeCloseTo(Math.sqrt(10025));
  });
  it('resolves weighted variants and nested event volume', () => {
    const events = {
      open: {
        sounds: [
          { name: 'chest/open1', weight: 1 },
          { name: 'chest/open2', weight: 3 },
        ],
      },
      nested: { sounds: [{ name: 'open', type: 'event', volume: 0.5 }] },
    };
    expect(resolveSound(events, 'minecraft:nested', () => 0.6)).toEqual({ name: 'chest/open2', volume: 0.5, pitch: 1 });
  });
  it('rejects missing and recursively self-referencing sound events', () => {
    expect(resolveSound({}, 'missing')).toBeNull();
    expect(resolveSound({ loop: { sounds: [{ name: 'loop', type: 'event' }] } }, 'loop')).toBeNull();
  });
});
