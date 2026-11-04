/**
 * Golden checksums of the studio's facing rules over every input (ids 0..255, metas 0..15, every look, face, quarter turn,
 * mirror axis and version): the tables mirror the bot's DesignFacing / BuildBlocks.Rotate, so a refactor must not move one value.
 */
import { describe, expect, it } from 'vitest';
import {
  DIRS,
  facingOf,
  firstHalfOf,
  mirrorMeta,
  partnerOf,
  placeMeta,
  rotateMeta,
  withFacing,
  type Version,
} from '@/features/studio/facing';

const VERSIONS: Version[] = ['b1.8.1', '1.12.2'];

/** FNV-1a over the values, in order. */
function checksum(each: (push: (v: unknown) => void) => void): string {
  let h = 0x811c9dc5;
  let n = 0;
  each((v) => {
    const s = JSON.stringify(v) ?? 'undefined';
    for (let i = 0; i < s.length; i++) h = Math.imul(h ^ s.charCodeAt(i), 0x01000193) >>> 0;
    h = Math.imul(h ^ 0x2c, 0x01000193) >>> 0;
    n++;
  });
  return `${n}:${h.toString(16)}`;
}

/** The value, or 'throws' (a rail shape 10..15 has no mirror: the rules throw there, and that is pinned too). */
function safe(f: () => unknown): unknown {
  try {
    return f();
  } catch {
    return 'throws';
  }
}

function eachIdMeta(f: (id: number, meta: number) => void): void {
  for (let id = 0; id < 256; id++) for (let meta = 0; meta < 16; meta++) f(id, meta);
}

describe('facing golden', () => {
  it('placeMeta', () => {
    const sum = checksum((push) =>
      eachIdMeta((id, meta) => {
        for (const v of VERSIONS)
          for (const look of DIRS) for (let face = 0; face < 6; face++) push(placeMeta(id, meta, look, face, v));
      }),
    );
    expect(sum).toBe('196608:1b324075');
  });

  it('rotateMeta and mirrorMeta', () => {
    const sum = checksum((push) =>
      eachIdMeta((id, meta) => {
        for (const v of VERSIONS) for (let q = -1; q <= 4; q++) push(rotateMeta(id, meta, q, v));
        push(safe(() => mirrorMeta(id, meta, 'x')));
        push(safe(() => mirrorMeta(id, meta, 'z')));
      }),
    );
    expect(sum).toBe('57344:5890803b');
  });

  it('facingOf, withFacing and the two-cell halves', () => {
    const sum = checksum((push) =>
      eachIdMeta((id, meta) => {
        push(facingOf(id, meta));
        for (const d of DIRS) push(withFacing(id, meta, d));
        push(partnerOf(id, meta));
        push(firstHalfOf(id, meta));
      }),
    );
    expect(sum).toBe('28672:c84ca22b');
  });
});
