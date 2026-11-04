/**
 * The synthetic director's tools and gear (the mocks): gear policies, material rules validated against each material's floor,
 * a pawn's equipment, the bulk gear op (fair spread, each stored unit reserved for one pawn) and attack confirms. Pure-ish
 * helpers over plain data so `storySim.ts` only glues them in.
 */
import type { StoryGearItem, StoryMaterialInfo, StoryMaterialRule, StoryPolicy, StoryGearResult } from './storyGear.js';
import {
  ARMOR_SLOT,
  armorScore,
  distributeWeapons,
  gearItemInfo,
  gearSummary,
  STORY_TOOL_KINDS,
  tierProblem,
  tierRank,
  toolLabel,
  weaponScore,
  type GearUnit,
} from './storyGear.js';
import type { StoryPawn, StoryStorage } from './story.js';

const fail = (status: number, msg: string, extra?: Record<string, unknown>) =>
  Object.assign(new Error(msg), { status, ...(extra ?? {}) });

/** The rules editor catalog (b1.8.1 / 1.12.2 shared names). */
export const SIM_MATERIALS: StoryMaterialInfo[] = (() => {
  const g = (
    id: string,
    label: string,
    tool: string | null,
    floor: string | null,
    members: string[],
    keepTool: string | null = null,
  ): StoryMaterialInfo => ({
    id,
    label,
    group: null,
    tool,
    floor,
    keepTool,
    members,
  });
  const m = (
    id: string,
    label: string,
    group: string | null,
    tool: string | null,
    floor: string | null,
    keepTool: string | null = null,
  ): StoryMaterialInfo => ({
    id,
    label,
    group,
    tool,
    floor,
    keepTool,
  });
  return [
    g('group:ores', 'Ores', 'pickaxe', 'wood', [
      'coal_ore',
      'iron_ore',
      'gold_ore',
      'diamond_ore',
      'lapis_ore',
      'redstone_ore',
      'emerald_ore',
    ]),
    g('group:stone', 'Stone', 'pickaxe', 'wood', ['stone', 'cobblestone', 'sandstone', 'netherrack']),
    g('group:soil', 'Dirt, sand and gravel', 'shovel', null, ['dirt', 'grass', 'sand', 'gravel', 'clay']),
    g('group:logs', 'Logs', 'axe', null, ['log']),
    g('group:leaves', 'Leaves', null, null, ['leaves'], 'shears'),
    g('group:wool', 'Wool', 'shears', null, ['wool']),
    g('group:web', 'Cobweb', 'sword', 'wood', ['web']),
    m('coal_ore', 'Coal ore', 'group:ores', 'pickaxe', 'wood'),
    m('iron_ore', 'Iron ore', 'group:ores', 'pickaxe', 'stone'),
    m('gold_ore', 'Gold ore', 'group:ores', 'pickaxe', 'iron'),
    m('diamond_ore', 'Diamond ore', 'group:ores', 'pickaxe', 'iron'),
    m('lapis_ore', 'Lapis ore', 'group:ores', 'pickaxe', 'stone'),
    m('redstone_ore', 'Redstone ore', 'group:ores', 'pickaxe', 'iron'),
    m('emerald_ore', 'Emerald ore', 'group:ores', 'pickaxe', 'iron'),
    m('stone', 'Stone', 'group:stone', 'pickaxe', 'wood'),
    m('cobblestone', 'Cobblestone', 'group:stone', 'pickaxe', 'wood'),
    m('sandstone', 'Sandstone', 'group:stone', 'pickaxe', 'wood'),
    m('netherrack', 'Netherrack', 'group:stone', 'pickaxe', 'wood'),
    m('obsidian', 'Obsidian', null, 'pickaxe', 'diamond'),
    m('dirt', 'Dirt', 'group:soil', 'shovel', null),
    m('grass', 'Grass block', 'group:soil', 'shovel', null),
    m('sand', 'Sand', 'group:soil', 'shovel', null),
    m('gravel', 'Gravel', 'group:soil', 'shovel', null),
    m('clay', 'Clay', 'group:soil', 'shovel', null),
    m('snow_layer', 'Snow', null, 'shovel', 'wood'),
    m('log', 'Log', 'group:logs', 'axe', null),
    m('leaves', 'Leaves', 'group:leaves', null, null, 'shears'),
    m('wool', 'Wool', 'group:wool', 'shears', null),
    m('web', 'Cobweb', 'group:web', 'sword', 'wood', 'shears'),
  ];
})();

