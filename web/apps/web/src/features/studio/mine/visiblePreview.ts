import { keyOf, N6, type V3 } from '../canvas';
import { moduleCells, type MineModuleInstance } from './modules';
import type { MinePreview } from './minePreview';

/** Visibility is an editor mask. Hidden pieces keep their excavation; overlap with a shown piece remains visible. */
export function visibleMinePreview(preview: MinePreview, modules: readonly MineModuleInstance[]): MinePreview {
  if (!modules.some((m) => m.visible === false)) return preview;
  const hidden = new Set<number>(), shown = new Set<number>();
  for (const m of modules) {
    const keys = m.visible === false ? hidden : shown;
    for (const [x, y, z] of moduleCells(m.spec, m.at).cells) keys.add(keyOf(x, y, z));
  }
  for (const k of shown) hidden.delete(k);
  const planned = preview.planned.filter((p) => !hidden.has(keyOf(...p)));
  // Reuse the already discovered ore graph: changing an eye toggle never samples/generates terrain again.
  const ores = new Map(preview.veins.map((p) => [p.join(','), p])), found = new Set<string>(), queue: V3[] = [];
  const expose = (p: V3) => {
    const key = p.join(','), ore = ores.get(key);
    if (!ore || found.has(key)) return;
    found.add(key); queue.push(ore);
  };
  for (const p of planned) { expose(p); for (const [x, y, z] of N6) expose([p[0] + x, p[1] + y, p[2] + z]); }
  for (let i = 0; i < queue.length; i++) {
    const p = queue[i]!;
    for (const [x, y, z] of N6) expose([p[0] + x, p[1] + y, p[2] + z]);
  }
  return { planned, veins: queue };
}
