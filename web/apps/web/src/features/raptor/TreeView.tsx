'use client';

/**
 * Lazy RAPTOR tree: roots first, children fetched when a row opens (`/api/raptor/tree?root&depth=1`). Revealing a node opens
 * every ancestor on its path. Keyboard: ↑/↓ move, →/← open/close, Enter selects.
 */
import type { RaptorTreeNode } from '@mineclaw/contracts';
import { ChevronRight } from 'lucide-react';
import { useEffect, useMemo, useRef, useState, type KeyboardEvent } from 'react';
import { useApi } from '@/lib/api/query';
import { cn, num } from '@/lib/utils';
import { Badge, SkeletonLines } from '@/components/ui/primitives';

function Children({
  root,
  depth,
  open,
  toggle,
  selected,
  onSelect,
}: {
  root: number;
  depth: number;
  open: Set<number>;
  toggle: (id: number) => void;
  selected: number | null;
  onSelect: (id: number) => void;
}) {
  const q = useApi('raptor.tree', { query: { root, depth: 1 } });
  const rows = useMemo(() => {
    const top = q.data?.nodes.find((n) => n.index === root);
    const byId = new Map(q.data?.nodes.map((n) => [n.index, n]));
    return (top?.children ?? []).map((c) => byId.get(c)).filter((n): n is RaptorTreeNode => !!n);
  }, [q.data, root]);
  if (q.isPending) return <SkeletonLines lines={2} className="py-1" />;
  return (
    <>
      {rows.map((n) => (
        <Row key={n.index} n={n} depth={depth} open={open} toggle={toggle} selected={selected} onSelect={onSelect} />
      ))}
    </>
  );
}

function Row({
  n,
  depth,
  open,
  toggle,
  selected,
  onSelect,
}: {
  n: RaptorTreeNode;
  depth: number;
  open: Set<number>;
  toggle: (id: number) => void;
  selected: number | null;
  onSelect: (id: number) => void;
}) {
  const isOpen = open.has(n.index);
  const isSel = selected === n.index;
  const ref = useRef<HTMLDivElement>(null);
  useEffect(() => {
    if (isSel) ref.current?.scrollIntoView({ block: 'nearest' });
  }, [isSel]);
  return (
    <>
      <div
        ref={ref}
        role="treeitem"
        aria-level={depth + 1}
        aria-expanded={n.isLeaf ? undefined : isOpen}
        aria-selected={isSel}
        data-node={n.index}
        tabIndex={isSel ? 0 : -1}
        onClick={() => onSelect(n.index)}
        onDoubleClick={() => !n.isLeaf && toggle(n.index)}
        className={cn(
          'group flex h-9 cursor-pointer items-center gap-2 rounded-md pr-3 text-[13px] outline-none',
          isSel ? 'bg-accent-soft text-fg' : 'hover:bg-surface-2',
          'focus-visible:ring-2 focus-visible:ring-[var(--ring)]',
        )}
        style={{ paddingLeft: `${8 + depth * 18}px` }}
      >
        <button
          tabIndex={-1}
          aria-hidden
          onClick={(e) => {
            e.stopPropagation();
            if (!n.isLeaf) toggle(n.index);
          }}
          className={cn('grid size-5 place-items-center rounded text-subtle hover:text-fg', n.isLeaf && 'invisible')}
        >
          <ChevronRight className={cn('size-3.5 transition-transform', isOpen && 'rotate-90')} />
        </button>
        <Badge tone={n.isLeaf ? 'neutral' : 'accent'} className="w-14 justify-center">
          {n.kindLabel}
        </Badge>
        <span className="min-w-0 flex-1 truncate text-fg">{n.title}</span>
        {n.dirty ? <Badge tone="warn">dirty</Badge> : null}
        <span className="tabular hidden w-28 shrink-0 text-right text-[11px] text-subtle sm:block">
          {n.isLeaf ? `${num(n.tokens)} tok` : `${n.childCount} children · ${num(n.tokens)} tok`}
        </span>
      </div>
      {isOpen && !n.isLeaf ? <Children root={n.index} depth={depth + 1} open={open} toggle={toggle} selected={selected} onSelect={onSelect} /> : null}
    </>
  );
}

export function TreeView({ selected, onSelect, reveal }: { selected: number | null; onSelect: (id: number) => void; reveal?: number[] }) {
  const roots = useApi('raptor.tree', { query: { depth: 0 } });
  const [open, setOpen] = useState<Set<number>>(new Set());
  const container = useRef<HTMLDivElement>(null);
  const revealKey = reveal?.join(',');
  useEffect(() => {
    if (!revealKey) return;
    setOpen((o) => new Set([...o, ...revealKey.split(',').map(Number)]));
  }, [revealKey]);
  const toggle = (id: number) =>
    setOpen((o) => {
      const next = new Set(o);
      if (next.has(id)) next.delete(id);
      else next.add(id);
      return next;
    });

  const onKey = (e: KeyboardEvent<HTMLDivElement>) => {
    const items = [...(container.current?.querySelectorAll<HTMLElement>('[role=treeitem]') ?? [])];
    const i = items.findIndex((el) => Number(el.dataset.node) === selected);
    const go = (j: number) => {
      const el = items[Math.max(0, Math.min(items.length - 1, j))];
      if (el) {
        onSelect(Number(el.dataset.node));
        el.focus();
      }
    };
    if (e.key === 'ArrowDown') go(i + 1);
    else if (e.key === 'ArrowUp') go(i - 1);
    else if (e.key === 'ArrowRight' && selected !== null && !open.has(selected)) toggle(selected);
    else if (e.key === 'ArrowLeft' && selected !== null && open.has(selected)) toggle(selected);
    else return;
    e.preventDefault();
  };

  return (
    <div ref={container} role="tree" aria-label="RAPTOR tree" onKeyDown={onKey} className="p-2">
      {roots.isPending ? (
        <SkeletonLines lines={4} className="p-3" />
      ) : roots.data?.note ? (
        <p className="p-6 text-center text-sm text-muted">{roots.data.note}</p>
      ) : (
        roots.data?.top.map((id) => {
          const n = roots.data.nodes.find((x) => x.index === id);
          return n ? <Row key={id} n={n} depth={0} open={open} toggle={toggle} selected={selected} onSelect={onSelect} /> : null;
        })
      )}
      {open.size > 0 ? (
        <button onClick={() => setOpen(new Set())} className="mt-2 ml-2 text-xs text-muted hover:text-fg">
          Collapse all
        </button>
      ) : null}
    </div>
  );
}
