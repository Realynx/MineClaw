// The mesh worker: decodes MCWC payloads (raw-deflate inflate), keeps the columns, meshes sections and answers world
// queries. Requests run strictly in order (an `apply` awaits its inflates before the next message is handled).

import { MeshEngine } from './engine';
import type { WorkerRequest, WorkerResponse } from './protocol';
import { meshBuffers } from './sink';
import type { SectionMesh } from './mesher';
import { holoBuffers } from './holo';

const engine = new MeshEngine();
interface WorkerScope {
  postMessage(message: unknown, transfer: Transferable[]): void;
  onmessage: ((ev: MessageEvent<WorkerRequest>) => void) | null;
}
const scope = self as unknown as WorkerScope;
let queue: Promise<void> = Promise.resolve();

type Result = Extract<WorkerResponse, { ok: true }>['result'];
/** A request's answer and the buffers it hands over (meshes travel without a copy). */
type Answer = { result: Result; transfer?: Transferable[] };
type Handlers = { [K in WorkerRequest['op']]: (m: Extract<WorkerRequest, { op: K }>) => Answer | Promise<Answer> };

const HANDLERS: Handlers = {
  configure: (m) => {
    engine.configure(m.version, m.assets, m.textured, m.fastLeaves);
    return { result: null };
  },
  apply: async (m) => ({ result: await engine.apply(m.buf) }),
  touch: (m) => ({ result: engine.touch(m.dim, m.cols) }),
  patch: (m) => ({ result: engine.patch(m.dim, m.blocks) }),
  drop: (m) => ({ result: engine.drop(m.dim, m.cols) }),
  mesh: (m) => {
    const meshes = engine.mesh(m.dim, m.sections, m.cut);
    return { result: meshes, transfer: meshes.flatMap((s: SectionMesh) => [...meshBuffers(s.solid), ...meshBuffers(s.translucent)]) };
  },
  pick: (m) => ({ result: engine.pick(m.dim, m.o, m.d, m.max, m.cut) }),
  blocksAt: (m) => ({ result: engine.blocksAt(m.dim, m.pts) }),
  scan: (m) => ({ result: engine.scan(m.dim, m.min, m.max, m.ids, m.maxHits) }),
  stacks: (m) => ({ result: engine.stacks(m.dim, m.min, m.max, m.skip, m.depth, m.above, m.maxHits) }),
  lights: (m) => ({ result: engine.lights(m.dim, m.pts) }),
  occluded: (m) => ({ result: engine.occluded(m.dim, m.cam, m.bodies) }),
  view: (m) => ({ result: engine.viewInfo(m.dim, m.cam, m.body, m.cut) }),
  blockEntities: (m) => ({ result: engine.setBlockEntities(m.dim, m.cols) }),
  holo: (m) => {
    const rs = engine.meshHolo(m.dim, m.reqs);
    return { result: rs, transfer: holoBuffers(rs) };
  },
  clear: () => {
    engine.clear();
    return { result: null };
  },
};

async function handle(m: WorkerRequest): Promise<void> {
  try {
    const run = HANDLERS[m.op] as ((m: WorkerRequest) => Answer | Promise<Answer>) | undefined;
    if (!run) return;
    const { result, transfer } = await run(m);
    reply({ id: m.id, ok: true, result }, transfer);
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
