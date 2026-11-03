// Bots, players, mobs, drops and objects at the EntityStore's interpolated positions, shared by every view of the runtime.
//
// Once per frame (`update`) every entity is animated and its posed part matrices are written into one row of a float
// "bone" texture:
//   - limb swing from how far it moved, body yaw toward the walk direction, head yaw/pitch
//   - idle sway, main- and off-hand swings, eating, hurt flash, death fall and fade
//   - baby layouts, riding and sneaking poses
// Each view (`showFor`) then only chooses instances: one instanced draw per model kind present (base skin, its layers,
// armour), one for item sprites (held items, drops, paintings, particles, fire), one for item/block cubes, one for shadows,
// one for far LOD boxes. Nothing is rebuilt per view. Models are our own data (render/entities/models), textured from the
// bundled entity sheet.
//
// Entity events from the delta stream drive the one-off animations (`onEvents`): swings, hurts, deaths, crit and magic-crit
// sparks, eating, teleport puffs and lightning bolts. Display mobs (spawner cages) come from the block-entity layer through
// `showDisplayMob` / `hideDisplayMob`.

import * as THREE from 'three';
import type { AssetBundle, Pack } from '../assets/bundle';
import { FLAG_GLOWING, FLAG_INVISIBLE, FLAG_ON_FIRE, FLAG_SNEAKING, FLAG_SPRINTING, type EntityDto, type EntityEvent, type Equipment, type ItemRef } from '../types';
import type { GameVersion } from '../world/blocks';
import type { BotView, EntityView } from '../world/entities';
import { ARMOR_PIECES, armorFromEquipment, armorTexture, buildArmor } from './entities/armor';
import { BOX_SLOT, buildModel, type BuiltModel } from './entities/builder';
import { Lightning, Particles } from './entities/effects';
import { facingYaw, minecartBlock, motive, paintingSheet } from './entities/hanging';
import { isHandheld, itemLookById, itemLookByName, itemName, parseHeld, type ItemLook } from './entities/items';
import { createEntityMaterials, defaultWorldUniforms, sheetTexture, type EntityMaterials, type WorldUniforms } from './entities/material';
import { cubeGeometry, InstancedDraw, modelGeometry, ShadowDraw, spriteGeometry, type InstanceData } from './entities/meshes';
import { attackOffProgress, attackProgress, DEATH_GONE_MS, DEATH_SHRINK_MS, deathAngle, deathScale, eatProgress, hurtAmount, newMotion, stepMotion, wrapDeg, type Motion } from './entities/motion';
import { identity, mul, rootMatrix, ROW_FLOATS, Skeleton, type Affine } from './entities/pose';
import { canonical, MOB_NAMES, MODEL_BY_ID, mobLook, mobName, objectLook, objectName, playerLook } from './entities/registry';
import type { Look, PoseCtx } from './entities/types';
import { Variant } from './entities/variant';
import { LabelFactory } from './labels';

export { MOB_NAMES };

function hashHue(s: string): number {
  let h = 0;
  for (let i = 0; i < s.length; i++) h = (h * 31 + s.charCodeAt(i)) | 0;
  return ((h >>> 0) % 360) / 360;
}

export function botColour(b: { name: string; dto: { main: boolean } }): THREE.Color {
  return b.dto.main ? new THREE.Color(0xf2c14e) : new THREE.Color().setHSL(hashHue(b.name), 0.65, 0.55);
}

/** The display name of a mob type (labels). */
export function mobInfo(type: number): [string, number, number, number] {
  const n = MOB_NAMES[type];
  const look = mobLook('1.12.2', type, null, null, 0);
  const def = MODEL_BY_ID[look.model];
  const s = look.scale ?? 1;
  return [n ? n.replace(/_/g, ' ') : 'mob', look.colour, (def?.width ?? 0.6) * s, (def?.height ?? 1.8) * s];
}

/** Box size and colour of a non-bot entity (the LOD box; also the label height). */
export function entityLook(e: EntityView, version: GameVersion = '1.12.2'): { colour: number; w: number; h: number } {
  if (e.kind === 'player') return { colour: 0x4a7ad8, w: 0.6, h: 1.8 };
  if (e.kind === 'item') return { colour: 0xd8b040, w: 0.25, h: 0.25 };
  if (e.kind === 'mob') {
    const look = mobLook(version, e.type, e.name, e.dto.variant, e.id);
    const def = MODEL_BY_ID[look.model];
    const s = (look.scale ?? 1) * (e.dto.baby ? 0.5 : 1);
    return { colour: look.colour, w: (def?.width ?? 0.6) * s, h: (def?.height ?? 1.8) * s };
  }
  return { colour: 0x9a9a9a, w: 0.5, h: 0.5 };
}

export function entityText(e: EntityView): string {
  if (e.kind === 'item') return `${e.name ?? `item ${e.item}`}${e.count > 1 ? ` x${e.count}` : ''}`;
  if (e.kind === 'player') return e.name ?? 'player';
  if (e.kind === 'mob') return `${e.dto.baby ? 'baby ' : ''}${(MOB_NAMES[e.type] ?? e.name ?? 'mob').replace(/_/g, ' ')}`;
  if (e.kind === 'painting') return e.dto.motive ? `painting · ${e.dto.motive}` : 'painting';
  if (e.kind === 'item_frame') {
    const n = e.dto.frameItem ? itemName(e.dto.frameItem[0]) : null;
    return n ? `item frame · ${n.replace(/_/g, ' ')}` : 'item frame';
  }
  return e.name ?? e.kind.replace(/_/g, ' ');
}

// ------------------------------------------------------------------------------------------------------------ internals

type DrawKind = 'model' | 'item' | 'block' | 'orb' | 'sprite' | 'painting' | 'frame' | 'box';

/** A mob shown inside a block (spawner cages), driven by the block-entity layer. */
interface DisplayMob {
  dim: number;
  x: number;
  y: number;
  z: number;
  mob: string;
  scale: number;
  spin: number;
}

/** What one entity (or bot, or display mob) draws; kept across frames, refreshed by `update`. */
interface Drawn {
  key: string;
  bot: boolean;
  dim: number;
  x: number;
  y: number;
  z: number;
  kind: DrawKind;
  sig: string;
  look: Look | null;
  model: ModelEntry | null;
  v: Variant;
  w: number;
  h: number;
  colour: number;
  row: number;
  /** Bones written this frame (else only the box slot is valid). */
  posed: boolean;
  /** The last `showFor` drew it as a model (else as a LOD box, or not at all: null). */
  detail: boolean | null;
  /** Drops, falling blocks, fireworks: what is drawn; minecarts / item frames: the block or item inside. */
  item: ItemLook | null;
  count: number;
  /** Main hand (right arm), off hand (left arm), a block worn on the head (pumpkin). */
  held: ItemLook | null;
  heldTool: boolean;
  heldOff: ItemLook | null;
  heldOffTool: boolean;
  /** The main hand holds a bow (drawn while using an item). */
  bow: boolean;
  headBlock: ItemLook | null;
  /** Worn armour materials [helmet, chestplate, leggings, boots]. */
  armor: ReadonlyArray<string | null> | null;
  baby: boolean;
  invisible: boolean;
  glowing: boolean;
  burning: boolean;
  flash: number;
  hurt: number;
  sky: number;
  block: number;
  bright: number;
  age: number;
  /** Objects keep the yaw they are given (no walk turning); null = living. */
  fixedYaw: number | null;
  fixedPitch: number;
  /** Paintings and item frames: front yaw, size (blocks), painting texture rect, item rotation (45° steps). */
  hang: { yaw: number; w: number; h: number; tex: [number, number, number, number] | null; rot: number } | null;
  display: DisplayMob | null;
  /** The death smoke was puffed. */
  smoked: boolean;
  /** Bots: the held/equipment input last resolved. */
  eqSig: string;
  motion: Motion;
  frame: number;
  ent: EntityView | null;
  botView: BotView | null;
}

interface ModelEntry {
  built: BuiltModel;
  skel: Skeleton;
  geometry: THREE.BufferGeometry;
  opaque: InstancedDraw;
  translucent: InstancedDraw | null;
  /** Armour geometry on this model's bones (bipeds; built on first use). */
  armor?: InstancedDraw | null;
}

interface BotObj {
  root: THREE.Group;
  ring: THREE.Mesh;
  label: THREE.Sprite | null;
  labelText: string;
}

interface Label {
  sprite: THREE.Sprite;
  text: string;
}

/** Extra slots in an entity's bone row (models with more parts than CONTENT_SLOT skip these extras). */
const HELD_SLOT = 62;
const FIRE_SLOT = 61;
const OFF_SLOT = 60;
const HEAD_SLOT = 59;
const CONTENT_SLOT = 58;
/** Particles per bone row. */
const PER_ROW = 63;
const F_EMISSIVE = 1, F_FLAT = 2, F_BLOCKS = 4, F_ENTITY = 8, F_BILLBOARD = 16, F_FIRE = 32, F_FRONT = 64, F_SCROLL = 128;
const LIVING = new Set(['mob', 'player', 'armor_stand']);
/** Flag bits read by number (the wire's final layout: 0x04 riding, 0x10 using an item). */
const FLAG_RIDING = 0x04, FLAG_USING_ITEM = 0x10;

