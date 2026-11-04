/**
 * Version-preferred mine designs (./mine-presets.json, shared with the bot's MinePresets.cs): template steps on the plot in
 * absolute y. Inserted as one edit; the session is then tagged `preferredFor` its version.
 */
import data from './mine-presets.json';
import { fromStep, type MineCell, type MineCells, type P3 } from './mineTemplates';
import type { MineTerrain } from './mineTerrain';

export interface MinePreset {
  id: string;
  version: string;
  name: string;
  ore: string;
  y: number;
  notes: string;
  steps: Array<Record<string, unknown>>;
}

export const MINE_PRESETS: readonly MinePreset[] = (data as unknown as { presets: MinePreset[] }).presets;

/** The presets preferred for a version. */
export const presetsFor = (version: string): MinePreset[] => MINE_PRESETS.filter((p) => p.version === version);

/** A preset's cells on a terrain (its steps in order), with the first step's entry. */
export function presetCells(p: MinePreset, terrain: MineTerrain | null): MineCells {
  const cells: MineCell[] = [];
  const path: P3[] = [];
  let entry: P3 | null = null;
  for (const step of p.steps) {
    const c = fromStep(step, terrain);
    cells.push(...c.cells);
    path.push(...c.path);
    entry ??= c.entry;
  }
  return { cells, entry: entry ?? [0, 0, 0], path, label: p.name };
}
