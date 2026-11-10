/**
 * The simulator's orientation table (`@mineclaw/redstone`'s rotateMeta and mirrorMeta) against the studio's own
 * (`features/studio/facing.ts`, which matches the bot's DesignFacing): a library component turned or mirrored must read
 * the same as a selection the studio turns or mirrors, for every redstone block, every meta and every turn, in both
 * game versions.
 */
import { mirrorMeta, rotateMeta, type RedstoneVersion } from '@mineclaw/redstone';
import { describe, expect, it } from 'vitest';
import { mirrorMeta as studioMirror, rotateMeta as studioRotate } from '@/features/studio/facing';

/**
 * Every block with redstone behaviour in either version (the engine's part lists), and the blocks the library's layouts
 * and the harness are made of: stone, glass, glowstone, slabs, the redstone block, dust and the lever.
 */
const REDSTONE_IDS = [
  1, 20, 23, 25, 27, 28, 29, 33, 34, 36, 44, 46, 55, 64, 69, 70, 71, 72, 75, 76, 77, 89, 93, 94, 96, 107, 123, 124, 126,
  131, 132, 143, 146, 147, 148, 149, 150, 151, 152, 154, 157, 158, 167, 178, 183, 184, 185, 186, 187, 193, 194, 195,
  196, 197, 218,
];
const VERSIONS: readonly RedstoneVersion[] = ['b1.8.1', '1.12.2'];
const METAS = Array.from({ length: 16 }, (_, m) => m);

/** A plain rail's shapes are 0..9; the game has no other meta for it (and the studio's mirror has no rule for one). */
const exists = (id: number, meta: number) => id !== 66 || meta <= 9;

/** Every case where the two tables give different metas. */
function differences(ids: readonly number[]): string[] {
  const out: string[] = [];
  for (const v of VERSIONS)
    for (const id of ids)
      for (const meta of METAS) {
        if (!exists(id, meta)) continue;
        for (let q = -4; q <= 4; q++) {
          const a = rotateMeta(id, meta, q, v);
          const b = studioRotate(id, meta, q, v);
          if (a !== b) out.push(`${v} id ${id} meta ${meta} turned ${q}: engine ${a}, studio ${b}`);
        }
        for (const axis of ['x', 'z'] as const) {
          const a = mirrorMeta(id, meta, axis, v);
          const b = studioMirror(id, meta, axis);
          if (a !== b) out.push(`${v} id ${id} meta ${meta} mirrored ${axis}: engine ${a}, studio ${b}`);
        }
      }
  return out;
}

describe('the simulator’s orientation table and the studio’s', () => {
  it('agree on every redstone block, meta, turn and mirror in both versions', () => {
    expect(differences(REDSTONE_IDS)).toEqual([]);
  });

  it('agree on every other block id too, so a custom component of any blocks turns the same', () => {
    expect(differences(Array.from({ length: 256 }, (_, id) => id))).toEqual([]);
  });

  it('really turn the parts: a repeater, a torch, a lever and a piston change their meta', () => {
    // A check that the comparison is not of two tables that both leave everything alone.
    expect(rotateMeta(93, 0, 1, '1.12.2')).toBe(1);
    expect(rotateMeta(76, 1, 1, 'b1.8.1')).toBe(3);
    expect(rotateMeta(69, 5, 1, 'b1.8.1')).toBe(6);
    expect(rotateMeta(33, 2, 1, '1.12.2')).toBe(5);
    expect(mirrorMeta(93, 1, 'x', '1.12.2')).toBe(3);
  });
});
