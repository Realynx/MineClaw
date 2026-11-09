import type { EntityEvent } from '../types';
import type { GameVersion } from '../world/blocks';
import { ASSETS_CHANGED, readLocalPack, type LocalPack } from '../assets/localStore';

type V3 = [number, number, number];
export interface SoundListener { dim: number; version: GameVersion; position: V3; forward: V3; up: V3; topDown: boolean }
interface Variant { name: string; type?: string; volume?: number; pitch?: number; weight?: number }
export interface SoundChoice { name: string; volume: number; pitch: number }
const RETRY_MS = 5000;

/** Resolve weighted sound variants and referenced events, with bounded recursion for malformed resource packs. */
export function resolveSound(events: Record<string, unknown>, event: string, random = Math.random, depth = 0): SoundChoice | null {
  if (depth > 8) return null;
  const raw = events[event.replace(/^minecraft:/, '')];
  if (!raw || typeof raw !== 'object') return null;
  const values = (raw as { sounds?: unknown }).sounds;
  if (!Array.isArray(values)) return null;
  const choices: Variant[] = values.flatMap((v: unknown) => typeof v === 'string' ? [{ name: v }] : v && typeof v === 'object' && typeof (v as Variant).name === 'string' ? [v as Variant] : []);
  const weight = (v: Variant) => Number.isFinite(v.weight) && v.weight! > 0 ? Math.min(1000, v.weight!) : 1;
  let selected = random() * choices.reduce((n, v) => n + weight(v), 0);
  const choice = choices.find((v) => (selected -= weight(v)) < 0);
  if (!choice) return null;
  const clean = (n: number | undefined) => n !== undefined && Number.isFinite(n) ? Math.max(0, Math.min(4, n)) : 1;
  const result = choice.type === 'event' ? resolveSound(events, choice.name, random, depth + 1) : { name: choice.name.replace(/^minecraft:/, ''), volume: 1, pitch: 1 };
  return result ? { name: result.name, volume: result.volume * clean(choice.volume), pitch: result.pitch * clean(choice.pitch) } : null;
}

/** Top-down distance uses X/Z only, so zoom and terrain height do not silence the colony. */
export function soundPosition(position: V3, listener: SoundListener): V3 { return [position[0], listener.topDown ? 0 : position[1], position[2]]; }
export function soundDistance(position: V3, listener: SoundListener): number {
  const p = soundPosition(position, listener), centre = soundPosition(listener.position, listener);
  return Math.hypot(...p.map((n, i) => n - centre[i]));
}

