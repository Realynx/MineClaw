/**
 * Tools, gear and drafted combat in the browser (docs/web/story-mode.md "Tools and gear", "Drafted combat"): the rule editor's
 * tier choices (never below a material's floor), the bulk gear ops and their fair-spread preview, the policy a pawn follows,
 * and who an attack is on (players and bots need a one-time confirm per target per session).
 */
import {
  allowedTiers,
  armorScore,
  distributeWeapons,
  gearItemInfo,
  STORY_TIERS_HARVEST,
  storyItemLabel,
  tierProblem,
  tierRank,
  weaponScore,
  type GearUnit,
  type StoryColonyState,
  type StoryGearItem,
  type StoryGearOp,
  type StoryMaterialInfo,
  type StoryMaterialRule,
  type StoryPawn,
  type StoryPolicy,
} from '@mineclaw/contracts';

// ------------------------------------------------------------------ rules

export const TIER_LABELS: Record<string, string> = { wood: 'Wooden', stone: 'Stone', iron: 'Iron', diamond: 'Diamond' };
export const ENCHANT_LABELS: Record<string, string> = {
  fortune: 'Fortune',
  silk_touch: 'Silk Touch',
  any: 'Any enchanted',
};
export const TOOL_LABELS: Record<string, string> = {
  pickaxe: 'Pickaxe',
  axe: 'Axe',
  shovel: 'Shovel',
  hoe: 'Hoe',
  shears: 'Shears',
  sword: 'Sword',
};

/** Each tier for a material's tier select: below the floor it is shown but disabled (greyed), with why. */
export function tierChoices(info: Pick<StoryMaterialInfo, 'id' | 'floor' | 'tool'> | undefined): Array<{
  tier: string;
  label: string;
  disabled: boolean;
  why: string | null;
}> {
  const floor = info?.floor ?? null;
  const ok = new Set<string>(allowedTiers(floor));
  return STORY_TIERS_HARVEST.map((t) => ({
    tier: t,
    label: TIER_LABELS[t]!,
    disabled: !ok.has(t),
    why: ok.has(t) ? null : tierProblem(info?.id ?? '', info?.tool ?? null, t, floor),
  }));
}

/** What the editor refuses before sending (the bot validates too). Null = fine. */
export function ruleProblem(
  rule: { material: string; tool?: string | null; tier?: string | null; enchant?: string | null },
  info: StoryMaterialInfo | undefined,
  version: string | number | null | undefined,
): string | null {
  if (!info) return `unknown material '${rule.material}'`;
  const p = tierProblem(rule.material, rule.tool ?? info.tool ?? null, rule.tier ?? null, info.floor ?? null);
  if (p) return p;
  if (rule.enchant && String(version ?? '') === 'b1.8.1') return 'b1.8.1 has no enchantments';
  return null;
}

/** Enchantments exist on 1.12.2 only (b1.8.1 has none; an unknown version shows them). */
export const enchantsSupported = (version: string | number | null | undefined) => String(version ?? '') !== 'b1.8.1';

/** "Stone pickaxe or better" for a rule (the floor when no tier is chosen; "hand is fine" when neither). */
export function ruleSummary(
  r: Pick<StoryMaterialRule, 'tool' | 'tier' | 'floor' | 'enchant' | 'fallback'> & { material?: string },
): string {
  const tier = tierRank(r.tier) >= tierRank(r.floor) ? r.tier : r.floor;
  const tool = TOOL_LABELS[r.tool] ?? r.tool;
  const base = tier
    ? `${TIER_LABELS[tier] ?? tier} ${tool.toLowerCase()} or better`
    : `${tool} preferred (hand is fine)`;
  if (!r.enchant) return base;
  const e = ENCHANT_LABELS[r.enchant] ?? r.enchant;
  return `${base}, ${e}${r.fallback === 'wait' ? ' (waits for one)' : ' when there is one'}`;
}

// ------------------------------------------------------------------ policies