export const DEFAULT_POLICIES: StoryPolicy[] = [
  {
    id: 'default',
    name: 'Default',
    default: true,
    toolsByWork: true,
    tools: [],
    weapon: 'best',
    armor: 'drafted',
    food: 2,
  },
  {
    id: 'soldier',
    name: 'Soldiers',
    default: false,
    toolsByWork: false,
    tools: [],
    weapon: 'best',
    armor: 'always',
    food: 4,
  },
];

export interface SimGear {
  policies: StoryPolicy[];
  rules: StoryMaterialRule[];
  seq: number;
  /** Before a draft: what each pawn held (restored on undraft). */
  heldBefore: Map<string, StoryGearItem | null>;
  stocked: boolean;
}

export function newSimGear(): SimGear {
  return { policies: structuredCloneList(DEFAULT_POLICIES), rules: [], seq: 1, heldBefore: new Map(), stocked: false };
}

function structuredCloneList<T>(v: T[]): T[] {
  return JSON.parse(JSON.stringify(v)) as T[];
}

/** A gear item for an [id, meta] (meta = damage for tools, so durability follows). */
export function gearItem(id: number, meta: number, ench: Record<string, number> = {}): StoryGearItem {
  const g = gearItemInfo(id);
  const kind = g?.kind ?? 'other';
  return {
    item: [id, meta],
    name: g?.name ?? String(id),
    label: g
      ? toolLabel(g.name.replace(/^(wooden|stone|iron|diamond|golden|leather|chainmail)_/, ''), g.tier)
      : `Item ${id}`,
    kind,
    tier: g?.tier ?? null,
    durability: g ? Math.max(0, Math.min(1, (g.max - meta) / g.max)) : null,
    ench,
    slot: ARMOR_SLOT[kind] ?? null,
  };
}

/** Seeded equipment: a pawn's starting tools and weapon by its place in the colony. */
export function seedEquipment(i: number): NonNullable<StoryPawn['equipment']> {
  const sets: Array<Array<[number, number, Record<string, number>?]>> = [
    [
      [257, 40, { efficiency: 2, fortune: 2 }],
      [273, 90],
      [267, 30],
    ],
    [
      [274, 120],
      [271, 10],
    ],
    [
      [290, 20],
      [268, 50],
    ],
    [[275, 60]],
    [],
  ];
  const items = (sets[i % sets.length] ?? []).map(([id, meta, ench]) => gearItem(id, meta, ench ?? {}));
  const weapons = items.filter((x) => x.kind === 'sword' || x.kind === 'bow');
  const tools = items.filter((x) => !weapons.includes(x));
  const armor = i === 0 ? [gearItem(306, 30)] : [];
  return { held: tools[0] ?? weapons[0] ?? null, armor, tools, weapons };
}

/** Gear the colony keeps in its first storage (so Arm / Equip armour have something to spread). */
export const SIM_ARMORY: Array<[number, number, number]> = [
  [267, 0, 2], // iron swords
  [272, 0, 1], // stone sword
  [276, 0, 1], // diamond sword
  [307, 0, 1], // iron chestplate
  [298, 0, 2], // leather helmets
  [301, 0, 1], // leather boots
  [274, 0, 2], // stone pickaxes
  [292, 0, 1], // iron hoe
  [271, 0, 1], // wooden axe
];

// ------------------------------------------------------------------ policies

export function policiesOp(
  gear: SimGear,
  pawns: Map<string, { p: StoryPawn }>,
  body: { op: string; policy?: Partial<StoryPolicy> & { name?: string }; id?: string; pawns?: string[] },
) {
  if (body.op === 'set') {
    const spec = body.policy ?? {};
    const name = String(spec.name ?? '').trim();
    if (!name || name.length > 40) throw fail(400, 'policy.name: 1..40 characters');
    let p = spec.id ? gear.policies.find((x) => x.id === spec.id) : undefined;
    if (spec.id && !p) throw fail(400, `no policy '${spec.id}'`);
    if (!p) {
      p = {
        id: `pol${gear.seq++}`,
        name,
        default: false,
        toolsByWork: true,
        tools: [],
        weapon: 'best',
        armor: 'drafted',
        food: 0,
      };
      gear.policies.push(p);
    }
    p.name = name;
    if (spec.toolsByWork !== undefined) p.toolsByWork = spec.toolsByWork;
    if (spec.tools !== undefined) p.tools = [...spec.tools];
    if (spec.weapon !== undefined) p.weapon = spec.weapon;
    if (spec.armor !== undefined) p.armor = spec.armor;
    if (spec.food !== undefined) p.food = spec.food;
    return { id: p.id };
  }
  const p = gear.policies.find((x) => x.id === body.id);
  if (!p) throw fail(400, `no policy '${body.id ?? ''}'`);
  if (body.op === 'remove') {
    if (p.default) throw fail(400, 'the default policy stays (edit it instead)');
    gear.policies = gear.policies.filter((x) => x !== p);
    for (const ps of pawns.values()) if (ps.p.policy === p.id) ps.p.policy = null;
    return { id: p.id, removed: true };
  }
  if (body.op === 'assign') {
    const names: string[] = [];
    for (const n of body.pawns ?? []) {
      const ps = pawns.get(n);
      if (!ps) throw fail(404, `no pawn '${n}'`);
      ps.p.policy = p.default ? null : p.id;
      names.push(n);
    }
    return { id: p.id, pawns: names };
  }
  throw fail(400, 'op must be set|remove|assign');
}

