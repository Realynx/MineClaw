import { DEFAULT_VIEWER_QUALITY, getViewerQuality, PROFILES, resetViewerQuality } from '@mineclaw/world-viewer/settings';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import {
  AUTO_FIELDS,
  concreteFields,
  DEFAULT_SETTINGS,
  effectiveQuality,
  parseSettings,
  PRESET_FIELDS,
  SETTINGS_KEY,
  SETTINGS_VERSION,
  withField,
  withPreset,
  type ViewerSettings,
} from '@/features/settings/schema';
import {
  _resetForTests,
  getViewerSettings,
  initViewerSettings,
  resetViewerSettings,
  saveViewerSettings,
  setField,
  setPreset,
} from '@/features/settings/store';

const SW = PROFILES.software;
const GPU = PROFILES.gpu;

describe('viewer settings schema', () => {
  it('defaults to Auto: today’s viewer with mobs and players as far as the ground', () => {
    expect(DEFAULT_SETTINGS.preset).toBe('auto');
    expect(DEFAULT_SETTINGS.v).toBe(SETTINGS_VERSION);
    const q = effectiveQuality(DEFAULT_SETTINGS);
    // Exactly the viewer's own defaults (what an app that never stored anything renders with).
    expect(q).toEqual(DEFAULT_VIEWER_QUALITY);
    expect(q.entityDistance).toBe('ground');
    expect(q.renderScale).toBeNull();
    expect(parseSettings(undefined)).toEqual(DEFAULT_SETTINGS);
    expect(parseSettings('garbage')).toEqual(DEFAULT_SETTINGS);
    expect(parseSettings([1, 2])).toEqual(DEFAULT_SETTINGS);
  });

  it('round-trips through JSON', () => {
    const s: ViewerSettings = { v: 1, preset: 'custom', custom: { ...PRESET_FIELDS.low, entityDistance: 64, fpsCap: 40 }, perfOverlay: true };
    expect(parseSettings(JSON.parse(JSON.stringify(s)))).toEqual(s);
  });

  it('migrates: an unknown (newer) version starts over, an unversioned flat object becomes Custom', () => {
    expect(parseSettings({ v: 99, preset: 'low' })).toEqual(DEFAULT_SETTINGS);
    expect(parseSettings({ v: 'one', preset: 'low' })).toEqual(DEFAULT_SETTINGS);
    const v0 = parseSettings({ drawDistance: 96, entityDistance: 32, clouds: true, perfOverlay: true });
    expect(v0.preset).toBe('custom');
    expect(v0.perfOverlay).toBe(true);
    expect(v0.custom).toMatchObject({ drawDistance: 96, entityDistance: 32, clouds: true });
    // Unknown keys are dropped, missing ones take the defaults.
    expect(v0.custom.fog).toBe(true);
    expect('bogus' in parseSettings({ bogus: 1 }).custom).toBe(false);
    expect(parseSettings({ preset: 'high' }).preset).toBe('high');
  });

  it('validates and clamps field by field (a bad field takes its default, the others stay)', () => {
    const s = parseSettings({
      v: 1,
      preset: 'nonsense',
      custom: { drawDistance: 9999, coverCap: 1, entityDistance: 3, renderScale: 0.1, fpsCap: 45, entityModelDistance: 500, fog: 'yes', labels: false },
      perfOverlay: 'no',
    });
    expect(s.preset).toBe('auto');
    expect(s.perfOverlay).toBe(false);
    expect(s.custom).toMatchObject({ drawDistance: 256, coverCap: 64, entityDistance: 16, renderScale: 0.5, fpsCap: 40, entityModelDistance: 128, fog: true, labels: false });
    expect(parseSettings({ v: 1, custom: { fpsCap: 0 } }).custom.fpsCap).toBeNull();
    expect(parseSettings({ v: 1, custom: { fpsCap: 1000 } }).custom.fpsCap).toBe(60);
    expect(parseSettings({ v: 1, custom: { drawDistance: Number.NaN } }).custom.drawDistance).toBe(PRESET_FIELDS.medium.drawDistance);
    expect(parseSettings({ v: 1, custom: 'x' }).custom).toEqual(PRESET_FIELDS.medium);
  });
});

