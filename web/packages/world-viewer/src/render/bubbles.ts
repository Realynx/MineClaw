// Chat bubbles over speakers' heads, like a game: `runtime.say(name, text)` (or `WorldSource.chat`) puts a rounded speech
// bubble with a tail over the bot or player of that name, in every view of the runtime. One model (who said what, when), one
// canvas texture per message (LRU-capped, shared by every view), and per view only a few sprite positions and scales.
//
// - Text: § codes stripped, whitespace collapsed, word-wrapped to 32 characters a line, at most 3 lines (an ellipsis cuts
//   the rest); long words are hard-broken.
// - Stack: the last 3 messages per speaker, the newest at the bottom (with the tail), older ones above it; a repeat of the
//   newest text within 2 s (several bots heard the same line) is ignored.
// - Timing: a message holds 6 s + 0.5 s per extra line, then fades out over 0.5 s (a short fade-in on arrival).
// - Drawing: sprites with depth test off (on top of terrain, like labels), anchored over the name label, offset in screen
//   space (so stacks read in top-down views too). Their size follows distance but is clamped to a readable range in CSS
//   pixels. A speaker off screen, in another dimension or not in the world shows nothing. Thumbnails (`compact`) draw only
//   the newest message, one short line.

import * as THREE from 'three';

export type BubbleKind = 'chat' | 'said' | 'whisper' | 'viewer';
export type BubbleMode = 'full' | 'compact' | 'off';

export const BUBBLE = {
  /** Characters per line. */
  wrap: 32,
  maxLines: 3,
  /** Messages kept per speaker. */
  stack: 3,
  holdMs: 6000,
  perLineMs: 500,
  fadeMs: 500,
  fadeInMs: 120,
  /** A repeat of a speaker's newest text within this window is the same line heard twice. */
  dedupeMs: 2000,
  /** Speakers tracked at once (the oldest go first). */
  maxSpeakers: 48,
  /** Compact (thumbnail) bubbles: one line of this many characters. */
  compactWrap: 18,
} as const;

/** Removes Minecraft § formatting codes and control characters, and collapses whitespace. */
export function cleanText(text: string): string {
  return text
    .replace(/§./g, '')
    .replace(/[\u0000-\u001f\u007f]/g, ' ')
    .replace(/\s+/g, ' ')
    .trim();
}

/**
 * Word-wraps `text` to `width` characters a line, at most `maxLines` lines. Words longer than a line are broken; text that
 * does not fit ends the last line with an ellipsis.
 */
export function wrapText(text: string, width: number = BUBBLE.wrap, maxLines: number = BUBBLE.maxLines): string[] {
  const words = cleanText(text).split(' ').filter(Boolean);
  const lines: string[] = [];
  let cur = '';
  let cut = false;
  const push = (l: string): boolean => {
    if (lines.length >= maxLines) {
      cut = true;
      return false;
    }
    lines.push(l);
    return true;
  };
  outer: for (let w of words) {
    // A word longer than a line: fill the current line, then whole lines of it.
    while ([...w].length > width) {
      const room = cur ? width - [...cur].length - 1 : width;
      if (room >= 4 || !cur) {
        const head = [...w].slice(0, room).join('');
        if (!push(cur ? `${cur} ${head}` : head)) break outer;
        w = [...w].slice(room).join('');
        cur = '';
      } else {
        if (!push(cur)) break outer;
        cur = '';
      }
    }
    if (!w) continue;
    const next = cur ? `${cur} ${w}` : w;
    if ([...next].length <= width) cur = next;
    else {
      if (!push(cur)) break;
      cur = w;
    }
  }
  if (cur && !cut) push(cur);
  if (cut && lines.length) {
    const last = [...lines[lines.length - 1]!];
    lines[lines.length - 1] = `${last.slice(0, Math.min(last.length, width - 1)).join('').trimEnd()}…`;
  }
  return lines;
}

/** How long a message of `lines` lines stays fully visible (ms), before its fade. */
export function holdMs(lines: number): number {
  return BUBBLE.holdMs + BUBBLE.perLineMs * Math.max(0, lines - 1);
}

