/**
 * Story Mode tools, gear and drafted combat (docs/web/story-mode.md "Tools and gear", "Drafted combat"): the vocabulary, a
 * pawn's equipment, gear policies (the genre's Assign tab), material tool rules with the version's minimum tier per material
 * (the floor nobody can go below), the bulk gear op, and the helpers the web's editors and the mocks share.
 */
import { z } from 'zod';

/** [id, meta] (the same shape as `StoryItemKey` in story.ts, which imports this module). */
const StoryItemKey = z.tuple([z.number().int(), z.number().int()]);

// ------------------------------------------------------------------ vocabulary

export const STORY_TOOL_KINDS = ['pickaxe', 'axe', 'shovel', 'hoe', 'shears', 'sword'] as const;
export const StoryToolKind = z.enum(STORY_TOOL_KINDS);
export type StoryToolKind = z.infer<typeof StoryToolKind>;

/** Harvest tiers, lowest first (gold items harvest like wood). */
export const STORY_TIERS_HARVEST = ['wood', 'stone', 'iron', 'diamond'] as const;
export const StoryToolTier = z.enum(STORY_TIERS_HARVEST);
export type StoryToolTier = z.infer<typeof StoryToolTier>;

export const STORY_ENCHANTS = ['fortune', 'silk_touch', 'any'] as const;
export const StoryEnchantPref = z.enum(STORY_ENCHANTS);
export type StoryEnchantPref = z.infer<typeof StoryEnchantPref>;

export const STORY_FALLBACKS = ['plain', 'wait'] as const;
export const StoryToolFallback = z.enum(STORY_FALLBACKS);
export type StoryToolFallback = z.infer<typeof StoryToolFallback>;

export const STORY_ARMOR_MODES = ['none', 'drafted', 'always', 'night'] as const;
export const StoryArmorMode = z.enum(STORY_ARMOR_MODES);
export type StoryArmorMode = z.infer<typeof StoryArmorMode>;

export const STORY_WEAPON_PREFS = ['none', 'best', 'sword', 'axe', 'bow'] as const;
export const StoryWeaponPref = z.enum(STORY_WEAPON_PREFS);
export type StoryWeaponPref = z.infer<typeof StoryWeaponPref>;

export const STORY_GEAR_OPS = ['arm', 'armor', 'pickup', 'return', 'tools'] as const;
export const StoryGearOp = z.enum(STORY_GEAR_OPS);
export type StoryGearOp = z.infer<typeof StoryGearOp>;

// ------------------------------------------------------------------ state

/** One carried / worn item: durability left 0..1 (null = none), enchantments by name ("fortune": 3). */
export const StoryGearItem = z.object({
  item: StoryItemKey,
  name: z.string().nullish(),
  label: z.string().nullish(),
  kind: z
    .string()
    .default('other')
    .describe('pickaxe | axe | shovel | hoe | shears | sword | bow | helmet | chestplate | leggings | boots | other'),
  tier: z.string().nullish().describe('wood | stone | iron | gold | diamond | leather | chain'),
  durability: z.number().nullish().describe('left, 0..1 (null = the item has none)'),
  ench: z.record(z.string(), z.number()).default({}),
  slot: z.string().nullish().describe('armour: head | chest | legs | feet'),
});
export type StoryGearItem = z.infer<typeof StoryGearItem>;

export const StoryEquipment = z.object({
  held: StoryGearItem.nullish(),
  armor: z.array(StoryGearItem).default([]),
  tools: z.array(StoryGearItem).default([]),
  weapons: z.array(StoryGearItem).default([]),
});
export type StoryEquipment = z.infer<typeof StoryEquipment>;

export const StoryGearTask = z.object({
  id: z.union([z.string(), z.number()]),
  op: z.string().describe('arm | armor | pickup | return | tools | equip | restore | policy'),
  label: z.string().nullish(),
});
export type StoryGearTask = z.infer<typeof StoryGearTask>;

