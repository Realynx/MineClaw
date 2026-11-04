'use client';

/**
 * The Work tab's role editor (docs/web/story-mode.md "Roles"): one row per role in the same 1–4 grid as the pawns, so a role reads
 * like a pawn's row. Click a cell to cycle it (every pawn with the role follows at once), rename, pick an icon, delete (its pawns
 * turn Custom with the same values), add a role, reset the built-in ones.
 */
import { roleSummary, type StoryRole, type StoryRolesRequest } from '@mineclaw/contracts';
import { Check, Pencil, Plus, RotateCcw, Trash2, X } from 'lucide-react';
import { useState } from 'react';
import { Button, Input } from '@/components/ui/primitives';
import { cn } from '@/lib/utils';
import { cyclePriority, WORK_LABELS, WORK_TYPES } from '../model';
import { ROLE_ICON_KEYS, RoleIcon } from './roleIcons';

/** The icon after this one (the last wraps to none: the name's initial). */
export const nextIcon = (icon: string | null | undefined): string | null => {
  const i = icon ? ROLE_ICON_KEYS.indexOf(icon) : -1;
  return i + 1 < ROLE_ICON_KEYS.length ? ROLE_ICON_KEYS[i + 1]! : null;
};

export function RolesEditor({
  roles,
  canWrite,
  onOp,
}: {
  roles: ReadonlyArray<StoryRole>;
  canWrite: boolean;
  onOp: (body: StoryRolesRequest) => void;
}) {
  const [local, setLocal] = useState<Record<string, Record<string, number>>>({});
  const [renaming, setRenaming] = useState<{ id: string; name: string } | null>(null);
  const [confirmDelete, setConfirmDelete] = useState<string | null>(null);
  const [adding, setAdding] = useState('');
  const value = (r: StoryRole, t: string) => local[r.id]?.[t] ?? r.priorities[t] ?? 0;
  const cycle = (r: StoryRole, t: string, back: boolean) => {
    if (!canWrite) return;
    const v = cyclePriority(value(r, t), back);
    setLocal((l) => ({ ...l, [r.id]: { ...l[r.id], [t]: v } }));
    onOp({ op: 'update', role: { id: r.id, priorities: { [t]: v } } });
  };
  const rename = () => {
    if (!renaming) return;
    const name = renaming.name.trim();
    if (name) onOp({ op: 'update', role: { id: renaming.id, name } });
    setRenaming(null);
  };
  const add = () => {
    const name = adding.trim();
    if (!name) return;
    onOp({ op: 'add', role: { name } });
    setAdding('');
  };
  return (
    <div className="mt-2 border-t border-border pt-2" data-testid="roles-editor">
      <div className="mb-1 flex items-center gap-2 px-1">
        <span className="text-[11px] font-semibold tracking-wide text-muted uppercase">Roles</span>
        <span className="text-[10.5px] text-subtle">Editing a role changes every pawn that has it.</span>
      </div>
      <table className="border-separate border-spacing-0 text-xs">
        <tbody>
          {roles.map((r) => (
            <tr key={r.id} data-role-row={r.id}>
              <td className="pr-2">
                <span className="flex min-w-36 items-center gap-1">
                  <button
                    type="button"
                    aria-label={`${r.name} icon: ${r.icon ?? 'initial'} (click for the next)`}
                    title="Click for the next icon"
                    disabled={!canWrite}
                    onClick={() => onOp({ op: 'update', role: { id: r.id, icon: nextIcon(r.icon) } })}
                    className="grid size-5 place-items-center rounded hover:bg-surface-2"
                  >
                    <RoleIcon icon={r.icon} name={r.name} className="text-accent" />
                  </button>
                  {renaming?.id === r.id ? (
                    <form
                      className="flex items-center gap-0.5"
                      onSubmit={(e) => {
                        e.preventDefault();
                        rename();
                      }}
                    >
                      <Input
                        autoFocus
                        aria-label="Role name"
                        value={renaming.name}
                        maxLength={40}
                        onChange={(e) => setRenaming({ id: r.id, name: e.target.value })}
                        onKeyDown={(e) => e.key === 'Escape' && setRenaming(null)}
                        className="h-6 w-28 px-1 text-xs"
                      />
                      <button type="submit" aria-label="Save name" className="text-muted hover:text-fg">
                        <Check className="size-3.5" />
                      </button>
                    </form>
                  ) : (
                    <>
                      <span className="font-semibold" title={roleSummary(r, (t) => WORK_LABELS[t] ?? t)}>
                        {r.name}
                      </span>
                      {canWrite ? (
                        <button
                          type="button"
                          aria-label={`Rename ${r.name}`}
                          onClick={() => setRenaming({ id: r.id, name: r.name })}
                          className="text-subtle hover:text-fg"
                        >
                          <Pencil className="size-3" />
                        </button>
                      ) : null}
                    </>
                  )}
                </span>
              </td>
              {WORK_TYPES.map((t) => {
                const v = value(r, t);
                return (
                  <td key={t} className="p-0.5">
                    <button
                      type="button"
                      data-role-cell={`${r.id}:${t}`}
                      aria-label={`${r.name} ${WORK_LABELS[t]}: ${v || 'off'}`}
                      onClick={(e) => cycle(r, t, e.shiftKey)}
                      onContextMenu={(e) => {
                        e.preventDefault();
                        cycle(r, t, true);
                      }}
                      disabled={!canWrite}
                      className="h-6 w-11 rounded border border-dashed border-border bg-surface-2 text-[12px] font-bold tabular-nums"
                      title="click: 1 → 4 → off"
                    >
                      <span className={cn(v === 1 ? 'text-fg' : v === 0 ? 'text-subtle' : 'text-muted')}>{v || '·'}</span>
                    </button>
                  </td>
                );
              })}
              <td className="px-1 text-[10.5px] whitespace-nowrap text-subtle" title={r.pawns.join(', ')}>
                {r.pawns.length ? `${r.pawns.length} pawn${r.pawns.length > 1 ? 's' : ''}` : ''}
              </td>
              <td className="pl-1">
                {canWrite ? (
                  confirmDelete === r.id ? (
                    <span className="flex items-center gap-1 text-[10.5px]">
                      <button
                        type="button"
                        className="font-semibold text-danger"
                        onClick={() => {
                          setConfirmDelete(null);
                          onOp({ op: 'remove', id: r.id });
                        }}
                        title={r.pawns.length ? 'Its pawns keep these priorities as Custom' : undefined}
                      >
                        Delete
                      </button>
                      <button type="button" aria-label="Keep" onClick={() => setConfirmDelete(null)}>
                        <X className="size-3" />
                      </button>
                    </span>
                  ) : (
                    <button
                      type="button"
                      aria-label={`Delete ${r.name}`}
                      onClick={() => setConfirmDelete(r.id)}
                      className="text-subtle hover:text-danger"
                    >
                      <Trash2 className="size-3" />
                    </button>
                  )
                ) : null}
              </td>
            </tr>
          ))}
        </tbody>
      </table>
      {canWrite ? (
        <div className="mt-1.5 flex flex-wrap items-center gap-1.5 px-1">
          <form
            className="flex items-center gap-1"
            onSubmit={(e) => {
              e.preventDefault();
              add();
            }}
          >
            <Input
              aria-label="New role name"
              placeholder="New role…"
              value={adding}
              maxLength={40}
              onChange={(e) => setAdding(e.target.value)}
              className="h-7 w-32 px-1.5 text-xs"
            />
            <Button type="submit" size="sm" variant="ghost" disabled={!adding.trim()}>
              <Plus className="size-3.5" /> Add role
            </Button>
          </form>
          <Button
            type="button"
            size="sm"
            variant="ghost"
            onClick={() => {
              setLocal({});
              onOp({ op: 'reset_defaults' });
            }}
            title="The built-in roles as shipped (removed ones come back); your own roles stay"
          >
            <RotateCcw className="size-3.5" /> Reset built-in roles
          </Button>
        </div>
      ) : null}
    </div>
  );
}