// ------------------------------------------------------------------ material rules

export function materialRulesOp(
  gear: SimGear,
  body: {
    op: string;
    material?: string;
    rule?: { material: string; tool?: string; tier?: string | null; enchant?: string | null; fallback?: string };
  },
  version: string | null,
) {
  if (body.op === 'remove') {
    const before = gear.rules.length;
    gear.rules = gear.rules.filter((r) => r.material !== body.material);
    return { removed: gear.rules.length < before };
  }
  if (body.op !== 'set' || !body.rule) throw fail(400, 'op must be set|remove');
  const r = body.rule;
  const info = SIM_MATERIALS.find((m) => m.id === r.material);
  if (!info) throw fail(400, `unknown material '${r.material}'`);
  const tool = r.tool ?? info.tool ?? info.keepTool ?? 'pickaxe';
  if (!(STORY_TOOL_KINDS as readonly string[]).includes(tool))
    throw fail(400, `tool must be ${STORY_TOOL_KINDS.join('|')}`);
  // A material that needs a tool takes only a tool that harvests it (cobweb: a sword or shears).
  if (info.floor && tool !== info.tool && !(tool === 'shears' && info.keepTool === 'shears'))
    throw fail(400, `a ${tool} can't harvest ${r.material.replace('group:', '')}`);
  const problem = tierProblem(r.material, tool, r.tier ?? null, info.floor);
  if (problem) throw fail(400, problem);
  if (r.enchant && version === 'b1.8.1') throw fail(400, 'b1.8.1 has no enchantments');
  const rule: StoryMaterialRule = {
    material: r.material,
    label: info.label,
    tool,
    tier: r.tier ?? null,
    enchant: r.enchant ?? null,
    fallback: r.fallback ?? 'plain',
    floor: info.floor ?? null,
  };
  gear.rules = [...gear.rules.filter((x) => x.material !== r.material), rule];
  return { rule };
}

// ------------------------------------------------------------------ the bulk gear op

interface PawnRef {
  p: StoryPawn;
}

const combat = (p: StoryPawn) => Math.max(p.skills.guard?.level ?? 0, p.skills.hunt?.level ?? 0);

/** Pawns in the fair order: combat skill first, then guard priority (1 before 4, off last), then name. */
export function gearOrder<T extends PawnRef>(list: T[]): T[] {
  const pr = (p: StoryPawn) => {
    const v = p.priorities.guard ?? 0;
    return v > 0 ? v : 9;
  };
  return [...list].sort((a, b) => combat(b.p) - combat(a.p) || pr(a.p) - pr(b.p) || a.p.name.localeCompare(b.p.name));
}

/** Every stored unit of the gear items (one per item, by chest), for the distribution. */
function storedUnits(storages: StoryStorage[], score: (id: number) => number): GearUnit[] {
  const out: GearUnit[] = [];
  for (const s of storages)
    for (const [id, meta, n] of s.contents)
      if (score(id) >= 0)
        for (let i = 0; i < n; i++)
          out.push({ from: `${s.chest[0]},${s.chest[1]},${s.chest[2]}`, id, meta, score: score(id) });
  return out;
}

function takeFrom(storages: StoryStorage[], u: GearUnit): void {
  const s = storages.find((x) => `${x.chest[0]},${x.chest[1]},${x.chest[2]}` === u.from);
  if (!s) return;
  const i = s.contents.findIndex(([id, meta]) => id === u.id && meta === u.meta);
  if (i < 0) return;
  const st = s.contents[i]!;
  if (st[2] <= 1) s.contents.splice(i, 1);
  else s.contents[i] = [st[0], st[1], st[2] - 1];
}

