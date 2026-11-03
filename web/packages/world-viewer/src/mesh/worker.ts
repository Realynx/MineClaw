// The mesh worker: decodes MCWC payloads (raw-deflate inflate), keeps the columns, meshes sections and answers world
// queries. Requests run strictly in order (an `apply` awaits its inflates before the next message is handled).

import { MeshEngine } from './engine';
import type { WorkerRequest, WorkerResponse } from './protocol';
import { meshBuffers } from './sink';
import type { SectionMesh } from './mesher';

const engine = new MeshEngine();
interface WorkerScope {
  postMessage(message: unknown, transfer: Transferable[]): void;
  onmessage: ((ev: MessageEvent<WorkerRequest>) => void) | null;
}
const scope = self as unknown as WorkerScope;
let queue: Promise<void> = Promise.resolve();

async function handle(m: WorkerRequest): Promise<void> {
  try {
    switch (m.op) {
      case 'configure':
        engine.configure(m.version, m.assets, m.textured);
        return reply({ id: m.id, ok: true, result: null });
      case 'apply':
        return reply({ id: m.id, ok: true, result: await engine.apply(m.buf) });
      case 'touch':
        return reply({ id: m.id, ok: true, result: engine.touch(m.dim, m.cols) });
      case 'patch':
        return reply({ id: m.id, ok: true, result: engine.patch(m.dim, m.blocks) });
      case 'drop':
        return reply({ id: m.id, ok: true, result: engine.drop(m.dim, m.cols) });
      case 'mesh': {
        const meshes = engine.mesh(m.dim, m.sections, m.cut);
        const transfer = meshes.flatMap((s: SectionMesh) => [...meshBuffers(s.solid), ...meshBuffers(s.translucent)]);
        return reply({ id: m.id, ok: true, result: meshes }, transfer);
      }
      case 'pick':
        return reply({ id: m.id, ok: true, result: engine.pick(m.dim, m.o, m.d, m.max, m.cut) });
      case 'view':
        return reply({ id: m.id, ok: true, result: engine.viewInfo(m.dim, m.cam, m.body, m.cut) });
      case 'clear':
        engine.clear();
        return reply({ id: m.id, ok: true, result: null });
    }
  } catch (e) {
    reply({ id: m.id, ok: false, error: e instanceof Error ? e.message : String(e) });
  }
}

function reply(r: WorkerResponse, transfer: Transferable[] = []): void {
  scope.postMessage(r, transfer);
}

scope.onmessage = (ev: MessageEvent<WorkerRequest>) => {
  const m = ev.data;
  queue = queue.then(() => handle(m));
};