/** A message's opacity `age` ms after it arrived (0 once gone). */
export function bubbleAlpha(age: number, lines: number): number {
  if (age < 0) return 0;
  const hold = holdMs(lines);
  if (age < BUBBLE.fadeInMs) return age / BUBBLE.fadeInMs;
  if (age <= hold) return 1;
  return Math.max(0, 1 - (age - hold) / BUBBLE.fadeMs);
}

export interface BubbleMessage {
  id: number;
  /** The name as said (display). */
  speaker: string;
  text: string;
  lines: string[];
  kind: BubbleKind;
  at: number;
}

let nextId = 1;

/** Who said what, when: the last few messages per speaker (lower-cased name), expiring by time. */
export class ChatBubbles {
  private readonly bySpeaker = new Map<string, BubbleMessage[]>();
  /** Called with every message that leaves (expired or pushed out of a stack). */
  onDrop: ((m: BubbleMessage) => void) | null = null;

  /** Adds a message; false when it was ignored (empty, or a repeat of the speaker's newest line). */
  say(name: string, text: string, kind: BubbleKind = 'chat', now: number = performance.now()): BubbleMessage | null {
    const key = name.trim().toLowerCase();
    const lines = wrapText(text);
    if (!key || !lines.length) return null;
    let list = this.bySpeaker.get(key);
    const newest = list?.[list.length - 1];
    if (newest && newest.text === cleanText(text) && now - newest.at < BUBBLE.dedupeMs) return null;
    if (!list) {
      list = [];
      this.bySpeaker.set(key, list);
      if (this.bySpeaker.size > BUBBLE.maxSpeakers) {
        const oldest = this.bySpeaker.keys().next().value as string;
        this.drop(oldest);
      }
    } else {
      // Most recent speakers last (the map's order is the eviction order).
      this.bySpeaker.delete(key);
      this.bySpeaker.set(key, list);
    }
    const m: BubbleMessage = { id: nextId++, speaker: name.trim(), text: cleanText(text), lines, kind, at: now };
    list.push(m);
    // Shift first: with no onDrop an optional call would skip its argument and never shorten the list.
    while (list.length > BUBBLE.stack) {
      const old = list.shift()!;
      this.onDrop?.(old);
    }
    return m;
  }

  /** Drops expired messages. */
  prune(now: number): void {
    for (const [k, list] of this.bySpeaker) {
      while (list.length && bubbleAlpha(now - list[0]!.at, list[0]!.lines.length) <= 0 && now - list[0]!.at > BUBBLE.fadeInMs) {
        const old = list.shift()!;
        this.onDrop?.(old);
      }
      if (!list.length) this.bySpeaker.delete(k);
    }
  }

  private drop(key: string): void {
    for (const m of this.bySpeaker.get(key) ?? []) this.onDrop?.(m);
    this.bySpeaker.delete(key);
  }

  /** Lower-cased speaker names with live messages. */
  speakers(): IterableIterator<string> {
    return this.bySpeaker.keys();
  }

  /** A speaker's live messages, oldest first (the newest is drawn at the bottom, nearest the head). */
  messages(key: string): readonly BubbleMessage[] {
    return this.bySpeaker.get(key) ?? [];
  }

  get size(): number {
    return this.bySpeaker.size;
  }

  clear(): void {
    for (const k of [...this.bySpeaker.keys()]) this.drop(k);
  }
}

// ------------------------------------------------------------------------------------------------------- speakers

export interface SpeakerBot {
  name: string;
  dim: number;
  x: number;
  y: number;
  z: number;
  dto: { short?: string | null };
}

export interface SpeakerEntity {
  key: string;
  kind: string;
  name: string | null;
  dim: number;
  x: number;
  y: number;
  z: number;
}

export interface Speaker {
  /** The entity layer's key (bot name, or "dim:id" for a player). */
  key: string;
  bot: boolean;
  dim: number;
  x: number;
  y: number;
  z: number;
}

