export interface PlayerCollisionBox { minX: number; minY: number; minZ: number; maxX: number; maxY: number; maxZ: number }
export interface PlayerCollisionResult {
  boxes: readonly PlayerCollisionBox[];
  loaded: boolean;
  medium?: 'water' | 'lava' | 'web' | 'ladder' | null;
  slipperiness?: number;
  /** A ladder (or on 1.12.2 a vine, or an open trapdoor over a ladder) fills the cell at the centre of the box's bottom:
   * the body climbs there. */
  climbable?: boolean;
}
export type CollisionCell = { id: number; meta: number };
export type CollisionLookup = (x: number, y: number, z: number) => CollisionCell;
