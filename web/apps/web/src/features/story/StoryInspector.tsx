'use client';

/**
 * The bottom-left inspector for the selection: a pawn (state, job and queue, needs, skills, traits), several bodies, an AI
 * body (task, brain, its directives), chests (the Storage tab: priority, the loot tree with tri-state checks, search, allow
 * all / clear, copy / paste, label, contents) or a workbench (the Bills tab: add from the recipe picker, reorder, pause,
 * delete, and the bill dialog).
 */
import {
  STORY_STORAGE_PRIORITIES,
  storyItemLabel,
  type StoryAiBody,
  targetPos,
  type StoryBill,
  type StoryColonyState,
  type StoryOrder,
  type StoryFilter,
  type StoryPawn,
  type StoryRecipe,
  type StoryStorage,
  type StoryWorkbench,
} from '@mineclaw/contracts';
import { ChevronDown, ChevronRight, ClipboardPaste, Copy, Flame, GripVertical, MessageSquare, Minus, Pause, Play, Plus, Search, Shield, ShieldOff, Trash2, X } from 'lucide-react';
import { useMemo, useRef, useState } from 'react';
import { Dialog } from '@/components/ui/overlays';
import { Button, Input } from '@/components/ui/primitives';
import { cn } from '@/lib/utils';
import { WORK_LABELS, WORK_TYPES } from './model';
import { panel } from './StoryHud';
import { DirectiveList } from './StoryPanels';
import {
  addBillBody,
  allowAll,
  billProgress,
  billSummary,
  buildTree,
  CLEAR_FILTER,
  draftOf,
  filterSummary,
  itemAllowed,
  newDraft,
  nodeState,
  PRIORITY_LABELS,
  recipeLine,
  reorderBills,
  searchRecipes,
  searchTree,
  storageSetBody,
  toggleItem,
  toggleNode,
  updateBillBody,
  type BillDraft,
  type StorageClip,
  type TreeNode,
} from './storage';
import { useRecipes, type StoryWrite } from './useStory';
import { AiMark, Avatar, ItemIcon } from './visuals';

type V3 = [number, number, number];

function orderWhere(t: StoryOrder['target']): string {
  const p = targetPos(t);
  if (p) return p.map((n) => Math.floor(n)).join(', ');
  return t && typeof t === 'object' && !Array.isArray(t) && t.entity !== null && t.entity !== undefined ? `#${t.entity}` : '';
}
const k3 = (p: readonly number[]) => `${p[0]},${p[1]},${p[2]}`;

function Bar({ value, color, label }: { value: number; color: string; label: string }) {
  return (
    <div className="flex items-center gap-1.5 text-[11px]">
      <span className="w-10 text-muted">{label}</span>
      <span className="block h-2 flex-1 overflow-hidden rounded-full bg-surface-3">
        <span className="block h-full" style={{ width: `${Math.max(0, Math.min(1, value / 20)) * 100}%`, background: color }} />
      </span>
      <span className="w-8 text-right tabular-nums">{Math.round(value)}/20</span>
    </div>
  );
}

function Shell({ title, sub, onClose, children, avatar, testid }: { title: string; sub?: string; onClose: () => void; children: React.ReactNode; avatar?: React.ReactNode; testid?: string }) {
  return (
    <section className={cn(panel, 'pointer-events-auto flex max-h-[52vh] w-[21rem] flex-col')} aria-label={`Inspect ${title}`} data-testid={testid ?? 'inspector'}>
      <header className="flex items-center gap-2 border-b border-border px-2.5 py-2">
        {avatar}
        <div className="min-w-0 flex-1">
          <div className="truncate text-sm font-semibold">{title}</div>
          {sub ? <div className="truncate text-[11px] text-muted">{sub}</div> : null}
        </div>
        <button type="button" onClick={onClose} aria-label="Close the inspector" className="rounded p-0.5 hover:bg-surface-2">
          <X className="size-4" />
        </button>
      </header>
      <div className="min-h-0 flex-1 overflow-auto p-2.5">{children}</div>
    </section>
  );
}

