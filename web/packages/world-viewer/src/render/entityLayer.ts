// Bots, players, mobs, drops and objects at the EntityStore's interpolated positions, shared by every view of the runtime.
//
// Once per frame (`update`) every entity is animated (limb swing from how far it moved, body yaw toward the walk direction,
// head yaw/pitch, idle sway, arm swings, hurt flash, death fall) and its posed part matrices are written into one row of a
// float "bone" texture. Each view (`showFor`) then only chooses instances: one instanced draw per model kind present (base
// skin and its layers — eyes, wool, saddle, overlays — are instances of the same geometry), one for item sprites, one for
// item/block cubes, one for shadows, one for far LOD boxes. Nothing is rebuilt per view; ~20 mobs + bots cost a handful of
// draw calls. Models are our own data (render/entities/models), textured from the bundled entity sheet.
//
// Not on the wire today (render paths exist, data does not): equipment/armour, mob health and hurt events (only bots' hp
// drops flash), arm swings (hook: `swing(key)`), deaths of mobs (hook: `kill(key)`), objects / XP orbs (the supervisor sends
// players, mobs and drops only), head yaw separate from body yaw for mobs, skins, horse/villager variants (picked by id).

import * as THREE from 'three';
import type { AssetBundle, Pack } from '../assets/bundle';
import type { BotView, EntityView } from '../world/entities';
import type { GameVersion } from '../world/blocks';
import { ARMOR_PIECES, armorTexture, buildArmor } from './entities/armor';
import { BOX_SLOT, buildModel, type BuiltModel } from './entities/builder';
import { isHandheld, itemLookById, itemLookByName, parseHeld, type ItemLook } from './entities/items';
import { createEntityMaterials, defaultWorldUniforms, sheetTexture, type EntityMaterials, type WorldUniforms } from './entities/material';
import { cubeGeometry, InstancedDraw, modelGeometry, ShadowDraw, spriteGeometry, type InstanceData } from './entities/meshes';
import { attackProgress, deathAngle, hurtAmount, newMotion, stepMotion, wrapDeg, type Motion } from './entities/motion';
import { identity, mul, partLocal, rootMatrix, ROW_FLOATS, Skeleton, type Affine } from './entities/pose';
import { MOB_NAMES, MODEL_BY_ID, mobLook, objectLook, objectName, playerLook } from './entities/registry';
import type { Look, PoseCtx } from './entities/types';
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
  const look = mobLook('1.12.2', type, null, 0, 0);
  const def = MODEL_BY_ID[look.model];
  const s = look.scale ?? 1;
  return [n ? n.replace(/_/g, ' ') : 'mob', look.colour, (def?.width ?? 0.6) * s, (def?.height ?? 1.8) * s];
}

/** Box size and colour of a non-bot entity (the LOD box; also the label height). */
export function entityLook(e: EntityView, version: GameVersion = '1.12.2'): { colour: number; w: number; h: number } {
  if (e.kind === 'player') return { colour: 0x4a7ad8, w: 0.6, h: 1.8 };
  if (e.kind === 'item') return { colour: 0xd8b040, w: 0.25, h: 0.25 };
  if (e.kind === 'mob') {
    const look = mobLook(version, e.type, e.name, e.dto.variant ?? 0, e.id);
    const def = MODEL_BY_ID[look.model];
    const s = look.scale ?? 1;
    return { colour: look.colour, w: (def?.width ?? 0.6) * s, h: (def?.height ?? 1.8) * s };
  }
  return { colour: 0x9a9a9a, w: 0.5, h: 0.5 };
}

export function entityText(e: EntityView): string {
  if (e.kind === 'item') return `${e.name ?? `item ${e.item}`}${e.count > 1 ? ` x${e.count}` : ''}`;
  if (e.kind === 'player') return e.name ?? 'player';
  if (e.kind === 'mob') return (MOB_NAMES[e.type] ?? e.name ?? 'mob').replace(/_/g, ' ');
  return e.name ?? e.kind;
}

// ------------------------------------------------------------------------------------------------------------ internals

type DrawKind = 'model' | 'item' | 'block' | 'orb' | 'box';

