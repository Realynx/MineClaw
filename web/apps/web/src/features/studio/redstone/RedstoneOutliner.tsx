'use client';

/**
 * The redstone Library tab's outliner: the design's placed components on the shared ComponentOutliner (select, frame,
 * duplicate, unlink, delete, and Ctrl- or Shift-click ticks to combine), each row with its definition, its blocks and how
 * many of its ports are joined; under it the selected one's name, corner, turn and mirror, ungroup, and what each of its
 * ports is joined to. Selection is the page's: a component picked in the world is the one picked here.
 */
import type { Pos } from '@mineclaw/redstone';
import {
  Boxes,
  CopyPlus,
  Cpu,
  FlipHorizontal2,
  FlipVertical2,
  RotateCcw,
  RotateCw,
  Trash2,
  Ungroup,
  Unlink,
} from 'lucide-react';
import { useState } from 'react';
import { cn } from '@/lib/utils';
import { ComponentOutliner, OutlinerRowAction, type OutlinerItem } from '../components/ComponentOutliner';
import type { ComponentInstance } from './componentData';
import type { PlaceableDef, PortLink, PortState } from './RedstoneComponentController';
import type { RedstoneComponents } from './useRedstoneComponents';

/** Three whole numbers, by spaces or commas. */
export function parsePos(text: string): Pos | null {
  const parts = text.trim().split(/[\s,]+/);
  if (parts.length !== 3 || !parts.every((s) => /^-?\d+$/.test(s))) return null;
  return [Number(parts[0]), Number(parts[1]), Number(parts[2])];
}

const smallButton = 'rounded p-0.5 text-muted hover:bg-surface-2 hover:text-fg disabled:opacity-40';

/** What a port is joined to, in words; `rowOf` names another component as its outliner row does. */
export function linkText(link: PortLink | null, rowOf: (id: string) => string): string {
  if (!link) return 'open';
  return link.to === 'dust' ? 'joined to dust' : `joined to ${rowOf(link.id)} ${link.port}`;
}

const portLine = (p: PortState, rowOf: (id: string) => string) =>
  `${p.kind === 'in' ? '→' : '←'}${p.name} ${linkText(p.link, rowOf)}`;

export function RedstoneOutliner({
  components,
  defs,
}: {
  components: RedstoneComponents;
  defs: readonly PlaceableDef[];
}) {
  const c = components.controller;
  if (!c) return null;
  const list = components.snap.components;
  const own = components.snap.customDefs;
  const defName = (inst: ComponentInstance) => defs.find((d) => d.id === inst.def)?.name ?? inst.def;
  const nameOf = (inst: ComponentInstance) => inst.label ?? defName(inst);
  const rows = new Map(list.map((inst, i) => [inst.id, `${nameOf(inst)} ${i + 1}`]));
  const rowOf = (id: string) => rows.get(id) ?? id;
  // Kept by the controller per change of the components or the canvas: this renders at the aim's rate while flying.
  const facts = c.rows();
  const ports = new Map(list.map((inst) => [inst.id, facts.get(inst.id)?.ports ?? []]));
  const items: OutlinerItem[] = list.map((inst) => {
    const blocks = facts.get(inst.id)?.blocks ?? 0;
    const mine = ports.get(inst.id) ?? [];
    const joined = mine.filter((p) => p.link !== null).length;
    const counts = `${blocks} block${blocks === 1 ? '' : 's'} · ${joined}/${mine.length} ports joined`;
    return {
      id: inst.id,
      name: nameOf(inst),
      detail: inst.label ? `${defName(inst)} · ${counts}` : counts,
      title: [`${nameOf(inst)} · ${inst.at.join(', ')}`, ...mine.map((p) => portLine(p, rowOf))].join('\n'),
      glyph: own.some((d) => d.id === inst.def) ? (
        <Boxes className="size-3.5 shrink-0 text-muted" aria-hidden />
      ) : (
        <Cpu className="size-3.5 shrink-0 text-muted" aria-hidden />
      ),
    };
  });
  const disabled = !components.editable;
  const selected = list.find((inst) => inst.id === components.selectedId) ?? null;
  return (
    <>
      <ComponentOutliner
        items={items}
        selectedId={components.selectedId}
        disabled={disabled}
        onSelect={components.select}
        onFrame={components.frame}
        onDuplicate={components.duplicate}
        onCombine={components.combine}
        checked={components.checked}
        onCheck={components.setChecked}
        labels={{ list: 'Redstone components', combine: 'Combine redstone components', testId: 'redstone-components' }}
        hint="Select here or in the world, and drag a part there to move it. Double-click to frame. Ctrl- or Shift-click ticks parts to combine."
        empty={<p>Nothing placed yet: choose a gate above and place it.</p>}
        combineHint="Choose parts to move, turn and copy as one component of your own (Ctrl+G). Ungroup gives them back (Ctrl+Shift+G)."
        rowActions={(item, label) => (
          <>
            <OutlinerRowAction
              label={`Duplicate ${label}`}
              title="Duplicate: a copy beside it (Ctrl/Cmd+D)"
              disabled={disabled}
              onClick={() => components.duplicate(item.id)}
            >
              <CopyPlus className="size-3.5" />
            </OutlinerRowAction>
            <OutlinerRowAction
              label={`Unlink ${label}`}
              title="Unlink: keep its blocks as plain cells"
              disabled={disabled}
              onClick={() => c.detach(item.id)}
            >
              <Unlink className="size-3.5" />
            </OutlinerRowAction>
            <OutlinerRowAction
              label={`Delete ${label}`}
              title="Delete it and its blocks; what stood there comes back (Ctrl+Z undoes)"
              danger
              disabled={disabled}
              onClick={() => components.remove(item.id)}
            >
              <Trash2 className="size-3.5" />
            </OutlinerRowAction>
          </>
        )}
      />
      {selected ? (
        <SelectedComponent
          key={`${selected.id}:${selected.at.join(',')}`}
          inst={selected}
          row={rowOf(selected.id)}
          defName={defName(selected)}
          ports={ports.get(selected.id) ?? []}
          rowOf={rowOf}
          components={components}
          ungroupable={own.some((d) => d.id === selected.def)}
          disabled={disabled}
        />
      ) : null}
    </>
  );
}