/** The policy a pawn follows (its own, else the colony's default). */
export function policyOf(p: Pick<StoryPawn, 'policy'>, policies: readonly StoryPolicy[]): StoryPolicy | null {
  return policies.find((x) => x.id === p.policy) ?? policies.find((x) => x.default) ?? policies[0] ?? null;
}

export const ARMOR_LABELS: Record<string, string> = {
  none: 'Never',
  drafted: 'When drafted',
  always: 'Always',
  night: 'At night',
};
export const WEAPON_LABELS: Record<string, string> = {
  none: 'None',
  best: 'Best melee',
  sword: 'Sword',
  axe: 'Axe',
  bow: 'Bow',
};

// ------------------------------------------------------------------ equipment

/** Durability left as a percentage and a tone (worn below 10 %: it gets swapped when a fresh one is in storage). */
export function durability(it: Pick<StoryGearItem, 'durability'>): { pct: number | null; tone: 'ok' | 'low' | 'worn' } {
  if (it.durability === null || it.durability === undefined) return { pct: null, tone: 'ok' };
  const pct = Math.round(it.durability * 100);
  return { pct, tone: pct < 10 ? 'worn' : pct < 35 ? 'low' : 'ok' };
}

/** "Fortune III", "Silk Touch". */
export function enchantLabel(name: string, level: number): string {
  const n = name
    .split('_')
    .map((w) => w.charAt(0).toUpperCase() + w.slice(1))
    .join(' ');
  const roman = ['', 'I', 'II', 'III', 'IV', 'V'][level] ?? String(level);
  return level > 1 || name !== 'silk_touch' ? `${n} ${roman}`.trim() : n;
}

// ------------------------------------------------------------------ the bulk gear op

export const GEAR_OPS: ReadonlyArray<{ op: StoryGearOp; label: string; hint: string }> = [
  { op: 'arm', label: 'Arm (best weapon)', hint: 'Each gets the best weapon left, one per pawn' },
  { op: 'armor', label: 'Equip armour', hint: 'The best armour spread over the selection' },
  { op: 'pickup', label: 'Pick up…', hint: 'Fetch an item from storage, the same for each' },
  { op: 'return', label: 'Return gear', hint: 'Take off armour, store tools and weapons' },
  { op: 'tools', label: 'Equip tools for their work', hint: 'The tools each pawn’s work and policy need' },
];

const combat = (p: StoryPawn) => Math.max(p.skills.guard?.level ?? 0, p.skills.hunt?.level ?? 0);

/** The fair order: combat skill first, then the guard priority (1 first, off last), then the name (as the director does). */
export function fairOrder<T extends StoryPawn>(pawns: readonly T[]): T[] {
  const pr = (p: StoryPawn) => (p.priorities.guard ?? 0) || 9;
  return [...pawns].sort((a, b) => combat(b) - combat(a) || pr(a) - pr(b) || a.name.localeCompare(b.name));
}

const unitsOf = (state: StoryColonyState | undefined, score: (id: number) => number): GearUnit[] => {
  const out: GearUnit[] = [];
  for (const s of state?.storages ?? [])
    for (const [id, meta, n] of s.contents)
      if (score(id) >= 0) for (let i = 0; i < n; i++) out.push({ from: s.chest.join(','), id, meta, score: score(id) });
  return out;
};

export interface PreviewLine {
  pawn: string;
  display: string;
  text: string;
  missing: boolean;
}

const nameOf = (id: number, meta: number) => {
  const g = gearItemInfo(id);
  return g ? g.name.replace(/_/g, ' ') : storyItemLabel(id, meta).toLowerCase();
};

/**
 * What a bulk Arm would give each selected pawn, from what the page knows (their weapons, the storage contents): the same fair
 * spread the director uses, so the toast it answers with matches the preview.
 */