/**
 * The body a speaker's name belongs to, case-insensitively: a bot by its name or short key (main "maid_chan", hive and
 * subhive bodies), else a player entity by its username. Null when nobody of that name is in the world.
 */
export function findSpeaker(name: string, bots: readonly SpeakerBot[], ents: readonly SpeakerEntity[]): Speaker | null {
  const k = name.trim().toLowerCase();
  if (!k) return null;
  let byShort: SpeakerBot | null = null;
  for (const b of bots) {
    if (b.name.toLowerCase() === k) return { key: b.name, bot: true, dim: b.dim, x: b.x, y: b.y, z: b.z };
    if (!byShort && b.dto.short && b.dto.short.toLowerCase() === k) byShort = b;
  }
  if (byShort) return { key: byShort.name, bot: true, dim: byShort.dim, x: byShort.x, y: byShort.y, z: byShort.z };
  for (const e of ents) if (e.kind === 'player' && e.name && e.name.toLowerCase() === k) return { key: e.key, bot: false, dim: e.dim, x: e.x, y: e.y, z: e.z };
  return null;
}

// ------------------------------------------------------------------------------------------------------- layout

/** Head heights where the name label sits (bots: the label root offset; players: their height plus the label lift). */
export const HEAD_Y = { bot: 2.2, player: 2.05 } as const;

/** On-screen line height (CSS px) of a bubble whose anchor is `depth` blocks in front of the camera: larger near, clamped. */
export function linePx(depth: number, mode: BubbleMode): number {
  if (mode === 'compact') return 10;
  return Math.max(14, Math.min(22, (22 * 10) / Math.max(0.1, depth)));
}

/**
 * Bottom offsets (in units of the bubbles' own heights, bottom up) of a stack drawn newest first: each sits `gap` above
 * the one below it, the lowest `base` above the anchor. `heights` are newest first.
 */
export function stackOffsets(heights: readonly number[], base: number, gap: number): number[] {
  const out: number[] = [];
  let y = base;
  for (const h of heights) {
    out.push(y);
    y += h + gap;
  }
  return out;
}

// ------------------------------------------------------------------------------------------------------- textures

export interface TexEntry<T> {
  tex: T;
  w: number;
  h: number;
  refs: number;
}

/** Reference-counted textures by key; at most `keep` unreferenced ones are kept (least recently used dropped first). */
export class TextureCache<T extends { dispose(): void }> {
  private readonly map = new Map<string, TexEntry<T>>();
  constructor(private readonly draw: (key: string) => TexEntry<T>, public keep = 24) {}

  acquire(key: string): TexEntry<T> {
    let e = this.map.get(key);
    if (e) this.map.delete(key);
    else e = this.draw(key);
    this.map.set(key, e); // most recently used last
    e.refs++;
    return e;
  }

  release(key: string): void {
    const e = this.map.get(key);
    if (!e) return;
    e.refs = Math.max(0, e.refs - 1);
    this.trim();
  }

  private trim(): void {
    let idle = 0;
    for (const e of this.map.values()) if (e.refs === 0) idle++;
    if (idle <= this.keep) return;
    for (const [k, e] of this.map) {
      if (idle <= this.keep) break;
      if (e.refs > 0) continue;
      e.tex.dispose();
      this.map.delete(k);
      idle--;
    }
  }

  get size(): number {
    return this.map.size;
  }

  clear(): void {
    for (const e of this.map.values()) e.tex.dispose();
    this.map.clear();
  }
}

const FONT_PX = 24;
const LINE_H = 30;
const PAD_X = 14, PAD_Y = 9, TAIL = 11, RADIUS = 14, BORDER = 2.5;

const STYLE: Record<BubbleKind, { bg: string; fg: string; border: string; italic?: boolean }> = {
  chat: { bg: 'rgba(255,255,255,0.95)', fg: '#16181d', border: 'rgba(20,24,32,0.35)' },
  said: { bg: 'rgba(255,255,255,0.95)', fg: '#16181d', border: 'rgba(60,110,200,0.65)' },
  viewer: { bg: 'rgba(232,241,255,0.96)', fg: '#16233d', border: 'rgba(60,110,200,0.45)' },
  whisper: { bg: 'rgba(242,234,255,0.95)', fg: '#3a2a62', border: 'rgba(120,90,190,0.55)', italic: true },
};