function TabRow<T extends string>({ tabs, value, onChange }: { tabs: Array<[T, string]>; value: T; onChange: (t: T) => void }) {
  return (
    <div className="mb-2 flex gap-0.5 border-b border-border text-xs" role="tablist">
      {tabs.map(([id, label]) => (
        <button key={id} type="button" role="tab" aria-selected={value === id} onClick={() => onChange(id)} className={cn('-mb-px border-b-2 px-2 py-1 font-semibold', value === id ? 'border-accent text-fg' : 'border-transparent text-muted hover:text-fg')}>
          {label}
        </button>
      ))}
    </div>
  );
}

// ------------------------------------------------------------------ pawn

export function PawnInspector({ pawn, canWrite, onDraft, onClearQueue, onClose }: { pawn: StoryPawn; canWrite: boolean; onDraft: (d: boolean) => void; onClearQueue: () => void; onClose: () => void }) {
  const [tab, setTab] = useState<'overview' | 'skills' | 'orders'>('overview');
  return (
    <Shell title={pawn.display} sub={`${pawn.state ?? 'idle'}${pawn.online === false ? ' · offline' : ''}`} onClose={onClose} avatar={<Avatar name={pawn.name} size={30} />}>
      <TabRow<'overview' | 'skills' | 'orders'> tabs={[['overview', 'Overview'], ['skills', 'Skills'], ['orders', 'Orders']]} value={tab} onChange={setTab} />
      {tab === 'overview' ? (
        <div className="space-y-2 text-xs">
          <div className="rounded-md bg-surface-2 px-2 py-1.5">
            <div className="text-[10.5px] font-semibold tracking-wide text-subtle uppercase">Doing</div>
            <div className="font-medium">{pawn.job?.label ?? (pawn.drafted ? 'Drafted: awaiting orders' : 'Nothing')}</div>
            {(pawn.job?.progress !== null && pawn.job?.progress !== undefined) ? (
              <span className="mt-1 block h-1 overflow-hidden rounded-full bg-surface-3">
                <span className="block h-full bg-accent" style={{ width: `${pawn.job.progress * 100}%` }} />
              </span>
            ) : null}
          </div>
          <Bar value={pawn.hp ?? 20} color="#e5484d" label="Health" />
          <Bar value={pawn.food ?? 20} color="#e2a336" label="Food" />
          <div>
            <div className="mb-1 text-[10.5px] font-semibold tracking-wide text-subtle uppercase">Traits</div>
            <ul className="flex flex-wrap gap-1">
              {pawn.traits.map((t) => (
                <li key={t.id} className="rounded bg-surface-2 px-1.5 py-0.5" title={t.effect ?? undefined}>
                  {t.label}
                </li>
              ))}
            </ul>
          </div>
          {pawn.held ? <div className="text-muted">Holding {pawn.held}</div> : null}
          <Button size="sm" variant={pawn.drafted ? 'subtle' : 'secondary'} disabled={!canWrite} onClick={() => onDraft(!pawn.drafted)}>
            {pawn.drafted ? <ShieldOff className="size-3.5" /> : <Shield className="size-3.5" />}
            {pawn.drafted ? 'Undraft (R)' : 'Draft (R)'}
          </Button>
        </div>
      ) : null}
      {tab === 'skills' ? (
        <ul className="space-y-1 text-xs">
          {WORK_TYPES.map((t) => {
            const s = pawn.skills[t];
            return (
              <li key={t} className="flex items-center gap-1.5">
                <span className="w-14 text-muted">{WORK_LABELS[t]}</span>
                <span className="block h-2 flex-1 overflow-hidden rounded-full bg-surface-3">
                  <span className="block h-full bg-accent" style={{ width: `${((s?.level ?? 0) / 20) * 100}%` }} />
                </span>
                <span className="w-5 text-right tabular-nums">{s?.level ?? 0}</span>
                {s?.passion ? <Flame className="size-3 text-[#ff9f43]" aria-label="passion" /> : <span className="w-3" />}
              </li>
            );
          })}
        </ul>
      ) : null}
      {tab === 'orders' ? (
        <div className="space-y-1 text-xs">
          {pawn.queue.length === 0 ? <p className="text-muted">No queued orders. Right-click the map with this pawn selected (Shift queues).</p> : null}
          <ol className="space-y-0.5">
            {pawn.queue.map((o, i) => (
              <li key={String(o.id ?? i)} className="flex items-center gap-1.5 rounded bg-surface-2 px-1.5 py-1">
                <span className="w-4 text-subtle">{i + 1}</span>
                <span className="font-medium capitalize">{o.kind}</span>
                <span className="text-muted">{orderWhere(o.target)}</span>
                <span className="ml-auto text-subtle">{o.state}</span>
              </li>
            ))}
          </ol>
          {pawn.queue.length ? (
            <Button size="sm" variant="ghost" disabled={!canWrite} onClick={onClearQueue}>
              Clear orders
            </Button>
          ) : null}
        </div>
      ) : null}
    </Shell>
  );
}