function putInto(storages: StoryStorage[], id: number, meta: number): void {
  const s = storages[0];
  if (!s) return;
  const i = s.contents.findIndex(([a, b]) => a === id && b === meta);
  if (i >= 0) s.contents[i] = [id, meta, s.contents[i]![2] + 1];
  else s.contents.push([id, meta, 1]);
}

function equip(p: StoryPawn) {
  return (p.equipment ??= { held: null, armor: [], tools: [], weapons: [] });
}

const WORK_TOOLS: Record<string, string[]> = {
  mine: ['pickaxe', 'shovel'],
  chop: ['axe'],
  farm: ['hoe'],
  build: ['pickaxe', 'axe'],
};

/** The tool kinds a pawn's policy has it carry. */
export function policyKinds(p: StoryPawn, policies: StoryPolicy[]): string[] {
  const pol = policies.find((x) => x.id === p.policy) ?? policies.find((x) => x.default) ?? null;
  const kinds = new Set<string>(pol?.tools ?? []);
  if (pol?.toolsByWork ?? true)
    for (const [w, ks] of Object.entries(WORK_TOOLS)) if ((p.priorities[w] ?? 0) > 0) for (const k of ks) kinds.add(k);
  return [...kinds];
}

export function gearOp(
  gear: SimGear,
  pawns: PawnRef[],
  storages: StoryStorage[],
  body: { op: string; item?: [number, number]; count?: number },
  label: (id: number, meta: number) => string,
): { results: StoryGearResult[]; summary: string } {
  const ordered = gearOrder(pawns);
  const results: StoryGearResult[] = ordered.map((x) => ({
    pawn: x.p.name,
    display: x.p.display,
    got: [],
    missing: [],
  }));
  const res = (name: string) => results.find((r) => r.pawn === name)!;
  switch (body.op) {
    case 'arm': {
      const carried = ordered.map((x) => ({
        name: x.p.name,
        carried: [
          ...(x.p.equipment?.weapons ?? []),
          ...(x.p.equipment?.tools ?? []).filter((t) => t.kind === 'axe'),
        ].map((w) => ({
          from: `pawn:${x.p.name}`,
          id: w.item[0],
          meta: w.item[1],
          score: weaponScore(w.item[0]),
        })),
      }));
      for (const pick of distributeWeapons(carried, storedUnits(storages, weaponScore))) {
        const p = ordered.find((x) => x.p.name === pick.pawn)!.p;
        const e = equip(p);
        if (pick.take) {
          takeFrom(storages, pick.take);
          const it = gearItem(pick.take.id, pick.take.meta);
          e.weapons.push(it);
          if (p.drafted) e.held = it;
          res(p.name).got.push(it.label ?? 'a weapon');
        } else if (pick.keeps) res(p.name).got.push(`keeps ${gearItem(pick.keeps.id, pick.keeps.meta).label}`);
        else res(p.name).missing.push('no weapon in storage');
      }
      break;
    }
    case 'armor': {
      const pool = storedUnits(storages, armorScore).sort((a, b) => b.score - a.score);
      for (const slot of ['head', 'chest', 'legs', 'feet']) {
        for (const x of ordered) {
          const e = equip(x.p);
          const worn = e.armor.find((a) => a.slot === slot);
          const i = pool.findIndex(
            (u) => ARMOR_SLOT[gearItemInfo(u.id)?.kind ?? ''] === slot && (!worn || u.score > armorScore(worn.item[0])),
          );
          if (i < 0) continue;
          const u = pool.splice(i, 1)[0]!;
          takeFrom(storages, u);
          if (worn) {
            e.armor = e.armor.filter((a) => a !== worn);
            putInto(storages, worn.item[0], worn.item[1]);
          }
          const it = gearItem(u.id, u.meta);
          e.armor.push(it);
          res(x.p.name).got.push(it.label ?? 'armour');
        }
      }
      for (const r of results) if (!r.got.length) r.missing.push('no better armour in storage');
      break;
    }
    case 'pickup': {
      if (!body.item) throw fail(400, 'pickup needs an item');
      const [id, meta] = body.item;
      const n = body.count ?? 1;
      const name = label(id, meta);
      for (const x of ordered) {
        let got = 0;
        for (const s of storages) {
          while (got < n) {
            const i = s.contents.findIndex(([a, b]) => a === id && (b === meta || meta < 0));
            if (i < 0) break;
            takeFrom(storages, {
              from: `${s.chest[0]},${s.chest[1]},${s.chest[2]}`,
              id,
              meta: s.contents[i]![1],
              score: 0,
            });
            got++;
          }
        }
        const inv = x.p.inventory;
        const j = inv.findIndex(([a, b]) => a === id && b === Math.max(0, meta));
        if (got) {
          if (j >= 0) inv[j] = [id, inv[j]![1], inv[j]![2] + got];
          else inv.push([id, Math.max(0, meta), got]);
          if (gearItemInfo(id)) {
            const it = gearItem(id, Math.max(0, meta));
            const e = equip(x.p);
            if (weaponScore(id) >= 0 && it.kind !== 'axe') e.weapons.push(it);
            else if (armorScore(id) >= 0) e.armor.push(it);
            else e.tools.push(it);
          }
          res(x.p.name).got.push(`${got} ${name}`);
        }
        if (got < n) res(x.p.name).missing.push(`${n - got} ${name}`);
      }
      break;
    }
    case 'return': {
      for (const x of ordered) {
        const e = equip(x.p);
        const all = [...e.armor, ...e.tools, ...e.weapons];
        for (const it of all) putInto(storages, it.item[0], it.item[1]);
        e.armor = [];
        e.tools = [];
        e.weapons = [];
        e.held = null;
        if (all.length) res(x.p.name).got.push(`returned ${all.length} item${all.length === 1 ? '' : 's'}`);
        else res(x.p.name).missing.push('nothing to return');
      }
      break;
    }
    case 'tools': {
      const pool = storedUnits(storages, (id) => {
        const g = gearItemInfo(id);
        return g && g.kind !== 'sword' && g.kind !== 'bow' && !ARMOR_SLOT[g.kind]
          ? tierRank(g.tier === 'gold' ? 'wood' : g.tier) + 1
          : -1;
      }).sort((a, b) => b.score - a.score);
      for (const x of ordered) {
        const e = equip(x.p);
        for (const kind of policyKinds(x.p, gear.policies)) {
          if (e.tools.some((t) => t.kind === kind)) continue;
          const i = pool.findIndex((u) => gearItemInfo(u.id)?.kind === kind);
          if (i < 0) {
            res(x.p.name).missing.push(`no ${kind} in storage`);
            continue;
          }
          const u = pool.splice(i, 1)[0]!;
          takeFrom(storages, u);
          const it = gearItem(u.id, u.meta);
          e.tools.push(it);
          res(x.p.name).got.push(it.label ?? kind);
        }
      }
      break;
    }
    default:
      throw fail(400, 'op must be arm|armor|pickup|return|tools');
  }
  for (const x of ordered)
    if (res(x.p.name).got.length) x.p.gearTasks = [{ id: `g${gear.seq++}`, op: body.op, label: `gear: ${body.op}` }];
  return { results, summary: gearSummary(results) };
}

