// Behaviour pin for the complexity refactor of render/entities/** (docs/dev/complexity.md, WP12), below the entity layer:
// every model definition built (geometry, parts, uv rectangles) and posed through its Skeleton with seeded pose contexts
// (walking, riding, attacking, eating, babies, look tokens, beta rules), Variant decoding, the registry's names and looks over
// every mob type × name × variant, and seeded stepMotion sequences. The digests were taken from the code before it was
// reshaped.

import { describe, expect, it } from 'vitest';
import { buildModel } from '../src/render/entities/builder';
import { newMotion, stepMotion } from '../src/render/entities/motion';
import { Skeleton } from '../src/render/entities/pose';
import { ALL_MODELS, MOB_NAMES, mobLook, mobName, objectLook, objectName } from '../src/render/entities/registry';
import type { PoseCtx } from '../src/render/entities/types';
import { Variant } from '../src/render/entities/variant';
import { Digest, rng } from './digest';

const TOKENS = ['aggressive', 'angry', 'tame', 'tamed', 'sitting', 'collar=red', 'collar:blue', 'collar=7', 'saddle', 'saddled', 'chested', 'sheared', 'red', 'black', 'size=3', 'size=0', 'charged', 'powered', 'fuse', 'screaming', 'creamy', 'white_dots', 'armor=iron', 'armor=diamond', 'profession=librarian', 'type=2', 'small', 'arms', 'show_arms', 'invulnerable', 'converting', 'holding_rose', 'rose', 'carpet=red', 'open', 'golden', 'toast', '3', '-1', 'Light Blue', 'minecraft:red', 'eating', 'begging', 'rearing', 'standing', 'jumping', 'shooting', 'attacking', 'spell', 'mouth_open', 'baby', 'red_tabby', 'siamese', 'ocelot', 'cat=2'];
const tokensOf = (r: () => number) => Array.from({ length: Math.floor(r() * 4) }, () => TOKENS[Math.floor(r() * TOKENS.length)]).join(r() < 0.5 ? ',' : ' ; ');

function ctxOf(r: () => number): PoseCtx {
  const look = r() < 0.6 ? tokensOf(r) : null;
  const num = r() < 0.5 ? Math.floor(r() * 64) : null;
  const pick = <T,>(a: readonly T[]) => a[Math.floor(r() * a.length)];
  return {
    swing: r() * 40, amount: r() < 0.3 ? 0 : r(), age: r() * 2000, headYaw: r() * 180 - 90, pitch: r() * 120 - 60,
    attack: r() < 0.5 ? 0 : r(), attackOff: r() < 0.7 ? 0 : r(), eat: r() < 0.7 ? 0 : r(), baby: r() < 0.25,
    v: Variant.of(num, look), variant: num ?? 0, flags: Math.floor(r() * 32), sneaking: r() < 0.2, riding: r() < 0.2,
    holding: r() < 0.4, aggressive: r() < 0.3, using: r() < 0.2, bow: r() < 0.2,
    standPose: r() < 0.2 ? { head: [r() * 40, 10, 0], body: [0, r() * 30, 0], leftArm: [-20, 0, -10], rightArm: [pick([-90, 10]), 0, 10], leftLeg: [5, 0, 0], rightLeg: [-5, 0, 0] } : null,
    beta: r() < 0.3, seed: r(), speed: r() < 0.5 ? 0 : r() * 0.5,
  } as PoseCtx;
}

const replacer = (k: string, v: unknown) => (k === 'def' ? undefined : ArrayBuffer.isView(v) ? Array.from(v as Float32Array) : v);