type Variant = 'full' | 'stacked' | 'compact';

function texKey(m: BubbleMessage, v: Variant): string {
  return `${v}\u0000${m.kind}\u0000${m.text}`;
}

/** Canvas pixels of a bubble (its lines, tail or not). */
function drawBubble(lines: string[], kind: BubbleKind, tail: boolean): TexEntry<THREE.CanvasTexture> {
  const canvas = typeof document !== 'undefined' ? document.createElement('canvas') : (new OffscreenCanvas(4, 4) as unknown as HTMLCanvasElement);
  const ctx = canvas.getContext('2d') as CanvasRenderingContext2D | null;
  const st = STYLE[kind] ?? STYLE.chat;
  const font = `${st.italic ? 'italic ' : ''}500 ${FONT_PX}px system-ui, -apple-system, "Segoe UI", sans-serif`;
  let tw = 40;
  if (ctx) {
    ctx.font = font;
    for (const l of lines) tw = Math.max(tw, Math.ceil(ctx.measureText(l).width));
  }
  const bw = tw + PAD_X * 2, bh = lines.length * LINE_H + PAD_Y * 2;
  canvas.width = Math.ceil(bw + BORDER * 2);
  canvas.height = Math.ceil(bh + BORDER * 2 + (tail ? TAIL : 2));
  if (ctx) {
    const x0 = BORDER, y0 = BORDER, cx = canvas.width / 2;
    ctx.beginPath();
    // Rounded box with the tail cut into its bottom edge.
    ctx.moveTo(x0 + RADIUS, y0);
    ctx.arcTo(x0 + bw, y0, x0 + bw, y0 + bh, RADIUS);
    ctx.arcTo(x0 + bw, y0 + bh, x0, y0 + bh, RADIUS);
    if (tail) {
      ctx.lineTo(cx + TAIL * 0.8, y0 + bh);
      ctx.lineTo(cx, y0 + bh + TAIL - 1);
      ctx.lineTo(cx - TAIL * 0.8, y0 + bh);
    }
    ctx.arcTo(x0, y0 + bh, x0, y0, RADIUS);
    ctx.arcTo(x0, y0, x0 + bw, y0, RADIUS);
    ctx.closePath();
    ctx.fillStyle = st.bg;
    ctx.fill();
    ctx.lineWidth = BORDER;
    ctx.strokeStyle = st.border;
    ctx.stroke();
    ctx.fillStyle = st.fg;
    ctx.font = font;
    ctx.textBaseline = 'middle';
    ctx.textAlign = 'center';
    lines.forEach((l, i) => ctx.fillText(l, cx, y0 + PAD_Y + LINE_H * (i + 0.5) + 1));
  }
  const tex = new THREE.CanvasTexture(canvas);
  tex.colorSpace = THREE.SRGBColorSpace;
  tex.minFilter = THREE.LinearMipmapLinearFilter;
  tex.magFilter = THREE.LinearFilter;
  tex.generateMipmaps = true;
  return { tex, w: canvas.width, h: canvas.height, refs: 0 };
}

/** One message's canvas line height in texture pixels (what `linePx` scales). */
const TEX_LINE = LINE_H;

// ------------------------------------------------------------------------------------------------------- the layer

export interface BubbleViewOptions {
  dim: number;
  camera: THREE.PerspectiveCamera;
  /** Render height of the view (device pixels). */
  height: number;
  /** Render pixels per CSS pixel (sizes are chosen in CSS pixels). */
  pxScale: number;
  mode: BubbleMode;
  /** Bubbles over bots / players follow their layers. */
  bots: boolean;
  players: boolean;
  /** World height of the name label drawn over a body in this view (0 = none): the bubble sits above it. */
  labelLift?: (key: string, bot: boolean) => number;
}

