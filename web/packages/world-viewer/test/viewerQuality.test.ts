import * as THREE from 'three';
import { afterEach, beforeAll, describe, expect, it } from 'vitest';
import { EntityLayer, MAX_FAR_BOXES } from '../src/render/entityLayer';
import { PROFILES, QualityController, recommendedPreset } from '../src/render/quality';
import {
  DEFAULT_VIEWER_QUALITY,
  effectiveProfile,
  entityReach,
  FrameLimiter,
  getViewerQuality,
  motionLine,
  resetViewerQuality,
  setViewerQuality,
  subscribeViewerQuality,
  viewerQualityRev,
} from '../src/render/viewerQuality';
import type { EntityDto } from '../src/types';
import type { EntityView } from '../src/world/entities';

afterEach(() => resetViewerQuality());

describe('viewer quality store', () => {
  it('starts at the defaults: today’s viewer, with entities as far as the ground', () => {
    expect(getViewerQuality()).toEqual(DEFAULT_VIEWER_QUALITY);
    expect(DEFAULT_VIEWER_QUALITY.entityDistance).toBe('ground');
    expect(DEFAULT_VIEWER_QUALITY.drawDistance).toBe(128);
    expect(DEFAULT_VIEWER_QUALITY.coverCap).toBe(192);
  });

  it('clamps to what the renderer honours, notifies, and bumps the revision only on a real change', () => {
    let calls = 0;
    const off = subscribeViewerQuality(() => calls++);
    const r0 = viewerQualityRev();
    setViewerQuality({ drawDistance: 999, coverCap: 10, entityDistance: 2, renderScale: 9, fpsCap: 0, entityModelDistance: -5 });
    const q = getViewerQuality();
    expect(q.drawDistance).toBe(256);
    expect(q.coverCap).toBe(64);
    expect(q.entityDistance).toBe(16);
    expect(q.renderScale).toBe(2);
    expect(q.fpsCap).toBeNull();
    expect(q.entityModelDistance).toBe(0);
    expect(viewerQualityRev()).toBe(r0 + 1);
    expect(calls).toBe(1);
    setViewerQuality({ drawDistance: 256 }); // unchanged
    expect(viewerQualityRev()).toBe(r0 + 1);
    expect(calls).toBe(1);
    off();
  });
});

describe('effective profile', () => {
  it('Auto keeps every view at the distance it had (the Live pane asked 128, thumbnails 40, Story 112)', () => {
    for (const name of ['software', 'gpu'] as const) {
      const base = new QualityController(PROFILES[name]);
      const eff = new QualityController(effectiveProfile(PROFILES[name], DEFAULT_VIEWER_QUALITY));
      for (const want of [40, 96, 112, 128]) expect(eff.drawDistance(want)).toBe(base.drawDistance(want));
      expect(eff.state.profile.pixelRatioCap).toBe(PROFILES[name].pixelRatioCap);
      expect(eff.state.profile.entityDetailDistance).toBe(PROFILES[name].entityDetailDistance);
      expect(eff.state.profile.clouds).toBe(PROFILES[name].clouds);
    }
  });

  it('lays the user’s fields over the profile', () => {
    const p = effectiveProfile(PROFILES.software, { ...DEFAULT_VIEWER_QUALITY, drawDistance: 256, renderScale: 0.5, entityModelDistance: 64, clouds: true });
    expect(p).toMatchObject({ maxDraw: 256, minDraw: 48, pixelRatioCap: 0.5, entityDetailDistance: 64, clouds: true, targetMs: 22 });
    const low = effectiveProfile(PROFILES.gpu, { ...DEFAULT_VIEWER_QUALITY, drawDistance: 32 });
    expect(low.minDraw).toBe(32);
    expect(new QualityController(low).drawDistance(128)).toBe(32);
  });

  it('recommends Auto for software WebGL and High with a GPU', () => {
    expect(recommendedPreset({ software: true })).toBe('auto');
    expect(recommendedPreset({ software: false })).toBe('high');
  });
});

describe('entity reach', () => {
  it('same as ground: the whole picture in top-down / iso (from the look-at point), the drawn distance otherwise', () => {
    expect(entityReach(DEFAULT_VIEWER_QUALITY, 117, 117)).toEqual({ radius: 117, fromCentre: true });
    expect(entityReach(DEFAULT_VIEWER_QUALITY, 0, 128)).toEqual({ radius: 128, fromCentre: false });
  });

  it('a block distance, never past the ground', () => {
    const q = { ...DEFAULT_VIEWER_QUALITY, entityDistance: 48 };
    expect(entityReach(q, 117, 117)).toEqual({ radius: 48, fromCentre: true });
    expect(entityReach({ ...q, entityDistance: 200 }, 0, 96)).toEqual({ radius: 96, fromCentre: false });
  });
});

describe('frame limiter', () => {
  const run = (cap: number | null, hz = 60, seconds = 3) => {
    const l = new FrameLimiter();
    let n = 0;
    for (let i = 0; i < hz * seconds; i++) if (l.due((i * 1000) / hz, cap)) n++;
    return n / seconds;
  };

  it('holds the cap on average and draws every frame without one', () => {
    expect(run(null)).toBe(60);
    expect(run(30)).toBeCloseTo(30, 0);
    expect(Math.abs(run(40) - 40)).toBeLessThanOrEqual(1);
    expect(run(60)).toBeCloseTo(60, 0);
    expect(Math.abs(run(60, 144) - 60)).toBeLessThanOrEqual(1);
  });

  it('does not burst after a stall', () => {
    const l = new FrameLimiter();
    expect(l.due(0, 30)).toBe(true);
    expect(l.due(5000, 30)).toBe(true); // a long stall
    expect(l.due(5016, 30)).toBe(false);
    expect(l.due(5033, 30)).toBe(true);
  });
});