// ------------------------------------------------------------------ several bodies, AI

export function GroupInspector({ names, state, canWrite, onDraft, onClose }: { names: string[]; state: StoryColonyState | undefined; canWrite: boolean; onDraft: (d: boolean) => void; onClose: () => void }) {
  const pawns = (state?.pawns ?? []).filter((p) => names.includes(p.name));
  const ai = (state?.ai ?? []).filter((a) => names.includes(a.name) && !pawns.some((p) => p.name === a.name));
  return (
    <Shell title={`${names.length} selected`} sub={[pawns.length && `${pawns.length} pawn${pawns.length === 1 ? '' : 's'}`, ai.length && `${ai.length} AI`].filter(Boolean).join(' · ')} onClose={onClose}>
      <ul className="space-y-1 text-xs">
        {pawns.map((p) => (
          <li key={p.name} className="flex items-center gap-1.5">
            <Avatar name={p.name} size={18} />
            <span className="font-semibold">{p.display}</span>
            <span className="truncate text-muted">{p.job?.label ?? p.state}</span>
          </li>
        ))}
        {ai.map((a) => (
          <li key={a.name} className="flex items-center gap-1.5">
            <Avatar name={a.name} size={18} ai />
            <span className="font-semibold">{a.name}</span>
            <AiMark className="size-3 text-[#a78bfa]" />
            <span className="truncate text-muted">{a.task}</span>
          </li>
        ))}
      </ul>
      {pawns.length ? (
        <div className="mt-2 flex gap-1.5">
          <Button size="sm" disabled={!canWrite} onClick={() => onDraft(true)}>
            <Shield className="size-3.5" /> Draft all
          </Button>
          <Button size="sm" variant="ghost" disabled={!canWrite} onClick={() => onDraft(false)}>
            Undraft
          </Button>
        </div>
      ) : null}
    </Shell>
  );
}

export function AiInspector({ body, state, onChat, onClose }: { body: StoryAiBody; state: StoryColonyState | undefined; onChat: () => void; onClose: () => void }) {
  return (
    <Shell title={body.name} sub={`${body.kind}${body.owner ? ` · driven by ${body.owner}` : ''}`} onClose={onClose} avatar={<Avatar name={body.name} size={30} ai />}>
      <div className="space-y-2 text-xs">
        <div className="rounded-md bg-surface-2 px-2 py-1.5">
          <div className="text-[10.5px] font-semibold tracking-wide text-subtle uppercase">Task</div>
          <div className="font-medium">{body.task ?? '–'}</div>
          {body.brainState ? <div className="text-muted">{body.brainState}</div> : null}
          {body.script ? <div className="text-muted">script {body.script}</div> : null}
        </div>
        <Bar value={body.hp ?? 20} color="#e5484d" label="Health" />
        <Bar value={body.food ?? 20} color="#e2a336" label="Food" />
        <Button size="sm" onClick={onChat}>
          <MessageSquare className="size-3.5" /> Chat (T)
        </Button>
        <div>
          <div className="mb-1 text-[10.5px] font-semibold tracking-wide text-subtle uppercase">Directives</div>
          <DirectiveList directives={state?.directives ?? []} bot={body.name} />
        </div>
      </div>
    </Shell>
  );
}

// ------------------------------------------------------------------ storage

function Check({ state, onClick, label }: { state: 'on' | 'off' | 'partial'; onClick: () => void; label: string }) {
  return (
    <button type="button" role="checkbox" aria-checked={state === 'partial' ? 'mixed' : state === 'on'} aria-label={label} onClick={onClick} className={cn('grid size-3.5 shrink-0 place-items-center rounded-[3px] border', state === 'off' ? 'border-border-strong bg-surface' : 'border-accent bg-accent text-accent-fg')}>
      {state === 'on' ? <svg viewBox="0 0 10 10" className="size-2.5"><path d="M2 5.2 4.2 7.4 8.2 2.8" stroke="currentColor" strokeWidth="1.6" fill="none" /></svg> : state === 'partial' ? <Minus className="size-2.5" /> : null}
    </button>
  );
}