/** A gear policy (the Assign tab): tools by work, extra tool kinds, weapon, armour mode, food to carry. */
export const StoryPolicy = z.object({
  id: z.string(),
  name: z.string(),
  default: z.boolean().default(false),
  toolsByWork: z.boolean().default(true),
  tools: z.array(z.string()).default([]),
  weapon: z.string().default('best'),
  armor: z.string().default('drafted'),
  food: z.number().int().default(0),
  spare: z.boolean().default(false).describe('carry one spare of each tool kind; else extras go back to storage'),
});
export type StoryPolicy = z.infer<typeof StoryPolicy>;

/** A material's tool rule: a block name or `group:<id>`; `floor` is the version's minimum (read-only). */
export const StoryMaterialRule = z.object({
  material: z.string(),
  label: z.string().nullish(),
  tool: z.string(),
  tier: z.string().nullish().describe('the preferred minimum; null = just the floor'),
  enchant: z.string().nullish().describe('fortune | silk_touch | any'),
  fallback: z.string().default('plain').describe('plain | wait'),
  floor: z.string().nullish(),
});
export type StoryMaterialRule = z.infer<typeof StoryMaterialRule>;

/** The rules editor's catalog: groups first, then their member blocks (`group` set). */
export const StoryMaterialInfo = z.object({
  id: z.string(),
  label: z.string(),
  group: z.string().nullish(),
  tool: z.string().nullish().describe('the best tool; null = the hand is best'),
  floor: z.string().nullish().describe('the lowest tier that keeps the drop; null = the hand harvests it'),
  keepTool: z.string().nullish().describe('the tool that keeps the block itself (shears for leaves)'),
  members: z.array(z.string()).nullish(),
});
export type StoryMaterialInfo = z.infer<typeof StoryMaterialInfo>;

export const StorySpareTools = z.object({
  count: z.number().int().default(0),
  kinds: z.array(z.string()).default([]),
});
export type StorySpareTools = z.infer<typeof StorySpareTools>;

/** Enchanted tools known to be in a chest. */
export const StoryEnchantedStack = z.object({ item: StoryItemKey, ench: z.record(z.string(), z.number()).default({}) });
export type StoryEnchantedStack = z.infer<typeof StoryEnchantedStack>;

// ------------------------------------------------------------------ writes

const Name = z.string().min(1).max(64);
const PawnNames = z.array(Name).min(1).max(64);

export const StoryPolicySpec = z
  .object({
    id: z.string().min(1).max(64).optional(),
    name: z.string().trim().min(1).max(40),
    toolsByWork: z.boolean().optional(),
    tools: z.array(StoryToolKind).max(6).optional(),
    weapon: StoryWeaponPref.optional(),
    armor: StoryArmorMode.optional(),
    food: z.number().int().min(0).max(16).optional(),
    spare: z.boolean().optional(),
  })
  .strict();
export type StoryPolicySpec = z.infer<typeof StoryPolicySpec>;

/** POST /api/story/colonies/{id}/policies. */
export const StoryPoliciesRequest = z.discriminatedUnion('op', [
  z.object({ op: z.literal('set'), policy: StoryPolicySpec }).strict(),
  z.object({ op: z.literal('remove'), id: Name }).strict(),
  z.object({ op: z.literal('assign'), id: Name, pawns: PawnNames }).strict(),
]);
export type StoryPoliciesRequest = z.infer<typeof StoryPoliciesRequest>;

export const StoryPoliciesResponse = z.object({
  id: z.string().nullish(),
  removed: z.boolean().nullish(),
  pawns: z.array(z.string()).nullish(),
});
export type StoryPoliciesResponse = z.infer<typeof StoryPoliciesResponse>;

export const StoryMaterialRuleSpec = z
  .object({
    material: Name,
    tool: StoryToolKind.optional(),
    tier: StoryToolTier.nullable().optional(),
    enchant: StoryEnchantPref.nullable().optional(),
    fallback: StoryToolFallback.optional(),
  })
  .strict();
export type StoryMaterialRuleSpec = z.infer<typeof StoryMaterialRuleSpec>;

