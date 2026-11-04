/**
 * Story Mode work roles (docs/web/story-mode.md "Roles"): named priority templates a pawn's Work row follows. The director (bot)
 * owns the real rules (StoryColony.Roles.cs); these are their reference copies for the web's grid and the mock director. Pure.
 */
import { STORY_WORK_TYPES, type StoryPawn, type StoryRole, type StoryRolesRequest } from './story.js';

type Prios = Record<string, number>;

const full = (ps: Partial<Record<(typeof STORY_WORK_TYPES)[number], number>>): Prios =>
  Object.fromEntries(STORY_WORK_TYPES.map((t) => [t, ps[t] ?? 0]));

/** The built-in roles, as shipped (the bot's StoryColony.DefaultRoles). */
export const STORY_DEFAULT_ROLES: ReadonlyArray<Omit<StoryRole, 'pawns'>> = [
  { id: 'farmer', name: 'Farmer', icon: 'wheat', builtin: true, priorities: full({ farm: 1, cook: 3, haul: 3, doctor: 4 }) },
  { id: 'miner', name: 'Miner', icon: 'pickaxe', builtin: true, priorities: full({ mine: 1, haul: 3, build: 4 }) },
  { id: 'woodcutter', name: 'Woodcutter', icon: 'axe', builtin: true, priorities: full({ chop: 1, haul: 3 }) },
  { id: 'builder', name: 'Builder', icon: 'brick', builtin: true, priorities: full({ build: 1, haul: 2, craft: 3 }) },
  { id: 'hauler', name: 'Hauler', icon: 'package', builtin: true, priorities: full({ haul: 1, chop: 3 }) },
  { id: 'crafter', name: 'Crafter', icon: 'hammer', builtin: true, priorities: full({ craft: 1, cook: 2, haul: 3 }) },
  { id: 'cook', name: 'Cook', icon: 'pot', builtin: true, priorities: full({ cook: 1, farm: 3, haul: 3, doctor: 4 }) },
  { id: 'fisher', name: 'Fisher', icon: 'fish', builtin: true, priorities: full({ fish: 1, cook: 3, haul: 3 }) },
  { id: 'guard', name: 'Guard / Hunter', icon: 'shield', builtin: true, priorities: full({ guard: 1, hunt: 1, haul: 3 }) },
  {
    id: 'generalist',
    name: 'Generalist',
    icon: 'shapes',
    builtin: true,
    priorities: Object.fromEntries(STORY_WORK_TYPES.map((t) => [t, t === 'haul' ? 2 : 3])),
  },
];

/** A fresh copy of the built-in roles (the mock's colony seeds these). */
export const defaultRoles = (): Array<Omit<StoryRole, 'pawns'>> =>
  STORY_DEFAULT_ROLES.map((r) => ({ ...r, priorities: { ...r.priorities } }));

/** The role a pawn follows (null = Custom, or its role is gone). */
export const roleOf = (p: Pick<StoryPawn, 'role'>, roles: ReadonlyArray<StoryRole> | null | undefined): StoryRole | null =>
  (p.role && roles?.find((r) => r.id === p.role)) || null;

/** A pawn's effective priority for one work type: its role's while it has one, else its own. */
export const effectivePriority = (
  p: Pick<StoryPawn, 'role' | 'priorities'>,
  roles: ReadonlyArray<StoryRole> | null | undefined,
  t: string,
): number => {
  const r = roleOf(p, roles);
  return (r ? r.priorities[t] : p.priorities[t]) ?? 0;
};

/** The work types a role does, best first ("Farm 1 · Cook 3 · Haul 3"), for a tooltip or a summary. */
export function roleSummary(r: Pick<StoryRole, 'priorities'>, label: (t: string) => string = (t) => t): string {
  const on = STORY_WORK_TYPES.filter((t) => (r.priorities[t] ?? 0) > 0).sort(
    (a, b) => r.priorities[a]! - r.priorities[b]! || STORY_WORK_TYPES.indexOf(a) - STORY_WORK_TYPES.indexOf(b),
  );
  return on.length ? on.map((t) => `${label(t)} ${r.priorities[t]}`).join(' · ') : 'no work';
}

const err = (message: string) => Object.assign(new Error(message), { status: 400 });

type SimRole = Omit<StoryRole, 'pawns'>;
type SimPawn = { name: string; role?: string | null | undefined; priorities: Prios };

/** Writes the role's values into the pawn (the bot keeps the effective values in `priorities`). */
export function applyRole(p: SimPawn, r: SimRole): void {
  for (const t of STORY_WORK_TYPES) p.priorities[t] = r.priorities[t] ?? 0;
}

const slug = (name: string) =>
  name
    .toLowerCase()
    .replace(/[^a-z0-9]/g, '-')
    .replace(/^-+|-+$/g, '')
    .slice(0, 24) || 'role';

/** The mock director's `POST …/roles` (the bot's RolesOp): mutates `roles` and the pawns, returns the reply's id. */
export function rolesOp(roles: SimRole[], pawns: SimPawn[], body: StoryRolesRequest): { id: string | null; removed?: boolean } {
  const builtin = (id: string) => STORY_DEFAULT_ROLES.some((d) => d.id === id);
  const follow = (r: SimRole) => pawns.filter((p) => p.role === r.id).forEach((p) => applyRole(p, r));
  switch (body.op) {
    case 'add':
    case 'update': {
      const spec = body.role;
      let r = body.op === 'update' ? roles.find((x) => x.id === body.role.id) : undefined;
      if (body.op === 'update' && !r) throw err(`role.id: no such role '${body.role.id}'`);
      const name = (spec.name ?? r?.name ?? '').trim();
      if (name.length < 1 || name.length > 40) throw err('role.name: 1..40 characters');
      if (roles.some((x) => x !== r && x.name.toLowerCase() === name.toLowerCase())) throw err(`role.name: '${name}' is taken`);
      if (!r) {
        const base = slug(name);
        let id = base;
        for (let n = 2; roles.some((x) => x.id === id) || builtin(id); n++) id = `${base}-${n}`;
        r = { id, name, icon: null, builtin: false, priorities: full({}) };
        roles.push(r);
      }
      r.name = name;
      if (spec.icon !== undefined) r.icon = spec.icon ?? null;
      for (const [t, v] of Object.entries(spec.priorities ?? {})) if (v !== undefined) r.priorities[t] = v;
      follow(r);
      return { id: r.id };
    }
    case 'remove': {
      const i = roles.findIndex((x) => x.id === body.id);
      if (i < 0) throw err(`id: no such role '${body.id}'`);
      roles.splice(i, 1);
      for (const p of pawns) if (p.role === body.id) p.role = null; // Custom, the same values
      return { id: body.id, removed: true };
    }
    case 'reset_defaults': {
      defaultRoles().forEach((d, i) => {
        if (roles.some((x) => x.id !== d.id && x.name.toLowerCase() === d.name.toLowerCase())) d.name += ' (built-in)';
        const have = roles.find((x) => x.id === d.id);
        if (have) Object.assign(have, d);
        else roles.splice(Math.min(i, roles.length), 0, d);
        follow(have ?? d);
      });
      return { id: null };
    }
  }
}

/** The state's `roles`: each role with the pawns that have it. */
export const rolesState = (roles: ReadonlyArray<SimRole>, pawns: ReadonlyArray<SimPawn>): StoryRole[] =>
  roles.map((r) => ({
    ...r,
    priorities: { ...r.priorities },
    builtin: STORY_DEFAULT_ROLES.some((d) => d.id === r.id),
    pawns: pawns.filter((p) => p.role === r.id).map((p) => p.name),
  }));
