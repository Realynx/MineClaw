// Helpers for the behaviour-pinning golden tests: a seeded PRNG and a digest of strings and typed arrays.

import { createHash } from 'node:crypto';

/** mulberry32: a small seeded PRNG so the inputs are the same on every run. */
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

/** A SHA-256 over in-memory values (no files, so line endings never matter). */
export class Digest {
  private readonly h = createHash('sha256');
  add(s: string): void {
    this.h.update(s);
    this.h.update('\n');
  }
  bytes(a: ArrayBufferView | null | undefined): void {
    if (!a) {
      this.add('null');
      return;
    }
    this.add(`${a.constructor.name}:${a.byteLength}`);
    this.h.update(new Uint8Array(a.buffer, a.byteOffset, a.byteLength));
  }
  hex(): string {
    return this.h.digest('hex').slice(0, 32);
  }
}
