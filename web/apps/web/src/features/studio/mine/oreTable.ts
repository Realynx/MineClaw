/**
 * The ore heights of each game version (./ore-heights.json, shared with the bot's OreTable.cs): the mine terrain generator
 * and the studio's ore-height guide read the same file.
 */
import data from './ore-heights.json';

export interface OreBand {
  id: string;
  name: string;
  block: number;
  meta: number;
  min: number;
  max: number;
  veins: number;
  size: number;
  shape: 'uniform' | 'triangle';
  center: number;
  spread: number;
  best: number;
  color: string | null;
  note: string | null;
}

export interface OreVersion {
  version: string;
  ores: OreBand[];
  pockets: OreBand[];
  bedrockFloor: number;
  bedrockTop: number;
  lava: number;
}

interface RawFeature {
  id: string;
  name: string;
  block: number;
  meta: number;
  min: number;
  max: number;
  veins: number;
  size: number;
  shape?: string;
  center?: number;
  spread?: number;
  best?: number;
  color?: string;
  note?: string;
}

const band = (f: RawFeature): OreBand => ({
  id: f.id,
  name: f.name,
  block: f.block,
  meta: f.meta,
  min: f.min,
  max: f.max,
  veins: f.veins,
  size: f.size,
  shape: f.shape === 'triangle' ? 'triangle' : 'uniform',
  center: f.center ?? 0,
  spread: f.spread ?? 0,
  best: f.best ?? Math.trunc((f.min + f.max) / 2),
  color: f.color ?? null,
  note: f.note ?? null,
});

const raw = data as unknown as {
  sources: string[];
  versions: Record<string, { bedrock: { floor: number; top: number }; lava: number; ores: RawFeature[]; pockets: RawFeature[] }>;
};

const TABLES = new Map<string, OreVersion>(
  Object.entries(raw.versions).map(([v, t]) => [
    v,
    {
      version: v,
      ores: t.ores.map(band),
      pockets: t.pockets.map(band),
      bedrockFloor: t.bedrock.floor,
      bedrockTop: t.bedrock.top,
      lava: t.lava,
    },
  ]),
);

/** The file's public sources. */
export const ORE_SOURCES: readonly string[] = raw.sources;

/** The table of a version (anything else: b1.8.1). */
export function oreTable(version: string): OreVersion {
  return TABLES.get(version) ?? TABLES.get('b1.8.1')!;
}

/** The ore at a block state, or null. */
export function oreOf(version: string, id: number, meta: number): OreBand | null {
  return oreTable(version).ores.find((o) => o.block === id && (o.meta === meta || o.meta === 0)) ?? null;
}

/** Whether y lies in an ore's band (its generated range). */
export const inBand = (o: OreBand, y: number): boolean => y >= o.min && y <= o.max;

/**
 * The relative chance of an ore vein starting at y: 1 inside a uniform band, the triangle's weight (1 at the centre) for a
 * triangular one, 0 outside.
 */
export function bandWeight(o: OreBand, y: number): number {
  if (!inBand(o, y)) return 0;
  if (o.shape !== 'triangle' || o.spread <= 0) return 1;
  return Math.max(0, 1 - Math.abs(y - o.center) / o.spread);
}