/** The wire's appearance tokens (`look`) next to the legacy numeric `variant`. */
const lookOf = (dto: object): string | null => {
  const l = (dto as { look?: unknown }).look;
  return typeof l === 'string' && l ? l : null;
};

/** An armor stand's pose from the wire (part → [x, y, z] degrees), or null. */
const standPoseOf = (dto: EntityDto): Readonly<Record<string, readonly number[]>> | null => {
  const p = (dto as { pose?: unknown }).pose;
  return p && typeof p === 'object' ? (p as Record<string, readonly number[]>) : null;
};

export interface EntityShowOptions {
  bots: boolean;
  entities: boolean;
  labels: boolean;
  selected?: string | null;
  /** Entities beyond this are not drawn (the view's draw distance; fog hides them anyway). */
  drawDistance?: number;
  /** The view's vertical field of view (degrees): narrow (iso) views see far entities large, so detail reaches further. */
  fov?: number;
}

export class EntityLayer {
  readonly group = new THREE.Group();
  /** Beyond this distance (blocks) from the view's camera entities draw as plain coloured boxes and are not animated. */
  detailDistance = 48;
  /** Optional per-entity light: [sky 0..15, block 0..15] at a world position (null = open sky). */
  lightAt: ((dim: number, x: number, y: number, z: number) => readonly [number, number] | null) | null = null;
  /** Entity events from the delta stream (swing, swing_off, hurt, death, crit, magic_crit, eat, teleport, lightning). */
  onEvents: ((events: EntityEvent[], nowMs: number) => void) | null = (events, now) => this.applyEvents(events, now);
  /** Blob shadows under entities. */
  shadows = true;
  private readonly labels = new LabelFactory();
  private readonly botObjs = new Map<string, BotObj>();
  private readonly entLabels = new Map<string, Label>();
  private readonly drawn = new Map<string, Drawn>();
  private readonly displays = new Map<string, DisplayMob>();
  private list: Drawn[] = [];
  private readonly world: WorldUniforms;
  private readonly mats: EntityMaterials;
  private readonly models = new Map<string, ModelEntry>();
  private readonly boxModel: ModelEntry;
  private readonly sprites: InstancedDraw;
  private readonly cubes: InstancedDraw;
  private readonly shadowDraw: ShadowDraw;
  private readonly particles = new Particles();
  private particleRow = 0;
  private readonly lightning = new Lightning();
  private readonly spriteGeo = spriteGeometry();
  private readonly cubeGeo = cubeGeometry();
  private readonly ringGeo = new THREE.RingGeometry(0.55, 0.75, 32).rotateX(-Math.PI / 2).translate(0, 0.03, 0);
  private readonly ringMat = new THREE.MeshBasicMaterial({ color: 0xffffff, transparent: true, opacity: 0.9, depthTest: false });
  private bones = new Float32Array(0);
  private boneTex: THREE.DataTexture | null = null;
  private boneRows = 0;
  private bundle: AssetBundle | null = null;
  private version: GameVersion = 'b1.8.1';
  private textures: THREE.Texture[] = [];
  private frame = 0;
  private now = 0;
  private lastCams: Array<{ p: THREE.Vector3; k: number }> = [];
  private nextCams: Array<{ p: THREE.Vector3; k: number }> = [];
  private visibleBots: Drawn[] = [];
  private drawnCount = 0;
  private heldCache = new Map<string, ItemLook | null>();
  private readonly root = new Float32Array(12);
  private readonly tmpA = new Float32Array(12);
  private readonly tmpB = new Float32Array(12);
  private readonly inst: InstanceData = { row: 0, slot: 0, layer: 0, tex: [0, 0, 1, 1], r: 1, g: 1, b: 1, flags: 0, hurt: 0, sky: 15, block: 0, flash: 0 };
  private readonly ctx: PoseCtx = {
    swing: 0, amount: 0, age: 0, headYaw: 0, pitch: 0, attack: 0, attackOff: 0, eat: 0, baby: false, v: Variant.NONE, variant: 0, flags: 0, sneaking: false, riding: false,
    holding: false, aggressive: false, using: false, bow: false, standPose: null, beta: false, seed: 0, speed: 0,
  };

  constructor(world?: WorldUniforms) {
    this.group.name = 'entities';
    this.world = world ?? defaultWorldUniforms();
    this.mats = createEntityMaterials(this.world);
    this.boxModel = this.entry(MODEL_BY_ID.box);
    this.sprites = new InstancedDraw(this.spriteGeo, this.mats.tile, true, 'item sprites');
    this.cubes = new InstancedDraw(this.cubeGeo, this.mats.tile, true, 'item cubes');
    this.shadowDraw = new ShadowDraw(this.mats.shadow);
    this.group.add(this.sprites.mesh, this.cubes.mesh, this.shadowDraw.mesh, this.lightning.group);
    this.ensureRows(32);
  }

  // ------------------------------------------------------------------------------------------------------------ setup

  /** Hands the layer the bundled textures of the world's version (models draw as boxes until then). */
  setBundle(bundle: AssetBundle | null): void {
    if (bundle === this.bundle) return;
    for (const t of this.textures) t.dispose();
    this.textures = [];
    this.bundle = bundle;
    this.heldCache.clear();
    for (const d of this.drawn.values()) d.sig = '';
    if (!bundle) {
      this.mats.shared.uTex.value = this.mats.shared.uItems.value = this.mats.shared.uBlocks.value = null;
      return;
    }
    this.version = bundle.version;
    const ent = sheetTexture(bundle.entity), items = sheetTexture(bundle.items), blocks = sheetTexture(bundle.blocks);
    this.textures.push(ent, items, blocks);
    this.mats.shared.uTex.value = ent;
    this.mats.shared.uItems.value = items;
    this.mats.shared.uBlocks.value = blocks;
    const grid = this.blocksGrid(bundle.pack);
    this.mats.shared.uBlocksGrid.value.set(grid[0], grid[1]);
  }

  /** Shares the world's per-view uniforms (time of day, fog) so entities light and fog like the sections. */
  setWorldUniforms(u: WorldUniforms): void {
    for (const m of [this.mats.model, this.mats.modelTranslucent, this.mats.tile, this.mats.shadow])
      for (const k of Object.keys(u) as Array<keyof WorldUniforms>) if (m.uniforms[k]) m.uniforms[k] = u[k];
  }

  /** The version used before a bundle arrives (labels, looks). */
  setVersion(v: GameVersion): void {
    if (v === this.version) return;
    this.version = v;
    for (const d of this.drawn.values()) d.sig = '';
  }

  private blocksGrid(pack: Pack): [number, number] {
    return pack.version === '1.12.2' ? [pack.blocks.columns, pack.blocks.rows] : [pack.terrain.columns, pack.terrain.rows];
  }

  private itemsGrid(pack: Pack): [number, number] {
    return [pack.items.columns, pack.items.rows];
  }

  private entry(def: (typeof MODEL_BY_ID)[string]): ModelEntry {
    let e = this.models.get(def.id);
    if (e) return e;
    const built = buildModel(def);
    const geometry = modelGeometry(built);
    const opaque = new InstancedDraw(geometry, this.mats.model, false, `entity ${def.id}`);
    this.group.add(opaque.mesh);
    e = { built, skel: new Skeleton(built), geometry, opaque, translucent: null };
    this.models.set(def.id, e);
    return e;
  }

  private translucentOf(e: ModelEntry): InstancedDraw {
    if (!e.translucent) {
      e.translucent = new InstancedDraw(e.geometry, this.mats.modelTranslucent, false, `entity ${e.built.def.id} (translucent)`);
      e.translucent.mesh.renderOrder = 2;
      this.group.add(e.translucent.mesh);
    }
    return e.translucent;
  }

  private ensureRows(n: number): void {
    if (n <= this.boneRows && this.boneTex) return;
    let rows = Math.max(32, this.boneRows);
    while (rows < n) rows *= 2;
    const data = new Float32Array(rows * ROW_FLOATS);
    data.set(this.bones.subarray(0, Math.min(this.bones.length, data.length)));
    this.bones = data;
    this.boneTex?.dispose();
    const t = new THREE.DataTexture(data, ROW_FLOATS / 4, rows, THREE.RGBAFormat, THREE.FloatType);
    t.magFilter = t.minFilter = THREE.NearestFilter;
    t.generateMipmaps = false;
    t.needsUpdate = true;
    this.boneTex = t;
    this.boneRows = rows;
    this.mats.shared.uBones.value = t;
  }

  // ------------------------------------------------------------------------------------------------------------ hooks

  private motionOf(key: string): Motion | null {
    return this.drawn.get(key)?.motion ?? null;
  }

  /** Swings the right arm (or, `off`, the left) of a bot (name) or entity (key "dim:id"). */
  swing(key: string, off = false, now = performance.now()): void {
    const m = this.motionOf(key);
    if (m) off ? (m.attackOffAt = now) : (m.attackAt = now);
  }