export function FilterTree({ tree, filter, onChange, compact }: { tree: TreeNode[]; filter: StoryFilter; onChange: (f: StoryFilter) => void; compact?: boolean }) {
  const [open, setOpen] = useState<Set<string>>(() => new Set(compact ? [] : tree.map((n) => n.id)));
  const [q, setQ] = useState('');
  const shown = useMemo(() => searchTree(tree, q), [tree, q]);
  const node = (n: TreeNode, depth: number) => {
    const isOpen = open.has(n.id) || !!q.trim();
    return (
      <li key={n.id}>
        <div className="flex items-center gap-1 py-0.5" style={{ paddingLeft: depth * 12 }}>
          <button type="button" onClick={() => setOpen((s) => (s.has(n.id) ? new Set([...s].filter((x) => x !== n.id)) : new Set([...s, n.id])))} className="text-subtle" aria-label={isOpen ? `Collapse ${n.label}` : `Expand ${n.label}`}>
            {isOpen ? <ChevronDown className="size-3" /> : <ChevronRight className="size-3" />}
          </button>
          <Check state={nodeState(filter, n)} onClick={() => onChange(toggleNode(filter, n))} label={n.label} />
          <span className="font-medium">{n.label}</span>
        </div>
        {isOpen ? (
          <ul>
            {n.children.map((c) => node(c, depth + 1))}
            {n.items.map((it) => (
              <li key={it.key} className="flex items-center gap-1 py-0.5" style={{ paddingLeft: depth * 12 + 16 }}>
                <Check state={itemAllowed(filter, n, it.key) ? 'on' : 'off'} onClick={() => onChange(toggleItem(filter, n, it.key))} label={it.label} />
                <ItemIcon id={it.id} meta={it.meta} size={16} />
                <span>{it.label}</span>
              </li>
            ))}
          </ul>
        ) : null}
      </li>
    );
  };
  return (
    <div className="text-[11.5px]">
      <div className="mb-1 flex items-center gap-1">
        <div className="relative flex-1">
          <Search className="pointer-events-none absolute top-1.5 left-1.5 size-3 text-subtle" />
          <Input aria-label="Search items" value={q} onChange={(e) => setQ(e.target.value)} placeholder="Search" className="h-6 pl-5 text-[11px]" />
        </div>
        <button type="button" onClick={() => onChange(allowAll(tree))} className="rounded bg-surface-2 px-1.5 py-0.5 font-medium hover:bg-surface-3">
          Allow all
        </button>
        <button type="button" onClick={() => onChange(CLEAR_FILTER)} className="rounded bg-surface-2 px-1.5 py-0.5 font-medium hover:bg-surface-3">
          Clear
        </button>
      </div>
      <ul className="max-h-48 overflow-auto rounded border border-border p-1" data-testid="filter-tree">
        {shown.map((n) => node(n, 0))}
      </ul>
    </div>
  );
}

