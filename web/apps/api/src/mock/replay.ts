/**
 * `MOCK_REPLAY_DIR=<dir>` (dev only, with `--mock-bridge`): serve a capture of a real bot instead of the synthetic hive, so a
 * browser sees production-sized payloads (hundreds of columns of real MCWC world, real entities, a real hive tree) over the real
 * gateway. The directory holds the supervisor's replies, saved as-is (snake_case):
 *
 *   status.json  bots.json  tree.json  entities.json  now.json  scripts.json  index.json  overlays.json  events.json  feed.json
 *   chunks_*.bin  (MCWC replies for the index's columns, any batching)   be_*.json (block-entity replies, any batching)
 *
 * Missing files fall back to the synthetic data. Columns not in the capture are answered as unknown (data_len 0).
 */
import { existsSync, readdirSync, readFileSync } from 'node:fs';
import { join } from 'node:path';

type Obj = Record<string, unknown>;

interface Column {
  dim: number;
  cx: number;
  cz: number;
  version: number;
  record: Buffer;
}

export class Replay {
  private readonly json = new Map<string, Obj>();
  private readonly raw = new Map<string, string>();
  private readonly columns = new Map<string, Column>();
  private readonly blockEntities = new Map<string, Obj>();
  private version = 1;

  constructor(readonly dir: string) {
    for (const f of readdirSync(dir)) {
      const path = join(dir, f);
      if (/^chunks.*\.bin$/.test(f)) this.addMcwc(readFileSync(path));
      else if (/^be.*\.json$/.test(f)) {
        const be = JSON.parse(readFileSync(path, 'utf8')) as { columns?: Obj[] };
        for (const c of be.columns ?? []) this.blockEntities.set(`${String(c.cx)},${String(c.cz)}`, c);
      } else if (f.endsWith('.json')) {
        const text = readFileSync(path, 'utf8');
        this.raw.set(f.slice(0, -5), text);
        try {
          this.json.set(f.slice(0, -5), JSON.parse(text) as Obj);
        } catch {
          // kept raw only
        }
      }
    }
  }

  static fromEnv(): Replay | null {
    const dir = process.env.MOCK_REPLAY_DIR;
    return dir && existsSync(dir) ? new Replay(dir) : null;
  }

  get(name: string): Obj | undefined {
    const v = this.json.get(name);
    return v ? structuredClone(v) : undefined;
  }

  /** The reply text as captured (keeps i64 hashes exact). */
  text(name: string): string | undefined {
    return this.raw.get(name);
  }

  private addMcwc(buf: Buffer): void {
    if (buf.length < 16 || buf.toString('ascii', 0, 4) !== 'MCWC') return;
    const version = buf.readUInt16LE(4);
    this.version = version;
    const dim = buf.readInt32LE(8);
    const count = buf.readInt32LE(12);
    let o = 16;
    for (let i = 0; i < count; i++) {
      const start = o;
      const cx = buf.readInt32LE(o),
        cz = buf.readInt32LE(o + 4);
      o += 8 + 8 + 8 + 1;
      const byLen = buf.readUInt8(o);
      o += 1 + byLen;
      const dataLen = buf.readInt32LE(o);
      o += 4 + dataLen;
      if (version >= 2) {
        const lightLen = buf.readInt32LE(o);
        o += 4 + lightLen;
      }
      this.columns.set(`${dim}:${cx},${cz}`, { dim, cx, cz, version, record: buf.subarray(start, o) });
    }
  }

  /** An MCWC reply for `cols` from the captured columns (unknown ones as data_len 0). */
  chunks(dim: number, cols: Array<[number, number]>): Buffer {
    const parts: Buffer[] = [];
    for (const [cx, cz] of cols) {
      const c = this.columns.get(`${dim}:${cx},${cz}`);
      if (c) parts.push(c.record);
      else {
        const r = Buffer.alloc(8 + 8 + 8 + 1 + 1 + 4 + (this.version >= 2 ? 4 : 0));
        r.writeInt32LE(cx, 0);
        r.writeInt32LE(cz, 4);
        parts.push(r);
      }
    }
    const head = Buffer.alloc(16);
    head.write('MCWC', 0, 'ascii');
    head.writeUInt16LE(this.version, 4);
    head.writeInt32LE(dim, 8);
    head.writeInt32LE(parts.length, 12);
    return Buffer.concat([head, ...parts]);
  }

  blockEntityColumns(dim: number, cols: Array<[number, number]>): Obj {
    return { dim, columns: cols.map(([cx, cz]) => this.blockEntities.get(`${cx},${cz}`)).filter(Boolean) };
  }
}