  /** Red hurt flash (vanilla hurtTime) on a bot or entity. Bots flash by themselves when their hp drops. */
  hurt(key: string, now = performance.now()): void {
    const m = this.motionOf(key);
    if (m) m.hurtAt = now;
  }

  /** Starts the death fall, then the body shrinks away (it stays drawn for that long even when the entity is removed). */
  kill(key: string, now = performance.now()): void {
    const m = this.motionOf(key);
    if (m && !m.deadAt) {
      m.deadAt = now;
      m.hurtAt = now;
    }
  }

  /**
   * A mob shown inside a block (the spinning mob in a spawner cage): `mob` is a mob name ("Zombie", "minecraft:zombie",
   * "cave_spider"…), drawn at (x, y, z) (its feet) scaled by `scale` and turned by `spinRad` about y. Call again to move or
   * turn it (cheap: one map update); `hideDisplayMob` removes it. Display mobs share the entity draw calls and LOD.
   */
  showDisplayMob(key: string, dim: number, x: number, y: number, z: number, mob: string, scale: number, spinRad: number): void {
    const d = this.displays.get(key);
    if (d) Object.assign(d, { dim, x, y, z, mob, scale, spin: spinRad });
    else this.displays.set(key, { dim, x, y, z, mob, scale, spin: spinRad });
  }

  hideDisplayMob(key: string): void {
    this.displays.delete(key);
  }

  /** The entity event animations: swings, hurts, deaths, sparks, eating, teleport puffs, lightning bolts. */
  applyEvents(events: EntityEvent[], now: number): void {
    for (const ev of events) {
      const dim = ev.dim ?? 0;
      if (ev.kind === 'lightning') {
        if (ev.x != null && ev.y != null && ev.z != null) this.lightning.strike(dim, ev.x, ev.y, ev.z, now, ev.at || now);
        continue;
      }
      const d = this.eventTarget(ev);
      const m = d?.motion;
      switch (ev.kind) {
        case 'swing':
          if (m) m.attackAt = now;
          break;
        case 'swing_off':
          if (m) m.attackOffAt = now;
          break;
        case 'hurt':
          if (m) m.hurtAt = now;
          break;
        case 'death':
          if (m && !m.deadAt) {
            m.deadAt = now;
            m.hurtAt = now;
          }
          break;
        case 'eat':
          if (m) m.eatAt = now;
          break;
        case 'crit':
        case 'magic_crit':
          if (d) this.particles.burst(ev.kind, d.dim, d.x, d.y + d.h * 0.6, d.z, now);
          else if (ev.x != null && ev.y != null && ev.z != null) this.particles.burst(ev.kind, dim, ev.x, ev.y + 1, ev.z, now);
          break;
        case 'teleport':
          if (ev.x != null && ev.y != null && ev.z != null) this.particles.burst('teleport', dim, ev.x, ev.y + 1, ev.z, now);
          if (d) this.particles.burst('teleport', d.dim, d.x, d.y + d.h * 0.5, d.z, now);
          break;
      }
    }
  }

  /** The bot or entity an event is about: (dim, id), or a bot by `name` (bots carry no entity id on the wire). */
  private eventTarget(ev: EntityEvent): Drawn | null {
    const name = (ev as { name?: unknown; bot?: unknown }).name ?? (ev as { bot?: unknown }).bot;
    if (typeof name === 'string') {
      const b = this.drawn.get(name);
      if (b) return b;
    }
    if (ev.id == null) return null;
    const d = this.drawn.get(`${ev.dim ?? 0}:${ev.id}`);
    if (d) return d;
    for (const x of this.drawn.values()) if (x.bot && (x.botView?.dto as { id?: number } | undefined)?.id === ev.id) return x;
    return null;
  }

  // ------------------------------------------------------------------------------------------------------------ frame

  /** Once per frame: every bot and entity (every dimension) at its interpolated position. */
  update(bots: BotView[], ents: EntityView[], timeMs: number, selected: ReadonlySet<string>): void {
    const frame = ++this.frame;
    this.now = timeMs;
    this.lastCams = this.nextCams;
    this.nextCams = [];
    this.mats.shared.uTime.value = timeMs / 1000;
    const list = this.list;
    list.length = 0;
    for (const b of bots) {
      const d = this.drawnFor(b.name, true, b.x, b.y, b.z, b.yaw, timeMs);
      d.frame = frame;
      d.botView = b;
      d.ent = null;
      d.dim = b.dim;
      this.refreshBot(d, b, timeMs);
      list.push(d);
      this.botLabel(b, selected, timeMs);
    }
    for (const e of ents) {
      const d = this.drawnFor(e.key, false, e.x, e.y, e.z, e.yaw, timeMs);
      d.frame = frame;
      d.ent = e;
      d.botView = null;
      d.dim = e.dim;
      this.refreshEntity(d, e, timeMs);
      list.push(d);
    }
    for (const [k, dm] of this.displays) {
      const d = this.drawnFor(`display:${k}`, false, dm.x, dm.y, dm.z, 0, timeMs);
      d.frame = frame;
      d.dim = dm.dim;
      d.display = dm;
      this.refreshDisplay(d, dm);
      list.push(d);
    }
    for (const [k, d] of this.drawn) {
      if (d.frame === frame) continue;
      // The dying stay until their body has shrunk away (the server usually removes them right after the death event).
      if (d.motion.deadAt && timeMs - d.motion.deadAt < DEATH_GONE_MS) {
        d.frame = frame;
        list.push(d);
      } else this.drawn.delete(k);
    }
    for (const [k, o] of this.botObjs) if (!this.drawn.get(k)?.bot) this.dropBot(k, o);
    // Bones: one row per entity, then the particles (63 per row).
    this.particles.step(timeMs);
    this.lightning.update(timeMs);
    const prows = Math.ceil(this.particles.list.length / PER_ROW);
    this.ensureRows(list.length + prows + 1);
    for (let i = 0; i < list.length; i++) {
      const d = list[i];
      d.row = i;
      this.writeRow(d, timeMs);
      d.bright = selected.has(d.key) || (d.botView && selected.has(d.botView.dto.short)) ? 1.25 : 1;
    }
    this.particleRow = list.length;
    this.particles.list.forEach((p, i) => this.particles.bone(p, timeMs, this.bones, (this.particleRow + Math.floor(i / PER_ROW)) * ROW_FLOATS + (i % PER_ROW) * 12));
    if (this.boneTex) this.boneTex.needsUpdate = true;
  }

  private drawnFor(key: string, bot: boolean, x: number, y: number, z: number, yaw: number, now: number): Drawn {
    let d = this.drawn.get(key);
    if (!d) {
      d = {
        key, bot, dim: 0, x, y, z, kind: 'box', sig: '', look: null, model: null, v: Variant.NONE, w: 0.6, h: 1.8, colour: 0x9a9a9a, row: 0, posed: false, detail: null,
        item: null, count: 1, held: null, heldTool: false, heldOff: null, heldOffTool: false, bow: false, headBlock: null, armor: null, baby: false, invisible: false, glowing: false,
        burning: false, flash: 0, hurt: 0, sky: 15, block: 0, bright: 1, age: 0, fixedYaw: null, fixedPitch: 0, hang: null, display: null, smoked: false, eqSig: '',
        motion: newMotion(x, y, z, yaw, key, now), frame: 0, ent: null, botView: null,
      };
      this.drawn.set(key, d);
    }
    d.x = x;
    d.y = y;
    d.z = z;
    return d;
  }

  private setLook(d: Drawn, look: Look | null): void {
    d.look = look;
    const def = look ? MODEL_BY_ID[look.model] : undefined;
    d.model = def && def.id !== 'box' && this.bundle ? this.entry(def) : null;
    d.kind = d.model ? 'model' : 'box';
    const s = (look?.scale ?? 1) * (d.baby ? 0.5 : 1);
    d.w = (def?.width ?? 0.6) * s;
    d.h = (def?.height ?? 1.8) * s;
    d.colour = look?.colour ?? 0x9a9a9a;
  }

  private itemById(ref: ItemRef | undefined, fallbackId = 0): ItemLook | null {
    const id = ref?.[0] ?? fallbackId;
    if (!this.bundle || id <= 0) return null;
    return itemLookById(this.version, this.bundle.pack, id, ref?.[1] ?? 0);
  }

  /** Hands, head block and armour from wire equipment (ids in the world's version). */
  private equip(d: Drawn, eq: Equipment | null | undefined, mainFallback: ItemLook | null, mainName: string | null): void {
    d.held = eq?.main ? this.itemById(eq.main) : mainFallback;
    d.heldTool = eq?.main ? isHandheld(itemName(eq.main[0]) ?? '') : !!mainName && isHandheld(parseHeld(mainName).name);
    d.heldOff = eq?.off ? this.itemById(eq.off) : null;
    d.heldOffTool = !!eq?.off && isHandheld(itemName(eq.off[0]) ?? '');
    d.bow = eq?.main?.[0] === 261 || (!!mainName && parseHeld(mainName).name === 'bow');
    // A pumpkin (or jack-o'-lantern) on the head is drawn as a block; skulls and other head items are not modelled.
    d.headBlock = eq?.head && (eq.head[0] === 86 || eq.head[0] === 91) ? this.itemById(eq.head) : null;
    d.armor = armorFromEquipment(eq) ?? armorOf(d.ent?.dto ?? d.botView?.dto ?? {});
  }

