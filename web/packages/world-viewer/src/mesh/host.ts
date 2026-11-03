// The main thread's handle on the mesh engine: a Web Worker (default) or the same engine in-thread.

import type { AtlasMap } from '../atlas/types';
import type { GameVersion } from '../world/blocks';
import type { ApplyResult } from './columns';
import { MeshEngine, type PatchResult, type TouchCol } from './engine';
import type { CutParams, SectionMesh } from './mesher';
import type { WorkerRequest, WorkerResponse } from './protocol';
import type { PickHit, V3, ViewInfo } from './query';

type Dirty = Array<[number, number, number]>;

export interface MeshHost {
  configure(version: GameVersion, atlas: AtlasMap | null): Promise<void>;
  /** Decodes and stores an MCWC payload; the buffer is transferred (unusable afterwards). */
  apply(buf: ArrayBuffer): Promise<ApplyResult>;
  touch(dim: number, cols: TouchCol[]): Promise<Dirty>;
  drop(dim: number, cols: Array<[number, number]>): Promise<Dirty>;
  /** Block changes applied in place; the sections to re-mesh and the columns not held. */
  patch(dim: number, blocks: Array<[number, number, number, number, number]>): Promise<PatchResult>;
  mesh(dim: number, sections: Dirty, cut: CutParams | null): Promise<SectionMesh[]>;
  pick(dim: number, o: V3, d: V3, max: number, cut: CutParams | null): Promise<PickHit | null>;
  viewInfo(dim: number, cam: V3, body: V3 | null, cut: CutParams | null): Promise<ViewInfo>;
  clear(): Promise<void>;
  dispose(): void;
}

/** The engine on the calling thread (tests; environments without module workers). */
export class InlineMeshHost implements MeshHost {
  readonly engine = new MeshEngine();
  private chain: Promise<unknown> = Promise.resolve();
  private run<T>(f: () => T | Promise<T>): Promise<T> {
    const p = this.chain.then(f);
    this.chain = p.catch(() => undefined);
    return p;
  }
  configure(version: GameVersion, atlas: AtlasMap | null) { return this.run(() => this.engine.configure(version, atlas)); }
  apply(buf: ArrayBuffer) { return this.run(() => this.engine.apply(buf)); }
  touch(dim: number, cols: TouchCol[]) { return this.run(() => this.engine.touch(dim, cols)); }
  drop(dim: number, cols: Array<[number, number]>) { return this.run(() => this.engine.drop(dim, cols)); }
  patch(dim: number, blocks: Array<[number, number, number, number, number]>) { return this.run(() => this.engine.patch(dim, blocks)); }
  mesh(dim: number, sections: Dirty, cut: CutParams | null) { return this.run(() => this.engine.mesh(dim, sections, cut)); }
  pick(dim: number, o: V3, d: V3, max: number, cut: CutParams | null) { return this.run(() => this.engine.pick(dim, o, d, max, cut)); }
  viewInfo(dim: number, cam: V3, body: V3 | null, cut: CutParams | null) { return this.run(() => this.engine.viewInfo(dim, cam, body, cut)); }
  clear() { return this.run(() => this.engine.clear()); }
  dispose(): void {
    this.engine.clear();
  }
}

type Pending = { resolve: (v: unknown) => void; reject: (e: Error) => void };
type Omit2<T> = T extends unknown ? Omit<T, 'id'> : never;

export class WorkerMeshHost implements MeshHost {
  private nextId = 1;
  private pending = new Map<number, Pending>();
  private disposed = false;

  constructor(private readonly worker: Worker) {
    worker.onmessage = (ev: MessageEvent<WorkerResponse>) => {
      const r = ev.data;
      const p = this.pending.get(r.id);
      if (!p) return;
      this.pending.delete(r.id);
      if (r.ok) p.resolve(r.result);
      else p.reject(new Error(r.error));
    };
    worker.onerror = (ev) => {
      const err = new Error(`mesh worker failed: ${ev.message ?? 'unknown error'}`);
      for (const p of this.pending.values()) p.reject(err);
      this.pending.clear();
    };
  }

  private send<T>(m: Omit2<WorkerRequest>, transfer: Transferable[] = []): Promise<T> {
    // After disposal requests never settle: the views asking are being torn down too (no unhandled rejections).
    if (this.disposed) return new Promise<T>(() => undefined);
    const id = this.nextId++;
    return new Promise<T>((resolve, reject) => {
      this.pending.set(id, { resolve: resolve as (v: unknown) => void, reject });
      this.worker.postMessage({ ...m, id }, transfer);
    });
  }

  configure(version: GameVersion, atlas: AtlasMap | null) { return this.send<void>({ op: 'configure', version, atlas }); }
  apply(buf: ArrayBuffer) { return this.send<ApplyResult>({ op: 'apply', buf }, [buf]); }
  touch(dim: number, cols: TouchCol[]) { return this.send<Dirty>({ op: 'touch', dim, cols }); }
  drop(dim: number, cols: Array<[number, number]>) { return this.send<Dirty>({ op: 'drop', dim, cols }); }
  patch(dim: number, blocks: Array<[number, number, number, number, number]>) { return this.send<PatchResult>({ op: 'patch', dim, blocks }); }
  mesh(dim: number, sections: Dirty, cut: CutParams | null) { return this.send<SectionMesh[]>({ op: 'mesh', dim, sections, cut }); }
  pick(dim: number, o: V3, d: V3, max: number, cut: CutParams | null) { return this.send<PickHit | null>({ op: 'pick', dim, o, d, max, cut }); }
  viewInfo(dim: number, cam: V3, body: V3 | null, cut: CutParams | null) { return this.send<ViewInfo>({ op: 'view', dim, cam, body, cut }); }
  clear() { return this.send<void>({ op: 'clear' }); }

  dispose(): void {
    if (this.disposed) return;
    this.disposed = true;
    this.worker.terminate();
    this.pending.clear();
  }
}

/** The default worker (bundlers emit it beside the library). Falls back to the in-thread engine where workers fail. */
export function createMeshHost(createWorker?: () => Worker): MeshHost {
  try {
    if (typeof Worker === 'undefined') return new InlineMeshHost();
    const w = createWorker ? createWorker() : new Worker(new URL('./worker.ts', import.meta.url), { type: 'module', name: 'mineclaw-mesher' });
    return new WorkerMeshHost(w);
  } catch {
    return new InlineMeshHost();
  }
}