/** On draft: the best weapon into the hand (what was held is remembered); on undraft: it comes back. */
export function draftGear(gear: SimGear, p: StoryPawn, drafted: boolean): void {
  const e = equip(p);
  if (drafted) {
    const best = [...e.weapons, ...e.tools.filter((t) => t.kind === 'axe')].sort(
      (a, b) => weaponScore(b.item[0]) - weaponScore(a.item[0]),
    )[0];
    if (!best) return;
    if (!gear.heldBefore.has(p.name)) gear.heldBefore.set(p.name, e.held ?? null);
    e.held = best;
  } else if (gear.heldBefore.has(p.name)) {
    e.held = gear.heldBefore.get(p.name) ?? null;
    gear.heldBefore.delete(p.name);
  }
}

export interface AttackTarget {
  name: string;
  kind: 'player' | 'bot' | 'pawn' | 'mob';
}

/** The 409 an attack on a player or a bot answers without `confirm: true`; own pawns are refused (400). */
export function checkAttack(t: AttackTarget | null, confirm: boolean | undefined): void {
  if (!t) return;
  if (t.kind === 'pawn') throw fail(400, `won't attack ${t.name}: one of the colony's own pawns`);
  if ((t.kind === 'player' || t.kind === 'bot') && !confirm)
    throw fail(409, `attacking ${t.name} needs confirm: true`, { confirm: { name: t.name, kind: t.kind } });
}
