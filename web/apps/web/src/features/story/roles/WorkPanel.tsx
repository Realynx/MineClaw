'use client';

/**
 * The Work tab: a grid of pawns by work type (click cycles 1–4 and off, skill as the tint, passion mark, sort by a column), with a
 * role at the start of each row (docs/web/story-mode.md "Roles"). A pawn with a role shows the role's priorities ("from role":
 * dashed cells); clicking one of them detaches the pawn to Custom with the same values plus that change, with an Undo. Rows can be
 * ticked to give several pawns a role at once, and the Roles section below edits the roles themselves.
 */
import type { StoryAiBody, StoryPawn, StoryRole, StoryRolesRequest } from '@mineclaw/contracts';
import { Flame, Undo2, Users } from 'lucide-react';
import { useEffect, useMemo, useState } from 'react';
import { Button } from '@/components/ui/primitives';
import { cn } from '@/lib/utils';
import { cyclePriority, nextSort, skillTint, sortPawns, WORK_LABELS, WORK_TYPES, type SortKey } from '../model';
import { panel } from '../StoryHud';
import { AiMark, Avatar } from '../visuals';
import { RoleIcon } from './roleIcons';
import { RolesEditor } from './RolesEditor';

/** A detach to undo: the pawn, its role before, shown for a few seconds. */
type Detached = { pawn: string; display: string; role: StoryRole };