export class ControllerAudio {
  private context: AudioContext | null = null;
  private master: GainNode | null = null;
  private packs = new Map<GameVersion, Promise<LocalPack | null>>();
  private buffers = new Map<string, Promise<AudioBuffer | null>>();
  private packRetryAt = new Map<GameVersion, number>();
  private bufferRetryAt = new Map<string, number>();
  private seen = new Map<string, number>();
  private voices = new Map<AudioBufferSourceNode, { pan: PannerNode; position: V3 }>();
  private disposed = false;
  private generation = 0;
  private pending = 0;
  private volume = 0.6;
  private timer: ReturnType<typeof setInterval>;
  private startedAt = Date.now();
  private foreground = true;
  private active = false;
  private unlocked = false;
  private recovering = false;
  private ready = false;
  constructor(private readonly listener: () => SoundListener | null, private readonly onReady?: (ready: boolean) => void) {
    this.foreground = typeof document.hasFocus !== 'function' || document.hasFocus();
    this.timer = setInterval(() => this.updateListener(), 50);
    window.addEventListener(ASSETS_CHANGED, this.changed);
    window.addEventListener('focus', this.focus);
    window.addEventListener('blur', this.blur);
    document.addEventListener?.('visibilitychange', this.visibility);
  }
  private readonly changed = () => {
    this.packs.clear(); this.buffers.clear(); this.clearFailures(); this.generation++; this.stop();
  };
  private readonly focus = () => { this.foreground = true; this.updateListener(); this.recover(); };
  private readonly blur = () => { this.foreground = false; this.updateListener(); };
  private readonly visibility = () => { this.updateListener(); if (!document.hidden) this.recover(); };
  private clearFailures(): void { this.packRetryAt.clear(); this.bufferRetryAt.clear(); }
  private publish(): void {
    if (this.disposed) return;
    const ready = this.active && this.context?.state === 'running';
    if (ready === this.ready) return;
    this.ready = ready;
    this.onReady?.(ready);
  }
  private readonly contextChanged = () => {
    if (this.context?.state === 'running') this.unlocked = true;
    else { this.generation++; this.stop(); }
    this.publish();
    this.recover();
  };
  /** An already unlocked context can usually resume after focus/OS interruption without another user gesture. */
  private recover(): void {
    const context = this.context;
    if (this.disposed || !this.unlocked || !this.active || !this.volume || !context || context.state === 'running' || context.state === 'closed' || this.recovering) return;
    this.recovering = true;
    void context.resume().then(() => this.publish()).catch(() => this.publish()).finally(() => { this.recovering = false; });
  }
  /** Call directly from a trusted click/key gesture: browser autoplay restrictions still apply. */
  async enable(): Promise<void> {
    if (this.disposed || !this.volume) return;
    this.foreground = true;
    this.clearFailures();
    if (!this.context || this.context.state === 'closed') {
      this.context = new AudioContext(); this.master = this.context.createGain(); this.master.gain.value = this.volume; this.master.connect(this.context.destination);
      this.context.onstatechange = this.contextChanged;
    }
    this.updateListener();
    // Do not await/coalesce a previous automatic resume: a new trusted gesture must reach the browser itself.
    if (this.context.state !== 'running') await this.context.resume();
    if (this.disposed) return;
    if (this.context.state === 'running') this.unlocked = true;
    this.publish();
  }
  setVolume(volume: number): void {
    this.volume = Number.isFinite(volume) ? Math.max(0, Math.min(1, volume)) : 0;
    if (this.master && this.context) this.master.gain.setTargetAtTime(this.volume, this.context.currentTime, 0.03);
    if (!this.volume) { this.generation++; this.stop(); }
  }
  receive(events: readonly EntityEvent[]): void {
    if (this.disposed) return;
    this.updateListener();
    const now = Date.now();
    for (const event of events) {
      if (event.kind !== 'sound' || !event.sound || event.at < this.startedAt || now - event.at > 1500 || event.at > now + 2000) continue;
      const key = `${event.dim}|${event.sound}|${event.x},${event.y},${event.z}|${event.at}`;
      if (this.seen.has(key)) continue;
      this.seen.set(key, now);
      if (this.active && this.context?.state === 'running' && this.volume > 0 && this.pending + this.voices.size < 32) {
        this.pending++; void this.play(event).finally(() => this.pending--);
      }
    }
    if (this.seen.size > 2048) for (const [key, at] of this.seen) if (now - at > 5000) this.seen.delete(key);
  }
  private pack(version: GameVersion): Promise<LocalPack | null> {
    if ((this.packRetryAt.get(version) ?? 0) > Date.now()) return Promise.resolve(null);
    let pending = this.packs.get(version);
    if (!pending) {
      pending = readLocalPack(version).catch(() => null).then((pack) => {
        if (!pack && this.packs.get(version) === pending) {
          this.packs.delete(version);
          this.packRetryAt.set(version, Date.now() + RETRY_MS);
        }
        return pack;
      });
      this.packs.set(version, pending);
    }
    return pending;
  }
  private buffer(key: string, file: Blob, context: AudioContext): Promise<AudioBuffer | null> {
    if ((this.bufferRetryAt.get(key) ?? 0) > Date.now()) return Promise.resolve(null);
    let pending = this.buffers.get(key);
    if (!pending) {
      pending = file.arrayBuffer().then((bytes) => context.decodeAudioData(bytes)).catch(() => null).then((buffer) => {
        if (!buffer && this.buffers.get(key) === pending) {
          this.buffers.delete(key);
          this.bufferRetryAt.set(key, Date.now() + RETRY_MS);
        }
        return buffer;
      });
      if (this.buffers.size >= 96) this.buffers.delete(this.buffers.keys().next().value!);
      this.buffers.set(key, pending);
    }
    return pending;
  }
  private async play(event: EntityEvent): Promise<void> {
    try {
      const listener = this.listener(), context = this.context, generation = this.generation;
      if (!listener || !context || event.dim !== listener.dim || [event.x, event.y, event.z].some((n) => typeof n !== 'number' || !Number.isFinite(n))) return;
      const position: V3 = [event.x!, event.y!, event.z!];
      const volume = Math.max(0, Math.min(4, event.volume ?? 1)), range = Math.max(16, 16 * volume);
      if (soundDistance(position, listener) >= range) return;
      const assets = await this.pack(listener.version);
      if (!assets || this.disposed || generation !== this.generation) return;
      const sound = resolveSound(assets.soundEvents, event.sound!);
      if (!sound || sound.name.startsWith('music/')) return;
      const file = assets.sounds[sound.name]; if (!file) return;
      const key = listener.version + ':' + sound.name;
      const decoded = await this.buffer(key, file, context);
      if (!decoded || this.disposed || this.generation !== generation || context.state !== 'running' || !this.volume || !this.active || Date.now() - event.at > 1500) return;
      const current = this.listener(); if (!current || current.dim !== listener.dim || current.version !== listener.version) return;
      this.updateListener();
      const source = context.createBufferSource(), gain = context.createGain(), pan = context.createPanner();
      source.buffer = decoded; source.playbackRate.value = Math.max(0.25, Math.min(4, (event.pitch ?? 1) * sound.pitch));
      gain.gain.value = Math.min(1, volume * sound.volume);
      pan.panningModel = 'HRTF'; pan.distanceModel = 'linear'; pan.refDistance = 1; pan.maxDistance = range; pan.rolloffFactor = 1;
      const p = soundPosition(position, current); pan.positionX.value = p[0]; pan.positionY.value = p[1]; pan.positionZ.value = p[2];
      source.connect(gain); gain.connect(pan); pan.connect(this.master!); this.voices.set(source, { pan, position });
      source.onended = () => { this.voices.delete(source); source.disconnect(); gain.disconnect(); pan.disconnect(); };
      source.start();
    } catch { /* An unsupported codec or missing custom resource must not interrupt the live controller. */ }
  }
  private updateListener(): void {
    if (this.disposed) return;
    const listener = this.listener(), context = this.context;
    const active = !!listener && this.foreground && !document.hidden;
    if (active !== this.active) {
      this.active = active;
      this.generation++; this.startedAt = Date.now(); this.stop();
      if (active) { this.clearFailures(); this.recover(); }
      this.publish();
    }
    if (!active || !listener || !context) return;
    const l = context.listener, time = context.currentTime;
    // The top-down camera looks vertically down; use map-up as the hearing direction to avoid a degenerate flattened basis.
    const forward: V3 = listener.topDown ? [0, 0, -1] : listener.forward, up: V3 = listener.topDown ? [0, 1, 0] : listener.up;
    const centre = soundPosition(listener.position, listener);
    [l.positionX, l.positionY, l.positionZ].forEach((p, i) => p.setTargetAtTime(centre[i], time, 0.04));
    [l.forwardX, l.forwardY, l.forwardZ].forEach((p, i) => p.setTargetAtTime(forward[i], time, 0.04));
    [l.upX, l.upY, l.upZ].forEach((p, i) => p.setTargetAtTime(up[i], time, 0.04));
    for (const { pan, position } of this.voices.values()) {
      const p = soundPosition(position, listener);
      [pan.positionX, pan.positionY, pan.positionZ].forEach((param, i) => param.setTargetAtTime(p[i], time, 0.04));
    }
  }
  private stop(): void { for (const voice of this.voices.keys()) { try { voice.stop(); } catch { /* already ended */ } } this.voices.clear(); }
  dispose(): void {
    this.disposed = true; this.generation++; this.stop(); clearInterval(this.timer);
    window.removeEventListener(ASSETS_CHANGED, this.changed);
    window.removeEventListener('focus', this.focus); window.removeEventListener('blur', this.blur);
    document.removeEventListener?.('visibilitychange', this.visibility);
    if (this.context) { this.context.onstatechange = null; void this.context.close().catch(() => undefined); }
    this.buffers.clear(); this.packs.clear(); this.clearFailures();
  }
}
