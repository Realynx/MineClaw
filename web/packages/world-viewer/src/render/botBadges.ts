import * as THREE from 'three';
import { LabelFactory } from './labels';
import { HEAD_Y } from './bubbles';

/** Application-specific information above a bot; account name remains its stable lookup key. */
export interface BotBadge {
  name: string;
  text: string;
  activity: string;
  icon?: string;
  color?: string;
}

type Position = { x: number; y: number; z: number; dim: number };
interface BadgeSprite {
  badge: BotBadge;
  key: string;
  sprite: THREE.Sprite;
  width: number;
  height: number;
  anchor: THREE.Vector3;
  dim: number | null;
  shownAt: number | null;
}

/** Readable CSS-pixel scale with a bounded range, without raycasts or per-frame texture regeneration. */
export function badgeSize(
  depth: number,
  distance: number,
  fov: number,
  cssHeight: number,
  range: number,
): { scale: number; alpha: number } | null {
  const max = Math.min(64, range);
  if (depth <= 0.05 || distance >= max || cssHeight <= 0) return null;
  const linePx = Math.max(11, Math.min(14, 16 - distance / 12));
  const worldPerPx = (2 * depth * Math.tan((fov * Math.PI) / 360)) / cssHeight;
  return { scale: (linePx * worldPerPx) / (28 * 0.012), alpha: Math.min(1, (max - distance) / 12) };
}

/** One view's badges. Terrain depth hides them; another view never inherits them. */
export class BotBadgeLayer {
  readonly group = new THREE.Group();
  private readonly labels = new LabelFactory();
  private readonly sprites = new Map<string, BadgeSprite>();
  private last: readonly BotBadge[] | null = null;
  private readonly point = new THREE.Vector3();

  set(badges: readonly BotBadge[]): void {
    if (badges === this.last) return;
    this.last = badges;
    const wanted = new Set<string>();
    for (const badge of badges) {
      const name = badge.name.toLowerCase();
      wanted.add(name);
      const key = JSON.stringify([badge.text, badge.activity, badge.icon, badge.color]);
      const old = this.sprites.get(name);
      if (old?.key === key) continue;
      if (old) this.remove(name, old);
      const sprite = this.labels.create(badge.text, {
        fg: '#f4f6f8',
        bg: 'rgba(10,12,16,0.86)',
        sub: badge.activity,
        border: badge.color,
        icon: badge.icon,
      });
      // A job change replaces the texture, but must not restart the position filter.
      this.sprites.set(name, {
        badge, key, sprite, width: sprite.scale.x, height: sprite.scale.y,
        anchor: old?.anchor ?? new THREE.Vector3(), dim: old?.dim ?? null, shownAt: old?.shownAt ?? null,
      });
      sprite.visible = false;
      this.group.add(sprite);
    }
    for (const [name, entry] of this.sprites) if (!wanted.has(name)) this.remove(name, entry);
  }

  show(
    camera: THREE.PerspectiveCamera,
    dim: number,
    cssHeight: number,
    range: number,
    position: (name: string) => Position | null,
    now = performance.now(),
  ): void {
    for (const entry of this.sprites.values()) {
      const { sprite, badge } = entry;
      sprite.visible = false;
      const bot = position(badge.name);
      if (!bot || bot.dim !== dim) {
        entry.shownAt = null;
        continue;
      }
      this.point.set(bot.x, bot.y + HEAD_Y.bot, bot.z);
      const elapsed = entry.shownAt === null ? Infinity : Math.max(0, now - entry.shownAt);
      // Short, frame-rate-independent damping softens steps and starts/stops without making labels trail far behind.
      // Teleports, dimension changes and returning to an inactive view must never glide across the world.
      if (entry.dim !== bot.dim || elapsed > 500 || entry.anchor.distanceToSquared(this.point) > 64)
        entry.anchor.copy(this.point);
      else entry.anchor.lerp(this.point, -Math.expm1(-elapsed / 90));
      entry.dim = bot.dim;
      entry.shownAt = now;
      this.point.copy(entry.anchor);
      const distance = this.point.distanceTo(camera.position);
      const view = camera.matrixWorldInverse.elements;
      const depth = -(view[2] * this.point.x + view[6] * this.point.y + view[10] * this.point.z + view[14]);
      const size = badgeSize(depth, distance, camera.fov, cssHeight, range);
      if (!size) continue;
      this.point.project(camera);
      if (
        Math.abs(this.point.x) > 1.05 ||
        this.point.y < -1.05 ||
        this.point.y > 1 ||
        this.point.z < -1 ||
        this.point.z > 1
      )
        continue;
      sprite.position.copy(entry.anchor);
      sprite.scale.set(entry.width * size.scale, entry.height * size.scale, 1);
      sprite.material.opacity = size.alpha;
      sprite.visible = true;
    }
  }

  /** World height above the shared head anchor, only when this view actually shows the badge. */
  labelLift(name: string): number {
    const sprite = this.sprites.get(name.toLowerCase())?.sprite;
    return this.group.visible && sprite?.visible ? sprite.scale.y : 0;
  }

  private remove(name: string, entry: BadgeSprite): void {
    this.group.remove(entry.sprite);
    this.labels.release(entry.sprite);
    this.sprites.delete(name);
  }

  dispose(): void {
    for (const [name, entry] of this.sprites) this.remove(name, entry);
    this.labels.dispose();
    this.group.removeFromParent();
    this.last = null;
  }
}