export function StorageInspector({
  chests,
  state,
  canWrite,
  write,
  clip,
  onClip,
  onClose,
}: {
  chests: V3[];
  state: StoryColonyState | undefined;
  canWrite: boolean;
  write: StoryWrite;
  clip: StorageClip | null;
  onClip: (c: StorageClip) => void;
  onClose: () => void;
}) {
  const tree = useMemo(() => buildTree(state?.categories), [state?.categories]);
  const list = chests.map((c) => (state?.storages ?? []).find((s) => k3(s.chest) === k3(c) || (s.doubleWith && k3(s.doubleWith) === k3(c))));
  const first = list.find((s): s is StoryStorage => !!s);
  const [pending, setPending] = useState<StoryFilter | null>(null);
  const filter = pending ?? first?.filter ?? CLEAR_FILTER;
  const registered = list.every(Boolean);
  const keys = chests.map((c, i) => (list[i]?.chest) ?? c);
  const set = (patch: { filter?: StoryFilter; priority?: string; label?: string }) => {
    if (patch.filter) setPending(patch.filter);
    void write('story.storage', storageSetBody(keys, patch) as never).finally(() => setPending(null));
  };
  const title = chests.length > 1 ? `${chests.length} chests` : first?.label || (first?.doubleWith ? 'Double chest' : 'Chest');
  return (
    <Shell title={title} sub={registered ? `Storage · ${PRIORITY_LABELS[(first?.priority ?? 'normal') as keyof typeof PRIORITY_LABELS] ?? first?.priority} · ${filterSummary(tree, filter)}` : 'Not storage yet'} onClose={onClose} testid="storage-inspector">
      <div className="space-y-2 text-xs">
        {!registered ? (
          <Button size="sm" variant="primary" disabled={!canWrite} onClick={() => void write('story.storage', { op: 'set', chests: keys, priority: 'normal', filter: allowAll(tree) } as never)}>
            Use as storage
          </Button>
        ) : (
          <>
            <div className="flex items-center gap-1.5">
              <label className="flex items-center gap-1">
                Priority
                <select aria-label="Storage priority" value={first?.priority ?? 'normal'} disabled={!canWrite} onChange={(e) => set({ priority: e.target.value })} className="h-6 rounded border border-border bg-surface-2 px-1">
                  {STORY_STORAGE_PRIORITIES.map((p) => (
                    <option key={p} value={p}>
                      {PRIORITY_LABELS[p]}
                    </option>
                  ))}
                </select>
              </label>
              <button type="button" onClick={() => onClip({ filter, priority: first?.priority ?? 'normal' })} className="ml-auto inline-flex items-center gap-1 rounded bg-surface-2 px-1.5 py-0.5 hover:bg-surface-3" title="Copy these settings">
                <Copy className="size-3" /> Copy
              </button>
              <button type="button" disabled={!clip || !canWrite} onClick={() => clip && set({ filter: clip.filter, priority: clip.priority })} className="inline-flex items-center gap-1 rounded bg-surface-2 px-1.5 py-0.5 hover:bg-surface-3 disabled:opacity-40" title="Paste copied settings onto the selected chests">
                <ClipboardPaste className="size-3" /> Paste
              </button>
            </div>
            {chests.length === 1 ? (
              <Input aria-label="Chest label" defaultValue={first?.label ?? ''} placeholder="Label (optional)" maxLength={40} className="h-6 text-[11px]" onBlur={(e) => e.target.value !== (first?.label ?? '') && set({ label: e.target.value })} />
            ) : null}
            <FilterTree tree={tree} filter={filter} onChange={(f) => canWrite && set({ filter: f })} />
            {chests.length === 1 && first ? (
              <div>
                <div className="mb-1 flex items-center text-[10.5px] font-semibold tracking-wide text-subtle uppercase">
                  Contents
                  <span className="ml-auto font-normal normal-case">{first.knownAt ? `seen ${new Date(String(first.knownAt)).toLocaleTimeString()}` : 'not seen yet'}</span>
                </div>
                <ul className="grid grid-cols-2 gap-x-2 gap-y-0.5">
                  {first.contents.map(([id, meta, n], i) => (
                    <li key={i} className="flex items-center gap-1">
                      <ItemIcon id={id} meta={meta} size={16} />
                      <span className="truncate">{storyItemLabel(id, meta)}</span>
                      <span className="ml-auto tabular-nums text-muted">{n}</span>
                    </li>
                  ))}
                </ul>
                {(first.freeSlots !== null && first.freeSlots !== undefined) ? <div className="mt-1 text-[10.5px] text-subtle">{first.freeSlots} free slots</div> : null}
              </div>
            ) : null}
            <Button size="sm" variant="ghost" disabled={!canWrite} onClick={() => void write('story.storage', { op: 'remove', chests: keys } as never)}>
              Stop using as storage
            </Button>
          </>
        )}
      </div>
    </Shell>
  );
}

// ------------------------------------------------------------------ bills

