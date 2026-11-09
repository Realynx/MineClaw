export interface PlayerCollisionBox { minX: number; minY: number; minZ: number; maxX: number; maxY: number; maxZ: number }
export interface PlayerCollisionResult {
  boxes: readonly PlayerCollisionBox[];
  loaded: boolean;
  medium?: 'water' | 'lava' | 'web' | 'ladder' | null;
  slipperiness?: number;
}
export type CollisionCell = { id: number; meta: number };
export type CollisionLookup = (x: number, y: number, z: number) => CollisionCell;
