import type { BlockEntityDto } from '../types';
// Messages between the main thread and the mesh worker. Every request carries an id; the worker answers in order.

import type { GameVersion } from '../world/blocks';
import type { ApplyResult } from './columns';
import type { PatchResult, TouchCol } from './engine';
import type { CutParams, SectionMesh } from './mesher';
import type { PickHit, V3, ViewInfo } from './query';
import type { MeshAssets } from './source';

export type WorkerRequest =
  | { id: number; op: 'configure'; version: GameVersion; assets: MeshAssets | null; textured: boolean; fastLeaves?: boolean }
  | { id: number; op: 'apply'; buf: ArrayBuffer }
  | { id: number; op: 'touch'; dim: number; cols: TouchCol[] }
  | { id: number; op: 'drop'; dim: number; cols: Array<[number, number]> }
  | { id: number; op: 'patch'; dim: number; blocks: Array<[number, number, number, number, number]> }
  | { id: number; op: 'mesh'; dim: number; sections: Array<[number, number, number]>; cut: CutParams | null }
  | { id: number; op: 'pick'; dim: number; o: V3; d: V3; max: number; cut: CutParams | null }
  | { id: number; op: 'view'; dim: number; cam: V3; body: V3 | null; cut: CutParams | null }
  | { id: number; op: 'lights'; dim: number; pts: number[] }
  | { id: number; op: 'blocksAt'; dim: number; pts: number[] }
  | { id: number; op: 'blockEntities'; dim: number; cols: Array<{ cx: number; cz: number; entities: BlockEntityDto[] }> }
  | { id: number; op: 'clear' };

export type WorkerResponse =
  | { id: number; ok: true; result: null | ApplyResult | SectionMesh[] | PickHit | ViewInfo | Array<[number, number, number]> | PatchResult | number[] }
  | { id: number; ok: false; error: string };