describe('entity model goldens', () => {
  it('every model built', () => {
    const d = new Digest();
    for (const def of ALL_MODELS) d.add(`${def.id} ${JSON.stringify(buildModel(def), replacer)}`);
    expect(d.hex()).toBe('512a72514d530c1caf26e1f3f3b95710');
  });

  it('every model posed', () => {
    const d = new Digest();
    const r = rng(71);
    for (const def of ALL_MODELS) {
      const sk = new Skeleton(buildModel(def));
      const out = new Float32Array(sk.model.parts.length * 12 + 24);
      for (let k = 0; k < 40; k++) {
        const root = Float32Array.from({ length: 12 }, (_, i) => (i % 5 === 0 ? 1 : 0) + (r() - 0.5) * 0.2);
        const c = ctxOf(r);
        sk.pose(k === 0 ? null : c, root, out, 12, k % 3 === 0 ? !c.baby : c.baby);
        d.add(def.id);
        d.bytes(out);
      }
    }
    expect(d.hex()).toBe('bec1e4ad4e18d4fcfdb937dd09bcdbef');
  });

  it('Variant, names and looks', () => {
    const d = new Digest();
    const r = rng(72);
    for (let k = 0; k < 3000; k++) {
      const raw = r() < 0.3 ? null : r() < 0.5 ? Math.floor(r() * 300) - 20 : r() < 0.5 ? tokensOf(r) : r() < 0.5 ? '' : NaN;
      const look = r() < 0.5 ? tokensOf(r) : r() < 0.5 ? null : '';
      const v = Variant.of(raw as number | string | null, look);
      d.add(`${JSON.stringify(v)} ${v.num} ${v.isString} ${v.has('tame', 'angry')} ${v.get('collar')} ${v.dye('collar')} ${v.dye()} ${v.numOf('size', -1)}`);
    }
    for (const [raw, look] of [['3,-1,size=2', '5'], [null, '7;8|9'], [12, 'x=1,4'], [NaN, '2'], [Infinity, null], ['', ''], ['a:b=c', 'Minecraft:Red - Dye, -0'], [-3, null]] as const) {
      const v = Variant.of(raw, look);
      d.add(`${JSON.stringify(v)} ${v.num} ${v.get('a')} ${v.get('x')}`);
    }
    const NAMES = [null, 'Zombie', 'PigZombie', 'minecraft:wither_skeleton', 'WitherSkeleton', 'Stray', 'EntityHorse', 'cave_spider', 'Ozelot', 'nope', 'skeleton', 'zombie', 'Wither Skeleton', 'LavaSlime', 'villager_golem', 'Husk'];
    const types = Object.keys(MOB_NAMES).map(Number).concat([-1, 0, 49, 999]);
    for (const type of types)
      for (const name of NAMES) {
        d.add(`${type} ${name} ${mobName(type, name)}`);
        for (const version of ['1.12.2', 'b1.8.1'] as const)
          for (let j = 0; j < 6; j++) {
            const variant = j === 0 ? null : j === 1 ? Math.floor(r() * 64) : tokensOf(r);
            d.add(JSON.stringify(mobLook(version, type, name, variant, Math.floor(r() * 1000))));
          }
      }
    for (let t = -1; t < 120; t++)
      for (const beta of [false, true]) {
        const n = objectName(t, beta);
        d.add(`${t} ${beta} ${n} ${n ? JSON.stringify(objectLook(beta ? 'b1.8.1' : '1.12.2', n, Math.floor(r() * 8), t)) : ''}`);
      }
    expect(d.hex()).toBe('2d80f0845187f73f07d8b7a560f8ee46');
  });

  it('stepMotion', () => {
    const d = new Digest();
    const r = rng(73);
    for (let e = 0; e < 60; e++) {
      const m = newMotion(r() * 10, 64, r() * 10, r() * 360 - 180, `e${e}`, 1000);
      let now = 1000, x = m.x, z = m.z, look = r() * 360 - 180;
      for (let k = 0; k < 120; k++) {
        now += r() < 0.05 ? 0 : r() < 0.05 ? 400 : r() < 0.05 ? -30 : r() * 60;
        const p = r();
        if (p < 0.4) {
          x += (r() - 0.5) * 0.6;
          z += (r() - 0.5) * 0.6;
        } else if (p < 0.45) x += 8;
        if (r() < 0.2) look += (r() - 0.5) * 300;
        stepMotion(m, x, 64, z, look, now, r() < 0.2);
        d.add(Object.values(m).map(String).join(','));
      }
    }
    expect(d.hex()).toBe('d6d32ae4a5dc7a897dbf34982dfd4d00');
  });
});