export function BillsInspector({ bench, state, canWrite, write, onPickChest, onClose }: { bench: StoryWorkbench; state: StoryColonyState | undefined; canWrite: boolean; write: StoryWrite; onPickChest: () => Promise<V3 | null>; onClose: () => void }) {
  const recipes = useRecipes(bench.kind);
  const [picker, setPicker] = useState(false);
  const [editing, setEditing] = useState<{ bill: StoryBill | null; draft: BillDraft; key: number } | null>(null);
  const [picking, setPicking] = useState(false);
  const dragFrom = useRef<number | null>(null);
  // "Pick a chest" closes the dialog while the next chest clicked on the map is chosen, then reopens it with that chest.
  const pickChest = async (cur: BillDraft) => {
    setEditing((e) => e && { ...e, draft: cur });
    setPicking(true);
    const c = await onPickChest();
    setPicking(false);
    setEditing((e) => e && { ...e, key: e.key + 1, draft: c ? { ...e.draft, store: { chest: c } } : e.draft });
  };
  const pos = bench.pos;
  const recipeOf = (id: string) => recipes.data?.find((r) => r.id === id);
  const label = bench.label ?? bench.kind.replace(/_/g, ' ');
  return (
    <Shell title={label[0]!.toUpperCase() + label.slice(1)} sub={`${bench.bills.length} bill${bench.bills.length === 1 ? '' : 's'} · top to bottom`} onClose={onClose} testid="bills-inspector">
      <div className="space-y-2 text-xs">
        {bench.supported === false ? (
          <p className="rounded-md bg-surface-2 px-2 py-1.5 text-muted" title="The colony has no way to work this bench yet">
            A {bench.kind.replace(/_/g, ' ')} takes no bills yet.
          </p>
        ) : null}
        <Button size="sm" variant="primary" disabled={!canWrite || bench.supported === false} onClick={() => setPicker(true)} title={bench.supported === false ? 'Not supported yet' : undefined}>
          <Plus className="size-3.5" /> Add bill
        </Button>
        <ol className="space-y-1" data-testid="bills">
          {bench.bills.map((b, i) => {
            const r = recipeOf(b.recipe);
            return (
              <li
                key={b.id}
                data-bill={b.id}
                draggable={canWrite}
                onDragStart={() => (dragFrom.current = i)}
                onDragOver={(e) => e.preventDefault()}
                onDrop={() => {
                  const from = dragFrom.current;
                  dragFrom.current = null;
                  if (from === null || from === i) return;
                  void write('story.bills', reorderBills(pos, bench.bills.map((x) => x.id), from, i) as never);
                }}
                className={cn('flex items-center gap-1.5 rounded-md border border-border bg-surface-2 px-1.5 py-1', b.paused && 'opacity-60')}
              >
                <GripVertical className="size-3 cursor-grab text-subtle" aria-hidden />
                {r ? <ItemIcon id={r.output[0]} meta={r.output[1]} size={18} /> : null}
                <button type="button" onClick={() => setEditing({ bill: b, draft: draftOf(b), key: 0 })} className="min-w-0 flex-1 text-left">
                  <div className="truncate font-semibold">{r?.label ?? b.recipe}</div>
                  <div className="text-[10.5px] text-muted">
                    {billSummary(b)} · {billProgress(b)}
                    {b.pawn !== 'any' ? ` · ${(state?.pawns ?? []).find((p) => p.name === b.pawn)?.display ?? b.pawn} only` : ''}
                  </div>
                  {b.suspendedReason ? <div className="text-[10.5px] text-danger">Suspended: {b.suspendedReason}</div> : null}
                </button>
                <button type="button" disabled={!canWrite} onClick={() => void write('story.bills', { op: b.paused ? 'resume' : 'pause', id: b.id } as never)} aria-label={b.paused ? `Resume ${r?.label ?? b.recipe}` : `Pause ${r?.label ?? b.recipe}`} className="rounded p-0.5 hover:bg-surface-3">
                  {b.paused ? <Play className="size-3.5" /> : <Pause className="size-3.5" />}
                </button>
                <button type="button" disabled={!canWrite} onClick={() => void write('story.bills', { op: 'remove', id: b.id } as never)} aria-label={`Delete ${r?.label ?? b.recipe}`} className="rounded p-0.5 text-danger hover:bg-danger-soft">
                  <Trash2 className="size-3.5" />
                </button>
              </li>
            );
          })}
        </ol>
      </div>
      <RecipePicker
        open={picker}
        recipes={recipes.data ?? []}
        state={state}
        onClose={() => setPicker(false)}
        onPick={(r) => {
          setPicker(false);
          setEditing({ bill: null, draft: newDraft(r), key: 0 });
        }}
      />
      {editing && !picking ? (
        <BillDialog
          recipe={recipeOf(editing.draft.recipe)}
          initial={editing.draft}
          isNew={!editing.bill}
          state={state}
          key={editing.key}
          onPickChest={pickChest}
          onClose={() => setEditing(null)}
          onSave={(d) => {
            const body = editing.bill ? updateBillBody(editing.bill, d) : addBillBody(pos, d);
            setEditing(null);
            if (body) void write('story.bills', body as never);
          }}
        />
      ) : null}
    </Shell>
  );
}

