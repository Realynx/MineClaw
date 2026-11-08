/** Human browser cosmetics only. Never import this service into the bridge or AI render pipeline. */
import { randomUUID } from 'node:crypto';
import { closeSync, existsSync, fsyncSync, mkdirSync, openSync, readFileSync, renameSync, unlinkSync, writeFileSync } from 'node:fs';
import { join } from 'node:path';
import { parseBridgeJson, toCamel } from '@mineclaw/contracts';
import type { BridgeClient } from '../bridge/bridge-client.js';
import { NO_IDENTITY } from '../bridge/signing.js';
import { Errors } from '../common/errors.js';

const MAX_PLAYERS = 512;
const MAX_BYTES = 64 * 1024;
const NAME = /^[a-z0-9_]{1,16}$/i;
interface Skin { name: string; slim: boolean; height: 32 | 64; png: string; revision: number }
type Fetch = typeof fetch;

/** No redirects, cookies or arbitrary hosts. Bound bytes before decoding JSON or passing PNGs to a browser. */
async function bytes(fetcher: Fetch, url: string): Promise<Buffer> {
  const response = await fetcher(url, { redirect: 'error', headers: { 'Cache-Control': 'no-store' }, signal: AbortSignal.timeout(5000) });
  if (!response.ok || !response.body) throw new Error('Unavailable skin');
  const reader = response.body.getReader();
  const parts: Uint8Array[] = [];
  let size = 0;
  try {
    while (true) {
      const { done, value } = await reader.read() as { done: boolean; value?: unknown };
      if (done) break;
      if (!(value instanceof Uint8Array)) throw new Error('Invalid skin response');
      size += value.byteLength;
      if (size > MAX_BYTES) throw new Error('Oversized skin response');
      parts.push(value);
    }
  } finally {
    await reader.cancel().catch(() => {});
  }
  return Buffer.concat(parts);
}

export function trustedSkinUrl(value: unknown): string {
  if (typeof value !== 'string') throw new Error('Missing skin URL');
  const url = new URL(value);
  if (!['http:', 'https:'].includes(url.protocol) || url.hostname !== 'textures.minecraft.net' || url.port ||
    url.username || url.password || url.search || url.hash || !/^\/texture\/[a-f0-9]{32,128}$/i.test(url.pathname))
    throw new Error('Untrusted skin URL');
  url.protocol = 'https:';
  return url.href;
}

async function download(name: string, fetcher: Fetch): Promise<Omit<Skin, 'revision'>> {
  const profile = JSON.parse((await bytes(fetcher, `https://api.mojang.com/users/profiles/minecraft/${name}`)).toString()) as { id?: unknown };
  if (typeof profile.id !== 'string' || !/^[a-f0-9]{32}$/i.test(profile.id)) throw new Error('Missing player UUID');
  const session = JSON.parse((await bytes(fetcher, `https://sessionserver.mojang.com/session/minecraft/profile/${profile.id}`)).toString()) as {
    properties?: { name?: unknown; value?: unknown }[];
  };
  const property = session.properties?.find((p) => p.name === 'textures');
  if (typeof property?.value !== 'string' || property.value.length > MAX_BYTES) throw new Error('Missing texture profile');
  const textures = JSON.parse(Buffer.from(property.value, 'base64').toString()) as { textures?: { SKIN?: { url?: unknown; metadata?: { model?: unknown } } } };
  const skin = textures.textures?.SKIN;
  const png = await bytes(fetcher, trustedSkinUrl(skin?.url));
  if (png.length < 33 || !png.subarray(0, 8).equals(Buffer.from([137, 80, 78, 71, 13, 10, 26, 10])) ||
      png.toString('ascii', 12, 16) !== 'IHDR' || png.readUInt32BE(16) !== 64) throw new Error('Invalid skin PNG');
  const height = png.readUInt32BE(20);
  if (height !== 32 && height !== 64) throw new Error('Invalid skin dimensions');
  return { name, slim: skin?.metadata?.model === 'slim', height, png: png.toString('base64') };
}

