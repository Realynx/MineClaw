/** Human browser cosmetics only. Never import this service into the bridge or AI render pipeline. */
import { randomUUID } from 'node:crypto';
import { existsSync, mkdirSync, readFileSync } from 'node:fs';
import { join } from 'node:path';
import { parseBridgeJson, toCamel, type DefaultSkin } from '@mineclaw/contracts';
import type { BridgeClient } from '../bridge/bridge-client.js';
import { NO_IDENTITY } from '../bridge/signing.js';
import { Errors } from '../common/errors.js';
import { SkinLibrary, writeAtomic } from './skin-library.js';
import { sanitizeSkinPng } from './skin-png.js';

const MAX_PLAYERS = 512;
const MAX_BYTES = 64 * 1024;
const NAME = /^[a-z0-9_]{1,16}$/i;
interface Skin { name: string; slim: boolean; height: 32 | 64; png: string; revision: number }
interface FeedSkin { name: string; slim: boolean; height: 32 | 64; png: string; base?: DefaultSkin }
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

/** A player's official skin, through Mojang's profile and session services only. Throws when unavailable. */
export async function download(name: string, fetcher: Fetch): Promise<Omit<Skin, 'revision'>> {
  const profile = JSON.parse((await bytes(fetcher, `https://api.mojang.com/users/profiles/minecraft/${name}`)).toString()) as { id?: unknown };
  if (typeof profile.id !== 'string' || !/^[a-f0-9]{32}$/i.test(profile.id)) throw new Error('Missing player UUID');
  const session = JSON.parse((await bytes(fetcher, `https://sessionserver.mojang.com/session/minecraft/profile/${profile.id}`)).toString()) as {
    properties?: { name?: unknown; value?: unknown }[];
  };
  const property = session.properties?.find((p) => p.name === 'textures');
  if (typeof property?.value !== 'string' || property.value.length > MAX_BYTES) throw new Error('Missing texture profile');
  const textures = JSON.parse(Buffer.from(property.value, 'base64').toString()) as { textures?: { SKIN?: { url?: unknown; metadata?: { model?: unknown } } } };
  const skin = textures.textures?.SKIN;
  const { png, height } = sanitizeSkinPng(await bytes(fetcher, trustedSkinUrl(skin?.url)));
  return { name, slim: skin?.metadata?.model === 'slim' && height === 64, height, png: png.toString('base64') };
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

  /** The pawn skin library and assignments (Controller → Skin Browser), served through `feed` beside the official skins. */
  readonly library: SkinLibrary;

  constructor(private readonly dir: string, private readonly bridge: Pick<BridgeClient, 'json'>, private readonly fetcher: Fetch = fetch) {
    this.path = join(dir, 'human-skins.json');
    this.library = new SkinLibrary(dir);
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
    writeAtomic(this.path, JSON.stringify({ enabled }) + '\n');
    this.enabled = enabled;
    this.enqueue();
    if (enabled) void this.refreshPlayers();
    return this.settings();
  }

  /** Whether official skins (and so "Look up a player") are on. */
  get officialEnabled(): boolean { return this.enabled; }

  /**
   * The human world views' skin feed: pawn skins chosen in the Skin Browser (always, they come from this server) and, while
   * the admin setting is on, official skins of the players seen in the world. A pawn's chosen skin wins over its official one.
   * The epoch changes with the setting and with every assignment change, so a view starts over and redraws every skin then.
   */
  feed(query: { epoch?: string; since?: number } = {}) {
    const epoch = `${this.epoch}.${this.enabled ? 1 : 0}.${this.library.generation}`;
    const fresh = query.epoch !== epoch || (query.since ?? 0) > this.revision;
    const since = fresh ? 0 : query.since ?? 0;
    const assigned = this.library.assigned();
    const official = this.enabled
      ? [...this.entries.values()].filter((s): s is Skin => !!s && s.revision > since && !this.library.has(s.name))
        .map(({ revision: _revision, ...skin }) => skin)
      : [];
    const skins: FeedSkin[] = [...(fresh ? assigned : []), ...official].slice(0, 512);
    return { enabled: this.enabled || assigned.length > 0, epoch, revision: this.revision, skins };
  }

  /** "Look up a player" in the Skin Browser: 409 while official skins are off, 404 when the player has no usable skin. */
  async lookup(name: string): Promise<{ slim: boolean; png: Buffer }> {
    if (!this.enabled || this.closed) throw Errors.conflict('Turn on Administration → Player skins to look up official skins');
    try {
      const skin = await download(name.toLowerCase(), this.fetcher);
      return { slim: skin.slim, png: Buffer.from(skin.png, 'base64') };
    } catch {
      throw Errors.notFound(`No usable skin found for ${name}`);
    }
  }

  close(): void { this.closed = true; clearInterval(this.timer); this.queue.length = 0; this.entries.clear(); this.observed.clear(); }
}