/** What one entity (or bot) draws; kept across frames, refreshed by `update`. */
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
  w: number;
  h: number;
  colour: number;
  row: number;
  /** Bones written this frame (else only the box slot is valid). */
  posed: boolean;
  /** The last `showFor` drew it as a model (else as a LOD box, or not at all: null). */
  detail: boolean | null;
  item: ItemLook | null;
  count: number;
  held: ItemLook | null;
  heldTool: boolean;
  /** Worn armour materials [helmet, chestplate, leggings, boots] (not on the wire today; read from an optional DTO `armor`). */
  armor: ReadonlyArray<string | null> | null;
  burning: boolean;
  flash: number;
  hurt: number;
  sky: number;
  block: number;
  bright: number;
  age: number;
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

/** Fire billboard and held item slots in an entity's bone row (models use fewer than 60 parts). */
const HELD_SLOT = 62;
const FIRE_SLOT = 61;
const F_EMISSIVE = 1, F_FLAT = 2, F_BLOCKS = 4, F_ENTITY = 8, F_BILLBOARD = 16, F_FIRE = 32, F_FRONT = 64;

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
  /** Optional per-entity light: [sky 0..15, block 0..15] at a world position (null = open sky). The runtime may provide it
   * once light is queryable on the main thread; until then entities take full sky light minus the time of day. */
  lightAt: ((dim: number, x: number, y: number, z: number) => readonly [number, number] | null) | null = null;
  /** Blob shadows under entities. */
  shadows = true;
  private readonly labels = new LabelFactory();
  private readonly botObjs = new Map<string, BotObj>();
  private readonly entLabels = new Map<string, Label>();
  private readonly drawn = new Map<string, Drawn>();
  private list: Drawn[] = [];
  private readonly world: WorldUniforms;
  private readonly mats: EntityMaterials;
  private readonly models = new Map<string, ModelEntry>();
  private readonly boxModel: ModelEntry;
  private readonly sprites: InstancedDraw;
  private readonly cubes: InstancedDraw;
  private readonly shadowDraw: ShadowDraw;
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
  private lastCams: Array<{ p: THREE.Vector3; k: number }> = [];
  private nextCams: Array<{ p: THREE.Vector3; k: number }> = [];
  private visibleBots: Drawn[] = [];
  private drawnCount = 0;
  private heldCache = new Map<string, ItemLook | null>();
  private readonly root = new Float32Array(12);
  private readonly tmpA = new Float32Array(12);
  private readonly tmpB = new Float32Array(12);
  private readonly inst: InstanceData = { row: 0, slot: 0, layer: 0, tex: [0, 0, 1, 1], r: 1, g: 1, b: 1, flags: 0, hurt: 0, sky: 15, block: 0, flash: 0 };
  private readonly ctx: PoseCtx = { swing: 0, amount: 0, age: 0, headYaw: 0, pitch: 0, attack: 0, variant: 0, flags: 0, sneaking: false, riding: false, holding: false, aggressive: false, beta: false, seed: 0, speed: 0 };

  constructor(world?: WorldUniforms) {
    this.group.name = 'entities';
    this.world = world ?? defaultWorldUniforms();
    this.mats = createEntityMaterials(this.world);
    this.boxModel = this.entry(MODEL_BY_ID.box);
    this.sprites = new InstancedDraw(this.spriteGeo, this.mats.tile, true, 'item sprites');
    this.cubes = new InstancedDraw(this.cubeGeo, this.mats.tile, true, 'item cubes');
    this.shadowDraw = new ShadowDraw(this.mats.shadow);
    this.group.add(this.sprites.mesh, this.cubes.mesh, this.shadowDraw.mesh);
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
    return pack.version === '1.12.2' ? [pack.items.columns, pack.items.rows] : [pack.items.columns, pack.items.rows];
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

  /** Swings the right arm of a bot (name) or entity (key "dim:id"). */
  swing(key: string): void {
    const m = this.motionOf(key);
    if (m) m.attackAt = performance.now();
  }

  /** Red hurt flash (vanilla hurtTime) on a bot or entity. Bots flash by themselves when their hp drops. */
  hurt(key: string): void {
    const m = this.motionOf(key);
    if (m) m.hurtAt = performance.now();
  }

  /** Starts the death fall (it lasts while the entity is still reported). Bots fall by themselves at hp 0. */
  kill(key: string): void {
    const m = this.motionOf(key);
    if (m && !m.deadAt) m.deadAt = performance.now();
  }

  // ------------------------------------------------------------------------------------------------------------ frame

  /** Once per frame: every bot and entity (every dimension) at its interpolated position. */
  update(bots: BotView[], ents: EntityView[], timeMs: number, selected: ReadonlySet<string>): void {
    const frame = ++this.frame;
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
    for (const [k, d] of this.drawn) if (d.frame !== frame) this.drawn.delete(k);
    for (const [k, o] of this.botObjs) if (!this.drawn.get(k)?.bot) this.dropBot(k, o);
    // Bones: one row per entity.
    this.ensureRows(list.length + 1);
    for (let i = 0; i < list.length; i++) {
      const d = list[i];
      d.row = i;
      this.writeRow(d, timeMs);
      d.bright = selected.has(d.key) || (d.botView && selected.has(d.botView.dto.short)) ? 1.25 : 1;
    }
    if (this.boneTex) this.boneTex.needsUpdate = true;
  }

  private drawnFor(key: string, bot: boolean, x: number, y: number, z: number, yaw: number, now: number): Drawn {
    let d = this.drawn.get(key);
    if (!d) {
      d = {
        key, bot, dim: 0, x, y, z, kind: 'box', sig: '', look: null, model: null, w: 0.6, h: 1.8, colour: 0x9a9a9a, row: 0, posed: false, detail: null, item: null, count: 1,
        held: null, heldTool: false, armor: null, burning: false, flash: 0, hurt: 0, sky: 15, block: 0, bright: 1, age: 0, motion: newMotion(x, y, z, yaw, key, now), frame: 0,
        ent: null, botView: null,
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
    const s = look?.scale ?? 1;
    d.w = (def?.width ?? 0.6) * s;
    d.h = (def?.height ?? 1.8) * s;
    d.colour = look?.colour ?? 0x9a9a9a;
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
    if (hp > 0 && m.deadAt) m.deadAt = 0;
    m.hp = hp;
    const held = b.dto.held ?? null;
    d.held = held ? this.heldLook(held) : null;
    d.heldTool = !!held && isHandheld(parseHeld(held).name);
    d.burning = false;
    d.flash = 0;
    d.armor = armorOf(b.dto);
    stepMotion(m, b.x, b.y, b.z, b.yaw, now);
  }

  private heldLook(held: string): ItemLook | null {
    const k = `${this.version}|${held}`;
    if (this.heldCache.has(k)) return this.heldCache.get(k)!;
    const l = this.bundle ? itemLookByName(this.version, this.bundle.pack, held) : null;
    if (this.bundle) this.heldCache.set(k, l);
    return l;
  }

  private refreshEntity(d: Drawn, e: EntityView, now: number): void {
    const variant = e.dto.variant ?? 0;
    const sig = `${this.version}|${!!this.bundle}|${e.kind}|${e.type}|${variant}|${e.item}|${e.dto.damage ?? 0}|${e.name ?? ''}`;
    const beta = this.version === 'b1.8.1';
    if (d.sig !== sig) {
      d.sig = sig;
      d.item = null;
      d.held = null;
      if (e.kind === 'player') this.setLook(d, playerLook(this.version, e.name ?? 'player', false));
      else if (e.kind === 'mob') this.setLook(d, mobLook(this.version, e.type, e.name, variant, e.id));
      else if (e.kind === 'item') {
        this.setLook(d, null);
        d.item = this.bundle ? itemLookById(this.version, this.bundle.pack, e.item, e.dto.damage ?? 0) : null;
        d.kind = d.item ? 'item' : 'box';
        d.w = d.h = 0.25;
        d.colour = 0xd8b040;
      } else if (e.kind === 'xp_orb' || e.kind === 'xporb' || e.kind === 'orb') {
        this.setLook(d, null);
        d.kind = this.bundle ? 'orb' : 'box';
        d.w = d.h = 0.25;
        d.colour = 0x9ae040;
      } else {
        // Objects (kind "object", type = Spawn Object type; or a kind naming the object).
        const name = e.kind === 'object' ? (objectName(e.type, beta) ?? e.name ?? '') : e.kind;
        const look = objectLook(this.version, name, variant, e.id);
        this.setLook(d, look);
        if (!look && (name === 'falling_block' || name === 'tnt' || name === 'falling_sand')) {
          const block = name === 'tnt' ? 46 : e.item || (beta && e.type === 71 ? 13 : 12);
          const l = this.bundle ? itemLookById(this.version, this.bundle.pack, block, e.dto.damage ?? 0) : null;
          d.item = l?.kind === 'cube' ? l : null;
          d.kind = d.item ? 'block' : 'box';
          d.w = d.h = 0.98;
          d.colour = name === 'tnt' ? 0xd04030 : 0xd8c890;
        } else if (!look) {
          d.w = d.h = 0.5;
          d.colour = 0x9a9a9a;
        }
      }
      if (e.kind === 'player' && e.item > 0 && this.bundle) d.held = itemLookById(this.version, this.bundle.pack, e.item, 0);
    }
    d.count = e.count;
    d.age = e.ageTicks;
    d.armor = armorOf(e.dto);
    d.burning = (e.flags & 1) !== 0;
    // Creeper fusing (Mob16 state 1) / primed TNT: white flashes.
    const fuse = e.kind === 'mob' && e.type === 50 && variant === 1;
    const tnt = d.kind === 'block' && d.colour === 0xd04030;
    d.flash = fuse ? (Math.sin(now / 60) > 0 ? 0.8 : 0) : tnt ? (Math.floor(e.ageTicks / 5) % 2 ? 0.8 : 0) : 0;
    stepMotion(d.motion, e.x, e.y, e.z, e.yaw, now, (e.flags & 8) !== 0);
  }

  /** Writes the entity's bone row: the LOD box always; the posed parts when some view's camera was near last frame. */
  private writeRow(d: Drawn, now: number): void {
    const off = d.row * ROW_FLOATS;
    const m = d.motion;
    const yaw = d.kind === 'model' ? m.body : d.ent?.yaw ?? 0;
    // LOD box: translate · rotY · scale(w, h, w).
    const a = (yaw * Math.PI) / 180;
    const b = this.bones;
    const bo = off + BOX_SLOT * 12;
    const c = Math.cos(a), s = Math.sin(a);
    b[bo] = c * d.w;
    b[bo + 1] = 0;
    b[bo + 2] = s * d.w;
    b[bo + 3] = d.x;
    b[bo + 4] = 0;
    b[bo + 5] = d.h;
    b[bo + 6] = 0;
    b[bo + 7] = d.y;
    b[bo + 8] = -s * d.w;
    b[bo + 9] = 0;
    b[bo + 10] = c * d.w;
    b[bo + 11] = d.z;
    const light = this.lightAt?.(d.dim, d.x, d.y + d.h * 0.5, d.z) ?? null;
    d.sky = light ? light[0] : 15;
    d.block = light ? light[1] : 0;
    d.hurt = hurtAmount(m, now);
    d.posed = false;
    if (!this.near(d)) return;
    if (d.kind === 'model' && d.model && d.look) this.poseModel(d, now, off);
    else if (d.kind === 'item') this.poseDrop(d, off);
    else if (d.kind === 'block') this.poseBlock(d, off);
    else if (d.kind === 'orb') this.poseOrb(d, off);
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
    const s0 = def.scale ?? 1, k = look.scale ?? 1;
    const sc: [number, number, number] = typeof s0 === 'number' ? [s0 * k, s0 * k, s0 * k] : [s0[0] * k, s0[1] * k, s0[2] * k];
    const flags = d.ent?.flags ?? 0;
    const sneaking = (flags & 2) !== 0;
    const isPlayer = d.bot || d.ent?.kind === 'player';
    rootMatrix(this.root, {
      x: d.x,
      y: d.y,
      z: d.z,
      yaw: m.body,
      death: deathAngle(m, now),
      scale: sc,
      offset: def.offset ?? [0, 0, 0],
      lift: sneaking && isPlayer ? -0.125 : 0,
    });
    const c = this.ctx;
    const lookYaw = d.botView ? d.botView.yaw : d.ent?.yaw ?? m.body;
    c.swing = m.swing;
    c.amount = m.deadAt ? 0 : m.amount;
    c.age = (now / 50) + m.seed * 1000;
    c.headYaw = Math.max(-75, Math.min(75, wrapDeg(lookYaw - m.body)));
    c.pitch = Math.max(-90, Math.min(90, d.botView?.dto.pitch ?? 0));
    c.attack = attackProgress(m, now);
    c.variant = d.ent?.dto.variant ?? 0;
    c.flags = flags;
    c.sneaking = sneaking;
    c.riding = (flags & 4) !== 0;
    c.holding = !!d.held;
    c.aggressive = false;
    c.beta = this.version === 'b1.8.1';
    c.seed = m.seed;
    c.speed = m.speed;
    e.skel.pose(c, this.root, this.bones, off);
    if (d.held) this.poseHeld(d, e, off);
    if (d.burning) this.poseFire(d, off);
  }

  /** The held item in the right hand: arm matrix · hand offset (pixels, arm space) · item orientation · size. */
  private poseHeld(d: Drawn, e: ModelEntry, off: number): void {
    const arm = e.built.index.rightArm;
    if (arm == null) return;
    const am = e.skel.partMatrix(arm);
    const l = this.tmpA, out = this.tmpB;
    // Arm space: pixels, y down the arm, −z forward; the fist is around (−1, 10, 0). Item geometry is y-up (0..1).
    const h = HELD_POSES[d.held!.kind === 'cube' ? 'block' : d.heldTool ? 'tool' : 'flat'];
    // Columns: where the item's x (right), y (up) and z (normal) axes go in arm space, times its size.
    for (let r = 0; r < 3; r++) {
      l[r * 4] = h.right[r] * h.size;
      l[r * 4 + 1] = h.up[r] * h.size;
      l[r * 4 + 2] = h.normal[r] * h.size;
      l[r * 4 + 3] = h.at[r];
    }
    // Pre-translate in item units (centre the cube / put the grip at the fist).
    l[3] += l[0] * h.pre[0] + l[1] * h.pre[1] + l[2] * h.pre[2];
    l[7] += l[4] * h.pre[0] + l[5] * h.pre[1] + l[6] * h.pre[2];
    l[11] += l[8] * h.pre[0] + l[9] * h.pre[1] + l[10] * h.pre[2];
    mul(out, am, l);
    this.bones.set(out, off + HELD_SLOT * 12);
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
    const rnd = () => ((seed = (Math.imul(seed, 1103515245) + 12345) | 0) >>> 0) / 4294967296 * 2 - 1;
    for (let k = 0; k < n; k++) {
      const j = k === 0 ? [0, 0, 0] : [rnd() * 0.15, rnd() * 0.15, rnd() * 0.15];
      const m = this.tmpA;
      const c = Math.cos(spin), s = Math.sin(spin);
      m[0] = c * size;
      m[1] = 0;
      m[2] = s * size;
      m[3] = d.x + j[0];
      m[4] = 0;
      m[5] = size;
      m[6] = 0;
      m[7] = d.y + bob + (cube ? 0 : 0.02) + j[1];
      m[8] = -s * size;
      m[9] = 0;
      m[10] = c * size;
      m[11] = d.z + j[2];
      this.bones.set(m, off + k * 12);
    }
  }

  private poseBlock(d: Drawn, off: number): void {
    const m = identity(this.tmpA);
    const grow = d.colour === 0xd04030 ? 1 + Math.max(0, Math.sin(d.age / 40)) * 0.02 : 1;
    m[0] = m[5] = m[10] = 0.98 * grow;
    m[3] = d.x;
    m[7] = d.y;
    m[11] = d.z;
    this.bones.set(m, off);
  }

  private poseOrb(d: Drawn, off: number): void {
    const m = identity(this.tmpA);
    m[0] = m[5] = m[10] = 0.3;
    m[3] = d.x;
    m[7] = d.y + 0.1;
    m[11] = d.z;
    this.bones.set(m, off);
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
      const real = Math.hypot(d.x - cam.x, d.y - cam.y, d.z - cam.z);
      if (real > far) continue;
      const dist = real * zoom;
      if (d.bot) this.visibleBots.push(d);
      n++;
      d.detail = !(dist > detail || !d.posed || !this.bundle);
      if (d.detail) this.pushDetail(d, dist);
      else this.pushBox(d);
      if (!d.bot && opts.labels && d.ent) this.entityLabel(d, d.ent, dist, want);
    }
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
    i.flags = 0;
    i.hurt = d.hurt;
    i.sky = d.sky;
    i.block = d.block;
    i.flash = d.flash;
    i.faces = undefined;
    i.tintMask = 0;
    return i;
  }

  private pushBox(d: Drawn): void {
    const i = this.base(d, BOX_SLOT, 0);
    i.r = ((d.colour >> 16) & 255) / 255 * d.bright;
    i.g = ((d.colour >> 8) & 255) / 255 * d.bright;
    i.b = (d.colour & 255) / 255 * d.bright;
    i.flags = F_FLAT;
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
    if (this.shadows && dist < this.detailDistance) {
      const r = d.kind === 'item' ? 0.15 : Math.min(3, Math.max(0.25, d.w * 0.5 + 0.1));
      this.shadowDraw.push(d.x, d.y, d.z, r, d.motion.deadAt ? 0.2 : 0.45);
    }
    if (d.kind === 'model') {
      const e = d.model!, look = d.look!;
      const tex = this.rect(look.texture);
      if (!tex) return this.pushBox(d);
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
        (l.translucent ? this.translucentOf(e) : e.opaque).push(li);
      }
      if (d.armor) this.pushArmor(d, e);
      if (d.held) this.pushItem(d, d.held, HELD_SLOT, 1, false);
      if (d.burning) this.pushFire(d);
      return;
    }
    if (d.kind === 'item' && d.item) {
      const n = copies(d.count, this.version === 'b1.8.1');
      for (let k = 0; k < n; k++) this.pushItem(d, d.item, k, 1, this.version === 'b1.8.1');
    } else if (d.kind === 'block' && d.item) this.pushItem(d, d.item, 0, 1, false);
    else if (d.kind === 'orb') this.pushOrb(d);
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
      // Undyed leather (the dye colour is not on the wire either).
      if (/leather|cloth/.test(m)) tint(i, 0xa06540, d.bright);
      e.armor!.push(i);
    });
  }

  private pushItem(d: Drawn, l: ItemLook, slot: number, _k: number, billboard: boolean): void {
    const p = this.bundle!.pack;
    const i = this.base(d, slot, 0);
    if (l.kind === 'cube') {
      i.tex = [0, 0, 1, 1];
      i.flags = F_BLOCKS;
      i.faces = l.faces;
      i.tintMask = l.tintMask;
      tint(i, l.tint || undefined, d.bright);
      // Cubes tint only the masked faces; the instance colour is the tint.
      if (!l.tint) i.tintMask = 0;
      this.cubes.push(i);
      return;
    }
    const [cols, rows] = l.atlas === 'items' ? this.itemsGrid(p) : this.blocksGrid(p);
    const cx = l.cell % cols, cy = Math.floor(l.cell / cols);
    const e = 0.02;
    i.tex = [(cx + e) / cols, (cy + e) / rows, (1 - 2 * e) / cols, (1 - 2 * e) / rows];
    i.flags = (l.atlas === 'blocks' ? F_BLOCKS : 0) | (billboard ? F_BILLBOARD : 0);
    tint(i, l.tint || undefined, d.bright);
    this.sprites.push(i);
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
    l.sprite.position.set(d.x, d.y + d.h + bob, d.z);
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

  /** Entities (and bots) drawn in the last `showFor`. */
  get instances(): number {
    return this.drawnCount;
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
 * third-person pose, approximated); blocks sit in the fist turned 45°. */
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

/** Optional armour materials on a DTO (`armor: [helmet, chestplate, leggings, boots]`), absent from the wire today. */
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

function scale(m: Affine, k: number): void {
  for (let r = 0; r < 3; r++) for (let c = 0; c < 3; c++) m[r * 4 + c] *= k;
}
