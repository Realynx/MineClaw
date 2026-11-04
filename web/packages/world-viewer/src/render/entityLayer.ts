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
//
// Riders are extra rows a body owns and poses itself: the parrots on a player's shoulders (LayerEntityOnShoulder) and the
// arrows stuck in it (LayerArrow). Glinting items and armour are flagged for their draw's glint twin (one more draw call per
// geometry that has any).

import * as THREE from 'three';
import type { AssetBundle, Pack } from '../assets/bundle';
import { FLAG_GLOWING, FLAG_INVISIBLE, FLAG_ON_FIRE, FLAG_SNEAKING, FLAG_SPRINTING, type EntityDto, type EntityEvent, type EquipRef, type EquipSlot, type Equipment, type ItemRef } from '../types';
import { stuckArrows, type ArrowSpot } from './entities/arrows';
import type { GameVersion } from '../world/blocks';
import type { BotView, EntityView } from '../world/entities';
import { ARMOR_PIECES, armorDyes, armorFromEquipment, armorMaterial, armorPasses, buildArmor, buildElytra, buildSkull, SKULL_TEXTURES, skullKind } from './entities/armor';
import { BOX_SLOT, buildModel, type BuiltModel } from './entities/builder';
import { Lightning, Particles } from './entities/effects';
import { facingYaw, minecartBlock, motive, paintingSheet } from './entities/hanging';
import { isHandheld, itemLookById, itemLookByName, itemName, parseHeld, type ItemLook } from './entities/items';
import { createEntityMaterials, GHOST_FLAG, GHOST_LEVEL, GHOST_PLAYER, GHOST_SELECTED, GLINT_FLAG, defaultWorldUniforms, glintTexture, sheetTexture, type EntityMaterials, type WorldUniforms } from './entities/material';
import { cubeGeometry, InstancedDraw, modelGeometry, ShadowDraw, spriteGeometry, type InstanceData } from './entities/meshes';
import { attackOffProgress, attackProgress, DEATH_GONE_MS, DEATH_SHRINK_MS, deathAngle, deathScale, eatProgress, hurtAmount, newMotion, stepMotion, wrapDeg, type Motion } from './entities/motion';
import { identity, invert, mul, partLocal, rootMatrix, ROW_FLOATS, Skeleton, type Affine, type RootParams } from './entities/pose';
import { canonical, MOB_NAMES, MODEL_BY_ID, mobLook, mobName, objectLook, objectName, playerLook } from './entities/registry';
import type { Look, ModelDef, PoseCtx } from './entities/types';
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
  if (e.kind === 'mob') return mobBox(e, version);
  return { colour: 0x9a9a9a, w: 0.5, h: 0.5 };
}

/** A mob's look colour and hitbox (babies half size). */
function mobBox(e: EntityView, version: GameVersion): { colour: number; w: number; h: number } {
  const look = mobLook(version, e.type, e.name, e.dto.variant, e.id);
  const def = MODEL_BY_ID[look.model];
  const s = (look.scale ?? 1) * (e.dto.baby ? 0.5 : 1);
  return { colour: look.colour, w: (def?.width ?? 0.6) * s, h: (def?.height ?? 1.8) * s };
}

/** The shoulder parrots part of a rider signature: "left|right" colours, empty where none. */
function shoulderSig(shoulders: EntityDto['shoulders']): string {
  return `${shoulders?.left ?? ''}|${shoulders?.right ?? ''}`;
}

/** Selected bodies (by key, or a bot by its short key) draw brighter. */
function selectedBrightness(d: { key: string; botView: BotView | null }, selected: ReadonlySet<string>): number {
  return selected.has(d.key) || (d.botView && selected.has(d.botView.dto.short)) ? 1.25 : 1;
}

/** entityText per entity kind (other kinds: the name, else the kind). */
const KIND_TEXT = new Map<string, (e: EntityView) => string>([
  ['item', (e) => `${e.name ?? `item ${e.item}`}${e.count > 1 ? ` x${e.count}` : ''}`],
  ['player', (e) => e.name ?? 'player'],
  ['mob', (e) => `${e.dto.baby ? 'baby ' : ''}${(MOB_NAMES[e.type] ?? e.name ?? 'mob').replace(/_/g, ' ')}`],
  ['painting', (e) => (e.dto.motive ? `painting · ${e.dto.motive}` : 'painting')],
  ['item_frame', (e) => {
    const n = e.dto.frameItem ? itemName(e.dto.frameItem[0]) : null;
    return n ? `item frame · ${n.replace(/_/g, ' ')}` : 'item frame';
  }],
]);

export function entityText(e: EntityView): string {
  const text = KIND_TEXT.get(e.kind);
  return text ? text(e) : (e.name ?? e.kind.replace(/_/g, ' '));
}

// ------------------------------------------------------------------------------------------------------------ internals

type DrawKind = 'model' | 'item' | 'block' | 'orb' | 'sprite' | 'painting' | 'frame' | 'box';

/** Most far (box) entities one view draws; the rest are skipped (bots never are). */
export const MAX_FAR_BOXES = 1500;

/** A mob shown inside a block (spawner cages), driven by the block-entity layer. */
interface DisplayMob {
  dim: number;
  x: number;
  y: number;
  z: number;
  mob: string;
  scale: number;
  spin: number;
  /** Degrees about x after the spin (TileEntityMobSpawnerRenderer: −30, leaning back). */
  tilt: number;
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
  /** A flat item worn on the head (1.12.2 LayerCustomHead draws any head item that is not a helmet). */
  headItem: ItemLook | null;
  /** A skull worn on the head: its type (item meta 0 skeleton, 1 wither, 2 zombie, 3 player, 4 creeper, 5 dragon); −1 none. */
  skull: number;
  /** Elytra worn in the chest slot (LayerElytra). */
  elytra: boolean;
  /** When a creeper began fusing (its swell and flash run from it), 0 when it is not. */
  fuseAt: number;
  /** A minecart's content block id (0 none). */
  cartBlock: number;
  /** Worn armour materials [helmet, chestplate, leggings, boots]. */
  armor: ReadonlyArray<string | null> | null;
  /** Their dye colours (1.12.2 leather; null = undyed). */
  armorDye: ReadonlyArray<number | null> | null;
  /** The renderer draws head items (LayerCustomHead) / armour (LayerBipedArmor, LayerVillagerArmor) / elytra (LayerElytra). */
  headLayer: boolean;
  armorLayer: boolean;
  /** Head items sit a little higher on villager heads (LayerCustomHead: skulls 1.1875 px, blocks 1.875 px). */
  villagerHead: boolean;
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
  /** Glint (enchanted / effect items): armour pieces [helmet, chestplate, leggings, boots], hands, head item, elytra, and the
   * drop's or framed item. */
  armorGlint: ReadonlyArray<boolean> | null;
  heldGlint: boolean;
  heldOffGlint: boolean;
  headGlint: boolean;
  elytraGlint: boolean;
  itemGlint: boolean;
  /** An enderman's carried block (LayerHeldBlock); drawn as a cube, or as crossed planes for a plant. */
  carried: ItemLook | null;
  /** When the wire first said an iron golem holds out its flower (0 = it does not). */
  roseAt: number;
  /** A rider (shoulder parrot, stuck arrow): the body that owns and poses it; null for everything else. */
  parent: Drawn | null;
  /** Its riders, in the order they are pushed after it; null = none. */
  riders: Drawn[] | null;
  /** The riders' input last resolved (shoulders, arrow count, model). */
  ridersSig: string;
  /** A rider: a shoulder parrot (left/right) or a stuck arrow (its spot). */
  ride: { kind: 'shoulder'; left: boolean } | { kind: 'arrow'; spot: ArrowSpot } | null;
  /** A dropped item or an experience orb (the viewer settings' "drops" toggle). */
  drop: boolean;
  motion: Motion;
  frame: number;
  ent: EntityView | null;
  botView: BotView | null;
}

/** A body's hitbox (the look's model size × its scale, babies half) and box colour; no look: the default box. */
function setHitbox(d: Drawn, look: Look | null, def: ModelDef | undefined): void {
  const s = (look?.scale ?? 1) * (d.baby ? 0.5 : 1);
  d.w = (def?.width ?? 0.6) * s;
  d.h = (def?.height ?? 1.8) * s;
  d.colour = look?.colour ?? 0x9a9a9a;
}

/** A bot's health (bots carry it, entities send hurt events): a drop flashes it hurt, 0 starts the death fall, a living hp
 * again (respawn) ends it. */
function trackBotHealth(d: Drawn, hp: number, now: number): void {
  const m = d.motion;
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
}

/** Armour from the wire's equipment, else the older material list on the entity's or bot's record. */
function wornArmor(d: Drawn, eq: Equipment | null | undefined): ReadonlyArray<string | null> | null {
  return armorFromEquipment(eq) ?? armorOf(d.ent?.dto ?? d.botView?.dto ?? {});
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

/** One `showFor`'s view parameters and counters (one object per layer, refilled for each view: no per-frame allocation). */
interface ViewState {
  dim: number;
  cam: THREE.Vector3;
  zoom: number;
  bots: boolean;
  entities: boolean;
  labels: boolean;
  far: number;
  botDetail: number;
  cut: NonNullable<EntityShowOptions['cut']> | null;
  centre: { x: number; z: number } | null;
  drops: boolean;
  frustum: THREE.Frustum | null;
  n: number;
  boxes: number;
  botModels: number;
}

/** Extra slots in an entity's bone row (models with more parts than CONTENT_SLOT skip these extras). */
const HELD_SLOT = 62;
const FIRE_SLOT = 61;
const OFF_SLOT = 60;
const HEAD_SLOT = 59;
const CONTENT_SLOT = 58;
/** A worn skull: its head at 58, the dragon's jaw at 59 (never together with a head block or minecart contents). */
const SKULL_SLOT = CONTENT_SLOT;
/** LayerCustomHead's skull scale (and the villager lift in px). */
const SKULL_SCALE = 1.1875;
/** Elytra wings: left 56, right 57 (models with at most 56 parts). */
const ELYTRA_SLOT = 56;
/** A mooshroom's mushrooms: 3 × 2 crossed planes at 50..55 (an enderman's carried plant and an iron golem's poppy: 50, 51). */
const MUSHROOM_SLOT = 50;
/** Glinting items, by id, whatever their NBT (1.12.2 Item.hasEffect overrides): experience bottle, written book, nether star,
 * enchanted book, end crystal; the enchanted golden apple is 322 with meta > 0. */
const ALWAYS_GLINT = new Set([384, 387, 399, 403, 426]);
/** An item that glints in `version` by its id alone. */
export function glintsById(version: GameVersion, id: number, meta: number): boolean {
  return version === '1.12.2' && (ALWAYS_GLINT.has(id) || (id === 322 && meta > 0));
}
/** Mobs whose renderer has LayerCustomHead (RenderBiped and its subclasses, RenderVillager); players, bots and armor stands too. */
const HEAD_MOBS = new Set(['zombie', 'husk', 'zombie_pigman', 'skeleton', 'stray', 'wither_skeleton', 'zombie_villager', 'vex', 'villager']);
/** Mobs whose renderer has LayerBipedArmor / LayerVillagerArmor (the giant has armour but no custom head). */
const ARMOR_MOBS = new Set(['zombie', 'husk', 'zombie_pigman', 'skeleton', 'stray', 'wither_skeleton', 'zombie_villager', 'giant']);
/** Mobs with LayerElytra (RenderBiped's subclasses); players and armor stands too. */
const ELYTRA_MOBS = new Set(['zombie', 'husk', 'zombie_pigman', 'skeleton', 'stray', 'wither_skeleton', 'zombie_villager', 'vex']);
/** Particles per bone row. */
const PER_ROW = 63;
const F_EMISSIVE = 1, F_FLAT = 2, F_BLOCKS = 4, F_ENTITY = 8, F_BILLBOARD = 16, F_FIRE = 32, F_FRONT = 64, F_SCROLL = 128;
const LIVING = new Set(['mob', 'player', 'armor_stand']);
const ZERO3: readonly [number, number, number] = [0, 0, 0];
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
  /**
   * Measure `drawDistance` across the ground from this point instead of from the camera: a top-down / iso view hangs its camera
   * hundreds of blocks above what it looks at, so the camera distance would hide every mob of a zoomed-out map.
   */
  centre?: { x: number; z: number } | null;
  /** Dropped items and experience orbs (default on). */
  drops?: boolean;
  /** Entities wholly outside this frustum are skipped (bots too; their selection test only needs what is on screen). */
  frustum?: THREE.Frustum | null;
  /** The view's vertical field of view (degrees): narrow (iso) views see far entities large, so detail reaches further. */
  fov?: number;
  /** Occluded-body silhouettes: strength 0..1 per entity key (bot name, player "dim:id"); absent = none. */
  ghosts?: ReadonlyMap<string, number> | null;
  /** Name labels over bots (default: `labels`); off where the app draws its own. */
  botLabels?: boolean;
  /** [level cut] Entities with their feet at or above `y` within `r` of (x, z) are not drawn (the storey above is x-rayed);
   * those below `dimBelow` are dimmed like the blocks there. */
  cut?: { x: number; y: number; z: number; r: number; dimBelow: number | null } | null;
}