/** A bubble drawn in a view (stats, tests, e2e): its speaker, anchor in CSS pixels from the view's top-left, its lines. */
export interface ShownBubble {
  speaker: string;
  key: string;
  x: number;
  y: number;
  /** On-screen size (CSS px) of the newest bubble, and the y of its bottom (tail tip) and of the stack's top. */
  w: number;
  h: number;
  bottom: number;
  top: number;
  lines: string[];
  stacked: number;
}

interface Drawn {
  m: BubbleMessage;
  sprite: THREE.Sprite;
  /** Texture variants acquired (released when the message leaves). */
  held: Map<string, TexEntry<THREE.CanvasTexture>>;
}

const GAP_PX = 3, BASE_GAP_PX = 4;

export class BubbleLayer {
  readonly group = new THREE.Group();
  readonly model = new ChatBubbles();
  private readonly cache = new TextureCache<THREE.CanvasTexture>((key) => {
    const [v, kind, text] = key.split('\u0000') as [Variant, BubbleKind, string];
    const lines = v === 'compact' ? wrapText(text, BUBBLE.compactWrap, 1) : wrapText(text);
    return drawBubble(lines, kind, v !== 'stacked');
  });
  private readonly drawn = new Map<number, Drawn>();
  private readonly pool: THREE.Sprite[] = [];
  private speakersNow = new Map<string, Speaker>();
  private readonly shown = new Map<number, ShownBubble[]>();
  private readonly v = new THREE.Vector3();
  private now = 0;

  constructor() {
    this.group.name = 'bubbles';
    this.model.onDrop = (m) => this.release(m.id);
  }

  say(name: string, text: string, kind: BubbleKind = 'chat', now: number = performance.now()): boolean {
    return !!this.model.say(name, text, kind, now);
  }

  /** Once per frame: expire messages and place speakers (interpolated bodies of every dimension). */
  update(now: number, bots: readonly SpeakerBot[], ents: readonly SpeakerEntity[]): void {
    this.now = now;
    this.model.prune(now);
    this.speakersNow.clear();
    if (!this.model.size) return;
    let players: SpeakerEntity[] | null = null;
    for (const k of this.model.speakers()) {
      players ??= ents.filter((e) => e.kind === 'player');
      const s = findSpeaker(k, bots, players);
      if (s) this.speakersNow.set(k, s);
    }
  }

  private release(id: number): void {
    const d = this.drawn.get(id);
    if (!d) return;
    this.drawn.delete(id);
    for (const k of d.held.keys()) this.cache.release(k);
    d.sprite.visible = false;
    (d.sprite.material as THREE.SpriteMaterial).map = null;
    this.pool.push(d.sprite);
  }

  private drawnFor(m: BubbleMessage): Drawn {
    let d = this.drawn.get(m.id);
    if (d) return d;
    let sprite = this.pool.pop();
    if (!sprite) {
      sprite = new THREE.Sprite(new THREE.SpriteMaterial({ transparent: true, depthTest: false, depthWrite: false, sizeAttenuation: true }));
      sprite.renderOrder = 12; // over the labels (10)
      sprite.frustumCulled = false;
      this.group.add(sprite);
    }
    d = { m, sprite, held: new Map() };
    this.drawn.set(m.id, d);
    return d;
  }

  private texture(d: Drawn, v: Variant): TexEntry<THREE.CanvasTexture> {
    const k = texKey(d.m, v);
    let e = d.held.get(k);
    if (!e) d.held.set(k, (e = this.cache.acquire(k)));
    return e;
  }

