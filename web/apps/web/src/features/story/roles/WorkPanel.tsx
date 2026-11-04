'use client';

/**
 * The Work tab: a grid of pawns by work type (click cycles 1–4 and off, skill as the tint, passion mark, sort by a column), with a
 * role at the start of each row (docs/web/story-mode.md "Roles"). A pawn with a role shows the role's priorities ("from role":
 * dashed cells); clicking one of them detaches the pawn to Custom with the same values plus that change, with an Undo. Rows can be
 * ticked to give several pawns a role at once, and the Roles section below edits the roles themselves.
 */
import type { StoryAiBody, StoryPawn, StoryRole, StoryRolesRequest } from '@mineclaw/contracts';
import { Flame, Undo2, Users } from 'lucide-react';
import { useEffect, useMemo, useState, type ReactNode } from 'react';
import { Button } from '@/components/ui/primitives';
import { cn } from '@/lib/utils';
import { cyclePriority, nextSort, skillTint, sortPawns, WORK_LABELS, WORK_TYPES, type SortKey } from '../model';
import { panel } from '../StoryHud';
import { AiMark, Avatar } from '../visuals';
import { RoleIcon } from './roleIcons';
import { RolesEditor } from './RolesEditor';

/** A detach to undo: the pawn, its role before, shown for a few seconds. */
type Detached = { pawn: string; display: string; role: StoryRole };

type WorkProps = {
  pawns: StoryPawn[];
  ai: StoryAiBody[];
  canWrite: boolean;
  onSet: (pawns: string[], work: Record<string, number>) => void;
  roles?: ReadonlyArray<StoryRole>;
  /** The pawns selected on the map: ticked when the panel opens. */
  selected?: ReadonlyArray<string>;
  onRole?: (pawns: string[], role: string | null) => void;
  onRoleOp?: (body: StoryRolesRequest) => void;
};

/**
 * The grid's state: the values and roles shown before the colony's state catches up (`local`, `localRole`), the ticked rows,
 * the detach to undo, and the actions (cycle a cell, give a role, a roles-editor op).
 */
function useWorkGrid({ pawns, canWrite, onSet, roles = [], selected = [], onRole, onRoleOp }: WorkProps) {
  const [local, setLocal] = useState<Record<string, Record<string, number>>>({});
  const [localRole, setLocalRole] = useState<Record<string, string | null>>({});
  const [picked, setPicked] = useState<ReadonlySet<string>>(
    () => new Set(selected.filter((n) => pawns.some((p) => p.name === n))),
  );
  const [detached, setDetached] = useState<Detached | null>(null);
  useEffect(() => {
    if (!detached) return;
    const t = setTimeout(() => setDetached(null), 8000);
    return () => clearTimeout(t);
  }, [detached]);

  const roleIdOf = (p: StoryPawn) => (p.name in localRole ? localRole[p.name] : p.role) ?? null;
  const roleOf = (p: StoryPawn) => roles.find((r) => r.id === roleIdOf(p)) ?? null;
  const value = (p: StoryPawn, t: string) => {
    const r = roleOf(p);
    return r ? (r.priorities[t] ?? 0) : (local[p.name]?.[t] ?? p.priorities[t] ?? 0);
  };
  const valuesOf = (p: StoryPawn) => Object.fromEntries(WORK_TYPES.map((t) => [t, value(p, t)]));

  /** Give pawns a role (null = Custom: they keep the values they show). */
  const assign = (names: string[], role: string | null) => {
    if (!canWrite || !onRole || names.length === 0) return;
    const keep = Object.fromEntries(
      names.map((n) => {
        const p = pawns.find((x) => x.name === n);
        return [n, p ? valuesOf(p) : {}];
      }),
    );
    setLocalRole((l) => ({ ...l, ...Object.fromEntries(names.map((n) => [n, role])) }));
    setLocal((l) => ({ ...l, ...(role === null ? keep : Object.fromEntries(names.map((n) => [n, {}]))) }));
    onRole(names, role);
  };

  const set = (p: StoryPawn, t: string, back: boolean) => {
    if (!canWrite) return;
    const r = roleOf(p);
    const v = cyclePriority(value(p, t), back);
    if (r) {
      // A cell of a role row: the pawn turns Custom with the role's values and this change (the bot detaches it the same way).
      setLocalRole((l) => ({ ...l, [p.name]: null }));
      setLocal((l) => ({ ...l, [p.name]: { ...r.priorities, [t]: v } }));
      setDetached({ pawn: p.name, display: p.display, role: r });
    } else setLocal((l) => ({ ...l, [p.name]: { ...l[p.name], [t]: v } }));
    onSet([p.name], { [t]: v });
  };

  /** A removed role's pawns turn Custom with the same values. */
  const detachRemoved = (id: string) => {
    const gone = roles.find((r) => r.id === id);
    const mine = pawns.filter((p) => roleIdOf(p) === id);
    if (!gone || !mine.length) return;
    setLocalRole((l) => ({ ...l, ...Object.fromEntries(mine.map((p) => [p.name, null])) }));
    setLocal((l) => ({ ...l, ...Object.fromEntries(mine.map((p) => [p.name, { ...gone.priorities }])) }));
  };

  const roleOp = (body: StoryRolesRequest) => {
    if (!onRoleOp) return;
    if (body.op === 'remove') detachRemoved(body.id);
    onRoleOp(body);
  };

  const toggle = (name: string) =>
    setPicked((s) => {
      const n = new Set(s);
      if (n.has(name)) n.delete(name);
      else n.add(name);
      return n;
    });

  return { picked, setPicked, toggle, detached, setDetached, roleIdOf, roleOf, value, assign, set, roleOp };
}