export function WorkPanel({
  pawns,
  ai,
  canWrite,
  onSet,
  roles = [],
  selected = [],
  onRole,
  onRoleOp,
}: {
  pawns: StoryPawn[];
  ai: StoryAiBody[];
  canWrite: boolean;
  onSet: (pawns: string[], work: Record<string, number>) => void;
  roles?: ReadonlyArray<StoryRole>;
  /** The pawns selected on the map: ticked when the panel opens. */
  selected?: ReadonlyArray<string>;
  onRole?: (pawns: string[], role: string | null) => void;
  onRoleOp?: (body: StoryRolesRequest) => void;
}) {
  const [sort, setSort] = useState<SortKey>(null);
  const [local, setLocal] = useState<Record<string, Record<string, number>>>({});
  const [localRole, setLocalRole] = useState<Record<string, string | null>>({});
  const [picked, setPicked] = useState<ReadonlySet<string>>(
    () => new Set(selected.filter((n) => pawns.some((p) => p.name === n))),
  );
  const [detached, setDetached] = useState<Detached | null>(null);
  const [showRoles, setShowRoles] = useState(false);
  const rows = useMemo(() => sortPawns(pawns, sort), [pawns, sort]);
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

  const roleOp = (body: StoryRolesRequest) => {
    if (!onRoleOp) return;
    if (body.op === 'remove') {
      // Its pawns turn Custom with the same values.
      const gone = roles.find((r) => r.id === body.id);
      const mine = pawns.filter((p) => roleIdOf(p) === body.id);
      if (gone && mine.length) {
        setLocalRole((l) => ({ ...l, ...Object.fromEntries(mine.map((p) => [p.name, null])) }));
        setLocal((l) => ({ ...l, ...Object.fromEntries(mine.map((p) => [p.name, { ...gone.priorities }])) }));
      }
    }
    onRoleOp(body);
  };

  const toggle = (name: string) =>
    setPicked((s) => {
      const n = new Set(s);
      if (n.has(name)) n.delete(name);
      else n.add(name);
      return n;
    });
  const pickedNames = rows.filter((p) => picked.has(p.name)).map((p) => p.name);
  const withRoles = !!onRole;

  const roleSelect = (value: string | null, onChange: (v: string | null) => void, label: string, testId?: string) => (
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
            (v) => assign(pickedNames, v),
            `Set role for ${pickedNames.length} selected pawns`,
            'role-bulk-select',
          )}
          <button type="button" className="text-subtle hover:text-fg" onClick={() => setPicked(new Set())}>
            Clear
          </button>
        </div>
      ) : null}
      <table className="border-separate border-spacing-0 text-xs">
        <thead>
          <tr>
            {withRoles ? (
              <th className="sticky top-0 bg-surface pr-1">
                <input
                  type="checkbox"
                  aria-label="Select every pawn"
                  checked={rows.length > 0 && pickedNames.length === rows.length}
                  onChange={(e) => setPicked(e.target.checked ? new Set(rows.map((p) => p.name)) : new Set())}
                  className="size-3.5 accent-[var(--accent)]"
                />
              </th>
            ) : null}
            <th className="sticky top-0 bg-surface px-2 text-left">
              <button type="button" onClick={() => setSort(nextSort(sort, 'name'))} className="font-semibold">
                Pawn
              </button>
            </th>
            {withRoles ? <th className="sticky top-0 bg-surface px-1 text-left text-[11px] text-muted">Role</th> : null}
            {WORK_TYPES.map((t) => (
              <th key={t} className="sticky top-0 bg-surface px-0.5 pb-1">
                <button
                  type="button"
                  onClick={() => setSort(nextSort(sort, t))}
                  className={cn(
                    'w-11 rounded py-0.5 text-[11px] font-semibold',
                    sort?.col === t ? 'bg-accent-soft' : 'text-muted hover:text-fg',
                  )}
                  title={`Sort by ${WORK_LABELS[t]} skill`}
                >
                  {WORK_LABELS[t]}
                  {sort?.col === t ? (sort.dir === 1 ? ' ▾' : ' ▴') : ''}
                </button>
              </th>
            ))}
          </tr>
        </thead>
        <tbody>
          {rows.map((p) => {
            const r = withRoles ? roleOf(p) : null;
            return (
              <tr key={p.name} data-row={p.name} data-role={r?.id ?? 'custom'}>
                {withRoles ? (
                  <td className="pr-1">
                    <input
                      type="checkbox"
                      aria-label={`Select ${p.display}`}
                      checked={picked.has(p.name)}
                      onChange={() => toggle(p.name)}
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
                      {roleSelect(roleIdOf(p), (v) => assign([p.name], v), `${p.display} role`, `role-select-${p.name}`)}
                    </span>
                  </td>
                ) : null}
                {WORK_TYPES.map((t) => {
                  const v = value(p, t);
                  const sk = p.skills[t];
                  return (
                    <td key={t} className="p-0.5">
                      <button
                        type="button"
                        data-cell={`${p.name}:${t}`}
                        data-from-role={r ? r.id : undefined}
                        aria-label={`${p.display} ${WORK_LABELS[t]}: ${v || 'off'}${r ? ` (from ${r.name})` : ''} (skill ${sk?.level ?? 0}${sk?.passion ? ', passion' : ''})`}
                        onClick={(e) => set(p, t, e.shiftKey)}
                        onContextMenu={(e) => {
                          e.preventDefault();
                          set(p, t, true);
                        }}
                        disabled={!canWrite}
                        className={cn(
                          'relative h-7 w-11 rounded border text-[13px] font-bold tabular-nums',
                          r ? 'border-dashed border-accent/50' : 'border-border',
                        )}
                        style={{
                          background: `color-mix(in oklab, var(--accent) ${Math.round(skillTint(sk?.level) * (r ? 22 : 42))}%, var(--surface-2))`,
                        }}
                        title={
                          r
                            ? `From the ${r.name} role · skill ${sk?.level ?? 0}/20 · click to customise (the pawn turns Custom)`
                            : `Skill ${sk?.level ?? 0}/20${sk?.passion ? ' · loves this work' : ''} · click: 1 → 4 → off`
                        }
                      >
                        <span
                          className={cn(
                            v === 1 ? 'text-fg' : v === 0 ? 'text-subtle' : 'text-muted',
                            r && 'font-semibold italic opacity-80',
                          )}
                        >
                          {v || '·'}
                        </span>
                        {sk?.passion ? (
                          <Flame className="absolute top-0.5 right-0.5 size-2.5 text-[#ff9f43]" aria-hidden />
                        ) : null}
                        <span className="absolute bottom-0.5 left-1 text-[8px] font-medium text-subtle">
                          {sk?.level ?? 0}
                        </span>
                      </button>
                    </td>
                  );
                })}
              </tr>
            );
          })}
          {ai.map((a) => (
            <tr key={a.name} className="opacity-60" data-ai-row={a.name}>
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
          ))}
        </tbody>
      </table>
      {detached ? (
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
            onClick={() => {
              assign([detached.pawn], detached.role.id);
              setDetached(null);
            }}
          >
            <Undo2 className="size-3" /> Undo
          </button>
        </div>
      ) : null}
      <div className="mt-1 flex items-center gap-2 px-1">
        <p className="text-[10.5px] text-subtle">
          1 is done first; ties go left to right, then to skill. Right-click or Shift-click counts down.
          {withRoles ? ' Dashed cells come from the pawn’s role.' : ''}
        </p>
        {onRoleOp ? (
          <Button
            size="sm"
            variant={showRoles ? 'subtle' : 'ghost'}
            className="ml-auto"
            onClick={() => setShowRoles((s) => !s)}
            aria-expanded={showRoles}
            data-testid="roles-toggle"
          >
            <Users className="size-3.5" /> Roles
          </Button>
        ) : null}
      </div>
      {showRoles && onRoleOp ? <RolesEditor roles={roles} canWrite={canWrite} onOp={roleOp} /> : null}
    </section>
  );
}