describe('motion line', () => {
  it('reads like the overlay’s second line: frames a second, then the longest gaps in ms, the delay and the pauses', () => {
    expect(motionLine({ rate: 9.84, src: 110.4, arr: 239.6, frame: 180, buf: 250.2, pauses: 1 })).toBe(
      'motion 9.8/s · src 110 ms · arr 240 ms · frame 180 ms · buf 250 ms · pauses 1',
    );
    expect(motionLine({ rate: 0, src: 0, arr: 0, frame: 0, buf: 0, pauses: 0 })).toBe(
      'motion 0.0/s · src 0 ms · arr 0 ms · frame 0 ms · buf 0 ms · pauses 0',
    );
  });

  it('is on the settings entry, which a page can load without three.js', async () => {
    const settings = await import('../src/settings');
    expect(settings.motionLine).toBe(motionLine);
  });
});

describe('entity layer under the settings', () => {
  beforeAll(() => {
    if (typeof document === 'undefined') (globalThis as Record<string, unknown>).document = { createElement: () => ({ width: 0, height: 0, getContext: () => null }) };
  });
  const ent = (id: number, x: number, z: number, kind = 'mob'): EntityView => {
    const dto = { kind, dim: 0, x, y: 64, z, id, type: kind === 'mob' ? 54 : -1, item: kind === 'item' ? 1 : 0 } as EntityDto;
    return { key: `0:${id}`, id, dim: 0, kind: dto.kind, name: null, type: dto.type ?? -1, item: dto.item ?? 0, count: 1, x, y: 64, z, yaw: 0, ageTicks: 0, flags: 0, dto };
  };
  /** A top-down camera over (0, 64, 0) as Story Mode places it at zoom 112: 12° fov, ~530 blocks up. */
  const topCamera = () => {
    const cam = new THREE.PerspectiveCamera(12, 1.6, 100, 2000);
    cam.position.set(0, 64 + 112 / 2 / Math.tan((6 * Math.PI) / 180), 0);
    cam.up.set(0, 0, -1);
    cam.lookAt(0, 64, 0);
    cam.updateMatrixWorld();
    return cam;
  };
  const frustumOf = (cam: THREE.PerspectiveCamera) =>
    new THREE.Frustum().setFromProjectionMatrix(new THREE.Matrix4().multiplyMatrices(cam.projectionMatrix, cam.matrixWorldInverse));

  it('a zoomed-out top-down map keeps its mobs when measured across the ground (the camera is far above)', () => {
    const layer = new EntityLayer();
    const cam = topCamera();
    const ents = [ent(1, 0, 0), ent(2, 60, 20), ent(3, -80, -10)];
    layer.update([], ents, 1000, new Set());
    // The old rule: distance from the camera (> 500 blocks up) beyond the 112-block draw distance: nothing drawn.
    layer.showFor(0, cam.position, { bots: true, entities: true, labels: false, drawDistance: 112, fov: 12 });
    expect(layer.instances).toBe(0);
    // Same as ground: across the ground from the look-at point, the picture's whole cover.
    layer.showFor(0, cam.position, { bots: true, entities: true, labels: false, drawDistance: 117, centre: { x: 0, z: 0 }, fov: 12 });
    expect(layer.instances).toBe(3);
    // A 48-block entity distance keeps only the near one.
    layer.showFor(0, cam.position, { bots: true, entities: true, labels: false, drawDistance: 48, centre: { x: 0, z: 0 }, fov: 12 });
    expect(layer.instances).toBe(1);
    layer.dispose();
  });

  it('drops can be hidden; entities off screen are culled; far boxes are capped', () => {
    const layer = new EntityLayer();
    const cam = topCamera();
    const frustum = frustumOf(cam);
    // Picture: 112 tall, 179 wide; (85, 0) is inside, (0, 80) is beyond the top edge.
    const ents = [ent(1, 0, 0), ent(2, 2, 2, 'item'), ent(3, 85, 0), ent(4, 0, 80)];
    layer.update([], ents, 1000, new Set());
    const opts = { bots: true, entities: true, labels: false, drawDistance: 192, centre: { x: 0, z: 0 }, fov: 12 };
    layer.showFor(0, cam.position, opts);
    expect(layer.instances).toBe(4);
    layer.showFor(0, cam.position, { ...opts, drops: false });
    expect(layer.instances).toBe(3);
    layer.showFor(0, cam.position, { ...opts, frustum });
    expect(layer.instances).toBe(3);
    expect(layer.detailOf('0:4')).toBeNull();
    const many = Array.from({ length: MAX_FAR_BOXES + 50 }, (_, i) => ent(100 + i, (i % 60) - 30, Math.floor(i / 60) - 15));
    layer.update([], many, 2000, new Set());
    layer.showFor(0, cam.position, opts);
    expect(layer.instances).toBe(MAX_FAR_BOXES);
    layer.dispose();
  });
});