  private refreshBot(d: Drawn, b: BotView, now: number): void {
    const sig = `bot|${this.version}|${!!this.bundle}`;
    if (d.sig !== sig) {
      d.sig = sig;
      this.setLook(d, playerLook(this.version, b.name, true));
      d.colour = botColour(b).getHex();
    }
    const m = d.motion;
    const hp = b.dto.hp;
    if (Number.isFinite(m.hp) && hp < m.hp && hp > 0) m.hurtAt = now;
    if (hp <= 0 && !m.deadAt) {
      m.deadAt = now;
      m.hurtAt = now;
    }
    if (hp > 0 && m.deadAt) {
      m.deadAt = 0;
      d.smoked = false;
    }
    m.hp = hp;
    const extra = b.dto as { equipment?: Equipment | null; flags?: number; headYaw?: number | null };
    const held = b.dto.held ?? null;
    const sigEq = `${held}|${JSON.stringify(extra.equipment ?? null)}`;
    if (d.sig + sigEq !== d.eqSig) {
      this.equip(d, extra.equipment, held ? this.heldLook(held) : null, held);
      d.eqSig = d.sig + sigEq;
    }
    const flags = extra.flags ?? 0;
    d.burning = (flags & FLAG_ON_FIRE) !== 0;
    d.invisible = (flags & FLAG_INVISIBLE) !== 0;
    d.glowing = (flags & FLAG_GLOWING) !== 0;
    d.flash = 0;
    stepMotion(m, b.x, b.y, b.z, b.yaw, now, (flags & FLAG_SPRINTING) !== 0);
  }

  private heldLook(held: string): ItemLook | null {
    const k = `${this.version}|${held}`;
    if (this.heldCache.has(k)) return this.heldCache.get(k)!;
    const l = this.bundle ? itemLookByName(this.version, this.bundle.pack, held) : null;
    if (this.bundle) this.heldCache.set(k, l);
    return l;
  }

  private refreshEntity(d: Drawn, e: EntityView, now: number): void {
    const dto = e.dto;
    const sig = `${this.version}|${!!this.bundle}|${e.kind}|${e.type}|${dto.variant ?? ''}|${lookOf(dto) ?? ''}|${e.item}|${dto.damage ?? 0}|${e.name ?? ''}|${dto.baby ? 1 : 0}|${JSON.stringify(dto.equipment ?? null)}|${dto.motive ?? ''}|${dto.facing ?? ''}|${dto.rotation ?? ''}|${dto.frameItem ?? ''}|${dto.block ?? ''}|${dto.minecartType ?? ''}`;
    const beta = this.version === 'b1.8.1';
    if (d.sig !== sig) {
      d.sig = sig;
      d.v = Variant.of(dto.variant, lookOf(dto));
      d.baby = !!dto.baby;
      d.item = null;
      d.hang = null;
      d.fixedYaw = null;
      const kind = e.kind === 'object' ? (objectName(e.type, beta) ?? (e.name ? objectKind(e.name) : 'object')) : e.kind;
      if (kind === 'player') this.setLook(d, playerLook(this.version, e.name ?? 'player', false));
      else if (kind === 'mob') this.setLook(d, mobLook(this.version, e.type, e.name, d.v, e.id));
      else if (kind === 'item') {
        this.setLook(d, null);
        d.item = this.bundle && e.item > 0 ? itemLookById(this.version, this.bundle.pack, e.item, dto.damage ?? 0) : null;
        d.kind = d.item ? 'item' : 'box';
        d.w = d.h = 0.25;
        d.colour = 0xd8b040;
      } else if (kind === 'xp_orb' || kind === 'xporb' || kind === 'orb') {
        this.setLook(d, null);
        d.kind = this.bundle ? 'orb' : 'box';
        d.w = d.h = 0.25;
        d.colour = 0x9ae040;
      } else if (kind === 'painting') this.setPainting(d, e);
      else if (kind === 'item_frame') this.setFrame(d, e);
      else if (kind === 'firework' || kind === 'fireworks_rocket') {
        this.setLook(d, null);
        d.item = this.bundle && !beta ? itemLookByName(this.version, this.bundle.pack, 'fireworks') : null;
        d.kind = d.item ? 'sprite' : 'box';
        d.w = d.h = 0.25;
        d.colour = 0xd04040;
      } else if (kind === 'falling_block' || kind === 'falling_sand' || kind === 'tnt') {
        this.setLook(d, null);
        const ref: ItemRef = kind === 'tnt' ? [46, 0] : (dto.block ?? (e.item > 0 ? [e.item, dto.damage ?? 0] : [beta && e.type === 71 ? 13 : 12, 0]));
        const l = this.itemById(ref);
        d.item = l?.kind === 'cube' ? l : null;
        d.kind = d.item ? 'block' : 'box';
        d.w = d.h = 0.98;
        d.colour = kind === 'tnt' ? 0xd04030 : 0xd8c890;
      } else {
        // Objects and stands with a model (boat, minecart, arrow, ender crystal, armor stand…).
        const look = objectLook(this.version, kind, d.v, e.id);
        this.setLook(d, look);
        if (!look) {
          d.w = d.h = 0.5;
          d.colour = 0x9a9a9a;
        }
        if (kind === 'minecart' || kind.endsWith('_minecart')) {
          const block = minecartBlock(dto.minecartType ?? (kind === 'minecart' ? 0 : kind));
          d.item = block ? this.itemById([block, 0]) : null;
        }
      }
      // Hand items and armour: wire equipment, else a player's held item id.
      this.equip(d, dto.equipment, kind === 'player' && e.item > 0 ? this.itemById(null, e.item) : null, null);
      if (!LIVING.has(kind) && d.kind === 'model') d.fixedYaw = e.yaw;
    }
    d.count = e.count;
    d.age = e.ageTicks;
    const flags = e.flags;
    d.burning = (flags & FLAG_ON_FIRE) !== 0;
    d.invisible = (flags & FLAG_INVISIBLE) !== 0;
    d.glowing = (flags & FLAG_GLOWING) !== 0;
    if (d.fixedYaw !== null) this.objectAim(d, e);
    // Creeper fusing (Mob16 state 1 / "fuse"/"ignited") and primed TNT (fuse ticks): white flashes.
    const fuse = e.kind === 'mob' && e.type === 50 && ((!d.v.isString && d.v.num === 1) || d.v.has('fuse', 'fusing', 'ignited'));
    const tnt = d.kind === 'block' && d.colour === 0xd04030;
    const fuseTicks = dto.fuse ?? 80 - (e.ageTicks % 80);
    d.flash = fuse ? (Math.sin(now / 60) > 0 ? 0.8 : 0) : tnt ? (Math.floor(fuseTicks / 5) % 2 === 0 ? 0.8 : 0) : 0;
    if (d.motion.deadAt && now - d.motion.deadAt > 4000) d.motion.deadAt = 0; // still reported long after: it lives
    stepMotion(d.motion, e.x, e.y, e.z, e.yaw, now, (flags & FLAG_SPRINTING) !== 0);
  }

  /** Objects face their wire yaw; arrows (and anything flying without a yaw) turn and pitch along their velocity. */
  private objectAim(d: Drawn, e: EntityView): void {
    const vx = e.dto.vx ?? 0, vy = e.dto.vy ?? 0, vz = e.dto.vz ?? 0;
    const h = Math.hypot(vx, vz);
    const flying = d.look?.model === 'arrow';
    if (flying && h + Math.abs(vy) > 1e-3) {
      d.fixedYaw = (Math.atan2(-vx, vz) * 180) / Math.PI;
      d.fixedPitch = (-Math.atan2(vy, h) * 180) / Math.PI;
    } else d.fixedYaw = e.yaw;
  }

  private setPainting(d: Drawn, e: EntityView): void {
    this.setLook(d, null);
    const m = motive(e.dto.motive);
    const beta = this.version === 'b1.8.1';
    const r = this.rect(paintingSheet(beta));
    d.hang = {
      yaw: facingYaw(e.dto.facing),
      w: m.w / 16,
      h: m.h / 16,
      tex: r ? [r[0] + (m.u / 256) * r[2], r[1] + (m.v / 256) * r[3], (m.w / 256) * r[2], (m.h / 256) * r[3]] : null,
      rot: 0,
    };
    d.kind = r ? 'painting' : 'box';
    d.w = Math.max(d.hang.w, 0.1);
    d.h = d.hang.h;
    d.colour = 0x8a6a4a;
  }

  private setFrame(d: Drawn, e: EntityView): void {
    this.setLook(d, null);
    d.hang = { yaw: facingYaw(e.dto.facing), w: 0.75, h: 0.75, tex: null, rot: (e.dto.rotation ?? 0) & 7 };
    d.item = this.itemById(e.dto.frameItem);
    // b1.8.1 has no item frames (and no frame textures): a box.
    d.kind = this.bundle?.pack.version === '1.12.2' ? 'frame' : 'box';
    d.w = 0.75;
    d.h = 0.75;
    d.colour = 0xa08050;
  }