export function RecipePicker({ open, recipes, state, onPick, onClose }: { open: boolean; recipes: StoryRecipe[]; state: StoryColonyState | undefined; onPick: (r: StoryRecipe) => void; onClose: () => void }) {
  const [q, setQ] = useState('');
  const tree = useMemo(() => buildTree(state?.categories), [state?.categories]);
  const groups = useMemo(() => searchRecipes(recipes, q, tree), [recipes, q, tree]);
  return (
    <Dialog open={open} onOpenChange={(o) => !o && onClose()} title="Add a bill" description="What should be made here?" size="md">
      <Input aria-label="Search recipes" value={q} onChange={(e) => setQ(e.target.value)} placeholder="Search recipes or ingredients" className="mb-2 h-8 text-sm" autoFocus />
      <div className="max-h-[50vh] space-y-2 overflow-auto" data-testid="recipe-picker">
        {groups.map((g) => (
          <div key={g.group}>
            <div className="mb-0.5 text-[10.5px] font-semibold tracking-wide text-subtle uppercase">{g.label}</div>
            <ul className="space-y-0.5">
              {g.recipes.map((r) => (
                <li key={r.id}>
                  <button type="button" onClick={() => onPick(r)} className="flex w-full items-center gap-2 rounded px-1.5 py-1 text-left text-sm hover:bg-accent-soft" aria-label={`Recipe ${r.label}`}>
                    <ItemIcon id={r.output[0]} meta={r.output[1]} size={22} />
                    <span className="font-medium">{r.label}</span>
                    <span className="ml-auto text-xs text-muted">{recipeLine(r)}</span>
                  </button>
                </li>
              ))}
            </ul>
          </div>
        ))}
        {groups.length === 0 ? <p className="text-sm text-muted">No recipe matches.</p> : null}
      </div>
    </Dialog>
  );
}

function Stepper({ value, onChange, min, max, label, step = 1 }: { value: number; onChange: (v: number) => void; min: number; max: number; label: string; step?: number }) {
  return (
    <span className="inline-flex items-center gap-1">
      <button type="button" aria-label={`${label} −`} onClick={(e) => onChange(Math.max(min, value - (e.shiftKey ? step * 10 : step)))} className="grid size-6 place-items-center rounded bg-surface-2 hover:bg-surface-3">
        <Minus className="size-3" />
      </button>
      <input aria-label={label} value={value} onChange={(e) => onChange(Math.max(min, Math.min(max, Number(e.target.value) || min)))} className="h-6 w-12 rounded border border-border bg-surface text-center text-xs tabular-nums" />
      <button type="button" aria-label={`${label} +`} onClick={(e) => onChange(Math.min(max, value + (e.shiftKey ? step * 10 : step)))} className="grid size-6 place-items-center rounded bg-surface-2 hover:bg-surface-3">
        <Plus className="size-3" />
      </button>
    </span>
  );
}