describe('presets → fields', () => {
  it('each fixed preset applies its own fields; Auto leaves the profile-dependent ones to the detected profile', () => {
    for (const p of ['low', 'medium', 'high'] as const) expect(effectiveQuality({ ...DEFAULT_SETTINGS, preset: p })).toEqual({ ...PRESET_FIELDS[p], perfOverlay: false });
    expect(effectiveQuality(DEFAULT_SETTINGS)).toEqual({ ...AUTO_FIELDS, perfOverlay: false });
    expect(concreteFields(DEFAULT_SETTINGS, SW)).toMatchObject({ renderScale: 1, clouds: false, entityModelDistance: 24 });
    expect(concreteFields(DEFAULT_SETTINGS, GPU)).toMatchObject({ renderScale: 2, clouds: true, entityModelDistance: 64 });
    // Low is cheaper than Medium, Medium than High.
    expect(PRESET_FIELDS.low.drawDistance).toBeLessThan(PRESET_FIELDS.medium.drawDistance);
    expect(PRESET_FIELDS.medium.drawDistance).toBeLessThan(PRESET_FIELDS.high.drawDistance);
    for (const p of ['low', 'medium', 'high'] as const) expect(PRESET_FIELDS[p].entityDistance).toBe('ground');
  });

  it('editing any field switches to Custom from what was shown', () => {
    const s = withField({ ...DEFAULT_SETTINGS, preset: 'high' }, 'entityDistance', 48, SW);
    expect(s.preset).toBe('custom');
    expect(s.custom).toEqual({ ...PRESET_FIELDS.high, entityDistance: 48 });
    // From Auto the profile's values become concrete.
    const a = withField(DEFAULT_SETTINGS, 'fog', false, SW);
    expect(a.custom).toMatchObject({ fog: false, renderScale: 1, clouds: false, entityModelDistance: 24, drawDistance: 128 });
    // Clamped on the way in.
    expect(withField(DEFAULT_SETTINGS, 'drawDistance', 4000, SW).custom.drawDistance).toBe(256);
  });

  it('choosing Custom keeps the picture; a preset keeps the Custom fields for later', () => {
    const c = withPreset({ ...DEFAULT_SETTINGS, preset: 'low' }, 'custom', GPU);
    expect(c).toMatchObject({ preset: 'custom', custom: PRESET_FIELDS.low });
    const back = withPreset(c, 'high', GPU);
    expect(back.preset).toBe('high');
    expect(back.custom).toEqual(PRESET_FIELDS.low);
  });
});

describe('viewer settings store', () => {
  beforeEach(() => {
    localStorage.clear();
    _resetForTests();
    resetViewerQuality();
  });
  afterEach(() => vi.restoreAllMocks());

  it('loads what was stored and pushes it to every viewer (the world viewer’s quality store) on init', () => {
    localStorage.setItem(SETTINGS_KEY, JSON.stringify({ v: 1, preset: 'low', custom: PRESET_FIELDS.medium, perfOverlay: true }));
    initViewerSettings();
    expect(getViewerSettings().preset).toBe('low');
    expect(getViewerQuality()).toMatchObject({ drawDistance: 64, drops: false, perfOverlay: true, entityDistance: 'ground' });
  });

  it('saves and applies live; a reset goes back to Auto', () => {
    initViewerSettings();
    setField('entityDistance', 32, { software: true });
    expect(getViewerQuality().entityDistance).toBe(32);
    expect(JSON.parse(localStorage.getItem(SETTINGS_KEY)!)).toMatchObject({ v: 1, preset: 'custom', custom: { entityDistance: 32 } });
    setPreset('high', { software: false });
    expect(getViewerQuality()).toMatchObject({ drawDistance: 256, renderScale: 2, clouds: true });
    resetViewerSettings();
    expect(getViewerQuality()).toEqual(DEFAULT_VIEWER_QUALITY);
    expect(getViewerSettings().preset).toBe('auto');
  });

  it('works without storage: defaults on load, changes still apply for the session', () => {
    vi.spyOn(Storage.prototype, 'getItem').mockImplementation(() => {
      throw new Error('SecurityError');
    });
    vi.spyOn(Storage.prototype, 'setItem').mockImplementation(() => {
      throw new Error('QuotaExceededError');
    });
    initViewerSettings();
    expect(getViewerSettings()).toEqual(DEFAULT_SETTINGS);
    expect(() => saveViewerSettings({ ...DEFAULT_SETTINGS, preset: 'low' })).not.toThrow();
    expect(getViewerSettings().preset).toBe('low');
    expect(getViewerQuality().drawDistance).toBe(64);
  });

  it('ignores a corrupt stored value', () => {
    localStorage.setItem(SETTINGS_KEY, '{not json');
    initViewerSettings();
    expect(getViewerSettings()).toEqual(DEFAULT_SETTINGS);
  });

  it('follows a change made in another tab', () => {
    initViewerSettings();
    const next = JSON.stringify({ v: 1, preset: 'medium', custom: PRESET_FIELDS.medium, perfOverlay: false });
    window.dispatchEvent(new StorageEvent('storage', { key: SETTINGS_KEY, newValue: next }));
    expect(getViewerSettings().preset).toBe('medium');
    expect(getViewerQuality().entityModelDistance).toBe(32);
  });
});