export function previewArm(state: StoryColonyState | undefined, names: readonly string[]): PreviewLine[] {
  const pawns = fairOrder((state?.pawns ?? []).filter((p) => names.includes(p.name)));
  const picks = distributeWeapons(
    pawns.map((p) => ({
      name: p.name,
      carried: [...(p.equipment?.weapons ?? []), ...(p.equipment?.tools ?? []).filter((t) => t.kind === 'axe')].map(
        (w) => ({
          from: `pawn:${p.name}`,
          id: w.item[0],
          meta: w.item[1],
          score: weaponScore(w.item[0]),
        }),
      ),
    })),
    unitsOf(state, weaponScore),
  );
  return picks.map((pk) => {
    const p = pawns.find((x) => x.name === pk.pawn)!;
    const text = pk.take
      ? nameOf(pk.take.id, pk.take.meta)
      : pk.keeps
        ? `keeps ${nameOf(pk.keeps.id, pk.keeps.meta)}`
        : 'no weapon in storage';
    return { pawn: p.name, display: p.display, text, missing: !pk.take && !pk.keeps };
  });
}

/** How many armour pieces storage holds (the Equip armour button says so). */
export const storedArmor = (state: StoryColonyState | undefined) => unitsOf(state, armorScore).length;

/** Items the storages hold, merged, for "Pick up…" (most first). */
export function pickupChoices(
  state: StoryColonyState | undefined,
): Array<{ item: [number, number]; label: string; count: number }> {
  const m = new Map<string, { item: [number, number]; count: number }>();
  for (const s of state?.storages ?? [])
    for (const [id, meta, n] of s.contents) {
      const k = `${id}:${meta}`;
      const cur = m.get(k);
      if (cur) cur.count += n;
      else m.set(k, { item: [id, meta], count: n });
    }
  return [...m.values()]
    .map((x) => ({
      ...x,
      label: gearItemInfo(x.item[0]) ? nameOf(x.item[0], x.item[1]) : storyItemLabel(x.item[0], x.item[1]),
    }))
    .sort((a, b) => b.count - a.count || a.label.localeCompare(b.label));
}

/** The gear request for a selection (pawns only: AI bodies take no gear ops). */
export function gearBody(op: StoryGearOp, pawns: readonly string[], item?: [number, number], count?: number) {
  return { op, pawns: [...pawns], ...(item ? { item } : {}), ...(count && count > 1 ? { count } : {}) };
}

// ------------------------------------------------------------------ attacks

export interface AttackWho {
  name: string;
  kind: 'mob' | 'animal' | 'player' | 'bot';
  entity: number | null;
}

/** Players and other bots need the owner's confirm (once per target per session); mobs and animals don't. */
export const needsConfirm = (w: AttackWho) => w.kind === 'player' || w.kind === 'bot';

const CONFIRM_KEY = 'mcx-story-attack-ok';

function confirmed(): Set<string> {
  try {
    return new Set(JSON.parse(sessionStorage.getItem(CONFIRM_KEY) ?? '[]') as string[]);
  } catch {
    return new Set();
  }
}

/** Has this target been confirmed this session? */
export const isConfirmed = (w: AttackWho) => confirmed().has(w.name.toLowerCase());

/** Remember the confirm for this target (the rest of the session). */
export function rememberConfirm(w: AttackWho): void {
  const s = confirmed();
  s.add(w.name.toLowerCase());
  try {
    sessionStorage.setItem(CONFIRM_KEY, JSON.stringify([...s]));
  } catch {
    /* private mode: asked again next time */
  }
}

/** The attack order for a target (by entity id, or by name for an AI body, which has no entity id in the viewer). */
export function attackSpec(w: AttackWho, confirm: boolean) {
  return {
    kind: 'attack' as const,
    target: w.entity !== null ? { entity: w.entity } : { player: w.name },
    ...(confirm ? { confirm: true } : {}),
  };
}

/** The drafted pawns of a selection (attacks on a click go to them). */
export const draftedOf = (state: StoryColonyState | undefined, names: readonly string[]) =>
  (state?.pawns ?? []).filter((p) => names.includes(p.name) && p.drafted && p.online !== false).map((p) => p.name);