/** POST /api/story/colonies/{id}/material-rules (400 when the tier is below the material's floor). */
export const StoryMaterialRulesRequest = z.discriminatedUnion('op', [
  z.object({ op: z.literal('set'), rule: StoryMaterialRuleSpec }).strict(),
  z.object({ op: z.literal('remove'), material: Name }).strict(),
]);
export type StoryMaterialRulesRequest = z.infer<typeof StoryMaterialRulesRequest>;

export const StoryMaterialRulesResponse = z.object({
  rule: StoryMaterialRule.nullish(),
  removed: z.boolean().nullish(),
});
export type StoryMaterialRulesResponse = z.infer<typeof StoryMaterialRulesResponse>;

/** POST /api/story/colonies/{id}/gear: one op for the whole selection. */
export const StoryGearRequest = z
  .object({
    op: StoryGearOp,
    pawns: PawnNames,
    item: StoryItemKey.optional().describe('pickup: the item'),
    count: z.number().int().min(1).max(64).optional().describe('pickup: per pawn (default 1)'),
  })
  .strict();
export type StoryGearRequest = z.infer<typeof StoryGearRequest>;

export const StoryGearResult = z.object({
  pawn: z.string(),
  display: z.string().nullish(),
  got: z.array(z.string()).default([]),
  missing: z.array(z.string()).default([]),
});
export type StoryGearResult = z.infer<typeof StoryGearResult>;

export const StoryGearResponse = z.object({
  results: z.array(StoryGearResult).default([]),
  summary: z.string().default(''),
});
export type StoryGearResponse = z.infer<typeof StoryGearResponse>;

/** The 409 body of an attack order on a player or another bot without `confirm: true` (the bridge passes only `error`). */
export const StoryAttackConfirm = z.object({
  error: z.string().nullish(),
  confirm: z
    .object({ entity: z.number().int().nullish(), name: z.string().nullish(), kind: z.string().nullish() })
    .nullish(),
});
export type StoryAttackConfirm = z.infer<typeof StoryAttackConfirm>;

// ------------------------------------------------------------------ helpers (the web's editors and the mocks)

/** The rank of a harvest tier (wood 0 … diamond 3); null / unknown → -1. */
export function tierRank(t: string | null | undefined): number {
  return t ? (STORY_TIERS_HARVEST as readonly string[]).indexOf(t) : -1;
}

/** The tiers a rule may pick for a material: every one at or above its floor (the floor itself included). */
export function allowedTiers(floor: string | null | undefined): StoryToolTier[] {
  const f = Math.max(0, tierRank(floor));
  return STORY_TIERS_HARVEST.filter((_, i) => i >= f);
}

/** Why a tier is refused for a material ("iron_ore needs at least a stone pickaxe"), or null when it is allowed. */
export function tierProblem(
  material: string,
  tool: string | null | undefined,
  tier: string | null | undefined,
  floor: string | null | undefined,
): string | null {
  if (!tier || !floor) return null;
  if (tierRank(tier) >= tierRank(floor)) return null;
  const what = material.startsWith('group:') ? material.slice(6) : material;
  return `${what} needs at least a ${floor === 'wood' ? 'wooden' : floor} ${tool ?? 'pickaxe'}`;
}

/** "Wooden pickaxe", "Iron sword". */
export function toolLabel(kind: string, tier?: string | null): string {
  const t = tier === 'wood' ? 'wooden' : tier === 'gold' ? 'golden' : tier;
  const s = t ? `${t} ${kind}` : kind;
  return s.charAt(0).toUpperCase() + s.slice(1);
}