export class HumanSkins {
  private enabled = false;
  private readonly path: string;
  readonly epoch = randomUUID();
  private revision = 0;
  private readonly entries = new Map<string, Skin | null>();
  private readonly observed = new Set<string>();
  private readonly queue: string[] = [];
  private active = 0;
  private closed = false;
  private timer?: NodeJS.Timeout;

  constructor(private readonly dir: string, private readonly bridge: Pick<BridgeClient, 'json'>, private readonly fetcher: Fetch = fetch) {
    this.path = join(dir, 'human-skins.json');
    if (existsSync(this.path)) {
      const saved = JSON.parse(readFileSync(this.path, 'utf8')) as { enabled?: unknown };
      this.enabled = saved.enabled === true;
    }
  }

  start(): void {
    void this.refreshPlayers();
    // Discover joins even when no human browser has an SSE subscription. The bridge snapshot never receives skin data.
    this.timer ??= setInterval(() => void this.refreshPlayers(), 30_000);
    this.timer.unref();
  }

  private async refreshPlayers(): Promise<void> {
    if (!this.enabled || this.closed) return;
    try {
      const reply = await this.bridge.json({ method: 'GET', target: '/v1/sup/world/entities', identity: NO_IDENTITY, timeoutMs: 5000 });
      if (reply.status === 200) this.observeWorld(toCamel(parseBridgeJson(reply.text)));
    } catch { /* A disconnected game must not affect the web gateway. */ }
  }

  observeWorld(value: unknown): void {
    if (!value || typeof value !== 'object' || this.closed) return;
    const world = value as { bots?: { name?: unknown }[]; entities?: { kind?: unknown; name?: unknown }[] };
    for (const row of [...(Array.isArray(world.bots) ? world.bots : []), ...(Array.isArray(world.entities) ? world.entities.filter((e) => e?.kind === 'player') : [])]) {
      if (typeof row?.name !== 'string' || !NAME.test(row.name)) continue;
      const name = row.name.toLowerCase();
      if (this.observed.size < MAX_PLAYERS) this.observed.add(name);
    }
    this.enqueue();
  }

  private enqueue(): void {
    if (!this.enabled || this.closed) return;
    for (const name of this.observed) {
      if (this.entries.has(name)) continue;
      // In-flight work and failed profiles occupy a slot too: each name is resolved at most once per initialization.
      this.entries.set(name, null);
      this.queue.push(name);
    }
    this.pump();
  }

  private pump(): void {
    while (this.enabled && !this.closed && this.active < 4 && this.queue.length) {
      const name = this.queue.shift()!;
      this.active++;
      void download(name, this.fetcher).then((skin) => {
        if (!this.closed) this.entries.set(name, { ...skin, revision: ++this.revision });
      }).catch(() => {}).finally(() => { this.active--; this.pump(); });
    }
  }

  settings() {
    const cached = [...this.entries.values()].filter(Boolean).length;
    return { enabled: this.enabled, cached, unavailable: Math.max(0, this.entries.size - cached - this.active - this.queue.length) };
  }

  save(enabled: boolean) {
    mkdirSync(this.dir, { recursive: true, mode: 0o700 });
    const temp = this.path + '.' + randomUUID() + '.tmp';
    try {
      const fd = openSync(temp, 'wx', 0o600);
      try { writeFileSync(fd, JSON.stringify({ enabled }) + '\n'); fsyncSync(fd); } finally { closeSync(fd); }
      renameSync(temp, this.path);
    } catch {
      if (existsSync(temp)) unlinkSync(temp);
      throw Errors.internal();
    }
    this.enabled = enabled;
    this.enqueue();
    if (enabled) void this.refreshPlayers();
    return this.settings();
  }

  feed(query: { epoch?: string; since?: number } = {}) {
    const since = query.epoch === this.epoch && (query.since ?? 0) <= this.revision ? query.since ?? 0 : 0;
    return { enabled: this.enabled, epoch: this.epoch, revision: this.revision,
      skins: this.enabled ? [...this.entries.values()].filter((s): s is Skin => !!s && s.revision > since).map(({ revision: _revision, ...skin }) => skin) : [] };
  }

  close(): void { this.closed = true; clearInterval(this.timer); this.queue.length = 0; this.entries.clear(); this.observed.clear(); }
}