function RoleSelect({
  roles,
  value,
  onChange,
  label,
  testId,
  canWrite,
}: {
  roles: ReadonlyArray<StoryRole>;
  value: string | null;
  onChange: (v: string | null) => void;
  label: string;
  testId?: string;
  canWrite: boolean;
}) {
  return (
    <select
      aria-label={label}
      data-testid={testId}
      value={value ?? ''}
      disabled={!canWrite}
      onChange={(e) => onChange(e.target.value || null)}
      className="h-6 max-w-28 rounded border border-border bg-surface-2 px-1 text-[11px]"
    >
      <option value="">Custom</option>
      {roles.map((r) => (
        <option key={r.id} value={r.id}>
          {r.name}
        </option>
      ))}
    </select>
  );
}

export function WorkPanel(props: WorkProps) {
  const { pawns, ai, canWrite, roles = [], onRole, onRoleOp } = props;
  const [sort, setSort] = useState<SortKey>(null);
  const [showRoles, setShowRoles] = useState(false);
  const rows = useMemo(() => sortPawns(pawns, sort), [pawns, sort]);
  const g = useWorkGrid(props);
  const pickedNames = rows.filter((p) => g.picked.has(p.name)).map((p) => p.name);
  const withRoles = !!onRole;
  const roleSelect = (value: string | null, onChange: (v: string | null) => void, label: string, testId?: string) => (
    <RoleSelect roles={roles} value={value} onChange={onChange} label={label} testId={testId} canWrite={canWrite} />
  );

  return (
    <section
      className={cn(panel, 'pointer-events-auto max-h-[46vh] overflow-auto p-2')}
      aria-label="Work priorities"
      data-testid="work-panel"
    >
      {withRoles && pickedNames.length > 0 ? (
        <div className="mb-1 flex items-center gap-2 px-1 text-[11px]" data-testid="role-bulk">
          <span className="font-semibold">{pickedNames.length} selected</span>
          <span className="text-muted">Set role:</span>
          {roleSelect(
            null,
            (v) => g.assign(pickedNames, v),
            `Set role for ${pickedNames.length} selected pawns`,
            'role-bulk-select',
          )}
          <button type="button" className="text-subtle hover:text-fg" onClick={() => g.setPicked(new Set())}>
            Clear
          </button>
        </div>
      ) : null}
      <table className="border-separate border-spacing-0 text-xs">
        <thead>
          <WorkHeader
            withRoles={withRoles}
            sort={sort}
            onSort={(col) => setSort(nextSort(sort, col))}
            allPicked={rows.length > 0 && pickedNames.length === rows.length}
            onPickAll={(on) => g.setPicked(on ? new Set(rows.map((p) => p.name)) : new Set())}
          />
        </thead>
        <tbody>
          {rows.map((p) => (
            <WorkRow
              key={p.name}
              p={p}
              r={withRoles ? g.roleOf(p) : null}
              withRoles={withRoles}
              canWrite={canWrite}
              picked={g.picked.has(p.name)}
              onToggle={() => g.toggle(p.name)}
              roleSelect={roleSelect(
                g.roleIdOf(p),
                (v) => g.assign([p.name], v),
                `${p.display} role`,
                `role-select-${p.name}`,
              )}
              value={(t) => g.value(p, t)}
              onSet={(t, back) => g.set(p, t, back)}
            />
          ))}
          {ai.map((a) => (
            <AiWorkRow key={a.name} a={a} withRoles={withRoles} />
          ))}
        </tbody>
      </table>
      {g.detached ? (
        <DetachedNote
          detached={g.detached}
          onUndo={(d) => {
            g.assign([d.pawn], d.role.id);
            g.setDetached(null);
          }}
        />
      ) : null}
      <WorkFooter
        withRoles={withRoles}
        editable={!!onRoleOp}
        showRoles={showRoles}
        onToggleRoles={() => setShowRoles((s) => !s)}
      />
      {showRoles && onRoleOp ? <RolesEditor roles={roles} canWrite={canWrite} onOp={g.roleOp} /> : null}
    </section>
  );
}