  private refreshDisplay(d: Drawn, dm: DisplayMob): void {
    const sig = `display|${this.version}|${!!this.bundle}|${dm.mob}`;
    if (d.sig !== sig) {
      d.sig = sig;
      const name = dm.mob.toLowerCase().replace(/^minecraft:/, '').replace(/^entity/, '');
      this.setLook(d, mobLook(this.version, -1, mobName(-1, name), null, 0));
    }
    d.fixedYaw = (dm.spin * 180) / Math.PI;
    d.age = this.now / 50;
  }

  /** Writes the entity's bone row: the LOD box always; the posed parts when some view's camera was near last frame. */
  private writeRow(d: Drawn, now: number): void {
    const off = d.row * ROW_FLOATS;
    const m = d.motion;
    const yaw = d.hang ? d.hang.yaw : d.fixedYaw ?? (d.kind === 'model' ? m.body : d.ent?.yaw ?? 0);
    // LOD box: translate · rotY · scale(w, h, w).
    const a = (yaw * Math.PI) / 180;
    const b = this.bones;
    const bo = off + BOX_SLOT * 12;
    const c = Math.cos(a), s = Math.sin(a);
    const k = deathScale(m, now);
    b[bo] = c * d.w * k;
    b[bo + 1] = 0;
    b[bo + 2] = s * d.w * k;
    b[bo + 3] = d.x;
    b[bo + 4] = 0;
    b[bo + 5] = d.h * k;
    b[bo + 6] = 0;
    b[bo + 7] = d.y;
    b[bo + 8] = -s * d.w * k;
    b[bo + 9] = 0;
    b[bo + 10] = c * d.w * k;
    b[bo + 11] = d.z;
    const light = this.lightAt?.(d.dim, d.x, d.y + d.h * 0.5, d.z) ?? null;
    d.sky = light ? light[0] : 15;
    d.block = light ? light[1] : 0;
    d.hurt = hurtAmount(m, now);
    if (m.deadAt && !d.smoked && now - m.deadAt > DEATH_SHRINK_MS) {
      d.smoked = true;
      this.particles.burst('death', d.dim, d.x, d.y + 0.3, d.z, now);
    }
    d.posed = false;
    if (!this.near(d)) return;
    if (d.kind === 'model' && d.model && d.look) this.poseModel(d, now, off);
    else if (d.kind === 'item') this.poseDrop(d, off);
    else if (d.kind === 'block') this.poseBlock(d, off);
    else if (d.kind === 'orb' || d.kind === 'sprite') this.poseOrb(d, off);
    else if (d.kind === 'painting' || d.kind === 'frame') this.poseHanging(d, off);
    d.posed = d.kind !== 'box';
  }

  private near(d: Drawn): boolean {
    if (!this.lastCams.length) return true;
    const r2 = (this.detailDistance + 4) ** 2;
    for (const c of this.lastCams) if (((c.p.x - d.x) ** 2 + (c.p.y - d.y) ** 2 + (c.p.z - d.z) ** 2) * c.k * c.k <= r2) return true;
    return false;
  }

  private poseModel(d: Drawn, now: number, off: number): void {
    const e = d.model!, look = d.look!, def = e.built.def, m = d.motion;
    const layout = d.baby && def.baby ? true : false;
    const s0 = def.scale ?? 1, k = (look.scale ?? 1) * (d.baby && !def.baby ? 0.5 : 1) * deathScale(m, now) * (d.display?.scale ?? 1);
    const sc: [number, number, number] = typeof s0 === 'number' ? [s0 * k, s0 * k, s0 * k] : [s0[0] * k, s0[1] * k, s0[2] * k];
    const flags = d.ent?.flags ?? (d.botView?.dto as { flags?: number } | undefined)?.flags ?? 0;
    const sneaking = (flags & FLAG_SNEAKING) !== 0;
    const isPlayer = d.bot || d.ent?.kind === 'player';
    const body = d.fixedYaw ?? m.body;
    rootMatrix(this.root, {
      x: d.x,
      y: d.y,
      z: d.z,
      yaw: body,
      death: deathAngle(m, now),
      scale: sc,
      offset: def.offset ?? [0, 0, 0],
      lift: sneaking && isPlayer ? -0.125 : 0,
    });
    const c = this.ctx;
    // Head yaw: the wire's own when it sends one (1.12.2 head look), else the look yaw; smoothed toward it.
    const wireHead = d.ent?.dto.headYaw ?? (d.botView?.dto as { headYaw?: number | null } | undefined)?.headYaw ?? null;
    const lookYaw = wireHead ?? (d.botView ? d.botView.yaw : d.ent?.yaw ?? body);
    m.head = Number.isNaN(m.head) ? lookYaw : m.head + wrapDeg(lookYaw - m.head) * 0.35;
    c.swing = d.display ? 0 : m.swing;
    c.amount = m.deadAt || d.display ? 0 : m.amount;
    c.age = now / 50 + m.seed * 1000;
    c.headYaw = d.display ? 0 : Math.max(-75, Math.min(75, wrapDeg(m.head - body)));
    c.pitch = d.fixedYaw !== null && d.look?.model === 'arrow' ? d.fixedPitch : Math.max(-90, Math.min(90, d.botView?.dto.pitch ?? 0));
    c.attack = attackProgress(m, now);
    c.attackOff = attackOffProgress(m, now);
    c.eat = eatProgress(m, now);
    c.baby = d.baby;
    c.v = d.v;
    c.variant = d.v.num;
    c.flags = flags;
    c.sneaking = sneaking;
    c.riding = (flags & FLAG_RIDING) !== 0 || (d.ent?.dto as { vehicle?: unknown } | undefined)?.vehicle != null;
    c.holding = !!d.held;
    c.using = (flags & FLAG_USING_ITEM) !== 0;
    c.bow = d.bow;
    c.aggressive = d.v.has('aggressive', 'attacking', 'arms_raised', 'charging') || (c.using && d.bow);
    c.standPose = d.ent ? standPoseOf(d.ent.dto) : null;
    c.beta = this.version === 'b1.8.1';
    c.seed = m.seed;
    c.speed = m.speed;
    e.skel.pose(c, this.root, this.bones, off, layout);
    if (e.built.parts.length <= CONTENT_SLOT) {
      if (d.held) this.poseHeld(d, e, off, false);
      if (d.heldOff) this.poseHeld(d, e, off, true);
      if (d.headBlock) this.poseHeadBlock(e, off);
      if (d.item) this.poseContents(d, off, body);
    }
    if (d.burning) this.poseFire(d, off);
  }

  /** A held item in the right (or left) hand: arm matrix · hand offset (pixels, arm space) · item orientation · size. */
  private poseHeld(d: Drawn, e: ModelEntry, off: number, left: boolean): void {
    const arm = e.built.index[left ? 'leftArm' : 'rightArm'];
    if (arm == null) return;
    const am = e.skel.partMatrix(arm);
    const l = this.tmpA, out = this.tmpB;
    const item = left ? d.heldOff! : d.held!, tool = left ? d.heldOffTool : d.heldTool;
    // Arm space: pixels, y down the arm, −z forward; the fist is around (∓1, 10, 0). Item geometry is y-up (0..1).
    const h = HELD_POSES[item.kind === 'cube' ? 'block' : tool ? 'tool' : 'flat'];
    // Columns: where the item's x (right), y (up) and z (normal) axes go in arm space, times its size. The left hand mirrors
    // across x (and flips the item's right axis so the basis stays right-handed).
    const mx = left ? -1 : 1;
    for (let r = 0; r < 3; r++) {
      const sx = r === 0 ? mx : 1;
      l[r * 4] = h.right[r] * h.size * sx * mx;
      l[r * 4 + 1] = h.up[r] * h.size * sx;
      l[r * 4 + 2] = h.normal[r] * h.size * sx;
      l[r * 4 + 3] = h.at[r] * sx;
    }
    // Pre-translate in item units (centre the cube / put the grip at the fist).
    const px = h.pre[0] * mx;
    l[3] += l[0] * px + l[1] * h.pre[1] + l[2] * h.pre[2];
    l[7] += l[4] * px + l[5] * h.pre[1] + l[6] * h.pre[2];
    l[11] += l[8] * px + l[9] * h.pre[1] + l[10] * h.pre[2];
    mul(out, am, l);
    this.bones.set(out, off + (left ? OFF_SLOT : HELD_SLOT) * 12);
  }

  /** A pumpkin worn on the head: a block over the head box (rotated 180° about z so its front stays at the face). */
  private poseHeadBlock(e: ModelEntry, off: number): void {
    const head = e.built.index.head;
    if (head == null) return;
    const l = identity(this.tmpA);
    const s = 8.6;
    l[0] = -s;
    l[5] = -s;
    l[10] = s;
    l[7] = 0.3;
    mul(this.tmpB, e.skel.partMatrix(head), l);
    this.bones.set(this.tmpB, off + HEAD_SLOT * 12);
  }

