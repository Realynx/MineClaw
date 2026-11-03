/**
 * Viewer quality settings: the one typed, versioned, validated source of truth for how every world view of the explorer
 * renders on this device (hardware-dependent, so per device in localStorage, never on the server). A preset (Auto, Low,
 * Medium, High) or Custom fields; editing any field switches to Custom. Auto is the adaptive profile the viewer detects
 * (software WebGL or GPU), so three of its fields are "the profile's own" (null) until made concrete for display or editing.
 *
 * `parseSettings` never throws: unknown versions fall back to the defaults, an unversioned (v0) flat object is read as Custom
 * fields, and every field is validated and clamped on its own (a bad field takes its default, the rest are kept).
 */
import type { QualityProfile, ViewerQuality } from '@mineclaw/world-viewer/settings';
import { z } from 'zod';

export const SETTINGS_VERSION = 1;
export const SETTINGS_KEY = 'mcx-viewer-settings';

export const PRESETS = ['auto', 'low', 'medium', 'high', 'custom'] as const;
export type Preset = (typeof PRESETS)[number];
export type FixedPreset = Exclude<Preset, 'auto' | 'custom'>;

/** The quality fields with every value concrete (what Custom stores and the panel shows). */
export type QualityFields = Omit<ViewerQuality, 'perfOverlay' | 'renderScale' | 'clouds' | 'entityModelDistance'> & {
  renderScale: number;
  clouds: boolean;
  entityModelDistance: number;
};
export type FieldKey = keyof QualityFields;

export interface ViewerSettings {
  v: typeof SETTINGS_VERSION;
  preset: Preset;
  /** The Custom fields (kept while another preset is chosen, so switching back restores them). */
  custom: QualityFields;
  /** The fps readout over every world view (a diagnostic, not part of any preset). */
  perfOverlay: boolean;
}

/** Bounds and steps the panel offers (the renderer clamps to the same ranges). */
export const LIMITS = {
  drawDistance: { min: 32, max: 256, step: 16 },
  coverCap: { min: 64, max: 192, step: 16 },
  entityDistance: { min: 16, max: 256, step: 16 },
  renderScale: { min: 0.5, max: 2, step: 0.25 },
  entityModelDistance: { min: 0, max: 128, step: 8 },
} as const;
export const FPS_CAPS = [30, 40, 60] as const;

/** Low / Medium / High. Every preset keeps mobs and players as far as the ground. */
export const PRESET_FIELDS: Record<FixedPreset, QualityFields> = {
  low: {
    drawDistance: 64,
    coverCap: 128,
    entityDistance: 'ground',
    drops: false,
    renderScale: 0.75,
    fpsCap: null,
    adaptive: true,
    animatedTextures: false,
    clouds: false,
    weather: false,
    ambientOcclusion: true,
    fog: true,
    entityModelDistance: 16,
    labels: true,
    bubbles: true,
    xray: false,
  },
  medium: {
    drawDistance: 128,
    coverCap: 192,
    entityDistance: 'ground',
    drops: true,
    renderScale: 1,
    fpsCap: null,
    adaptive: true,
    animatedTextures: true,
    clouds: false,
    weather: true,
    ambientOcclusion: true,
    fog: true,
    entityModelDistance: 32,
    labels: true,
    bubbles: true,
    xray: true,
  },
  high: {
    drawDistance: 256,
    coverCap: 192,
    entityDistance: 'ground',
    drops: true,
    renderScale: 2,
    fpsCap: null,
    adaptive: true,
    animatedTextures: true,
    clouds: true,
    weather: true,
    ambientOcclusion: true,
    fog: true,
    entityModelDistance: 64,
    labels: true,
    bubbles: true,
    xray: true,
  },
};

/** Auto: today's adaptive viewer (profile-dependent fields null), with entities as far as the ground. */
export const AUTO_FIELDS: Omit<ViewerQuality, 'perfOverlay'> = {
  drawDistance: 128,
  coverCap: 192,
  entityDistance: 'ground',
  drops: true,
  renderScale: null,
  fpsCap: null,
  adaptive: true,
  animatedTextures: true,
  clouds: null,
  weather: true,
  ambientOcclusion: true,
  fog: true,
  entityModelDistance: null,
  labels: true,
  bubbles: true,
  xray: true,
};

export const DEFAULT_SETTINGS: ViewerSettings = {
  v: SETTINGS_VERSION,
  preset: 'auto',
  custom: PRESET_FIELDS.medium,
  perfOverlay: false,
};

// ------------------------------------------------------------------------------------------------------------ schema

const clampTo = (lim: { min: number; max: number }) => (v: number) => Math.max(lim.min, Math.min(lim.max, v));
const blocks = (lim: { min: number; max: number }, def: number) =>
  z.number().refine(Number.isFinite).transform((v) => clampTo(lim)(Math.round(v))).catch(def);
const flag = (def: boolean) => z.boolean().catch(def);
const fpsCap = z
  .number()
  .nullable()
  .transform((v) => {
    if (v === null || !Number.isFinite(v) || v <= 0) return null;
    // The nearest offered cap.
    return FPS_CAPS.reduce((a, b) => (Math.abs(b - v) < Math.abs(a - v) ? b : a));
  })
  .catch(null);