/** The selected component: its name, its corner (typed, as well as dragged), turn and mirror, ungroup, and its ports. */
function SelectedComponent(p: {
  inst: ComponentInstance;
  row: string;
  defName: string;
  ports: readonly PortState[];
  rowOf: (id: string) => string;
  components: RedstoneComponents;
  ungroupable: boolean;
  disabled: boolean;
}) {
  const { inst, row, components, disabled } = p;
  const c = components.controller!;
  const [at, setAt] = useState(inst.at.join(' '));
  return (
    <section
      className="border-b border-border px-3 py-2 text-[11px]"
      aria-label={`Selected component ${row}`}
      data-testid="redstone-selected-component"
    >
      <div className="flex items-center gap-1">
        <input
          aria-label={`Name of ${row}`}
          defaultValue={inst.label ?? ''}
          placeholder={p.defName}
          disabled={disabled}
          onKeyDown={(e) => e.key === 'Enter' && e.currentTarget.blur()}
          onBlur={(e) => e.target.value.trim() !== (inst.label ?? '') && c.rename(inst.id, e.target.value)}
          className="h-5 min-w-0 flex-1 rounded border border-transparent bg-transparent px-1 text-fg hover:border-border"
        />
        <button
          type="button"
          className={smallButton}
          aria-label={`Turn ${row}`}
          title="Turn clockwise (R or ])"
          disabled={disabled}
          onClick={() => c.turn(inst.id)}
        >
          <RotateCw className="size-3.5" />
        </button>
        <button
          type="button"
          className={smallButton}
          aria-label={`Turn ${row} back`}
          title="Turn counter-clockwise (Shift+R or [)"
          disabled={disabled}
          onClick={() => c.turn(inst.id, 3)}
        >
          <RotateCcw className="size-3.5" />
        </button>
        <button
          type="button"
          className={smallButton}
          aria-label={`Mirror ${row} east-west`}
          title="Mirror east-west (M)"
          disabled={disabled}
          onClick={() => c.mirror(inst.id, 'x')}
        >
          <FlipHorizontal2 className="size-3.5" />
        </button>
        <button
          type="button"
          className={smallButton}
          aria-label={`Mirror ${row} north-south`}
          title="Mirror north-south (Shift+M)"
          disabled={disabled}
          onClick={() => c.mirror(inst.id, 'z')}
        >
          <FlipVertical2 className="size-3.5" />
        </button>
        {p.ungroupable ? (
          <button
            type="button"
            className={smallButton}
            aria-label={`Ungroup ${row}`}
            title="Ungroup: its parts become components again (Ctrl+Shift+G)"
            disabled={disabled}
            onClick={() => components.ungroup(inst.id)}
          >
            <Ungroup className="size-3.5" />
          </button>
        ) : null}
      </div>
      <div className="mt-0.5 flex items-center gap-1 text-muted">
        <span>{p.defName} at</span>
        <input
          aria-label={`Corner of ${row}`}
          value={at}
          disabled={disabled}
          onChange={(e) => setAt(e.target.value)}
          onKeyDown={(e) => e.key === 'Enter' && e.currentTarget.blur()}
          onBlur={() => {
            const to = parsePos(at);
            if (!to || to.join(' ') === inst.at.join(' ') || !c.move(inst.id, to)) setAt(inst.at.join(' '));
          }}
          className="h-5 w-20 rounded border border-border bg-surface px-1 font-mono text-fg"
        />
      </div>
      {p.ports.length > 0 ? (
        <ul className="mt-1 space-y-0.5 font-mono text-[10px]" aria-label={`Ports of ${row}`}>
          {p.ports.map((port) => (
            <li
              key={`${port.kind}:${port.name}`}
              data-testid="component-port"
              data-joined={port.link !== null}
              className={port.link ? 'text-ok' : 'text-warn'}
            >
              <span className={cn('inline-block w-7', port.kind === 'in' ? 'text-muted' : 'text-fg')}>
                {port.kind === 'in' ? 'in' : 'out'}
              </span>
              {port.name} {linkText(port.link, p.rowOf)}
            </li>
          ))}
        </ul>
      ) : (
        <p className="mt-1 text-muted">No ports: nothing joins it.</p>
      )}
    </section>
  );
}