  /** Minecart contents: the block inside, at the cart's floor, turned with it. */
  private poseContents(d: Drawn, off: number, yaw: number): void {
    if (d.look?.model !== 'minecart') return;
    compose(this.tmpA, d.x, d.y + 0.3, d.z, (-yaw * Math.PI) / 180, 0, 0.6, 0.6, 0.6, 0, 0, 0);
    this.bones.set(this.tmpA, off + CONTENT_SLOT * 12);
  }

  private poseFire(d: Drawn, off: number): void {
    const b = this.bones, o = off + FIRE_SLOT * 12;
    const s = Math.max(d.w * 1.4, d.h * 0.85);
    identity(this.tmpA);
    this.tmpA[0] = this.tmpA[5] = this.tmpA[10] = s;
    this.tmpA[3] = d.x;
    this.tmpA[7] = d.y;
    this.tmpA[11] = d.z;
    b.set(this.tmpA, o);
  }

  /** Dropped items: RenderEntityItem bob and spin; 1–4 (beta) / 1–5 copies by stack size, a little scattered. */
  private poseDrop(d: Drawn, off: number): void {
    const beta = this.version === 'b1.8.1';
    const hover = d.motion.seed * Math.PI * 2;
    const bob = Math.sin(d.age / 10 + hover) * 0.1 + 0.1;
    const spin = d.age / 20 + hover;
    const n = copies(d.count, beta);
    const cube = d.item?.kind === 'cube';
    const size = cube ? 0.25 : 0.5;
    let seed = Math.floor(d.motion.seed * 1e6) | 1;
    const rnd = () => (((seed = (Math.imul(seed, 1103515245) + 12345) | 0) >>> 0) / 4294967296) * 2 - 1;
    for (let k = 0; k < n; k++) {
      const j = k === 0 ? [0, 0, 0] : [rnd() * 0.15, rnd() * 0.15, rnd() * 0.15];
      compose(this.tmpA, d.x + j[0], d.y + bob + (cube ? 0 : 0.02) + j[1], d.z + j[2], spin, 0, size, size, size, 0, 0, 0);
      this.bones.set(this.tmpA, off + k * 12);
    }
  }

  /** Falling blocks and primed TNT (which swells in its last 10 ticks, EntityTNTPrimed render). */
  private poseBlock(d: Drawn, off: number): void {
    const fuse = d.ent?.dto.fuse;
    let grow = 1;
    if (d.colour === 0xd04030 && fuse != null && fuse < 10) {
      const f = Math.min(1, Math.max(0, 1 - fuse / 10));
      grow = 1 + f * f * f * f * 0.3;
    }
    const s = 0.98 * grow;
    compose(this.tmpA, d.x, d.y - (s - 0.98) / 2, d.z, 0, 0, s, s, s, 0, 0, 0);
    this.bones.set(this.tmpA, off);
  }

  /** XP orbs and fireworks: one billboard. */
  private poseOrb(d: Drawn, off: number): void {
    const m = identity(this.tmpA);
    m[0] = m[5] = m[10] = d.kind === 'sprite' ? 0.5 : 0.3;
    m[3] = d.x;
    m[7] = d.y + 0.1;
    m[11] = d.z;
    this.bones.set(m, off);
  }

  /** Paintings (a flat picture of the motive's size) and item frames (a thin plate plus the framed item, turned in 45°
   * steps), standing off the wall toward their facing. */
  private poseHanging(d: Drawn, off: number): void {
    const h = d.hang!;
    const ry = (-h.yaw * Math.PI) / 180;
    const nx = -Math.sin((h.yaw * Math.PI) / 180), nz = Math.cos((h.yaw * Math.PI) / 180);
    if (d.kind === 'painting') {
      compose(this.tmpA, d.x + nx * 0.03, d.y, d.z + nz * 0.03, ry, 0, h.w, h.h, 1, 0, -0.5, 0);
      this.bones.set(this.tmpA, off);
      return;
    }
    // The 1.12.2 wire gives an item frame's hanging block (its integer corner), not the entity centre: the frame sits in that
    // block against the wall behind it, 1/32 off the wall, centred vertically.
    let fx = d.x, fy = d.y, fz = d.z;
    if (Number.isInteger(d.x) && Number.isInteger(d.y) && Number.isInteger(d.z)) {
      fx = d.x + 0.5 - nx * (0.5 - 1 / 32);
      fy = d.y + 0.5;
      fz = d.z + 0.5 - nz * (0.5 - 1 / 32);
    }
    compose(this.tmpA, fx, fy, fz, ry, 0, 0.75, 0.75, 1 / 16, 0, -0.5, 0);
    this.bones.set(this.tmpA, off);
    if (d.item) {
      const cube = d.item.kind === 'cube';
      const s = cube ? 0.32 : 0.5;
      const lift = 1 / 32 + (cube ? 0 : 0.01);
      compose(this.tmpA, fx + nx * lift, fy, fz + nz * lift, ry, (-h.rot * Math.PI) / 4, s, s, cube ? s : 1, 0, -0.5, cube ? 0 : 0);
      this.bones.set(this.tmpA, off + 12);
    }
  }

  // ------------------------------------------------------------------------------------------------------------ views

  /**
   * Before rendering one view: its dimension only, its instances (models near its camera, boxes beyond `detailDistance`),
   * its selection ring, and labels (bot labels always when `labels`; mob/drop labels near its camera).
   */
  showFor(dim: number, cam: THREE.Vector3, opts: EntityShowOptions): void {
    // Detail by apparent size: distances are scaled by the view's zoom relative to a 70° field of view.
    const zoom = opts.fov ? Math.tan((opts.fov * Math.PI) / 360) / Math.tan((35 * Math.PI) / 180) : 1;
    this.nextCams.push({ p: cam.clone(), k: zoom });
    for (const e of this.models.values()) {
      e.opaque.reset();
      e.translucent?.reset();
      e.armor?.reset();
    }
    this.sprites.reset();
    this.cubes.reset();
    this.shadowDraw.reset();
    this.visibleBots.length = 0;
    const far = (opts.drawDistance ?? 1e9) + 8, detail = this.detailDistance;
    const want = new Set<string>();
    let n = 0;
    for (const d of this.list) {
      d.detail = null;
      if (d.dim !== dim) continue;
      if (d.bot ? !opts.bots : !opts.entities) continue;
      if (d.motion.deadAt && this.now - d.motion.deadAt >= DEATH_GONE_MS) continue;
      const real = Math.hypot(d.x - cam.x, d.y - cam.y, d.z - cam.z);
      if (real > far) continue;
      const dist = real * zoom;
      if (d.bot) this.visibleBots.push(d);
      n++;
      d.detail = !(dist > detail || !d.posed || !this.bundle);
      if (d.detail) this.pushDetail(d, dist);
      else if (!d.invisible) this.pushBox(d);
      if (!d.bot && !d.display && opts.labels && d.ent && !d.invisible) this.entityLabel(d, d.ent, dist, want);
    }
    if (opts.entities) this.pushParticles(dim);
    this.lightning.showFor(dim);
    for (const e of this.models.values()) {
      e.opaque.commit();
      e.translucent?.commit();
      e.armor?.commit();
    }
    this.boxModel.opaque.commit();
    this.sprites.commit();
    this.cubes.commit();
    this.shadowDraw.commit();
    this.drawnCount = n;
    for (const [k, l] of this.entLabels) {
      if (want.has(k)) l.sprite.visible = true;
      else if (opts.labels) {
        this.group.remove(l.sprite);
        this.labels.release(l.sprite);
        this.entLabels.delete(k);
      } else l.sprite.visible = false;
    }
    for (const [name, o] of this.botObjs) {
      const d = this.drawn.get(name);
      o.root.visible = !!d && opts.bots && d.dim === dim;
      if (d) o.root.position.set(d.x, d.y, d.z);
      if (o.label) o.label.visible = opts.labels;
      o.ring.visible = !!opts.selected && (o.root.userData.name === opts.selected || o.root.userData.short === opts.selected);
    }
  }

  private base(d: Drawn, slot: number, layer: number): InstanceData {
    const i = this.inst;
    i.row = d.row;
    i.slot = slot;
    i.layer = layer;
    i.r = i.g = i.b = d.bright;
    i.flags = d.glowing ? F_EMISSIVE : 0;
    i.hurt = d.hurt;
    i.sky = d.sky;
    i.block = d.block;
    i.flash = d.glowing ? Math.max(d.flash, 0.12) : d.flash;
    i.faces = undefined;
    i.tintMask = 0;
    return i;
  }

  private pushBox(d: Drawn): void {
    const i = this.base(d, BOX_SLOT, 0);
    i.r = (((d.colour >> 16) & 255) / 255) * d.bright;
    i.g = (((d.colour >> 8) & 255) / 255) * d.bright;
    i.b = ((d.colour & 255) / 255) * d.bright;
    i.flags |= F_FLAT;
    i.tex = [0, 0, 1, 1];
    this.boxModel.opaque.push(i);
  }

  private rect(key: string): [number, number, number, number] | null {
    const p = this.bundle?.pack;
    const r = p?.entity.rects[key];
    if (!p || !r) return null;
    return [r[0] / p.entity.width, r[1] / p.entity.height, r[2] / p.entity.width, r[3] / p.entity.height];
  }

