/**
 * Turning and mirroring a component's layout. Positions move inside the box (and port cells with it, just outside the
 * box), metas move with their blocks through the shared orientation table, so a turned gate reads the same as a
 * selection the studio turns.
 */
import type { Cell, Pos, RedstoneVersion } from '../api.js';
import { mirrorMeta, mirrorSide, rotateMeta, turnSide } from '../orientation.js';
import type { ComponentLayout, Port } from './types.js';

/** Mirrors first, in the layout's own frame, then turns clockwise seen from above (types.ts). */
export function transformLayout(
  l: ComponentLayout,
  quarterTurns: number,
  mirror: 'x' | 'z' | null,
  v: RedstoneVersion,
): ComponentLayout {
  const q = ((Math.trunc(quarterTurns) % 4) + 4) % 4;
  const [sx, sy, sz] = l.size;
  // A position mirrored inside the box; a port cell just outside stays just outside on the other face. One clockwise
  // quarter turn then maps north to east: (x, z) -> (depth - 1 - z, x), where depth is the box's z size at that turn.
  const move = (p: Pos | Cell): Pos => {
    let x = mirror === 'x' ? sx - 1 - p[0] : p[0];
    let z = mirror === 'z' ? sz - 1 - p[2] : p[2];
    let depth = sz;
    let width = sx;
    for (let i = 0; i < q; i++) {
      [x, z] = [depth - 1 - z, x];
      [width, depth] = [depth, width];
    }
    return [x, p[1], z];
  };
  const cells = l.cells.map((c): Cell => {
    const [x, y, z] = move(c);
    const meta = mirror ? mirrorMeta(c[3], c[4], mirror, v) : c[4];
    return [x, y, z, c[3], rotateMeta(c[3], meta, q, v)];
  });
  cells.sort((a, b) => a[1] - b[1] || a[2] - b[2] || a[0] - b[0]);
  const ports = l.ports.map((p): Port => ({
    name: p.name,
    kind: p.kind,
    at: move(p.at),
    side: turnSide(mirror ? mirrorSide(p.side, mirror) : p.side, q),
  }));
  return { size: q % 2 === 1 ? [sz, sy, sx] : [sx, sy, sz], cells, ports };
}