const D = PRESET_FIELDS.medium;
export const fieldsSchema = z.object({
  drawDistance: blocks(LIMITS.drawDistance, D.drawDistance),
  coverCap: blocks(LIMITS.coverCap, D.coverCap),
  entityDistance: z
    .union([z.literal('ground'), z.number().refine(Number.isFinite).transform((v) => clampTo(LIMITS.entityDistance)(Math.round(v)))])
    .catch('ground'),
  drops: flag(D.drops),
  renderScale: z
    .number()
    .refine(Number.isFinite)
    .transform((v) => clampTo(LIMITS.renderScale)(v))
    .catch(D.renderScale),
  fpsCap,
  adaptive: flag(D.adaptive),
  animatedTextures: flag(D.animatedTextures),
  clouds: flag(D.clouds),
  weather: flag(D.weather),
  ambientOcclusion: flag(D.ambientOcclusion),
  fog: flag(D.fog),
  entityModelDistance: blocks(LIMITS.entityModelDistance, D.entityModelDistance),
  labels: flag(D.labels),
  bubbles: flag(D.bubbles),
  xray: flag(D.xray),
});

export const settingsSchema = z.object({
  v: z.literal(SETTINGS_VERSION),
  preset: z.enum(PRESETS).catch('auto'),
  custom: z.unknown().transform((c) => fieldsSchema.parse(c && typeof c === 'object' ? c : {})),
  perfOverlay: flag(false),
});

/** Reads whatever was stored (or nothing) into valid settings; never throws. */
export function parseSettings(raw: unknown): ViewerSettings {
  if (!raw || typeof raw !== 'object' || Array.isArray(raw)) return DEFAULT_SETTINGS;
  const o = raw as Record<string, unknown>;
  const v = o.v;
  if (v === undefined) {
    // v0: an unversioned flat object of fields (hand-edited, or a pre-release build): Custom with those fields.
    const preset = PRESETS.includes(o.preset as Preset) ? (o.preset as Preset) : 'custom';
    return settingsSchema.parse({ v: SETTINGS_VERSION, preset, custom: o, perfOverlay: o.perfOverlay });
  }
  // A newer (or garbled) version than this app knows: start from the defaults rather than guess.
  if (v !== SETTINGS_VERSION) return DEFAULT_SETTINGS;
  const r = settingsSchema.safeParse(o);
  return r.success ? r.data : DEFAULT_SETTINGS;
}

// ------------------------------------------------------------------------------------------------------------ mapping

/** What the renderer applies (`setViewerQuality`): the preset's fields, Auto's profile-dependent ones left null. */
export function effectiveQuality(s: ViewerSettings): ViewerQuality {
  const f = s.preset === 'auto' ? AUTO_FIELDS : s.preset === 'custom' ? s.custom : PRESET_FIELDS[s.preset];
  return { ...f, perfOverlay: s.perfOverlay };
}

/** The fields as the renderer will use them on this device (Auto's nulls filled from the detected profile). */
export function concreteFields(s: ViewerSettings, profile: Pick<QualityProfile, 'pixelRatioCap' | 'clouds' | 'entityDetailDistance'>): QualityFields {
  const q = effectiveQuality(s);
  return {
    drawDistance: q.drawDistance,
    coverCap: q.coverCap,
    entityDistance: q.entityDistance,
    drops: q.drops,
    renderScale: q.renderScale ?? profile.pixelRatioCap,
    fpsCap: q.fpsCap,
    adaptive: q.adaptive,
    animatedTextures: q.animatedTextures,
    clouds: q.clouds ?? profile.clouds,
    weather: q.weather,
    ambientOcclusion: q.ambientOcclusion,
    fog: q.fog,
    entityModelDistance: q.entityModelDistance ?? profile.entityDetailDistance,
    labels: q.labels,
    bubbles: q.bubbles,
    xray: q.xray,
  };
}

type Profile = Parameters<typeof concreteFields>[1];

/** Editing one field: Custom from what is shown now, with that field changed (validated and clamped). */
export function withField<K extends FieldKey>(s: ViewerSettings, key: K, value: QualityFields[K], profile: Profile): ViewerSettings {
  const custom = fieldsSchema.parse({ ...concreteFields(s, profile), [key]: value });
  return { ...s, preset: 'custom', custom };
}

/** Choosing a preset. Custom keeps what is shown (so picking it never jumps); the others keep the Custom fields for later. */
export function withPreset(s: ViewerSettings, preset: Preset, profile: Profile): ViewerSettings {
  if (preset === 'custom') return { ...s, preset, custom: concreteFields(s, profile) };
  return { ...s, preset };
}

export const PRESET_LABELS: Record<Preset, { label: string; hint: string }> = {
  auto: { label: 'Auto', hint: 'Adapts to this device: tuned for software WebGL, sharper with a GPU; lowers detail to hold its frame rate.' },
  low: { label: 'Low', hint: 'Short distances, lower resolution, no animations or weather: for slow machines.' },
  medium: { label: 'Medium', hint: 'Balanced: 128-block distance at native resolution.' },
  high: { label: 'High', hint: 'Long distance, full-density pixels, clouds and detailed models: for a real GPU.' },
  custom: { label: 'Custom', hint: 'Your own choices below.' },
};