  private pushDetail(d: Drawn, dist: number): void {
    if (this.shadows && dist < this.detailDistance && !d.display && !d.hang && !d.invisible) {
      const r = d.kind === 'item' ? 0.15 : Math.min(3, Math.max(0.25, d.w * 0.5 + 0.1));
      this.shadowDraw.push(d.x, d.y, d.z, r * deathScale(d.motion, this.now), d.motion.deadAt ? 0.2 : 0.45);
    }
    if (d.kind === 'model') {
      const e = d.model!, look = d.look!;
      const tex = this.rect(look.texture);
      if (!tex) return this.pushBox(d);
      // Invisible (0x20): no body, but armour and held items still show (vanilla draws them).
      if (!d.invisible) {
        const i = this.base(d, 0, 0);
        i.tex = tex;
        tint(i, look.tint, d.bright);
        (look.translucent ? this.translucentOf(e) : e.opaque).push(i);
        for (const l of look.layers ?? []) {
          const lt = this.rect(l.texture);
          if (!lt) continue;
          const li = this.base(d, 0, l.layer ? (e.built.layers[l.layer] ?? 0) : 0);
          li.tex = lt;
          tint(li, l.tint, d.bright);
          if (l.emissive) li.flags |= F_EMISSIVE;
          if (l.scroll) li.flags |= F_SCROLL;
          (l.translucent ? this.translucentOf(e) : e.opaque).push(li);
        }
      }
      if (e.built.parts.length <= CONTENT_SLOT) {
        if (d.armor) this.pushArmor(d, e);
        if (d.held) this.pushItem(d, d.held, HELD_SLOT, false);
        if (d.heldOff) this.pushItem(d, d.heldOff, OFF_SLOT, false);
        if (d.headBlock) this.pushItem(d, d.headBlock, HEAD_SLOT, false);
        if (d.item && d.look?.model === 'minecart') this.pushItem(d, d.item, CONTENT_SLOT, false);
      }
      if (d.burning) this.pushFire(d);
      return;
    }
    if (d.kind === 'item' && d.item) {
      const n = copies(d.count, this.version === 'b1.8.1');
      for (let k = 0; k < n; k++) this.pushItem(d, d.item, k, this.version === 'b1.8.1');
    } else if (d.kind === 'block' && d.item) this.pushItem(d, d.item, 0, false);
    else if (d.kind === 'sprite' && d.item) this.pushItem(d, d.item, 0, true);
    else if (d.kind === 'orb') this.pushOrb(d);
    else if (d.kind === 'painting') this.pushPainting(d);
    else if (d.kind === 'frame') this.pushFrame(d);
    if (d.burning) this.pushFire(d);
  }

  private pushArmor(d: Drawn, e: ModelEntry): void {
    if (e.armor === undefined) {
      const built = buildArmor(e.built);
      e.armor = built ? new InstancedDraw(modelGeometry(built), this.mats.model, false, `armour ${e.built.def.id}`) : null;
      if (e.armor) this.group.add(e.armor.mesh);
    }
    if (!e.armor) return;
    const beta = this.version === 'b1.8.1';
    ARMOR_PIECES.forEach((piece, k) => {
      const m = d.armor![k];
      if (!m) return;
      const tex = this.rect(armorTexture(m, piece, beta));
      if (!tex) return;
      const i = this.base(d, 0, k + 1);
      i.tex = tex;
      // Undyed leather (the dye colour is not on the wire).
      if (/leather|cloth/.test(m)) tint(i, 0xa06540, d.bright);
      e.armor!.push(i);
    });
  }

  private pushItem(d: Drawn, l: ItemLook, slot: number, billboard: boolean): void {
    const p = this.bundle!.pack;
    const i = this.base(d, slot, 0);
    if (l.kind === 'cube') {
      i.tex = [0, 0, 1, 1];
      i.flags |= F_BLOCKS;
      i.faces = l.faces;
      i.tintMask = l.tint ? l.tintMask : 0;
      tint(i, l.tint || undefined, d.bright);
      this.cubes.push(i);
      return;
    }
    const [cols, rows] = l.atlas === 'items' ? this.itemsGrid(p) : this.blocksGrid(p);
    const cx = l.cell % cols, cy = Math.floor(l.cell / cols);
    const e = 0.02;
    i.tex = [(cx + e) / cols, (cy + e) / rows, (1 - 2 * e) / cols, (1 - 2 * e) / rows];
    i.flags |= (l.atlas === 'blocks' ? F_BLOCKS : 0) | (billboard ? F_BILLBOARD : 0);
    tint(i, l.tint || undefined, d.bright);
    this.sprites.push(i);
  }

  private pushPainting(d: Drawn): void {
    const i = this.base(d, 0, 0);
    i.tex = d.hang!.tex!;
    i.flags |= F_ENTITY;
    this.sprites.push(i);
  }

  private pushFrame(d: Drawn): void {
    const p = this.bundle!.pack;
    if (p.version !== '1.12.2') return;
    const cell = (n: string) => {
      const t = p.blocks.tiles[n];
      return t ? t[1] * p.blocks.columns + t[0] : 0;
    };
    const bg = cell('blocks/itemframe_background'), wood = cell('blocks/planks_birch');
    const i = this.base(d, 0, 0);
    i.tex = [0, 0, 1, 1];
    i.flags |= F_BLOCKS;
    // Faces: down, up, north (the back, against the wall), south (the front), west, east.
    i.faces = [wood, wood, wood, bg, wood, wood];
    this.cubes.push(i);
    if (d.item) this.pushItem(d, d.item, 1, false);
  }

  private pushFire(d: Drawn): void {
    const p = this.bundle!.pack;
    const i = this.base(d, FIRE_SLOT, 0);
    const t = p.version === '1.12.2' ? p.blocks.tiles['blocks/fire_layer_0'] : undefined;
    if (t && p.version === '1.12.2') {
      const cols = p.blocks.columns, rows = p.blocks.rows;
      i.tex = [t[0] / cols, t[1] / rows, 1 / cols, 1 / rows];
      i.flags = F_BLOCKS | F_BILLBOARD | F_EMISSIVE | F_FRONT;
    } else {
      // b1.8.1 draws fire procedurally (its terrain tile is a placeholder): so do we.
      i.tex = [0, 0, 1, 1];
      i.flags = F_FIRE | F_BILLBOARD | F_EMISSIVE | F_FRONT;
    }
    i.hurt = 0;
    i.flash = 0;
    this.sprites.push(i);
  }

  private pushOrb(d: Drawn): void {
    const key = this.version === 'b1.8.1' ? 'item/xporb' : 'entity/experience_orb';
    const r = this.rect(key);
    if (!r) return this.pushBox(d);
    const v = d.count, icon = v >= 2477 ? 10 : v >= 1237 ? 9 : v >= 617 ? 8 : v >= 307 ? 7 : v >= 149 ? 6 : v >= 73 ? 5 : v >= 37 ? 4 : v >= 17 ? 3 : v >= 7 ? 2 : v >= 3 ? 1 : 0;
    const i = this.base(d, 0, 0);
    i.tex = [r[0] + ((icon % 4) / 4) * r[2], r[1] + (Math.floor(icon / 4) / 4) * r[3], r[2] / 4, r[3] / 4];
    const t = d.age / 2;
    i.r = (Math.sin(t) + 1) * 0.5;
    i.g = 1;
    i.b = (Math.sin(t + 4.1887903) + 1) * 0.1;
    i.flags = F_ENTITY | F_BILLBOARD | F_EMISSIVE;
    this.sprites.push(i);
  }

  private pushParticles(dim: number): void {
    const i = this.inst;
    this.particles.list.forEach((p, k) => {
      if (p.dim !== dim) return;
      i.row = this.particleRow + Math.floor(k / PER_ROW);
      i.slot = k % PER_ROW;
      i.layer = 0;
      i.tex = [0, 0, 1, 1];
      i.r = p.r;
      i.g = p.g;
      i.b = p.b;
      i.flags = F_FLAT | F_BILLBOARD | F_EMISSIVE;
      i.hurt = 0;
      i.sky = 15;
      i.block = 15;
      i.flash = 0;
      i.faces = undefined;
      i.tintMask = 0;
      this.sprites.push(i);
    });
  }

  // ------------------------------------------------------------------------------------------------------------ labels

