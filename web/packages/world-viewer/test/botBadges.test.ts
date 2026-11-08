import * as THREE from 'three';
import { afterEach, beforeEach, expect, it, vi } from 'vitest';
import { badgeSize, BotBadgeLayer } from '../src/render/botBadges';
import { BubbleLayer, HEAD_Y } from '../src/render/bubbles';

beforeEach(() => vi.stubGlobal('document', { createElement: () => ({ width: 0, height: 0, getContext: () => null }) }));
afterEach(() => vi.unstubAllGlobals());

const camera = () => {
  const c = new THREE.PerspectiveCamera(70, 1.5, 0.05, 400);
  c.position.set(0, 66.15, 10);
  c.lookAt(0, 66.15, 0);
  c.updateMatrixWorld();
  return c;
};
const badge = { name: 'maid_chan_109', text: '109', activity: 'Mining', color: '#ffaa00' };

it('keeps text readable in CSS pixels across distance and fades before the range boundary', () => {
  for (const distance of [2, 10, 30, 55]) {
    const size = badgeSize(distance, distance, 70, 600, 64)!;
    const worldPerPx = (2 * distance * Math.tan((70 * Math.PI) / 360)) / 600;
    const fontPx = (28 * 0.012 * size.scale) / worldPerPx;
    expect(fontPx).toBeGreaterThanOrEqual(11);
    expect(fontPx).toBeLessThanOrEqual(14.000001);
  }
  expect(badgeSize(60, 60, 70, 600, 64)?.alpha).toBeCloseTo(1 / 3);
  expect(badgeSize(32, 32, 70, 600, 32)).toBeNull();
  expect(badgeSize(-2, 2, 70, 600, 64)).toBeNull();
});

it('follows live positions, depth-tests against terrain and hides out-of-view or other-dimension pawns', () => {
  const layer = new BotBadgeLayer();
  layer.set([badge]);
  const sprite = layer.group.children[0] as THREE.Sprite;
  expect(sprite.material.depthTest).toBe(true);
  expect(sprite.material.depthWrite).toBe(false);
  const c = camera();
  const show = (p: { x: number; y: number; z: number; dim: number } | null) => layer.show(c, 0, 600, 64, () => p);
  show({ x: 0, y: 64, z: 0, dim: 0 });
  expect(sprite.visible).toBe(true);
  expect(sprite.position.toArray()).toEqual([0, 64 + HEAD_Y.bot, 0]);
  expect(layer.labelLift(badge.name)).toBe(sprite.scale.y);
  show({ x: 1, y: 64, z: -2, dim: 0 });
  expect(sprite.position.toArray()).toEqual([1, 64 + HEAD_Y.bot, -2]);
  for (const p of [
    null,
    { x: 0, y: 64, z: 0, dim: -1 },
    { x: 0, y: 64, z: 20, dim: 0 },
    { x: 100, y: 64, z: 0, dim: 0 },
    { x: 0, y: 64, z: -70, dim: 0 },
  ]) {
    show(p);
    expect(sprite.visible).toBe(false);
    expect(layer.labelLift(badge.name)).toBe(0);
  }
  layer.dispose();
});

it('lifts speech above the two-line badge only while that view shows it', () => {
  const badges = new BotBadgeLayer();
  badges.set([badge]);
  const c = camera();
  const bot = { name: badge.name, x: 0, y: 64, z: 0, dim: 0, dto: { short: '109' } };
  badges.show(c, 0, 600, 64, () => bot);
  const sprite = badges.group.children[0] as THREE.Sprite;
  const bubbles = new BubbleLayer();
  bubbles.say(badge.name, 'Found ore', 'said', 0);
  bubbles.update(1000, [bot], []);
  const view = {
    dim: 0,
    camera: c,
    height: 600,
    pxScale: 1,
    mode: 'full' as const,
    bots: true,
    players: true,
    labelLift: (key: string, isBot: boolean) => (isBot ? badges.labelLift(key) : 0),
  };
  bubbles.showFor(view, 1);
  const anchor = sprite.position.clone().project(c);
  const depth = -sprite.position.clone().applyMatrix4(c.matrixWorldInverse).z;
  const worldPerPx = (2 * depth * Math.tan((c.fov * Math.PI) / 360)) / 600;
  const badgeTop = (1 - anchor.y) * 300 - sprite.scale.y / worldPerPx;
  expect(bubbles.shownIn(1)[0].bottom).toBeLessThan(badgeTop);
  const raised = bubbles.shownIn(1)[0].bottom;
  badges.group.visible = false; // another view, or badges/labels switched off
  expect(badges.labelLift(badge.name)).toBe(0);
  bubbles.showFor(view, 2);
  expect(bubbles.shownIn(2)[0].bottom).toBeGreaterThan(raised);
  badges.dispose();
  bubbles.dispose();
});

it('reuses unchanged textures and releases changed or removed badges', () => {
  const layer = new BotBadgeLayer();
  layer.set([badge]);
  const first = layer.group.children[0] as THREE.Sprite;
  const released = vi.spyOn(first.material, 'dispose');
  layer.set([{ ...badge }]);
  expect(layer.group.children[0]).toBe(first);
  layer.set([{ ...badge, activity: 'Hauling' }]);
  expect(released).toHaveBeenCalledOnce();
  expect(layer.group.children[0]).not.toBe(first);
  layer.set([]);
  expect(layer.group.children).toHaveLength(0);
  layer.dispose();
});