/** Item names by their id, for the tools/weapons/armour the sims and previews deal in (b1.8.1 ids, shared by 1.12.2). */
export const STORY_GEAR_ITEMS: ReadonlyArray<{ id: number; name: string; kind: string; tier: string; max: number }> = [
  { id: 268, name: 'wooden_sword', kind: 'sword', tier: 'wood', max: 59 },
  { id: 269, name: 'wooden_shovel', kind: 'shovel', tier: 'wood', max: 59 },
  { id: 270, name: 'wooden_pickaxe', kind: 'pickaxe', tier: 'wood', max: 59 },
  { id: 271, name: 'wooden_axe', kind: 'axe', tier: 'wood', max: 59 },
  { id: 272, name: 'stone_sword', kind: 'sword', tier: 'stone', max: 131 },
  { id: 273, name: 'stone_shovel', kind: 'shovel', tier: 'stone', max: 131 },
  { id: 274, name: 'stone_pickaxe', kind: 'pickaxe', tier: 'stone', max: 131 },
  { id: 275, name: 'stone_axe', kind: 'axe', tier: 'stone', max: 131 },
  { id: 267, name: 'iron_sword', kind: 'sword', tier: 'iron', max: 250 },
  { id: 256, name: 'iron_shovel', kind: 'shovel', tier: 'iron', max: 250 },
  { id: 257, name: 'iron_pickaxe', kind: 'pickaxe', tier: 'iron', max: 250 },
  { id: 258, name: 'iron_axe', kind: 'axe', tier: 'iron', max: 250 },
  { id: 276, name: 'diamond_sword', kind: 'sword', tier: 'diamond', max: 1561 },
  { id: 277, name: 'diamond_shovel', kind: 'shovel', tier: 'diamond', max: 1561 },
  { id: 278, name: 'diamond_pickaxe', kind: 'pickaxe', tier: 'diamond', max: 1561 },
  { id: 279, name: 'diamond_axe', kind: 'axe', tier: 'diamond', max: 1561 },
  { id: 283, name: 'golden_sword', kind: 'sword', tier: 'gold', max: 32 },
  { id: 284, name: 'golden_shovel', kind: 'shovel', tier: 'gold', max: 32 },
  { id: 285, name: 'golden_pickaxe', kind: 'pickaxe', tier: 'gold', max: 32 },
  { id: 286, name: 'golden_axe', kind: 'axe', tier: 'gold', max: 32 },
  { id: 290, name: 'wooden_hoe', kind: 'hoe', tier: 'wood', max: 59 },
  { id: 291, name: 'stone_hoe', kind: 'hoe', tier: 'stone', max: 131 },
  { id: 292, name: 'iron_hoe', kind: 'hoe', tier: 'iron', max: 250 },
  { id: 293, name: 'diamond_hoe', kind: 'hoe', tier: 'diamond', max: 1561 },
  { id: 294, name: 'golden_hoe', kind: 'hoe', tier: 'gold', max: 32 },
  { id: 359, name: 'shears', kind: 'shears', tier: 'iron', max: 238 },
  { id: 261, name: 'bow', kind: 'bow', tier: 'wood', max: 384 },
  { id: 298, name: 'leather_helmet', kind: 'helmet', tier: 'leather', max: 55 },
  { id: 299, name: 'leather_chestplate', kind: 'chestplate', tier: 'leather', max: 80 },
  { id: 300, name: 'leather_leggings', kind: 'leggings', tier: 'leather', max: 75 },
  { id: 301, name: 'leather_boots', kind: 'boots', tier: 'leather', max: 65 },
  { id: 302, name: 'chainmail_helmet', kind: 'helmet', tier: 'chain', max: 165 },
  { id: 303, name: 'chainmail_chestplate', kind: 'chestplate', tier: 'chain', max: 240 },
  { id: 304, name: 'chainmail_leggings', kind: 'leggings', tier: 'chain', max: 225 },
  { id: 305, name: 'chainmail_boots', kind: 'boots', tier: 'chain', max: 195 },
  { id: 306, name: 'iron_helmet', kind: 'helmet', tier: 'iron', max: 165 },
  { id: 307, name: 'iron_chestplate', kind: 'chestplate', tier: 'iron', max: 240 },
  { id: 308, name: 'iron_leggings', kind: 'leggings', tier: 'iron', max: 225 },
  { id: 309, name: 'iron_boots', kind: 'boots', tier: 'iron', max: 195 },
  { id: 310, name: 'diamond_helmet', kind: 'helmet', tier: 'diamond', max: 363 },
  { id: 311, name: 'diamond_chestplate', kind: 'chestplate', tier: 'diamond', max: 528 },
  { id: 312, name: 'diamond_leggings', kind: 'leggings', tier: 'diamond', max: 495 },
  { id: 313, name: 'diamond_boots', kind: 'boots', tier: 'diamond', max: 429 },
  { id: 314, name: 'golden_helmet', kind: 'helmet', tier: 'gold', max: 77 },
  { id: 315, name: 'golden_chestplate', kind: 'chestplate', tier: 'gold', max: 112 },
  { id: 316, name: 'golden_leggings', kind: 'leggings', tier: 'gold', max: 105 },
  { id: 317, name: 'golden_boots', kind: 'boots', tier: 'gold', max: 91 },
];