  private botLabel(b: BotView, selected: ReadonlySet<string>, timeMs: number): void {
    let o = this.botObjs.get(b.name);
    if (!o) {
      const ring = new THREE.Mesh(this.ringGeo, this.ringMat.clone());
      ring.renderOrder = 9;
      const root = new THREE.Group();
      root.userData = { name: b.name, short: b.dto.short };
      root.add(ring);
      this.group.add(root);
      this.botObjs.set(b.name, (o = { root, ring, label: null, labelText: '' }));
    }
    o.root.position.set(b.x, b.y, b.z);
    const sel = selected.has(b.name) || selected.has(b.dto.short);
    (o.ring.material as THREE.MeshBasicMaterial).opacity = 0.6 + 0.3 * Math.sin(timeMs / 250);
    const held = b.dto.held ? `holding ${b.dto.held}` : b.dto.job ? b.dto.job : null;
    const text = `${b.name}${b.dto.main ? ' · main' : ''}`;
    const lt = `${text}|${held ?? ''}|${sel}`;
    if (o.labelText !== lt) {
      if (o.label) {
        o.root.remove(o.label);
        this.labels.release(o.label);
      }
      o.label = this.labels.create(text, { fg: '#ffffff', bg: sel ? 'rgba(30,60,120,0.85)' : 'rgba(0,0,0,0.6)', sub: held, border: sel ? '#9ec5ff' : null }, true);
      o.label.position.set(0, 2.2, 0);
      o.root.add(o.label);
      o.labelText = lt;
    }
  }

  private entityLabel(d: Drawn, e: EntityView, dist: number, want: Set<string>): void {
    const near = e.kind === 'player' ? 64 : e.kind === 'item' ? 12 : 24;
    if (dist > near) return;
    want.add(e.key);
    const text = entityText(e);
    let l = this.entLabels.get(e.key);
    if (l && l.text !== text) {
      this.group.remove(l.sprite);
      this.labels.release(l.sprite);
      this.entLabels.delete(e.key);
      l = undefined;
    }
    if (!l) {
      const sprite = this.labels.create(text, { fg: e.kind === 'player' ? '#cfe0ff' : '#ffffff', bg: 'rgba(0,0,0,0.45)' }, e.kind === 'player', e.kind === 'item' ? 0.7 : 0.85);
      this.group.add(sprite);
      this.entLabels.set(e.key, (l = { sprite, text }));
    }
    const bob = e.kind === 'item' ? 0.35 : 0.25;
    l.sprite.position.set(d.x, d.y + (d.hang ? d.h / 2 : d.h) + bob, d.z);
  }

  // ------------------------------------------------------------------------------------------------------------ queries

  /** The bot under a ray (selection), nearest first; only bots visible in the last `showFor` (hitbox test). */
  botAt(ray: THREE.Raycaster): string | null {
    let best: string | null = null, bestD = Infinity;
    const box = new THREE.Box3(), hit = new THREE.Vector3();
    for (const d of this.visibleBots) {
      const w = Math.max(0.3, d.w / 2) + 0.1;
      box.min.set(d.x - w, d.y, d.z - w);
      box.max.set(d.x + w, d.y + d.h + 0.1, d.z + w);
      if (!ray.ray.intersectBox(box, hit)) continue;
      const dd = hit.distanceTo(ray.ray.origin);
      if (dd < bestD) {
        bestD = dd;
        best = d.key;
      }
    }
    return best;
  }

  /** Entities (and bots, display mobs) drawn in the last `showFor`. */
  get instances(): number {
    return this.drawnCount;
  }

  /** Live particles and lightning bolts (tests, stats). */
  get effects(): { particles: number; bolts: number } {
    return { particles: this.particles.list.length, bolts: this.lightning.active };
  }

  /** Draw calls the last `showFor` set up (meshes with instances). */
  get drawCalls(): number {
    let n = 0;
    this.group.traverse((o) => {
      if ((o as THREE.Mesh).isMesh && o.visible && o.parent?.visible !== false) n++;
    });
    return n;
  }

  /** Whether the last `showFor` drew `key` as a full model (true) or a LOD box (false); null = not drawn. */
  detailOf(key: string): boolean | null {
    return this.drawn.get(key)?.detail ?? null;
  }

  private dropBot(k: string, o: BotObj): void {
    this.group.remove(o.root);
    if (o.label) this.labels.release(o.label);
    (o.ring.material as THREE.Material).dispose();
    this.botObjs.delete(k);
  }

  dispose(): void {
    for (const [k, o] of this.botObjs) this.dropBot(k, o);
    for (const l of this.entLabels.values()) this.labels.release(l.sprite);
    this.entLabels.clear();
    for (const e of this.models.values()) {
      e.opaque.dispose();
      e.translucent?.dispose();
      e.armor?.dispose();
      e.geometry.dispose();
    }
    this.models.clear();
    this.sprites.dispose();
    this.cubes.dispose();
    this.shadowDraw.dispose();
    this.lightning.dispose();
    for (const g of [this.spriteGeo, this.cubeGeo, this.ringGeo]) g.dispose();
    this.ringMat.dispose();
    this.mats.dispose();
    this.boneTex?.dispose();
    for (const t of this.textures) t.dispose();
    this.labels.dispose();
  }
}

/** Held item placement in arm space (pixels, y down the arm, −z forward): the fist position, the item's axes there (right,
 * up, normal: a right-handed basis), its size (pixels per item unit) and a pre-translation in item units (grip / centre).
 * Tools and flat items stick out forward and a little up from the fist, their plane along the body's side (vanilla's
 * third-person pose, approximated); blocks sit in the fist turned 45°. The left hand mirrors these across x. */
const UP_F: [number, number, number] = [0, -0.33, -0.944];
export const HELD_POSES: Record<'block' | 'tool' | 'flat', { at: [number, number, number]; right: [number, number, number]; up: [number, number, number]; normal: [number, number, number]; size: number; pre: [number, number, number] }> = {
  block: { at: [-1, 10.5, -2.5], right: [Math.SQRT1_2, 0, Math.SQRT1_2], up: [0, -1, 0], normal: [Math.SQRT1_2, 0, -Math.SQRT1_2], size: 6, pre: [0, -0.5, 0] },
  tool: { at: [-1, 9.5, -1], right: [0, -0.944, 0.33], up: UP_F, normal: [1, 0, 0], size: 10, pre: [0.35, -0.15, 0] },
  flat: { at: [-1, 9.5, -1.5], right: [0, -0.944, 0.33], up: UP_F, normal: [1, 0, 0], size: 7, pre: [0, -0.1, 0] },
};

/** RenderEntityItem copies by stack size (b1.8.1: 1, 2 (>1), 3 (>5), 4 (>20); 1.12.2: up to 5 at >48). */
export function copies(count: number, beta: boolean): number {
  if (beta) return count > 20 ? 4 : count > 5 ? 3 : count > 1 ? 2 : 1;
  return count > 48 ? 5 : count > 32 ? 4 : count > 16 ? 3 : count > 1 ? 2 : 1;
}

/** An object kind from the wire's type name ("FallingSand", "minecraft:falling_block", "MinecartChest", "PrimedTnt"). */
function objectKind(name: string): string {
  const n = canonical(name);
  return ({ falling_sand: 'falling_block', primed_tnt: 'tnt', tnt_primed: 'tnt', xp_orb: 'xp_orb', experience_orb: 'xp_orb', fireworks_rocket: 'firework', fireworks_rocket_entity: 'firework', minecart_chest: 'chest_minecart', minecart_furnace: 'furnace_minecart', minecart_tnt: 'tnt_minecart', minecart_hopper: 'hopper_minecart', minecart_spawner: 'spawner_minecart', minecart_command_block: 'command_block_minecart', minecart_rideable: 'minecart', ender_crystal: 'ender_crystal', thrown_egg: 'egg', snowball: 'snowball', item_frame: 'item_frame', painting: 'painting', armor_stand: 'armor_stand' } as Record<string, string>)[n] ?? n;
}

/** An older optional DTO form: `armor: [helmet, chestplate, leggings, boots]` material names. */
function armorOf(dto: object): ReadonlyArray<string | null> | null {
  const a = (dto as { armor?: unknown }).armor;
  return Array.isArray(a) && a.some((m) => typeof m === 'string') ? (a as Array<string | null>) : null;
}

function tint(i: InstanceData, rgb: number | undefined, k: number): void {
  if (rgb == null) return;
  i.r = (((rgb >> 16) & 255) / 255) * k;
  i.g = (((rgb >> 8) & 255) / 255) * k;
  i.b = ((rgb & 255) / 255) * k;
}

/** out = translate(x, y, z) · rotY(ry) · rotZ(rz) · scale(sx, sy, sz) · translate(px, py, pz). */
export function compose(out: Affine, x: number, y: number, z: number, ry: number, rz: number, sx: number, sy: number, sz: number, px: number, py: number, pz: number): Affine {
  const cy = Math.cos(ry), sy_ = Math.sin(ry), cz = Math.cos(rz), sz_ = Math.sin(rz);
  // R = Ry · Rz
  const r00 = cy * cz, r01 = -cy * sz_, r02 = sy_;
  const r10 = sz_, r11 = cz, r12 = 0;
  const r20 = -sy_ * cz, r21 = sy_ * sz_, r22 = cy;
  out[0] = r00 * sx;
  out[1] = r01 * sy;
  out[2] = r02 * sz;
  out[4] = r10 * sx;
  out[5] = r11 * sy;
  out[6] = r12 * sz;
  out[8] = r20 * sx;
  out[9] = r21 * sy;
  out[10] = r22 * sz;
  out[3] = x + out[0] * px + out[1] * py + out[2] * pz;
  out[7] = y + out[4] * px + out[5] * py + out[6] * pz;
  out[11] = z + out[8] * px + out[9] * py + out[10] * pz;
  return out;
}
