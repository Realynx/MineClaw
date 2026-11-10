/**
 * What "proven" means for a library component: its spec passes in every version it lists, in every rotation, both
 * mirrors and with all three feeders (rules L2 and L7 of report 4.5).
 */
import type { RedstoneVersion } from '../../src/api.js';
import { transformLayout } from '../../src/library/transform.js';
import type { ComponentDef, ComponentLayout, Feeder } from '../../src/library/types.js';
import { verifyComponent } from '../../src/library/verify.js';

export const FEEDERS: readonly Feeder[] = ['straight', 'spurLeft', 'spurRight'];

/** Every way the studio can place a layout: four turns, each plain and mirrored across x and across z. */
export const TRANSFORMS: ReadonlyArray<{ turns: number; mirror: 'x' | 'z' | null }> = [0, 1, 2, 3].flatMap((turns) =>
  ([null, 'x', 'z'] as const).map((mirror) => ({ turns, mirror })),
);

/** The versions a component lists, each with its layout. */
export function listed(def: ComponentDef): Array<[RedstoneVersion, ComponentLayout]> {
  return (Object.entries(def.layouts) as Array<[RedstoneVersion, ComponentLayout | undefined]>).filter(
    (e): e is [RedstoneVersion, ComponentLayout] => e[1] !== undefined,
  );
}

/** Every failure of one listed version, labelled with the placement and the feeder; empty when the spec is proven. */
export function prove(def: ComponentDef, v: RedstoneVersion): string[] {
  const layout = def.layouts[v];
  if (!layout) return [`${def.id} has no layout for ${v}`];
  const failures: string[] = [];
  for (const { turns, mirror } of TRANSFORMS) {
    const placed: ComponentDef = { ...def, layouts: { [v]: transformLayout(layout, turns, mirror, v) } };
    for (const feeder of FEEDERS) {
      const result = verifyComponent(placed, v, feeder);
      for (const f of result.failures) failures.push(`${turns} turns, mirror ${mirror ?? '-'}, ${feeder}: ${f}`);
    }
  }
  return failures;
}