  /** Before rendering one view: positions, sizes and opacities of the bubbles it shows (hides the rest). */
  showFor(o: BubbleViewOptions, viewId = 0): void {
    for (const d of this.drawn.values()) d.sprite.visible = false;
    const report: ShownBubble[] = [];
    this.shown.set(viewId, report);
    this.group.visible = o.mode !== 'off' && this.speakersNow.size > 0;
    if (!this.group.visible) return;
    const cam = o.camera;
    cam.updateMatrixWorld();
    const tanHalf = Math.tan((cam.fov * Math.PI) / 360);
    const cssH = o.height / Math.max(0.01, o.pxScale), cssW = cssH * cam.aspect;
    for (const [k, s] of this.speakersNow) {
      if (s.dim !== o.dim || (s.bot ? !o.bots : !o.players)) continue;
      const msgs = this.model.messages(k);
      if (!msgs.length) continue;
      const ay = s.y + (s.bot ? HEAD_Y.bot : HEAD_Y.player);
      // On screen? The anchor in view space (in front of the camera) and inside the frustum (a little margin).
      this.v.set(s.x, ay, s.z).applyMatrix4(cam.matrixWorldInverse);
      const depth = -this.v.z;
      if (depth < cam.near) continue;
      this.v.set(s.x, ay, s.z).project(cam);
      if (Math.abs(this.v.x) > 1.02 || this.v.y < -1.02 || this.v.y > 1.0) continue;
      // World units per CSS pixel at that depth.
      const wpp = (2 * depth * tanHalf) / cssH;
      const scale = (linePx(depth, o.mode) / TEX_LINE) * wpp; // world units per texture pixel
      const lift = o.labelLift?.(s.key, s.bot) ?? 0;
      // Newest first (bottom, with the tail), each fully faded one skipped; then stacked upward in screen space.
      const list = o.mode === 'compact' ? msgs.slice(-1) : msgs;
      const live: Array<{ m: BubbleMessage; d: Drawn; e: TexEntry<THREE.CanvasTexture>; alpha: number }> = [];
      for (let i = list.length - 1; i >= 0; i--) {
        const m = list[i]!;
        const alpha = bubbleAlpha(this.now - m.at, m.lines.length);
        if (alpha <= 0) continue;
        const d = this.drawnFor(m);
        live.push({ m, d, e: this.texture(d, o.mode === 'compact' ? 'compact' : live.length === 0 ? 'full' : 'stacked'), alpha });
      }
      if (!live.length) continue;
      const offsets = stackOffsets(live.map((l) => l.e.h * scale), lift + BASE_GAP_PX * wpp, GAP_PX * wpp);
      live.forEach(({ d, e, alpha }, i) => {
        const sp = d.sprite;
        const mat = sp.material as THREE.SpriteMaterial;
        if (mat.map !== e.tex) {
          if (!mat.map) mat.needsUpdate = true; // a map appearing changes the program; swapping maps does not
          mat.map = e.tex;
        }
        mat.opacity = alpha;
        const w = e.w * scale, h = e.h * scale;
        sp.scale.set(w, h, 1);
        sp.position.set(s.x, ay, s.z);
        sp.center.set(0.5, -offsets[i]! / h); // screen-aligned lift: stacks read the same from the side and from above
        sp.visible = true;
      });
      const top = live[0]!;
      const ax = ((this.v.x + 1) / 2) * cssW, ayPx = ((1 - this.v.y) / 2) * cssH;
      const last = live.length - 1;
      const first: ShownBubble = {
        speaker: top.m.speaker,
        key: s.key,
        x: ax,
        y: ayPx,
        w: (top.e.w * scale) / wpp,
        h: (top.e.h * scale) / wpp,
        bottom: ayPx - offsets[0]! / wpp,
        top: ayPx - (offsets[last]! + live[last]!.e.h * scale) / wpp,
        lines: top.m.lines,
        stacked: live.length,
      };
      if (first) report.push(first);
    }
  }

  /** The bubbles the last `showFor` of a view drew. */
  shownIn(viewId = 0): readonly ShownBubble[] {
    return this.shown.get(viewId) ?? [];
  }

  forgetView(viewId: number): void {
    this.shown.delete(viewId);
  }

  /** Live textures (tests, stats). */
  get textures(): number {
    return this.cache.size;
  }

  clear(): void {
    this.model.clear();
    this.speakersNow.clear();
  }

  dispose(): void {
    this.clear();
    for (const s of this.pool) (s.material as THREE.Material).dispose();
    this.pool.length = 0;
    this.group.clear();
    this.cache.clear();
  }
}