/** Bots (the hive's bodies, Story Mode's pawns: a few dozen at most) keep their full model to at least this apparent
 * distance, whatever the detailed-model setting (0 still means boxes for everyone): a top-down map sees them from far up. */
export const BOT_DETAIL_MIN = 96;

/** A body the silhouette test considers: its key and feet. */
export interface GhostCandidate {
  key: string;
  x: number;
  y: number;
  z: number;
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
  private readonly cullSphere = new THREE.Sphere();
  private readonly view: ViewState = {
    dim: 0, cam: new THREE.Vector3(), zoom: 1, bots: true, entities: true, labels: false, far: 0, botDetail: 0, cut: null, centre: null, drops: true, frustum: null, n: 0, boxes: 0, botModels: 0,
  };
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
  /** Worn skulls (one draw per skull geometry) and elytra; built on first use. */
  private readonly skullDraws = new Map<string, InstancedDraw>();
  private elytraDraw: InstancedDraw | null = null;
  private readonly extraGeos: THREE.BufferGeometry[] = [];
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
  private botModelCount = 0;
  private heldCache = new Map<string, ItemLook | null>();
  private readonly root = new Float32Array(12);
  /** The root without the sneaking sink (the elytra layer). */
  private readonly rootBase = new Float32Array(12);
  private readonly tmpA = new Float32Array(12);
  private readonly tmpB = new Float32Array(12);
  private readonly tmpC = new Float32Array(12);
  private readonly tmpD = new Float32Array(12);
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
      this.mats.shared.uTex.value = this.mats.shared.uItems.value = this.mats.shared.uBlocks.value = this.mats.shared.uGlint.value = null;
      return;
    }
    this.version = bundle.version;
    const ent = sheetTexture(bundle.entity), items = sheetTexture(bundle.items), blocks = sheetTexture(bundle.blocks);
    this.textures.push(ent, items, blocks);
    this.mats.shared.uTex.value = ent;
    this.mats.shared.uItems.value = items;
    this.mats.shared.uBlocks.value = blocks;
    const glint = bundle.env.glint ? glintTexture(bundle.env.glint) : null;
    if (glint) this.textures.push(glint);
    this.mats.shared.uGlint.value = glint;
    const grid = this.blocksGrid(bundle.pack);
    this.mats.shared.uBlocksGrid.value.set(grid[0], grid[1]);
  }

  /** Shares the world's per-view uniforms (time of day, fog) so entities light and fog like the sections. */
  setWorldUniforms(u: WorldUniforms): void {
    for (const m of [this.mats.model, this.mats.modelTranslucent, this.mats.tile, this.mats.shadow, this.mats.ghost, this.mats.glintModel, this.mats.glintTile])
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
    if (m) startDeath(m, now);
  }

  /**
   * A mob shown inside a block (the spinning mob in a spawner cage): `mob` is a mob name ("Zombie", "minecraft:zombie",
   * "cave_spider"…), drawn at (x, y, z) (its feet) scaled by `scale` and turned by `spinRad` about y. Call again to move or
   * turn it (cheap: one map update); `hideDisplayMob` removes it. Display mobs share the entity draw calls and LOD.
   */
  showDisplayMob(key: string, dim: number, x: number, y: number, z: number, mob: string, scale: number, spinRad: number, tiltDeg = 0): void {
    const d = this.displays.get(key);
    if (d) Object.assign(d, { dim, x, y, z, mob, scale, spin: spinRad, tilt: tiltDeg });
    else this.displays.set(key, { dim, x, y, z, mob, scale, spin: spinRad, tilt: tiltDeg });
  }

  hideDisplayMob(key: string): void {
    this.displays.delete(key);
  }

  /** An active spawner's particles: `ticks` ticks' worth of one flame and one smoke each, in the block at (x, y, z) (its
   * min corner). */
  spawnerParticles(dim: number, x: number, y: number, z: number, ticks: number, now = performance.now()): void {
    for (let i = 0; i < ticks; i++) {
      this.particles.burst('spawner_smoke', dim, x + 0.5, y + 0.5, z + 0.5, now);
      this.particles.burst('spawner_flame', dim, x + 0.5, y + 0.5, z + 0.5, now);
    }
  }

  /** The entity event animations: swings, hurts, deaths, sparks, eating, teleport puffs, lightning bolts. */
  applyEvents(events: EntityEvent[], now: number): void {
    for (const ev of events) {
      if (ev.kind === 'lightning') {
        if (hasPos(ev)) this.lightning.strike(ev.dim ?? 0, ev.x, ev.y, ev.z, now, ev.at || now);
        continue;
      }
      const handle = EntityLayer.EVENTS.get(ev.kind);
      if (handle) handle(this, ev, this.eventTarget(ev), now);
    }
  }

  /** What each entity event does to the body it is about (or, for sparks and puffs, at its position). */
  private static readonly EVENTS = new Map<string, (l: EntityLayer, ev: EntityEvent, d: Drawn | null, now: number) => void>([
    ['swing', (_l, _ev, d, now) => {
      if (d) d.motion.attackAt = now;
    }],
    ['swing_off', (_l, _ev, d, now) => {
      if (d) d.motion.attackOffAt = now;
    }],
    ['hurt', (_l, _ev, d, now) => {
      if (d) d.motion.hurtAt = now;
    }],
    ['eat', (_l, _ev, d, now) => {
      if (d) d.motion.eatAt = now;
    }],
    ['death', (_l, _ev, d, now) => {
      if (d) startDeath(d.motion, now);
    }],
    ['crit', (l, ev, d, now) => l.sparks(ev, d, now)],
    ['magic_crit', (l, ev, d, now) => l.sparks(ev, d, now)],
    ['teleport', (l, ev, d, now) => l.teleportPuffs(ev, d, now)],
  ]);

  /** Crit / magic-crit sparks around the body, else where the event says. */
  private sparks(ev: EntityEvent, d: Drawn | null, now: number): void {
    if (d) this.particles.burst(ev.kind, d.dim, d.x, d.y + d.h * 0.6, d.z, now);
    else if (hasPos(ev)) this.particles.burst(ev.kind, ev.dim ?? 0, ev.x, ev.y + 1, ev.z, now);
  }

  /** Teleport puffs where the event says and around the body. */
  private teleportPuffs(ev: EntityEvent, d: Drawn | null, now: number): void {
    if (hasPos(ev)) this.particles.burst('teleport', ev.dim ?? 0, ev.x, ev.y + 1, ev.z, now);
    if (d) this.particles.burst('teleport', d.dim, d.x, d.y + d.h * 0.5, d.z, now);
  }

  /** The bot or entity an event is about: (dim, id), or a bot by `name` (bots carry no entity id on the wire). */
  private eventTarget(ev: EntityEvent): Drawn | null {
    const bot = this.namedBot(ev);
    if (bot) return bot;
    if (ev.id == null) return null;
    return this.drawn.get(`${ev.dim ?? 0}:${ev.id}`) ?? this.botWithId(ev.id);
  }

  /** The bot an event names (`name`, or the older `bot`). */
  private namedBot(ev: EntityEvent): Drawn | undefined {
    const name = (ev as { name?: unknown; bot?: unknown }).name ?? (ev as { bot?: unknown }).bot;
    return typeof name === 'string' ? this.drawn.get(name) : undefined;
  }

  /** A bot whose wire record carries this entity id. */
  private botWithId(id: number): Drawn | null {
    for (const x of this.drawn.values()) if (x.bot && (x.botView?.dto as { id?: number } | undefined)?.id === id) return x;
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
    for (const b of bots) this.listBot(b, frame, timeMs, selected);
    for (const e of ents) this.listEntity(e, frame, timeMs);
    for (const [k, dm] of this.displays) this.listDisplay(k, dm, frame, timeMs);
    this.keepDying(frame, timeMs);
    this.dropGoneBots();
    // Bones: one row per entity, then the particles (63 per row).
    this.particles.step(timeMs);
    this.lightning.update(timeMs);
    const prows = Math.ceil(this.particles.list.length / PER_ROW);
    this.ensureRows(list.length + prows + 1);
    // Rows first: a body poses its riders' rows along with its own.
    for (let i = 0; i < list.length; i++) list[i].row = i;
    for (const d of list) {
      d.bright = selectedBrightness(d, selected);
      this.writeRow(d, timeMs);
    }
    this.particleRow = list.length;
    this.particles.list.forEach((p, i) => this.particles.bone(p, timeMs, this.bones, (this.particleRow + Math.floor(i / PER_ROW)) * ROW_FLOATS + (i % PER_ROW) * 12));
    if (this.boneTex) this.boneTex.needsUpdate = true;
  }

  /** Drops the bot objects (labels…) of bots no longer drawn. */
  private dropGoneBots(): void {
    for (const [k, o] of this.botObjs) if (!this.drawn.get(k)?.bot) this.dropBot(k, o);
  }

  private listBot(b: BotView, frame: number, timeMs: number, selected: ReadonlySet<string>): void {
    const d = this.drawnFor(b.name, true, b.x, b.y, b.z, b.yaw, timeMs);
    d.frame = frame;
    d.botView = b;
    d.ent = null;
    d.dim = b.dim;
    this.refreshBot(d, b, timeMs);
    this.list.push(d);
    this.botLabel(b, selected, timeMs);
  }

  /** An entity and, right after it, its riders (at its position, posed by it). */
  private listEntity(e: EntityView, frame: number, timeMs: number): void {
    const d = this.drawnFor(e.key, false, e.x, e.y, e.z, e.yaw, timeMs);
    d.frame = frame;
    d.ent = e;
    d.botView = null;
    d.dim = e.dim;
    this.refreshEntity(d, e, timeMs);
    this.list.push(d);
    if (!d.riders) return;
    for (const r of d.riders) {
      r.frame = frame;
      r.dim = d.dim;
      r.x = d.x;
      r.y = d.y;
      r.z = d.z;
      r.posed = false;
      this.list.push(r);
    }
  }

  private listDisplay(k: string, dm: DisplayMob, frame: number, timeMs: number): void {
    const d = this.drawnFor(`display:${k}`, false, dm.x, dm.y, dm.z, 0, timeMs);
    d.frame = frame;
    d.dim = dm.dim;
    d.display = dm;
    this.refreshDisplay(d, dm);
    this.list.push(d);
  }

  /** Drops what this frame no longer lists, except the dying: they stay until their body has shrunk away (the server
   * usually removes them right after the death event). */
  private keepDying(frame: number, timeMs: number): void {
    for (const [k, d] of this.drawn) {
      if (d.frame === frame) continue;
      if (d.motion.deadAt && timeMs - d.motion.deadAt < DEATH_GONE_MS) {
        d.frame = frame;
        this.list.push(d);
      } else this.drawn.delete(k);
    }
  }

  private drawnFor(key: string, bot: boolean, x: number, y: number, z: number, yaw: number, now: number): Drawn {
    let d = this.drawn.get(key);
    if (!d) {
      d = {
        key, bot, dim: 0, x, y, z, kind: 'box', sig: '', look: null, model: null, v: Variant.NONE, w: 0.6, h: 1.8, colour: 0x9a9a9a, row: 0, posed: false, detail: null,
        item: null, count: 1, held: null, heldTool: false, heldOff: null, heldOffTool: false, bow: false, headBlock: null, headItem: null, skull: -1, elytra: false, fuseAt: 0, cartBlock: 0,
        armor: null, armorDye: null, headLayer: false, armorLayer: false, villagerHead: false, baby: false, invisible: false, glowing: false,
        burning: false, flash: 0, hurt: 0, sky: 15, block: 0, bright: 1, age: 0, fixedYaw: null, fixedPitch: 0, hang: null, display: null, smoked: false, eqSig: '',
        armorGlint: null, heldGlint: false, heldOffGlint: false, headGlint: false, elytraGlint: false, itemGlint: false, carried: null, roseAt: 0,
        parent: null, riders: null, ridersSig: '', ride: null, drop: false,
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
    setHitbox(d, look, def);
  }

  private itemById(ref: ItemRef | EquipRef | undefined, fallbackId = 0): ItemLook | null {
    const id = ref?.[0] ?? fallbackId;
    if (!this.bundle || id <= 0) return null;
    return itemLookById(this.version, this.bundle.pack, id, ref?.[1] ?? 0);
  }

  /** Hands, head items, armour and elytra from wire equipment (ids in the world's version). */
  private equip(d: Drawn, eq: Equipment | null | undefined, mainFallback: ItemLook | null, mainName: string | null): void {
    this.equipHands(d, eq, mainFallback, mainName);
    this.equipHead(d, eq);
    this.equipBody(d, eq);
    this.equipGlints(d, eq);
  }

  private equipHands(d: Drawn, eq: Equipment | null | undefined, mainFallback: ItemLook | null, mainName: string | null): void {
    d.held = eq?.main ? this.itemById(eq.main) : mainFallback;
    d.heldTool = mainHandTool(eq, mainName);
    d.heldOff = eq?.off ? this.itemById(eq.off) : null;
    d.heldOffTool = !!eq?.off && isHandheld(itemName(eq.off[0]) ?? '');
    d.bow = holdsBow(eq, mainName);
  }

  /**
   * LayerCustomHead: a skull (1.12.2 item 397, its meta the type) as the skull model; any other head item that is not a
   * helmet as its item (blocks as a block, 1.12.2 flat items with their "head" transform). b1.8.1's RenderPlayer draws
   * only blocks.
   */
  private equipHead(d: Drawn, eq: Equipment | null | undefined): void {
    d.headBlock = d.headItem = null;
    d.skull = -1;
    const head = eq?.head;
    if (!head || !d.headLayer || armorMaterial(head[0], 'helmet') != null) return;
    if (!this.beta && head[0] === 397) d.skull = head[1] >= 0 && head[1] <= 5 ? head[1] : 0;
    else this.wearHeadItem(d, head);
  }

  private wearHeadItem(d: Drawn, head: NonNullable<EquipRef>): void {
    const l = this.itemById(head);
    if (l?.kind === 'cube') d.headBlock = l;
    else if (l && !this.beta) d.headItem = l;
  }

  /** Elytra (1.12.2, on the renderers with LayerElytra) and armour (the wire's equipment, else an older material list). */
  private equipBody(d: Drawn, eq: Equipment | null | undefined): void {
    d.elytra = !this.beta && eq?.chest?.[0] === 443 && this.elytraWearer(d);
    d.armor = d.armorLayer ? wornArmor(d, eq) : null;
    d.armorDye = d.armor ? armorDyes(eq) : null;
  }

  private equipGlints(d: Drawn, eq: Equipment | null | undefined): void {
    d.heldGlint = !!d.held && (eq?.main ? this.shiny(eq, 'main') : false);
    d.heldOffGlint = !!d.heldOff && this.shiny(eq, 'off');
    d.headGlint = (!!d.headBlock || !!d.headItem) && this.shiny(eq, 'head');
    d.elytraGlint = d.elytra && this.shiny(eq, 'chest');
    d.armorGlint = d.armor ? [this.shiny(eq, 'head'), this.shiny(eq, 'chest'), this.shiny(eq, 'legs'), this.shiny(eq, 'feet')] : null;
  }

  /** The glint: the wire's flag (NBT), or the item's own (Item.hasEffect by id). */
  private shiny(eq: Equipment | null | undefined, slot: EquipSlot): boolean {
    const ref = eq?.[slot];
    return !!ref && (!!eq?.glint?.[slot] || glintsById(this.version, ref[0], ref[1]));
  }

  private elytraWearer(d: Drawn): boolean {
    if (d.bot || d.ent?.kind === 'player' || d.look?.model === 'armor_stand') return true;
    return d.ent?.kind === 'mob' && ELYTRA_MOBS.has(mobName(d.ent.type, d.ent.name) ?? '');
  }

  /** Which equipment layers the entity's renderer has (head items, armour). */
  private layersFor(d: Drawn, kind: string, e: EntityView | null): void {
    const all = d.bot || kind === 'player' || kind === 'armor_stand';
    const name = !all && kind === 'mob' && e ? (mobName(e.type, e.name) ?? '') : '';
    d.headLayer = all || HEAD_MOBS.has(name);
    d.armorLayer = all || ARMOR_MOBS.has(name);
    d.villagerHead = name === 'villager' || name === 'zombie_villager';
  }

  private refreshBot(d: Drawn, b: BotView, now: number): void {
    const sig = `bot|${this.version}|${!!this.bundle}`;
    if (d.sig !== sig) {
      d.sig = sig;
      this.setLook(d, playerLook(this.version, b.name, true));
      d.colour = botColour(b).getHex();
    }
    const m = d.motion;
    trackBotHealth(d, b.dto.hp, now);
    const extra = b.dto as { equipment?: Equipment | null; flags?: number; headYaw?: number | null };
    this.refreshBotGear(d, b.dto.held ?? null, extra.equipment);
    const flags = extra.flags ?? 0;
    d.burning = (flags & FLAG_ON_FIRE) !== 0;
    d.invisible = (flags & FLAG_INVISIBLE) !== 0;
    d.glowing = (flags & FLAG_GLOWING) !== 0;
    d.flash = 0;
    stepMotion(m, b.x, b.y, b.z, b.yaw, now, (flags & FLAG_SPRINTING) !== 0);
  }

  /** A bot's held item and equipment, resolved again only when they (or the look) changed. */
  private refreshBotGear(d: Drawn, held: string | null, equipment: Equipment | null | undefined): void {
    const sigEq = `${held}|${JSON.stringify(equipment ?? null)}`;
    if (d.sig + sigEq === d.eqSig) return;
    this.layersFor(d, 'player', null);
    this.equip(d, equipment, held ? this.heldLook(held) : null, held);
    d.eqSig = d.sig + sigEq;
  }

  private heldLook(held: string): ItemLook | null {
    const k = `${this.version}|${held}`;
    if (this.heldCache.has(k)) return this.heldCache.get(k)!;
    const l = this.bundle ? itemLookByName(this.version, this.bundle.pack, held) : null;
    if (this.bundle) this.heldCache.set(k, l);
    return l;
  }

  private refreshEntity(d: Drawn, e: EntityView, now: number): void {
    const sig = `${this.version}|${!!this.bundle}|${entityKindSig(e)}|${entityExtraSig(e.dto)}`;
    if (d.sig !== sig) {
      d.sig = sig;
      this.applyEntityLook(d, e);
    }
    // LayerIronGolemFlower: holdRoseTick counts down 400 ticks from when the wire first said so.
    const rose = d.look?.model === 'iron_golem' && d.v.has('holding_rose', 'rose');
    if (rose && !d.roseAt) d.roseAt = now;
    else if (!rose) d.roseAt = 0;
    this.syncRiders(d, e, now);
    d.count = e.count;
    d.age = e.ageTicks;
    const flags = e.flags;
    d.burning = (flags & FLAG_ON_FIRE) !== 0;
    d.invisible = (flags & FLAG_INVISIBLE) !== 0;
    d.glowing = (flags & FLAG_GLOWING) !== 0;
    if (d.fixedYaw !== null) this.objectAim(d, e);
    entityFlash(d, e, now);
    if (d.motion.deadAt && now - d.motion.deadAt > 4000) d.motion.deadAt = 0; // still reported long after: it lives
    stepMotion(d.motion, e.x, e.y, e.z, e.yaw, now, (flags & FLAG_SPRINTING) !== 0);
  }

  /** What an entity draws (its model or item, size, colour, layers, equipment), re-resolved when its wire look changes. */
  private applyEntityLook(d: Drawn, e: EntityView): void {
    const dto = e.dto;
    d.v = Variant.of(dto.variant, lookOf(dto));
    d.baby = !!dto.baby;
    d.item = null;
    d.hang = null;
    d.fixedYaw = null;
    const kind = entityKind(e, this.beta);
    d.drop = kind === 'item' || isOrb(kind);
    this.setKindLook(d, e, kind);
    // Hand items and armour: wire equipment, else a player's held item id.
    this.layersFor(d, kind, e);
    this.equip(d, dto.equipment, kind === 'player' && e.item > 0 ? this.itemById(null, e.item) : null, null);
    if (!LIVING.has(kind) && d.kind === 'model') d.fixedYaw = e.yaw;
    // LayerHeldBlock: an enderman's block.
    d.carried = d.look?.model === 'enderman' && dto.carried ? this.itemById(dto.carried) : null;
  }

  /** How each entity kind gets its look; anything else is an object with a model (or a box). */
  private static readonly KIND_LOOKS = new Map<string, (l: EntityLayer, d: Drawn, e: EntityView, kind: string) => void>([
    ['player', (l, d, e) => l.setLook(d, playerLook(l.version, e.name ?? 'player', false))],
    ['mob', (l, d, e) => l.setLook(d, mobLook(l.version, e.type, e.name, d.v, e.id))],
    ['item', (l, d, e) => l.setDrop(d, e)],
    ['xp_orb', (l, d) => l.setOrb(d)],
    ['xporb', (l, d) => l.setOrb(d)],
    ['orb', (l, d) => l.setOrb(d)],
    ['painting', (l, d, e) => l.setPainting(d, e)],
    ['item_frame', (l, d, e) => l.setFrame(d, e)],
    ['firework', (l, d) => l.setFirework(d)],
    ['fireworks_rocket', (l, d) => l.setFirework(d)],
    ['falling_block', (l, d, e, kind) => l.setFallingBlock(d, e, kind)],
    ['falling_sand', (l, d, e, kind) => l.setFallingBlock(d, e, kind)],
    ['tnt', (l, d, e, kind) => l.setFallingBlock(d, e, kind)],
  ]);

  private setKindLook(d: Drawn, e: EntityView, kind: string): void {
    const set = EntityLayer.KIND_LOOKS.get(kind);
    if (set) set(this, d, e, kind);
    else this.setObject(d, e, kind);
  }

  private setDrop(d: Drawn, e: EntityView): void {
    const damage = e.dto.damage ?? 0;
    this.setLook(d, null);
    d.item = this.bundle && e.item > 0 ? itemLookById(this.version, this.bundle.pack, e.item, damage) : null;
    d.itemGlint = !!e.dto.glint || glintsById(this.version, e.item, damage);
    d.kind = d.item ? 'item' : 'box';
    d.w = d.h = 0.25;
    d.colour = 0xd8b040;
  }

  private setOrb(d: Drawn): void {
    this.setLook(d, null);
    d.kind = this.bundle ? 'orb' : 'box';
    d.w = d.h = 0.25;
    d.colour = 0x9ae040;
  }

  private setFirework(d: Drawn): void {
    this.setLook(d, null);
    d.item = this.bundle && !this.beta ? itemLookByName(this.version, this.bundle.pack, 'fireworks') : null;
    d.kind = d.item ? 'sprite' : 'box';
    d.w = d.h = 0.25;
    d.colour = 0xd04040;
  }

  /** Falling blocks and primed TNT: the block as a cube. */
  private setFallingBlock(d: Drawn, e: EntityView, kind: string): void {
    this.setLook(d, null);
    const l = this.itemById(fallingBlockRef(e, kind, this.beta));
    d.item = l?.kind === 'cube' ? l : null;
    d.kind = d.item ? 'block' : 'box';
    d.w = d.h = 0.98;
    d.colour = kind === 'tnt' ? 0xd04030 : 0xd8c890;
  }

  /** Objects and stands with a model (boat, minecart, arrow, ender crystal, armor stand…); minecarts show their block. */
  private setObject(d: Drawn, e: EntityView, kind: string): void {
    const look = objectLook(this.version, kind, d.v, e.id);
    this.setLook(d, look);
    if (!look) {
      d.w = d.h = 0.5;
      d.colour = 0x9a9a9a;
    }
    if (kind === 'minecart' || kind.endsWith('_minecart')) {
      const block = minecartBlock(e.dto.minecartType ?? (kind === 'minecart' ? 0 : kind));
      d.cartBlock = block;
      d.item = block ? this.itemById([block, 0]) : null;
    }
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
    d.itemGlint = !!e.dto.frameItem && (!!e.dto.glint || glintsById(this.version, e.dto.frameItem[0], e.dto.frameItem[1]));
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
    // The spawner turns the mob by +spin about y, then the mob renders with yaw 0 (rotY(180) inside): rotY(180 + spin), i.e.
    // a body yaw of −spin; rotX(tilt) there is rotX(−tilt) in the turned mob's frame.
    d.fixedYaw = (-dm.spin * 180) / Math.PI;
    d.age = this.now / 50;
  }

  /** Writes the entity's bone row: the LOD box always; the posed parts when some view's camera was near last frame. */
  private writeRow(d: Drawn, now: number): void {
    if (d.parent) return; // its body poses it
    const off = d.row * ROW_FLOATS;
    const m = d.motion;
    this.writeBox(d, off + BOX_SLOT * 12, now);
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
    this.poseKind(d, now, off);
    d.posed = d.kind !== 'box';
  }

  /** The LOD box: translate · rotY · scale(w, h, w), shrinking in death. */
  private writeBox(d: Drawn, bo: number, now: number): void {
    const yaw = d.hang ? d.hang.yaw : d.fixedYaw ?? (d.kind === 'model' ? d.motion.body : d.ent?.yaw ?? 0);
    const a = (yaw * Math.PI) / 180;
    const b = this.bones;
    const c = Math.cos(a), s = Math.sin(a);
    const k = deathScale(d.motion, now);
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
  }

  /** The posed parts of what it draws (models, drops, blocks, billboards, hanging entities). */
  private poseKind(d: Drawn, now: number, off: number): void {
    if (d.kind === 'model' && d.model && d.look) this.poseModel(d, now, off);
    else if (d.kind === 'item') this.poseDrop(d, off);
    else if (d.kind === 'block') this.poseBlock(d, off);
    else if (d.kind === 'orb' || d.kind === 'sprite') this.poseOrb(d, off);
    else if (d.kind === 'painting' || d.kind === 'frame') this.poseHanging(d, off);
  }

  private near(d: Drawn): boolean {
    if (!this.lastCams.length) return true;
    const detail = d.bot && this.detailDistance > 0 ? Math.max(this.detailDistance, BOT_DETAIL_MIN) : this.detailDistance;
    const r2 = (detail + 4) ** 2;
    for (const c of this.lastCams) if (((c.p.x - d.x) ** 2 + (c.p.y - d.y) ** 2 + (c.p.z - d.z) ** 2) * c.k * c.k <= r2) return true;
    return false;
  }

  private poseModel(d: Drawn, now: number, off: number): void {
    const e = d.model!;
    const flags = poseFlags(d);
    const sneaking = (flags & FLAG_SNEAKING) !== 0;
    const body = d.fixedYaw ?? d.motion.body;
    this.placeRoot(d, this.rootFor(d, now, body, sneaking), sneaking);
    turnHead(d, body);
    this.fillMotionPose(d, now, body);
    this.fillStatePose(d, now, flags, sneaking);
    e.skel.pose(this.ctx, this.root, this.bones, off, d.baby && e.built.def.baby ? true : false);
    this.poseAttachments(d, e, off, body, sneaking);
  }

  /** The model root: position (a screaming enderman jitters), yaw (a converting zombie villager shakes), death roll, scale,
   * the sneaking player's sink and a display mob's tilt. */
  private rootFor(d: Drawn, now: number, body: number, sneaking: boolean): RootParams {
    const def = d.model!.built.def;
    // RenderEnderman: a screaming enderman jitters (gaussian 0.02 on x and z).
    const screaming = isScreaming(d);
    const isPlayer = d.bot || d.ent?.kind === 'player';
    return {
      x: d.x + (screaming ? gaussian() * 0.02 : 0),
      y: d.y,
      z: d.z + (screaming ? gaussian() * 0.02 : 0),
      yaw: body + convertingShake(d, now),
      death: deathAngle(d.motion, now),
      scale: this.modelScale(d, now),
      offset: def.offset ?? ZERO3,
      lift: sneaking && isPlayer ? -0.125 : 0,
      tilt: d.display ? -d.display.tilt : 0,
    };
  }

  /** The look's scale (babies of adult-only models at half size, a display mob's own), shrinking in death, a fusing
   * creeper's swell. */
  private modelScale(d: Drawn, now: number): [number, number, number] {
    const def = d.model!.built.def, look = d.look!;
    const s0 = def.scale ?? 1, k = (look.scale ?? 1) * (d.baby && !def.baby ? 0.5 : 1) * deathScale(d.motion, now) * (d.display?.scale ?? 1);
    const sc: [number, number, number] = typeof s0 === 'number' ? [s0 * k, s0 * k, s0 * k] : [s0[0] * k, s0[1] * k, s0[2] * k];
    if (d.fuseAt) creeperSwell(sc, creeperFlash(d.fuseAt, now));
    return sc;
  }

  /** Writes the root matrix (and the root without the sneaking sink, for the elytra layer and riders). */
  private placeRoot(d: Drawn, root: RootParams, sneaking: boolean): void {
    // 1.12.2 ModelBiped.render sinks a sneaking adult's parts 0.2 (3.2 px) after the pose (its layers follow: armour, held
    // items, the custom head); the elytra layer does not.
    const sneakSink = sneaking && !d.baby && !this.beta && d.armorLayer;
    if (sneakSink || d.elytra || d.riders) rootMatrix(this.rootBase, root);
    rootMatrix(this.root, sneakSink ? { ...root, offset: [root.offset[0], root.offset[1] + 3.2, root.offset[2]] } : root);
  }

  /** The pose context's motion: limb swing, age, head angles, swings and eating. */
  private fillMotionPose(d: Drawn, now: number, body: number): void {
    const c = this.ctx, m = d.motion;
    c.swing = d.display ? 0 : m.swing;
    c.amount = m.deadAt || d.display ? 0 : m.amount;
    c.age = now / 50 + m.seed * 1000;
    c.headYaw = d.display ? 0 : Math.max(-75, Math.min(75, wrapDeg(m.head - body)));
    c.pitch = d.fixedYaw !== null && d.look?.model === 'arrow' ? d.fixedPitch : Math.max(-90, Math.min(90, d.botView?.dto.pitch ?? 0));
    c.attack = attackProgress(m, now);
    c.attackOff = attackOffProgress(m, now);
    c.eat = eatProgress(m, now);
  }

  /** The pose context's state: age, variant, flags, what it holds and does, the stand pose, the golem's rose. */
  private fillStatePose(d: Drawn, now: number, flags: number, sneaking: boolean): void {
    const c = this.ctx, m = d.motion;
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
    c.carrying = !!d.carried;
    c.rose = d.roseAt ? Math.max(0, 400 - (now - d.roseAt) / 50) : 0;
  }

  /** Held and worn items, minecart contents, mooshroom mushrooms, the enderman's block, the golem's rose, elytra, fire and
   * riders, each where the model has room for its bone slots. */
  private poseAttachments(d: Drawn, e: ModelEntry, off: number, body: number, sneaking: boolean): void {
    const parts = e.built.parts.length;
    if (parts <= CONTENT_SLOT) this.poseContentSlots(d, e, off, body);
    if (parts <= MUSHROOM_SLOT) this.poseMushroomSlots(d, e, off);
    if (d.elytra && parts <= ELYTRA_SLOT) this.poseElytra(d, off, sneaking, this.rootBase);
    if (d.burning) this.poseFire(d, off);
    if (d.riders) this.poseRiders(d, e, sneaking);
  }

  private poseContentSlots(d: Drawn, e: ModelEntry, off: number, body: number): void {
    if (d.held) this.poseHeld(d, e, off, false);
    if (d.heldOff) this.poseHeld(d, e, off, true);
    if (d.skull >= 0 || d.headBlock || d.headItem) this.poseHead(d, e, off, d.motion.swing);
    else if (this.snowPumpkin(d)) this.poseSnowPumpkin(e, off);
    if (d.item) this.poseContents(d, off, body);
  }

  private poseMushroomSlots(d: Drawn, e: ModelEntry, off: number): void {
    if (this.mushrooms(d)) this.poseMushrooms(e, off);
    if (d.carried) this.poseCarried(d, off);
    if (this.ctx.rose) this.poseRose(e, off);
  }

  // ------------------------------------------------------------------------------------------------------- held blocks

  /** The model root as vanilla's layer frame (blocks): root · scale(16). */
  private layerFrame(out: Float32Array, root: ArrayLike<number>): Float32Array {
    const s = identity(this.tmpD);
    s[0] = s[5] = s[10] = 16;
    return mul(out, root, s);
  }

  /** A block drawn by renderBlockBrightness in `frame` (blocks): the cube (x, z ±0.5, y 0..1) as the [0, 1]³ block turned
   * rotY(90°) first; b1.8.1's renderBlockOnInventory turns it the same way but centred on the origin (client code, not in
   * mc-dev: unverified). Crossed plants as the
   * two diagonal planes of block/cross (like the mooshroom's mushrooms). */
  private poseBlockIn(frame: ArrayLike<number>, look: ItemLook, off: number, centred: boolean): void {
    const r = partLocal(this.tmpA, 0, 0, 0, 0, Math.PI / 2, 0);
    const turned = mul(this.tmpB, frame, r);
    if (look.kind === 'cube') {
      const t = identity(this.tmpA);
      if (centred) t[7] = -0.5;
      else {
        t[3] = 0.5;
        t[11] = 0.5;
      }
      mul(this.tmpC, turned, t);
      this.bones.set(this.tmpC, off + CONTENT_SLOT * 12);
      return;
    }
    if (centred) {
      const t = identity(this.tmpA);
      t[3] = t[7] = t[11] = -0.5;
      mul(this.tmpC, turned, t);
      turned.set(this.tmpC);
    }
    crossPlanes(this.bones, off + MUSHROOM_SLOT * 12, turned, this.tmpA, this.tmpC);
  }

  /** LayerHeldBlock: in the model frame translate(0, 0.6875, −0.75) · rotX(20°) · rotY(45°) · translate(0.25, 0.1875, 0.25) ·
   * scale(−0.5, −0.5, 0.5), then the block. */
  private poseCarried(d: Drawn, off: number): void {
    const f = this.layerFrame(new Float32Array(12), this.root);
    const steps = [
      partLocal(new Float32Array(12), 0, 0.6875, -0.75, (20 * Math.PI) / 180, 0, 0),
      partLocal(new Float32Array(12), 0, 0, 0, 0, Math.PI / 4, 0),
      partLocal(new Float32Array(12), 0.25, 0.1875, 0.25, 0, 0, 0),
      scaleMatrix(-0.5, -0.5, 0.5),
    ];
    let m = f;
    for (const s of steps) m = mul(new Float32Array(12), m, s);
    this.poseBlockIn(m, d.carried!, off, this.beta);
  }

  /** LayerIronGolemFlower: rotX(5° + the right arm's angle) · rotX(90°) · translate(−0.9375, −0.625, −0.9375) · scale(0.5, −0.5,
   * 0.5), then the poppy (Blocks.RED_FLOWER) as crossed planes. */
  private poseRose(e: ModelEntry, off: number): void {
    const arm = e.skel.byName.rightArm?.rx ?? 0;
    const f = this.layerFrame(new Float32Array(12), this.root);
    const steps = [
      partLocal(new Float32Array(12), 0, 0, 0, (5 * Math.PI) / 180 + arm + Math.PI / 2, 0, 0),
      partLocal(new Float32Array(12), -0.9375, -0.625, -0.9375, 0, 0, 0),
      scaleMatrix(0.5, -0.5, 0.5),
    ];
    let m = f;
    for (const s of steps) m = mul(new Float32Array(12), m, s);
    const r = partLocal(this.tmpA, 0, 0, 0, 0, Math.PI / 2, 0);
    crossPlanes(this.bones, off + MUSHROOM_SLOT * 12, mul(this.tmpB, m, r), this.tmpA, this.tmpC);
  }

  // ------------------------------------------------------------------------------------------------------------ riders

  /** The parrots on a player's shoulders and the arrows stuck in it: rider rows, kept while the wire says so. */
  private syncRiders(d: Drawn, e: EntityView, now: number): void {
    if (e.kind !== 'player' && !d.riders) return;
    const modern = e.kind === 'player' && this.version === '1.12.2';
    const shoulders = modern ? e.dto.shoulders : null;
    const arrows = modern ? (e.dto.arrows ?? 0) : 0;
    const slim = d.look?.model === 'player_slim';
    const sig = `${this.version}|${!!this.bundle}|${shoulderSig(shoulders)}|${arrows}|${slim}|${e.id}`;
    if (sig === d.ridersSig) return;
    d.ridersSig = sig;
    const riders = this.makeRiders(d, e.id, shoulders, arrows, slim, now);
    d.riders = riders.length ? riders : null;
  }

  /** Rider rows: a parrot per shoulder that has one, then the stuck arrows (their spots on the model). */
  private makeRiders(d: Drawn, id: number, shoulders: EntityDto['shoulders'], arrows: number, slim: boolean, now: number): Drawn[] {
    const riders: Drawn[] = [];
    for (const [left, colour] of [[true, shoulders?.left], [false, shoulders?.right]] as const)
      if (colour) riders.push(this.rider(d, left ? 'shoulder_left' : 'shoulder_right', { kind: 'shoulder', left }, mobLook(this.version, 105, 'parrot', Variant.of(null, colour), 0), now));
    if (arrows > 0) {
      const look = objectLook(this.version, 'arrow', Variant.NONE, 0);
      stuckArrows(id, arrows, slim).forEach((spot, j) => riders.push(this.rider(d, `arrow${j}`, { kind: 'arrow', spot }, look, now)));
    }
    return riders;
  }

  /** One rider of `d` (its own drawn record, keyed under the body's). */
  private rider(d: Drawn, suffix: string, ride: NonNullable<Drawn['ride']>, look: Look | null, now: number): Drawn {
    const r = this.drawnFor(`${d.key}#${suffix}`, false, d.x, d.y, d.z, 0, now);
    r.parent = d;
    r.ride = ride;
    this.setLook(r, look);
    return r;
  }

  /**
   * The riders' bones, in the body's frame:
   * - a shoulder parrot (LayerEntityOnShoulder): translate(±0.4, sneaking −1.3 : −1.5, 0) blocks in the model frame, the parrot
   *   model posed with the player's limb swing and head angles (ageInTicks 0: standing, wings still);
   * - a stuck arrow (LayerArrow): the box's part frame (postRender), translate(spot), then the arrow drawn as at the origin
   *   with its yaw and pitch (our arrow's yaw and pitch are vanilla's negated).
   */
  private poseRiders(d: Drawn, e: ModelEntry, sneaking: boolean): void {
    for (const r of d.riders!) {
      if (!r.model || !r.look || !r.ride) continue;
      r.sky = d.sky;
      r.block = d.block;
      r.hurt = d.hurt;
      r.bright = d.bright;
      r.invisible = false;
      const off = r.row * ROW_FLOATS;
      if (r.ride.kind === 'shoulder') this.poseShoulder(r.model, r.ride.left, sneaking, off);
      else this.poseArrow(r.model, r.look, r.ride.spot, e, off);
      r.posed = true;
    }
  }

  /** A shoulder parrot: translate(±0.4, sneaking −1.3 : −1.5, 0) blocks in the body's model frame. */
  private poseShoulder(m: ModelEntry, left: boolean, sneaking: boolean, off: number): void {
    const t = identity(this.tmpA);
    t[3] = left ? 6.4 : -6.4;
    t[7] = (sneaking ? -1.3 : -1.5) * 16;
    mul(this.tmpB, this.rootBase, t);
    m.skel.pose(this.ctx, this.tmpB, this.bones, off);
  }

  /** A stuck arrow: the spot's part frame (or its pivot in the root frame), translate(spot), the arrow's own root (scale,
   * yaw) and its pitch. */
  private poseArrow(m: ModelEntry, look: Look, spot: ArrowSpot, e: ModelEntry, off: number): void {
    const c = this.ctx;
    const frame = this.arrowFrame(spot, e);
    const at = identity(this.tmpA);
    at[3] = spot.at[0];
    at[7] = spot.at[1];
    at[11] = spot.at[2];
    const placed = mul(new Float32Array(12), frame, at);
    const def = m.built.def, s0 = def.scale ?? 1, k = look.scale ?? 1;
    const sc: [number, number, number] = typeof s0 === 'number' ? [s0 * k, s0 * k, s0 * k] : [s0[0] * k, s0[1] * k, s0[2] * k];
    rootMatrix(this.tmpC, { x: 0, y: 0, z: 0, yaw: -spot.yaw, death: 0, scale: sc, offset: def.offset ?? ZERO3 });
    mul(this.tmpD, placed, this.tmpC);
    const pitch = c.pitch;
    c.pitch = -spot.pitch;
    m.skel.pose(c, this.tmpD, this.bones, off);
    c.pitch = pitch;
  }

  /** The layer frame an arrow spot hangs in: its part's (postRender), else its pivot in the body's root frame. */
  private arrowFrame(spot: ArrowSpot, e: ModelEntry): Float32Array {
    const pi = spot.part != null ? e.built.index[spot.part] : undefined;
    if (pi != null) return this.layerFrame(new Float32Array(12), e.skel.partMatrix(pi));
    const t = identity(this.tmpA);
    t[3] = spot.pivot[0];
    t[7] = spot.pivot[1];
    t[11] = spot.pivot[2];
    return this.layerFrame(new Float32Array(12), mul(this.tmpB, this.rootBase, t));
  }

  private get beta(): boolean {
    return this.version === 'b1.8.1';
  }

  /**
   * LayerCustomHead: the head part's frame (with the layer's own child transform: translate(0, 0.5 px) · scale(0.7) ·
   * translate(0, 16 px) where the model's child head is scale(0.75) · translate(0, 16 px)), then
   * - a skull: scale(1.1875) about the neck (the layer's scale(1.1875, −1.1875, −1.1875) and the skull renderer's
   *   scale(−1, −1, 1) and 180° turn cancel out), villagers 1.1875 px higher; the dragon head translate(0, −0.374375) ·
   *   scale(0.75) inside that, its jaw open by (sin(limbSwing·0.2π) + 1)·0.2;
   * - a block: translate(0, −0.25) · rotY(180°) · scale(0.625, −0.625, −0.625) (b1.8.1: scale(0.625, −0.625, 0.625)), the
   *   cube centred, villagers 0.1875 higher in that frame;
   * - a flat item: the same frame, then item/generated's head transform (rotY 180°, translate(0, 13, 7) px).
   */
  private poseHead(d: Drawn, e: ModelEntry, off: number, swing: number): void {
    const hi = e.built.index.head;
    if (hi == null) return;
    const frame = this.tmpC;
    frame.set(e.skel.partMatrix(hi));
    const def = e.built.def;
    if (d.baby && def.baby && !d.villagerHead) this.babyHeadFrame(frame, def.baby);
    const l = identity(this.tmpA);
    if (d.skull === 5) this.poseDragonSkull(frame, l, off, swing, d.villagerHead);
    else if (d.skull >= 0) this.poseSkull(frame, l, off, d.villagerHead);
    else if (d.headBlock) this.poseHeadBlock(frame, l, off, d.villagerHead);
    else this.poseHeadItem(frame, l, off, d.villagerHead);
  }

  /** A skull: scale(1.1875) about the neck, villagers 1.1875 px higher. */
  private poseSkull(frame: Float32Array, l: Float32Array, off: number, villager: boolean): void {
    this.poseHeadPiece(frame, l, off + SKULL_SLOT * 12, SKULL_SCALE, SKULL_SCALE, SKULL_SCALE, villager ? -SKULL_SCALE : 0, 0);
  }

  /** A block: the cube (x, z ±0.5, y 0..1) centred, then diag(−10, −10, 10) px (b1.8.1: turned 180°, its pumpkin face is the
   * south one), up 4 px (+1.875 for villagers). */
  private poseHeadBlock(frame: Float32Array, l: Float32Array, off: number, villager: boolean): void {
    const s = 10;
    this.poseHeadPiece(frame, l, off + HEAD_SLOT * 12, this.beta ? s : -s, -s, this.beta ? -s : s, s * 0.5 - 4 - (villager ? 1.875 : 0), 0);
  }

  /** A flat item (the sprite: x −0.5..0.5, y 0..1): x' = 10x, y' = −10y − 7.125, z' = 4.375 px (villagers 1.875 higher). */
  private poseHeadItem(frame: Float32Array, l: Float32Array, off: number, villager: boolean): void {
    this.poseHeadPiece(frame, l, off + HEAD_SLOT * 12, 10, -10, -10, -7.125 - (villager ? 1.875 : 0), 4.375);
  }

  /** The child head's frame: root · T(0, 0.5) · S(0.7 / headScale) · root⁻¹ · head (the child head already carries the
   * model's own layout). */
  private babyHeadFrame(frame: Float32Array, baby: NonNullable<ModelDef['baby']>): void {
    const hs = typeof baby.headScale === 'number' ? baby.headScale : baby.headScale[1];
    const adj = identity(this.tmpA);
    adj[0] = adj[5] = adj[10] = 0.7 / hs;
    adj[7] = 0.5;
    invert(this.tmpB, this.root);
    mul(this.tmpD, adj, this.tmpB);
    mul(this.tmpB, this.root, this.tmpD);
    mul(this.tmpD, this.tmpB, frame);
    frame.set(this.tmpD);
  }

  /** A head piece in the head frame: diag(sx, sy, sz) and translate(0, ty, tz) px, written at bone offset `at`. */
  private poseHeadPiece(frame: Float32Array, l: Float32Array, at: number, sx: number, sy: number, sz: number, ty: number, tz: number): void {
    l[0] = sx;
    l[5] = sy;
    l[10] = sz;
    l[7] = ty;
    l[11] = tz;
    mul(this.tmpB, frame, l);
    this.bones.set(this.tmpB, at);
  }

  /** ModelDragonHead inside the skull frame: T(0, −0.374375 blocks) · S(0.75), all × 1.1875; its jaw: the pivot (0, 4, −8)
   * in the head, rotX((sin(limbSwing·π·0.2) + 1)·0.2). */
  private poseDragonSkull(frame: Float32Array, l: Float32Array, off: number, swing: number, villager: boolean): void {
    const k = SKULL_SCALE;
    l[0] = l[5] = l[10] = k * 0.75;
    l[7] = -k * 0.374375 * 16 - (villager ? k : 0);
    mul(this.tmpB, frame, l);
    this.bones.set(this.tmpB, off + SKULL_SLOT * 12);
    partLocal(this.tmpD, 0, 4, -8, (Math.sin(swing * Math.PI * 0.2) + 1) * 0.2, 0, 0);
    mul(this.tmpA, this.tmpB, this.tmpD);
    this.bones.set(this.tmpA, off + (SKULL_SLOT + 1) * 12);
  }

  /** LayerSnowmanHead: a pumpkin unless the wire says it was sheared off (1.12.2 snow golems only). */
  private snowPumpkin(d: Drawn): boolean {
    return d.look?.model === 'snow_golem' && !d.invisible && !d.v.has('sheared', 'no_pumpkin', 'nopumpkin');
  }

  /** The pumpkin on a snow golem's head: translate(0, −0.34375) · rotY(180°) · scale(0.625, −0.625, −0.625), the cube
   * centred (as poseHead's block, 5.5 px up instead of 4). */
  private poseSnowPumpkin(e: ModelEntry, off: number): void {
    const hi = e.built.index.head;
    if (hi == null) return;
    const l = identity(this.tmpA);
    l[0] = -10;
    l[5] = -10;
    l[10] = 10;
    l[7] = 5 - 5.5;
    mul(this.tmpB, e.skel.partMatrix(hi), l);
    this.bones.set(this.tmpB, off + HEAD_SLOT * 12);
  }

  /** LayerMooshroomMushroom: adult mooshrooms carry three red mushrooms (two on the back, one on the head). */
  private mushrooms(d: Drawn): boolean {
    return d.look?.texture === 'entity/cow/mooshroom' && !d.baby && !d.invisible;
  }

  /**
   * The mushrooms as crossed sprites (the block/cross model: two diagonal planes from 0.8 to 15.2 px), each plane one bone:
   * in the model root frame (blocks) scale(1, −1, 1) · translate(0.2, 0.35, 0.5) · rotY(42°), then translate(−0.5, −0.5, 0.5)
   * for the first and translate(0.1, 0, −0.6) · rotY(42°) · translate(−0.5, −0.5, 0.5) for the second; the third on the head
   * scale(1, −1, 1) · translate(0, 0.7, −0.2) · rotY(12°) · translate(−0.5, −0.5, 0.5); each block then rotY(90°).
   */
  private poseMushrooms(e: ModelEntry, off: number): void {
    const hi = e.built.index.head;
    const blocks = identity(this.tmpC);
    blocks[0] = blocks[5] = blocks[10] = 16; // root/part frames are in pixels; vanilla's layer works in blocks
    const frames: Array<[ArrayLike<number>, number[]]> = [];
    const back = mul(new Float32Array(12), this.root, blocks);
    const head = hi != null ? mul(new Float32Array(12), e.skel.partMatrix(hi), blocks) : null;
    const a = (42 * Math.PI) / 180, b = (12 * Math.PI) / 180;
    // [frame, chain of (tx, ty, tz, rotY) steps]
    // BlockModelRenderer.renderModelBrightness turns the block 90° about y before drawing it (which centres it here).
    const q = Math.PI / 2;
    frames.push([back, [0.2, 0.35, 0.5, a, -0.5, -0.5, 0.5, q]]);
    frames.push([back, [0.2, 0.35, 0.5, a, 0.1, 0, -0.6, a, -0.5, -0.5, 0.5, q]]);
    if (head) frames.push([head, [0, 0.7, -0.2, b, -0.5, -0.5, 0.5, q]]);
    let slot = MUSHROOM_SLOT;
    for (const [frame, steps] of frames) {
      // scale(1, −1, 1) first.
      const m = identity(this.tmpA);
      m[5] = -1;
      let cur = mul(new Float32Array(12), frame, m);
      for (let s = 0; s < steps.length; s += 4) {
        compose(this.tmpD, steps[s], steps[s + 1], steps[s + 2], steps[s + 3], 0, 1, 1, 1, 0, 0, 0);
        cur = mul(new Float32Array(12), cur, this.tmpD);
      }
      // The two cross planes in block space: centre (0.5, y, 0.5), along (±1, 0, 1) × 14.4/16 / √2·√2, 1 tall.
      for (const dir of [1, -1]) {
        const p = identity(this.tmpA);
        const w = 14.4 / 16;
        p[0] = w * dir;
        p[8] = w;
        p[2] = -dir * 0.01;
        p[10] = 0.01;
        p[3] = 0.5;
        p[11] = 0.5;
        mul(this.tmpB, cur, p);
        this.bones.set(this.tmpB, off + slot * 12);
        slot++;
      }
    }
  }

  /** LayerElytra: in the body's root frame, translate(0, 0, 0.125) (2 px back); ModelElytra's wings at (±5, h, 0) turned
   * (x 15°, z ∓15°; sneaking x 40°, z ∓45°, y ±5°, h = 3); a child's at scale(0.5) · translate(0, 24 px, −1.6 px). */
  private poseElytra(d: Drawn, off: number, sneaking: boolean, root: ArrayLike<number>): void {
    const pre = identity(this.tmpA);
    if (d.baby) {
      pre[0] = pre[5] = pre[10] = 0.5;
      pre[7] = 12;
      pre[11] = 2 - 0.8; // translate(0, 0, 2) · scale(0.5) · translate(0, 24, −1.6)
    } else pre[11] = 2;
    mul(this.tmpB, root, pre);
    const rx = sneaking ? 0.6981317 : 0.2617994, rz = sneaking ? -0.7853982 : -0.2617994, ry = sneaking ? 0.08726646 : 0, h = sneaking ? 3 : 0;
    partLocal(this.tmpD, 5, h, 0, rx, ry, rz);
    mul(this.tmpC, this.tmpB, this.tmpD);
    this.bones.set(this.tmpC, off + ELYTRA_SLOT * 12);
    partLocal(this.tmpD, -5, h, 0, rx, -ry, -rz);
    mul(this.tmpC, this.tmpB, this.tmpD);
    this.bones.set(this.tmpC, off + (ELYTRA_SLOT + 1) * 12);
  }

  /** A held item in the right (or left) hand: arm matrix · hand offset (pixels, arm space) · item orientation · size. */
  private poseHeld(d: Drawn, e: ModelEntry, off: number, left: boolean): void {
    const arm = e.built.index[left ? 'leftArm' : 'rightArm'];
    if (arm == null) return;
    const am = e.skel.partMatrix(arm);
    const l = this.tmpA, out = this.tmpB;
    const item = left ? d.heldOff! : d.held!, tool = left ? d.heldOffTool : d.heldTool;
    // Arm space: pixels, y down the arm, −z forward; the fist is around (∓1, 10, 0). Item geometry is y-up (0..1).
    heldBasis(l, HELD_POSES[item.kind === 'cube' ? 'block' : tool ? 'tool' : 'flat'], left ? -1 : 1);
    mul(out, am, l);
    this.bones.set(out, off + (left ? OFF_SLOT : HELD_SLOT) * 12);
  }


  /**
   * Minecart contents: the block inside, turned with the cart. 1.12.2 RenderMinecart: translate(0, 0.375) · rotY(180 − yaw)
   * · scale(0.75) · translate(−0.5, (offset − 8)/16, 0.5) · (renderBlockBrightness) rotY(90°), the display offset 6 (chest 8,
   * hopper 1): a 0.75 block turned rotY(270 − yaw), its bottom 0.375 + 0.75·(offset − 8)/16 up.
   */
  private poseContents(d: Drawn, off: number, yaw: number): void {
    if (d.look?.model !== 'minecart') return;
    if (this.beta) compose(this.tmpA, d.x, d.y + 0.3, d.z, (-yaw * Math.PI) / 180, 0, 0.6, 0.6, 0.6, 0, 0, 0);
    else {
      const j = d.cartBlock === 54 ? 8 : d.cartBlock === 154 ? 1 : 6;
      compose(this.tmpA, d.x, d.y + 0.375 + (0.75 * (j - 8)) / 16, d.z, ((270 - yaw) * Math.PI) / 180, 0, 0.75, 0.75, 0.75, 0, 0, 0);
    }
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
    this.resetDraws();
    this.visibleBots.length = 0;
    this.ghostMap = opts.ghosts ?? null;
    this.ghostSelected = opts.selected ?? null;
    const v = this.beginView(dim, cam, opts, zoom);
    const want = new Set<string>();
    for (const d of this.list) this.showOne(d, v, want);
    if (opts.entities) this.pushParticles(dim);
    this.lightning.showFor(dim);
    this.commitDraws();
    this.drawnCount = v.n;
    this.botModelCount = v.botModels;
    this.syncEntityLabels(want, opts.labels);
    for (const [name, o] of this.botObjs) this.syncBotObj(name, o, dim, opts);
  }

  private resetDraws(): void {
    for (const e of this.models.values()) {
      e.opaque.reset();
      e.translucent?.reset();
      e.armor?.reset();
    }
    this.sprites.reset();
    this.cubes.reset();
    this.shadowDraw.reset();
    for (const s of this.skullDraws.values()) s.reset();
    this.elytraDraw?.reset();
  }

  private commitDraws(): void {
    for (const e of this.models.values()) {
      e.opaque.commit();
      e.translucent?.commit();
      e.armor?.commit();
    }
    this.boxModel.opaque.commit();
    this.sprites.commit();
    this.cubes.commit();
    this.shadowDraw.commit();
    for (const s of this.skullDraws.values()) s.commit();
    this.elytraDraw?.commit();
  }

  /** Fills the reused view state for one `showFor`. */
  private beginView(dim: number, cam: THREE.Vector3, opts: EntityShowOptions, zoom: number): ViewState {
    const v = this.view;
    v.dim = dim;
    v.cam = cam;
    v.zoom = zoom;
    v.bots = opts.bots;
    v.entities = opts.entities;
    v.labels = opts.labels;
    v.far = (opts.drawDistance ?? 1e9) + 8;
    v.botDetail = this.detailDistance > 0 ? Math.max(this.detailDistance, BOT_DETAIL_MIN) : 0;
    v.cut = opts.cut ?? null;
    v.centre = opts.centre ?? null;
    v.drops = opts.drops !== false;
    v.frustum = opts.frustum ?? null;
    v.n = v.boxes = v.botModels = 0;
    return v;
  }

  /** One drawn entity in this view: skipped, a full model, or a LOD box (and its label). */
  private showOne(d: Drawn, v: ViewState, want: Set<string>): void {
    d.detail = null;
    if (d.parent) {
      this.showRider(d, v.dim);
      return;
    }
    if (!this.wanted(d, v)) return;
    const real = Math.hypot(d.x - v.cam.x, d.y - v.cam.y, d.z - v.cam.z);
    if (!this.inRange(d, v, real)) return;
    const dist = real * v.zoom;
    const near = this.isNear(d, v, dist);
    // Far boxes are a few pixels each: past the cap (a zoomed-out map over a mob farm) the rest are skipped, bots never.
    if (!near && !d.bot && !d.display && ++v.boxes > MAX_FAR_BOXES) return;
    this.drawOne(d, v, near, dist, want);
  }

  /** A rider shows with its body's detail. */
  private showRider(d: Drawn, dim: number): void {
    if (d.parent!.detail && d.posed && d.dim === dim && this.bundle) {
      d.detail = true;
      this.pushDetail(d, 0);
    }
  }

  /** In this view's dimension, of a kind it shows, not long dead, not a hidden drop. */
  private wanted(d: Drawn, v: ViewState): boolean {
    if (d.dim !== v.dim) return false;
    if (d.bot ? !v.bots : !v.entities) return false;
    if (d.motion.deadAt && this.now - d.motion.deadAt >= DEATH_GONE_MS) return false;
    return !(d.drop && !v.drops);
  }

  /** Within the draw distance, not in the cut-away storey, not outside the frustum. */
  private inRange(d: Drawn, v: ViewState, real: number): boolean {
    if ((v.centre ? Math.hypot(d.x - v.centre.x, d.z - v.centre.z) : real) > v.far) return false;
    if (cutAway(v.cut, d.x, d.y, d.z)) return false;
    if (!v.frustum) return true;
    const sphere = this.cullSphere;
    sphere.center.set(d.x, d.y + d.h * 0.5, d.z);
    sphere.radius = Math.max(d.w, d.h) + 0.5;
    return v.frustum.intersectsSphere(sphere);
  }

  /** Drawn as a full model: within the detail distance (bots further), posed, and with textures. */
  private isNear(d: Drawn, v: ViewState, dist: number): boolean {
    return !(dist > (d.bot ? v.botDetail : this.detailDistance) || !d.posed || !this.bundle);
  }

  private drawOne(d: Drawn, v: ViewState, near: boolean, dist: number, want: Set<string>): void {
    if (d.bot) this.visibleBots.push(d);
    v.n++;
    d.detail = near;
    if (near && d.bot) v.botModels++;
    this.pushShown(d, v.cut, dist);
    if (!d.bot && !d.display && v.labels && d.ent && !d.invisible) this.entityLabel(d, d.ent, dist, want);
  }

  /** Its instances (dimmed below a level cut like the blocks there). */
  private pushShown(d: Drawn, cut: ViewState['cut'], dist: number): void {
    const bright = d.bright;
    if (cut && cut.dimBelow !== null && d.y < cut.dimBelow) d.bright = bright * 0.55;
    if (d.detail) this.pushDetail(d, dist);
    else if (!d.invisible) this.pushBox(d);
    d.bright = bright;
  }

  /** Keeps the entity labels this view wants; the rest are dropped (labels on) or hidden. */
  private syncEntityLabels(want: Set<string>, labels: boolean): void {
    for (const [k, l] of this.entLabels) {
      if (want.has(k)) l.sprite.visible = true;
      else if (labels) {
        this.group.remove(l.sprite);
        this.labels.release(l.sprite);
        this.entLabels.delete(k);
      } else l.sprite.visible = false;
    }
  }

  /** A bot's ring and name label in this view. */
  private syncBotObj(name: string, o: BotObj, dim: number, opts: EntityShowOptions): void {
    const d = this.drawn.get(name);
    o.root.visible = !!d && opts.bots && d.dim === dim && !cutAway(opts.cut ?? null, d.x, d.y, d.z);
    if (d) o.root.position.set(d.x, d.y, d.z);
    if (o.label) o.label.visible = opts.botLabels ?? opts.labels;
    o.ring.visible = !!opts.selected && (o.root.userData.name === opts.selected || o.root.userData.short === opts.selected);
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
    this.ghostFlags(d, i, this.boxModel.opaque);
    this.boxModel.opaque.push(i);
  }

  // ------------------------------------------------------------------------------------------------------- silhouettes

  private ghostMap: ReadonlyMap<string, number> | null = null;
  private ghostSelected: string | null = null;

  /** Bots and players in `dim` within `max` blocks of `cam` (not invisible): what a view tests for occlusion. */
  ghostCandidates(dim: number, cam: THREE.Vector3, max: number): GhostCandidate[] {
    const out: GhostCandidate[] = [];
    for (const d of this.list) {
      if (d.dim !== dim || d.invisible || d.display || d.motion.deadAt) continue;
      if (!d.bot && d.ent?.kind !== 'player') continue;
      if (Math.hypot(d.x - cam.x, d.y - cam.y, d.z - cam.z) > max) continue;
      out.push({ key: d.key, x: d.x, y: d.y, z: d.z });
    }
    return out;
  }

  /** Flags the body's base instance for the silhouette pass (when this view found it hidden) and makes the draw's ghost twin. */
  private ghostFlags(d: Drawn, i: InstanceData, draw: InstancedDraw): void {
    const a = this.ghostMap?.get(d.key) ?? 0;
    if (a < 0.02 || d.invisible || (!d.bot && d.ent?.kind !== 'player')) return;
    const sel = this.ghostSelected;
    const selected = d.bot && !!sel && (d.key === sel || this.botObjs.get(d.key)?.root.userData.short === sel);
    i.flags |= GHOST_FLAG | (d.bot ? (selected ? GHOST_SELECTED : 0) : GHOST_PLAYER);
    i.flags += Math.max(1, Math.min(15, Math.round(a * 15))) * GHOST_LEVEL;
    const g = draw.ghostMesh(this.mats.ghost);
    if (g.created) this.group.add(g.mesh);
  }

  private rect(key: string): [number, number, number, number] | null {
    const p = this.bundle?.pack;
    const r = p?.entity.rects[key];
    if (!p || !r) return null;
    return [r[0] / p.entity.width, r[1] / p.entity.height, r[2] / p.entity.width, r[3] / p.entity.height];
  }

  private pushDetail(d: Drawn, dist: number): void {
    if (this.castsShadow(d, dist)) this.pushShadow(d);
    if (d.kind === 'model') this.pushModel(d);
    else this.pushObject(d);
  }

  /** No blob under spawner display mobs, hanging entities, riders or the invisible. */
  private castsShadow(d: Drawn, dist: number): boolean {
    return this.shadows && dist < this.detailDistance && !d.display && !d.hang && !d.invisible && !d.parent;
  }

  private pushShadow(d: Drawn): void {
    const r = d.kind === 'item' ? 0.15 : Math.min(3, Math.max(0.25, d.w * 0.5 + 0.1));
    this.shadowDraw.push(d.x, d.y, d.z, r * deathScale(d.motion, this.now), d.motion.deadAt ? 0.2 : 0.45);
  }

  /** A posed model: its body and layers, what it wears and holds, elytra and fire (a box without its texture). */
  private pushModel(d: Drawn): void {
    const e = d.model!, look = d.look!;
    const tex = this.rect(look.texture);
    if (!tex) return this.pushBox(d);
    // Invisible (0x20): no body, but armour and held items still show (vanilla draws them).
    if (!d.invisible) this.pushBody(d, e, look, tex);
    if (e.built.parts.length <= CONTENT_SLOT) {
      this.pushWorn(d, e);
      this.pushModelExtras(d, e);
    }
    if (d.elytra && e.built.parts.length <= ELYTRA_SLOT) this.pushElytra(d);
    if (d.burning) this.pushFire(d);
  }

  private pushBody(d: Drawn, e: ModelEntry, look: Look, tex: [number, number, number, number]): void {
    const i = this.base(d, 0, 0);
    i.tex = tex;
    tint(i, look.tint, d.bright);
    if (!look.translucent) this.ghostFlags(d, i, e.opaque);
    (look.translucent ? this.translucentOf(e) : e.opaque).push(i);
    if (look.layers) for (const l of look.layers) this.pushLayer(d, e, l);
  }

  private pushLayer(d: Drawn, e: ModelEntry, l: NonNullable<Look['layers']>[number]): void {
    const lt = this.rect(l.texture);
    if (!lt) return;
    const li = this.base(d, 0, l.layer ? (e.built.layers[l.layer] ?? 0) : 0);
    li.tex = lt;
    tint(li, l.tint, d.bright);
    if (l.emissive) li.flags |= F_EMISSIVE;
    if (l.scroll) li.flags |= F_SCROLL;
    (l.translucent ? this.translucentOf(e) : e.opaque).push(li);
  }

  /** Armour, hand items and the head item (a skull, a block, a flat item, or a snow golem's pumpkin). */
  private pushWorn(d: Drawn, e: ModelEntry): void {
    if (d.armor) this.pushArmor(d, e);
    if (d.held) this.pushItem(d, d.held, HELD_SLOT, false, d.heldGlint);
    if (d.heldOff) this.pushItem(d, d.heldOff, OFF_SLOT, false, d.heldOffGlint);
    if (d.skull >= 0) this.pushSkull(d);
    else if (d.headBlock) this.pushItem(d, d.headBlock, HEAD_SLOT, false, d.headGlint);
    else if (d.headItem) this.pushItem(d, d.headItem, HEAD_SLOT, false, d.headGlint);
    else if (this.snowPumpkin(d)) {
      const l = this.pumpkinLook();
      if (l) this.pushItem(d, l, HEAD_SLOT, false);
    }
  }

  /** Mooshroom mushrooms, minecart contents, an enderman's block, an iron golem's rose. */
  private pushModelExtras(d: Drawn, e: ModelEntry): void {
    const roomy = e.built.parts.length <= MUSHROOM_SLOT;
    if (this.mushrooms(d) && roomy) this.pushPlanes(d, this.mushroomLook(), 6);
    if (d.item && d.look?.model === 'minecart') this.pushItem(d, d.item, CONTENT_SLOT, false);
    if (d.carried && roomy) this.pushCarried(d, d.carried);
    if (this.holdsRose(d) && roomy) this.pushPlanes(d, this.roseLook(), 2);
  }

  /** `n` crossed-plane sprites from the first mushroom slot. */
  private pushPlanes(d: Drawn, l: ItemLook | null, n: number): void {
    if (l) for (let s = 0; s < n; s++) this.pushItem(d, l, MUSHROOM_SLOT + s, false);
  }

  /** LayerHeldBlock: a cube, or a plant as two crossed planes. */
  private pushCarried(d: Drawn, carried: ItemLook): void {
    if (carried.kind === 'cube') this.pushItem(d, carried, CONTENT_SLOT, false);
    else this.pushPlanes(d, carried, 2);
  }

  /** LayerIronGolemFlower: for 400 ticks from when the wire first said so. */
  private holdsRose(d: Drawn): boolean {
    return !!d.roseAt && this.now - d.roseAt < 400 * 50;
  }

  /** A dropped stack: 1–4 (b1.8.1) / 1–5 copies by its size, b1.8.1 ones as billboards. */
  private pushDrop(d: Drawn, item: ItemLook): void {
    const n = copies(d.count, this.version === 'b1.8.1');
    for (let k = 0; k < n; k++) this.pushItem(d, item, k, this.version === 'b1.8.1', d.itemGlint);
  }

  /** Drops, falling blocks, fireworks, orbs, paintings, item frames; then fire. */
  private pushObject(d: Drawn): void {
    if (d.kind === 'item' && d.item) this.pushDrop(d, d.item);
    else if ((d.kind === 'block' || d.kind === 'sprite') && d.item) this.pushItem(d, d.item, 0, d.kind === 'sprite');
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
      // LayerArmorBase: the piece (1.12.2 leather tinted by its dye, default 0xA06540), then leather's untinted overlay, then
      // the glint once over the piece when it is enchanted.
      let glint = !!d.armorGlint?.[k];
      for (const pass of armorPasses(m, piece, beta, d.armorDye?.[k])) {
        const tex = this.rect(pass.texture);
        if (!tex) continue;
        const i = this.base(d, 0, k + 1);
        i.tex = tex;
        if (pass.tint != null) tint(i, pass.tint, d.bright);
        if (glint) {
          i.flags |= GLINT_FLAG;
          glint = false;
          this.glintTwin(e.armor!, this.mats.glintModel);
        }
        e.armor!.push(i);
      }
    });
  }

  private pumpkinLook(): ItemLook | null {
    if (!this.bundle) return null;
    const k = 'pumpkin';
    if (!this.heldCache.has(k)) this.heldCache.set(k, itemLookById(this.version, this.bundle.pack, 86, 0));
    return this.heldCache.get(k) ?? null;
  }

  private mushroomLook(): ItemLook | null {
    const p = this.bundle?.pack;
    if (!p || p.version !== '1.12.2') return null;
    const t = p.blocks.tiles['blocks/mushroom_red'];
    return t ? { kind: 'sprite', atlas: 'blocks', cell: t[1] * p.blocks.columns + t[0], tint: 0 } : null;
  }

  /** The iron golem's poppy (Blocks.RED_FLOWER's default state: blocks/flower_rose). */
  private roseLook(): ItemLook | null {
    const p = this.bundle?.pack;
    if (!p || p.version !== '1.12.2') return null;
    const t = p.blocks.tiles['blocks/flower_rose'];
    return t ? { kind: 'sprite', atlas: 'blocks', cell: t[1] * p.blocks.columns + t[0], tint: 0 } : null;
  }

  /** Makes sure a draw's glint twin is in the scene (it draws the instances flagged GLINT_FLAG). */
  private glintTwin(draw: InstancedDraw, material: THREE.Material): void {
    if (!this.mats.shared.uGlint.value) return;
    const g = draw.glintMesh(material);
    if (g.created) this.group.add(g.mesh);
  }

  /** A worn skull: one instance of its skull geometry, textured by its type. */
  private pushSkull(d: Drawn): void {
    const kind = skullKind(d.skull);
    const tex = this.rect(SKULL_TEXTURES[d.skull] ?? SKULL_TEXTURES[0]);
    if (!tex) return;
    let draw = this.skullDraws.get(kind);
    if (!draw) {
      const g = modelGeometry(buildSkull(kind));
      this.extraGeos.push(g);
      draw = new InstancedDraw(g, this.mats.model, false, `skull ${kind}`);
      this.group.add(draw.mesh);
      this.skullDraws.set(kind, draw);
    }
    const i = this.base(d, SKULL_SLOT, 0);
    i.tex = tex;
    draw.push(i);
  }

  private pushElytra(d: Drawn): void {
    const tex = this.rect('entity/elytra');
    if (!tex) return;
    if (!this.elytraDraw) {
      const g = modelGeometry(buildElytra());
      this.extraGeos.push(g);
      this.elytraDraw = new InstancedDraw(g, this.mats.model, false, 'elytra');
      this.group.add(this.elytraDraw.mesh);
    }
    const i = this.base(d, ELYTRA_SLOT, 0);
    i.tex = tex;
    if (d.elytraGlint && this.mats.shared.uGlint.value) {
      i.flags |= GLINT_FLAG;
      this.glintTwin(this.elytraDraw, this.mats.glintModel);
    }
    this.elytraDraw.push(i);
  }

  private pushItem(d: Drawn, l: ItemLook, slot: number, billboard: boolean, glint = false): void {
    const p = this.bundle!.pack;
    const i = this.base(d, slot, 0);
    const shiny = glint && !!this.mats.shared.uGlint.value;
    if (shiny) i.flags |= GLINT_FLAG;
    if (l.kind === 'cube') {
      i.tex = [0, 0, 1, 1];
      i.flags |= F_BLOCKS;
      i.faces = l.faces;
      i.tintMask = l.tint ? l.tintMask : 0;
      tint(i, l.tint || undefined, d.bright);
      if (shiny) this.glintTwin(this.cubes, this.mats.glintTile);
      this.cubes.push(i);
      return;
    }
    if (shiny) this.glintTwin(this.sprites, this.mats.glintTile);
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
    if (d.item) this.pushItem(d, d.item, 1, false, d.itemGlint);
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

  /** Bots drawn as full (posed, skinned) models in the last `showFor`. */
  get botModels(): number {
    return this.botModelCount;
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

  /** World height of the name label the last `showFor` left visible over a bot (by name) or a player (entity key); 0 = none.
   * Chat bubbles sit above it. */
  labelLift(key: string, bot: boolean): number {
    if (bot) {
      const o = this.botObjs.get(key);
      return o?.label && o.label.visible && o.root.visible ? o.label.scale.y : 0;
    }
    const l = this.entLabels.get(key);
    return l && l.sprite.visible ? l.sprite.scale.y : 0;
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
    for (const s of this.skullDraws.values()) s.dispose();
    this.skullDraws.clear();
    this.elytraDraw?.dispose();
    for (const g of this.extraGeos) g.dispose();
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

/** EntityCreeper.getCreeperFlashIntensity: the fuse's progress over fuseTime − 2 = 28 ticks since it began (0 = not fusing). */
function creeperFlash(fuseAt: number, now: number): number {
  return fuseAt ? Math.min(1, Math.max(0, (now - fuseAt) / (28 * 50))) : 0;
}

/** At or above a level cut's floor within its radius (the x-rayed storey). */
function cutAway(cut: { x: number; y: number; z: number; r: number } | null, x: number, y: number, z: number): boolean {
  return !!cut && Math.floor(y + 0.01) >= cut.y && Math.hypot(x - cut.x, z - cut.z) <= cut.r;
}

/** RenderCreeper.preRenderCallback: the swell scale(h, i, h) from the flash intensity `f`. */
function creeperSwell(sc: [number, number, number], f: number): void {
  const g = 1 + Math.sin(f * 100) * f * 0.01, f4 = Math.min(1, Math.max(0, f)) ** 4;
  const h = (1 + f4 * 0.4) * g, i = (1 + f4 * 0.1) / g;
  sc[0] *= h;
  sc[1] *= i;
  sc[2] *= h;
}

function isScreaming(d: Drawn): boolean {
  return d.look?.model === 'enderman' && d.v.has('screaming');
}

/** RenderZombieVillager: a converting one shakes its yaw by cos(ticks · 3.25) · π/4 degrees. */
function convertingShake(d: Drawn, now: number): number {
  return d.look?.model === 'zombie_villager' && d.v.has('converting') ? Math.cos((now / 50) * 3.25) * Math.PI * 0.25 : 0;
}

/** The entity's (or bot's) wire flags. */
function poseFlags(d: Drawn): number {
  return d.ent?.flags ?? (d.botView?.dto as { flags?: number } | undefined)?.flags ?? 0;
}

/** The wire's own head yaw (1.12.2 head look), or null. */
function wireHeadYaw(d: Drawn): number | null {
  return d.ent?.dto.headYaw ?? (d.botView?.dto as { headYaw?: number | null } | undefined)?.headYaw ?? null;
}

/** Head yaw: the wire's own when it sends one, else the look yaw; smoothed toward it. */
function turnHead(d: Drawn, body: number): void {
  const m = d.motion;
  const lookYaw = wireHeadYaw(d) ?? (d.botView ? d.botView.yaw : d.ent?.yaw ?? body);
  m.head = Number.isNaN(m.head) ? lookYaw : m.head + wrapDeg(lookYaw - m.head) * 0.35;
}

/** A standard normal sample (Box–Muller), for java.util.Random.nextGaussian jitter. */
function gaussian(): number {
  return Math.sqrt(-2 * Math.log(1 - Math.random())) * Math.cos(2 * Math.PI * Math.random());
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

/** A held item's local transform in arm space into `l`. Columns: where the item's x (right), y (up) and z (normal) axes go,
 * times its size; the left hand (mx −1) mirrors across x (and flips the item's right axis so the basis stays right-handed).
 * Then the pre-translate in item units (centre the cube / put the grip at the fist). */
function heldBasis(l: Float32Array, h: (typeof HELD_POSES)['block'], mx: number): void {
  for (let r = 0; r < 3; r++) {
    const sx = r === 0 ? mx : 1;
    l[r * 4] = h.right[r] * h.size * sx * mx;
    l[r * 4 + 1] = h.up[r] * h.size * sx;
    l[r * 4 + 2] = h.normal[r] * h.size * sx;
    l[r * 4 + 3] = h.at[r] * sx;
  }
  const px = h.pre[0] * mx;
  l[3] += l[0] * px + l[1] * h.pre[1] + l[2] * h.pre[2];
  l[7] += l[4] * px + l[5] * h.pre[1] + l[6] * h.pre[2];
  l[11] += l[8] * px + l[9] * h.pre[1] + l[10] * h.pre[2];
}

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

/** Whether an event carries a position. */
function hasPos(ev: EntityEvent): ev is EntityEvent & { x: number; y: number; z: number } {
  return ev.x != null && ev.y != null && ev.z != null;
}

/** Starts the death fall (once). */
function startDeath(m: Motion, now: number): void {
  if (m.deadAt) return;
  m.deadAt = now;
  m.hurtAt = now;
}

/** The main hand holds a tool (drawn like one): by the wire's item, else the held item's name. */
function mainHandTool(eq: Equipment | null | undefined, mainName: string | null): boolean {
  if (eq?.main) return isHandheld(itemName(eq.main[0]) ?? '');
  return !!mainName && isHandheld(parseHeld(mainName).name);
}

function holdsBow(eq: Equipment | null | undefined, mainName: string | null): boolean {
  return eq?.main?.[0] === 261 || (!!mainName && parseHeld(mainName).name === 'bow');
}

/** An entity's kind; objects by their type (or wire type name). */
function entityKind(e: EntityView, beta: boolean): string {
  if (e.kind !== 'object') return e.kind;
  return objectName(e.type, beta) ?? (e.name ? objectKind(e.name) : 'object');
}

/** Experience orbs, by the wire's kind names. */
function isOrb(kind: string): boolean {
  return kind === 'xp_orb' || kind === 'xporb' || kind === 'orb';
}

/** The wire fields an entity's look depends on: its kind, type, variant, item, name, age and equipment. */
function entityKindSig(e: EntityView): string {
  const dto = e.dto;
  return `${e.kind}|${e.type}|${dto.variant ?? ''}|${lookOf(dto) ?? ''}|${e.item}|${dto.damage ?? 0}|${e.name ?? ''}|${dto.baby ? 1 : 0}|${JSON.stringify(dto.equipment ?? null)}`;
}

/** …and its hanging, block, minecart, glint and carried-block fields. */
function entityExtraSig(dto: EntityDto): string {
  return `${dto.motive ?? ''}|${dto.facing ?? ''}|${dto.rotation ?? ''}|${dto.frameItem ?? ''}|${dto.block ?? ''}|${dto.minecartType ?? ''}|${dto.glint ? 1 : 0}|${dto.carried ?? ''}`;
}

/** A falling block's block: TNT when primed, else the wire's block, its item id, or sand (b1.8.1 type 71: gravel). */
function fallingBlockRef(e: EntityView, kind: string, beta: boolean): ItemRef {
  if (kind === 'tnt') return [46, 0];
  return e.dto.block ?? (e.item > 0 ? [e.item, e.dto.damage ?? 0] : [beta && e.type === 71 ? 13 : 12, 0]);
}

/** A creeper fusing (Mob16 state 1 / "fuse"/"ignited"). */
function isFusing(d: Drawn, e: EntityView): boolean {
  return e.kind === 'mob' && e.type === 50 && ((!d.v.isString && d.v.num === 1) || d.v.has('fuse', 'fusing', 'ignited'));
}

/**
 * Creeper fusing and primed TNT (fuse ticks): white flashes. RenderCreeper.getColorMultiplier: a white overlay of f·0.2 on
 * odd tenths of the flash intensity f (the fuse's progress over 28 ticks, from when the wire said it began);
 * RenderTNTPrimed: (1 − (fuse + 1)/100)·0.8 every other 5 ticks.
 */
function entityFlash(d: Drawn, e: EntityView, now: number): void {
  const fuse = isFusing(d, e);
  if (fuse && !d.fuseAt) d.fuseAt = now;
  else if (!fuse) d.fuseAt = 0;
  if (fuse) d.flash = creeperOverlay(creeperFlash(d.fuseAt, now));
  else d.flash = d.kind === 'block' && d.colour === 0xd04030 ? tntFlash(e.dto.fuse ?? 80 - (e.ageTicks % 80)) : 0;
}

function creeperOverlay(intensity: number): number {
  return Math.floor(intensity * 10) % 2 === 0 ? 0 : intensity * 0.2;
}

function tntFlash(fuseTicks: number): number {
  return Math.floor(fuseTicks / 5) % 2 === 0 ? Math.max(0, 1 - (fuseTicks + 1) / 100) * 0.8 : 0;
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

/** A scale matrix. */
function scaleMatrix(x: number, y: number, z: number): Float32Array {
  const m = identity(new Float32Array(12));
  m[0] = x;
  m[5] = y;
  m[10] = z;
  return m;
}

/** block/cross's two diagonal planes (0.8..15.2 px, turned 45°, rescaled) in a block-space frame ([0, 1]³), as two sprite
 * bones at `at` (floats) in `out`. */
export function crossPlanes(out: Float32Array, at: number, frame: ArrayLike<number>, tmp: Float32Array, tmp2: Float32Array): void {
  const w = 14.4 / 16;
  for (const [k, dir] of [[0, 1], [1, -1]] as const) {
    const p = identity(tmp);
    p[0] = w * dir;
    p[8] = w;
    p[2] = -dir * 0.01;
    p[10] = 0.01;
    p[3] = 0.5;
    p[11] = 0.5;
    mul(tmp2, frame, p);
    out.set(tmp2, at + k * 12);
  }
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