export function BillDialog({ recipe, initial, isNew, state, onPickChest, onSave, onClose }: { recipe: StoryRecipe | undefined; initial: BillDraft; isNew: boolean; state: StoryColonyState | undefined; onPickChest: (current: BillDraft) => void; onSave: (d: BillDraft) => void; onClose: () => void }) {
  const [d, setD] = useState<BillDraft>(initial);
  const set = (p: Partial<BillDraft>) => setD((x) => ({ ...x, ...p }));
  // The ingredient tree: only the categories and items the recipe can use.
  const tree = useMemo(() => {
    const full = buildTree(state?.categories);
    const allowed = new Set((recipe?.inputs ?? []).flatMap((i) => [...(i.category ? [i.category] : []), ...(i.options ?? []).map(([a, m]) => `${a}:${m}`)]));
    const keep = (n: TreeNode): TreeNode | null => {
      if (allowed.has(n.id)) return n;
      const items = n.items.filter((it) => allowed.has(it.key));
      const children = n.children.map(keep).filter((c): c is TreeNode => !!c);
      return items.length || children.length ? { ...n, items, children } : null;
    };
    return full.map(keep).filter((n): n is TreeNode => !!n);
  }, [recipe, state?.categories]);
  const pawns = state?.pawns ?? [];
  return (
    <Dialog
      open
      onOpenChange={(o) => !o && onClose()}
      title={`${isNew ? 'New bill' : 'Bill'}: ${recipe?.label ?? d.recipe}`}
      description={recipe ? recipeLine(recipe) : undefined}
      size="md"
      footer={
        <>
          <Button variant="ghost" onClick={onClose}>
            Cancel
          </Button>
          <Button variant="primary" onClick={() => onSave(d)}>
            {isNew ? 'Add bill' : 'Save'}
          </Button>
        </>
      }
    >
      <div className="space-y-3 text-sm" data-testid="bill-dialog">
        <fieldset className="space-y-1.5">
          <legend className="text-xs font-semibold">Repeat</legend>
          <div className="flex flex-wrap items-center gap-2">
            <select aria-label="Repeat mode" value={d.mode} onChange={(e) => set({ mode: e.target.value as BillDraft['mode'], target: e.target.value === 'until' && d.target < 2 ? 64 : d.target })} className="h-7 rounded border border-border bg-surface-2 px-1 text-xs">
              <option value="count">Do X times</option>
              <option value="until">Do until you have X</option>
              <option value="forever">Do forever</option>
            </select>
            {d.mode !== 'forever' ? <Stepper label="Target" value={d.target} min={1} max={9999} onChange={(v) => set({ target: v })} /> : null}
            <span className="text-xs text-muted">{billSummary(d)}</span>
          </div>
        </fieldset>
        <fieldset>
          <legend className="mb-1 text-xs font-semibold">Ingredients</legend>
          <FilterTree tree={tree} filter={d.ingredients} onChange={(f) => set({ ingredients: f })} />
        </fieldset>
        <label className="flex items-center gap-2 text-xs">
          <span className="w-28 font-semibold">Search radius</span>
          <input type="range" min={4} max={128} value={d.radius} onChange={(e) => set({ radius: Number(e.target.value) })} aria-label="Ingredient search radius" className="flex-1 accent-[var(--accent)]" />
          <span className="w-16 text-right tabular-nums">{d.radius} blocks</span>
        </label>
        <div className="flex flex-wrap items-center gap-2 text-xs">
          <span className="w-28 font-semibold">Worker</span>
          <select aria-label="Worker" value={d.pawn} onChange={(e) => set({ pawn: e.target.value })} className="h-7 rounded border border-border bg-surface-2 px-1">
            <option value="any">Anyone</option>
            {pawns.map((p) => (
              <option key={p.name} value={p.name}>
                {p.display} (craft {p.skills.craft?.level ?? 0})
              </option>
            ))}
          </select>
          <label className="flex items-center gap-1">
            Min skill
            <Stepper label="Minimum skill" value={d.minSkill ?? 0} min={0} max={20} onChange={(v) => set({ minSkill: v || null })} />
          </label>
        </div>
        <div className="flex flex-wrap items-center gap-2 text-xs">
          <span className="w-28 font-semibold">Store in</span>
          <select aria-label="Store mode" value={d.store === 'best' ? 'best' : 'chest'} onChange={(e) => e.target.value === 'best' && set({ store: 'best' })} className="h-7 rounded border border-border bg-surface-2 px-1">
            <option value="best">Best storage</option>
            <option value="chest" disabled={d.store === 'best'}>
              {d.store === 'best' ? 'A chest (pick on the map)' : `Chest ${d.store.chest.join(', ')}`}
            </option>
          </select>
          <Button
            size="sm"
            variant="ghost"
            onClick={() => onPickChest(d)}
          >
            Pick a chest…
          </Button>
        </div>
        <label className="flex items-center gap-2 text-xs">
          <input type="checkbox" checked={d.paused} onChange={(e) => set({ paused: e.target.checked })} /> Paused
        </label>
      </div>
    </Dialog>
  );
}