/** The grid's hint and the Roles toggle (with a roles editor to open). */
function WorkFooter({
  withRoles,
  editable,
  showRoles,
  onToggleRoles,
}: {
  withRoles: boolean;
  editable: boolean;
  showRoles: boolean;
  onToggleRoles: () => void;
}) {
  return (
    <div className="mt-1 flex items-center gap-2 px-1">
      <p className="text-[10.5px] text-subtle">
        1 is done first; ties go left to right, then to skill. Right-click or Shift-click counts down.
        {withRoles ? ' Dashed cells come from the pawn’s role.' : ''}
      </p>
      {editable ? (
        <Button
          size="sm"
          variant={showRoles ? 'subtle' : 'ghost'}
          className="ml-auto"
          onClick={onToggleRoles}
          aria-expanded={showRoles}
          data-testid="roles-toggle"
        >
          <Users className="size-3.5" /> Roles
        </Button>
      ) : null}
    </div>
  );
}

/** One pawn's row: its tick, name, role and a cell per work type. */
function WorkRow({
  p,
  r,
  withRoles,
  canWrite,
  picked,
  onToggle,
  roleSelect,
  value,
  onSet,
}: {
  p: StoryPawn;
  r: StoryRole | null;
  withRoles: boolean;
  canWrite: boolean;
  picked: boolean;
  onToggle: () => void;
  roleSelect: ReactNode;
  value: (t: string) => number;
  onSet: (t: string, back: boolean) => void;
}) {
  return (
    <tr data-row={p.name} data-role={r?.id ?? 'custom'}>
      {withRoles ? (
        <td className="pr-1">
          <input
            type="checkbox"
            aria-label={`Select ${p.display}`}
            checked={picked}
            onChange={onToggle}
            className="size-3.5 accent-[var(--accent)]"
          />
        </td>
      ) : null}
      <td className="pr-2">
        <span className="flex items-center gap-1.5">
          <Avatar name={p.name} size={18} />
          <span className="font-semibold">{p.display}</span>
        </span>
      </td>
      {withRoles ? (
        <td className="pr-1">
          <span className="flex items-center gap-1">
            {r ? <RoleIcon icon={r.icon} name={r.name} className="text-accent" /> : null}
            {roleSelect}
          </span>
        </td>
      ) : null}
      {WORK_TYPES.map((t) => (
        <WorkCell key={t} p={p} t={t} v={value(t)} r={r} canWrite={canWrite} onSet={(back) => onSet(t, back)} />
      ))}
    </tr>
  );
}

/** The header row: tick-all, Pawn and Role, and one sortable column per work type. */
function WorkHeader({
  withRoles,
  sort,
  onSort,
  allPicked,
  onPickAll,
}: {
  withRoles: boolean;
  sort: SortKey;
  onSort: (col: Parameters<typeof nextSort>[1]) => void;
  allPicked: boolean;
  onPickAll: (on: boolean) => void;
}) {
  return (
    <tr>
      {withRoles ? (
        <th className="sticky top-0 bg-surface pr-1">
          <input
            type="checkbox"
            aria-label="Select every pawn"
            checked={allPicked}
            onChange={(e) => onPickAll(e.target.checked)}
            className="size-3.5 accent-[var(--accent)]"
          />
        </th>
      ) : null}
      <th className="sticky top-0 bg-surface px-2 text-left">
        <button type="button" onClick={() => onSort('name')} className="font-semibold">
          Pawn
        </button>
      </th>
      {withRoles ? <th className="sticky top-0 bg-surface px-1 text-left text-[11px] text-muted">Role</th> : null}
      {WORK_TYPES.map((t) => (
        <th key={t} className="sticky top-0 bg-surface px-0.5 pb-1">
          <button
            type="button"
            onClick={() => onSort(t)}
            className={cn(
              'w-11 rounded py-0.5 text-[11px] font-semibold',
              sort?.col === t ? 'bg-accent-soft' : 'text-muted hover:text-fg',
            )}
            title={`Sort by ${WORK_LABELS[t]} skill`}
          >
            {WORK_LABELS[t]}
            {sortMark(sort, t)}
          </button>
        </th>
      ))}
    </tr>
  );
}