const GEAR_BY_ID = new Map(STORY_GEAR_ITEMS.map((g) => [g.id, g]));
export const gearItemInfo = (id: number) => GEAR_BY_ID.get(id) ?? null;

export const ARMOR_SLOT: Readonly<Record<string, string>> = {
  helmet: 'head',
  chestplate: 'chest',
  leggings: 'legs',
  boots: 'feet',
};

/** How good a weapon is (higher better): swords by tier, then axes, a bow last among melee choices; -1 = not a weapon. */
export function weaponScore(id: number): number {
  const g = gearItemInfo(id);
  if (!g) return -1;
  const t = { wood: 0, gold: 0, stone: 1, iron: 2, diamond: 3 }[g.tier as 'wood'] ?? 0;
  if (g.kind === 'sword') return 10 + t * 2;
  if (g.kind === 'axe') return 9 + t * 2;
  if (g.kind === 'bow') return 5;
  return -1;
}

/** How good an armour piece is (higher better); -1 = not armour. */
export function armorScore(id: number): number {
  const g = gearItemInfo(id);
  if (!g || !ARMOR_SLOT[g.kind]) return -1;
  return { leather: 1, gold: 2, chain: 3, iron: 4, diamond: 5 }[g.tier as 'leather'] ?? 0;
}

export interface GearUnit {
  /** Where it is: a pawn's pocket ("pawn:<name>") or a chest ("x,y,z"). */
  from: string;
  id: number;
  meta: number;
  score: number;
}

export interface GearPick {
  pawn: string;
  /** What it gets (null = it keeps what it carries, which is at least as good). */
  take: GearUnit | null;
  keeps: GearUnit | null;
}

/**
 * The fair spread of the bulk Arm op: each pawn ends with one weapon. Pawns go in order (the caller sorts them by combat skill,
 * then priority); a pawn keeps its own best weapon when nothing stored beats it, else it takes the best stored weapon left. Each
 * stored unit goes to one pawn only (the reservation), so the best weapons spread one per pawn instead of piling on one.
 */
export function distributeWeapons(
  pawns: ReadonlyArray<{ name: string; carried: GearUnit[] }>,
  stored: GearUnit[],
): GearPick[] {
  const pool = [...stored].filter((u) => u.score >= 0).sort((a, b) => b.score - a.score);
  const out: GearPick[] = [];
  for (const p of pawns) {
    const own = [...p.carried].filter((u) => u.score >= 0).sort((a, b) => b.score - a.score)[0] ?? null;
    const best = pool[0] ?? null;
    if (best && (!own || best.score > own.score)) {
      pool.shift();
      out.push({ pawn: p.name, take: best, keeps: own });
    } else out.push({ pawn: p.name, take: null, keeps: own });
  }
  return out;
}

/** One line for a gear op's results ("Ash: iron sword · Bryn: stone sword · missing: Cato (no weapon in storage)"). */
export function gearSummary(results: ReadonlyArray<StoryGearResult>): string {
  const got = results.filter((r) => r.got.length).map((r) => `${r.display ?? r.pawn}: ${r.got.join(', ')}`);
  const miss = results.filter((r) => r.missing.length).map((r) => `${r.display ?? r.pawn} (${r.missing.join(', ')})`);
  const parts = [...got];
  if (miss.length) parts.push(`missing: ${miss.join('; ')}`);
  return parts.length ? parts.join(' · ') : 'Nothing to do';
}
