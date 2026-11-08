/**
 * Version-preferred mine designs (./mine-presets.json, shared with the bot's MinePresets.cs): template steps on the plot in
 * absolute y. Inserted as one edit; the session is then tagged `preferredFor` its version.
 */
import data from './mine-presets.json';
import { fromStep, spiralStep, stripStep, type MineCell, type MineCells, type P3 } from './mineTemplates';
import type { MineTerrain } from './mineTerrain';
import { defaultModule, type MineModuleSpec } from './modules';
import type { V3 } from '../canvas';

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

/** Keep each preset step parametric: editing, moving, deleting and saving work exactly like palette components. */
export function presetModules(p: MinePreset, terrain: MineTerrain | null): Array<{ spec: MineModuleSpec; at: V3 }> {
  return p.steps.map((step) => {
    if (String(step.template).startsWith('spiral')) {
      const s = spiralStep(step, terrain), size = s.size ?? 4, offset = Math.floor((size - 1) / 2);
      return { at: [s.x + offset, s.top, s.z + offset], spec: { ...defaultModule('spiral'), size, depth: s.top - s.bottom,
        clockwise: s.cw ?? true, core: s.core ?? size - 2, torchEvery: s.torchEvery ?? 0, landingDepths: (s.landings ?? []).map((y) => s.top - y) } };
    }
    const s = stripStep(step), length = s.length ?? 32, width = s.width ?? 1;
    const half = Math.floor((length - 1) / 2), across = Math.floor((width - 1) / 2), alongX = s.axis !== 'z';
    return { at: [s.x + (alongX ? half : across), s.y, s.z + (alongX ? across : half)], spec: {
      ...defaultModule((s.branchLength ?? 0) > 0 ? 'branches' : 'tunnel'), length, width, height: s.height ?? 2,
      branchLength: s.branchLength ?? 8, spacing: s.spacing ?? 4, sides: s.sides ?? 'both', torchEvery: s.torchEvery ?? 0,
      rotation: alongX ? 0 : 1,
    } };
  });
}

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