/** " ▾" / " ▴" on the sorted column. */
function sortMark(sort: SortKey, col: string): string {
  if (sort?.col !== col) return '';
  return sort.dir === 1 ? ' ▾' : ' ▴';
}

type Skill = StoryPawn['skills'][string] | undefined;

function cellLabel(p: StoryPawn, t: string, v: number, r: StoryRole | null, sk: Skill): string {
  const from = r ? ` (from ${r.name})` : '';
  return `${p.display} ${WORK_LABELS[t]}: ${v || 'off'}${from} (skill ${sk?.level ?? 0}${sk?.passion ? ', passion' : ''})`;
}

function cellTitle(r: StoryRole | null, sk: Skill): string {
  if (r) return `From the ${r.name} role · skill ${sk?.level ?? 0}/20 · click to customise (the pawn turns Custom)`;
  return `Skill ${sk?.level ?? 0}/20${sk?.passion ? ' · loves this work' : ''} · click: 1 → 4 → off`;
}

/** The skill as the cell's tint (paler from a role). */
function cellTint(sk: Skill, r: StoryRole | null): string {
  return `color-mix(in oklab, var(--accent) ${Math.round(skillTint(sk?.level) * (r ? 22 : 42))}%, var(--surface-2))`;
}

function valueTone(v: number): string {
  if (v === 1) return 'text-fg';
  return v === 0 ? 'text-subtle' : 'text-muted';
}

/** One priority cell: the value (1–4 or off), the skill as its tint and corner number, the passion flame; dashed from a role. */
function WorkCell({
  p,
  t,
  v,
  r,
  canWrite,
  onSet,
}: {
  p: StoryPawn;
  t: string;
  v: number;
  r: StoryRole | null;
  canWrite: boolean;
  onSet: (back: boolean) => void;
}) {
  const sk = p.skills[t];
  return (
    <td className="p-0.5">
      <button
        type="button"
        data-cell={`${p.name}:${t}`}
        data-from-role={r ? r.id : undefined}
        aria-label={cellLabel(p, t, v, r, sk)}
        onClick={(e) => onSet(e.shiftKey)}
        onContextMenu={(e) => {
          e.preventDefault();
          onSet(true);
        }}
        disabled={!canWrite}
        className={cn(
          'relative h-7 w-11 rounded border text-[13px] font-bold tabular-nums',
          r ? 'border-dashed border-accent/50' : 'border-border',
        )}
        style={{ background: cellTint(sk, r) }}
        title={cellTitle(r, sk)}
      >
        <span className={cn(valueTone(v), r && 'font-semibold italic opacity-80')}>{v || '·'}</span>
        {sk?.passion ? <Flame className="absolute top-0.5 right-0.5 size-2.5 text-[#ff9f43]" aria-hidden /> : null}
        <span className="absolute bottom-0.5 left-1 text-[8px] font-medium text-subtle">{sk?.level ?? 0}</span>
      </button>
    </td>
  );
}

function AiWorkRow({ a, withRoles }: { a: StoryAiBody; withRoles: boolean }) {
  return (
    <tr className="opacity-60" data-ai-row={a.name}>
      {withRoles ? <td /> : null}
      <td className="pr-2">
        <span className="flex items-center gap-1.5">
          <Avatar name={a.name} size={18} ai />
          <span className="font-semibold">{a.name}</span>
        </span>
      </td>
      <td colSpan={WORK_TYPES.length + (withRoles ? 1 : 0)} className="px-2 text-[11px] text-muted italic">
        <AiMark className="mr-1 inline size-3 text-[#a78bfa]" />
        AI-managed: send it directives instead
      </td>
    </tr>
  );
}

function DetachedNote({ detached, onUndo }: { detached: Detached; onUndo: (d: Detached) => void }) {
  return (
    <div
      className="mt-1 flex items-center gap-2 rounded bg-surface-2 px-2 py-1 text-[11px]"
      role="status"
      data-testid="role-detached"
    >
      <span>
        {detached.display} is now Custom (was {detached.role.name}).
      </span>
      <button
        type="button"
        className="flex items-center gap-1 font-semibold text-accent hover:underline"
        onClick={() => onUndo(detached)}
      >
        <Undo2 className="size-3" /> Undo
      </button>
    </div>
  );
}
