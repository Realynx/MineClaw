'use client';

/**
 * The redstone panel's Library tab: the proven gates of the design's game version, the components placed in it (turn,
 * mirror, move, rename, unlink, remove), and the user's own components: saved from a selection with ports picked around
 * it, taken from another saved redstone design, and kept in this browser's quick library (export and import as JSON).
 */
import type { DesignerSession } from '@mineclaw/contracts';
import type { ComponentCategory, Pos, RedstoneVersion } from '@mineclaw/redstone';
import { FlipHorizontal2, FlipVertical2, MousePointerClick, RotateCw, Trash2, Unlink } from 'lucide-react';
import { useState, useSyncExternalStore } from 'react';
import { Button } from '@/components/ui/primitives';
import { useApi } from '@/lib/api/query';
import { api, errorText } from '@/lib/api/client';
import { cn } from '@/lib/utils';
import type { Cell } from '../canvas';
import type { Studio } from '../studio';
import {
  browserStorage,
  exportLibrary,
  importLibrary,
  loadQuickLibrary,
  saveQuickLibrary,
  type ComponentInstance,
  type CustomDef,
} from './componentData';
import type { PlaceableDef } from './RedstoneComponentController';
import type { RedstoneComponents } from './useRedstoneComponents';

/** Another saved design's cells, as the tab inserts it (the page asks the designer; tests pass their own). */
export type LoadDesign = (
  designId: number,
) => Promise<{ name: string; cells: readonly Cell[]; version: RedstoneVersion }>;

const loadSaved: LoadDesign = async (designId) => {
  const s: DesignerSession = await api('designer.create', { body: { designId } });
  return { name: s.name, cells: s.blocks, version: s.version === '1.12.2' ? '1.12.2' : 'b1.8.1' };
};

const CATEGORIES: ReadonlyArray<{ id: ComponentCategory; label: string }> = [
  { id: 'logic', label: 'Logic' },
  { id: 'memory', label: 'Memory' },
  { id: 'timing', label: 'Timing' },
  { id: 'arithmetic', label: 'Arithmetic' },
  { id: 'routing', label: 'Routing' },
];

const IN_NAMES = ['A', 'B', 'C', 'D', 'E', 'F', 'G', 'H'];
const OUT_NAMES = ['Y', 'Z', 'Q', 'R', 'S', 'T', 'U', 'V'];

const sectionClass = 'border-b border-border px-3 py-2';
const headClass = 'text-[11px] font-semibold text-fg';
const smallButton = 'rounded p-0.5 text-muted hover:bg-surface-2 hover:text-fg disabled:opacity-40';

export function LibraryTab(p: {
  components: RedstoneComponents;
  studio: Studio;
  version: RedstoneVersion;
  loadDesign?: LoadDesign;
}) {
  const { components, studio, version } = p;
  const c = components.controller;
  const [chosen, setChosen] = useState<string | null>(null);
  const [turns, setTurns] = useState(0);
  const [mirror, setMirror] = useState<'x' | 'z' | null>(null);
  const [snapOn, setSnapOn] = useState(false);
  if (!c) return null;
  const defs = c.defs();
  const def = defs.find((d) => d.id === chosen) ?? null;
  const placeHere = () => def && components.setPick({ kind: 'place', def: def.id, turns, mirror, snap: snapOn });
  return (
    <div className="text-[11px]" data-testid="redstone-library">
      {components.snap.error ? (
        <p className="border-b border-border bg-danger-soft px-3 py-1.5 text-danger" role="alert">
          {components.snap.error}
        </p>
      ) : null}
      {components.snap.recovered ? (
        <p className="border-b border-border bg-info-soft px-3 py-1.5 text-info" role="status">
          The components you had not saved yet came back from this browser.
        </p>
      ) : null}
      <section className={sectionClass} aria-label="Place a component">
        <div className="flex items-center gap-1">
          <h3 className={headClass}>Place</h3>
          <span className="ml-auto" />
          <button
            type="button"
            className={smallButton}
            aria-label="Turn the next component"
            title="Turn clockwise"
            onClick={() => setTurns((t) => (t + 1) % 4)}
          >
            <RotateCw className="size-3.5" />
          </button>
          <span className="w-8 text-center font-mono text-muted">{turns * 90}°</span>
          <select
            aria-label="Mirror the next component"
            value={mirror ?? ''}
            onChange={(e) => setMirror(e.target.value === 'x' || e.target.value === 'z' ? e.target.value : null)}
            className="h-6 rounded border border-border bg-surface px-1 text-fg"
          >
            <option value="">no mirror</option>
            <option value="x">mirror E-W</option>
            <option value="z">mirror N-S</option>
          </select>
        </div>
        <label className="mt-1 flex items-center gap-1 text-muted" title="Join an input to the nearest output port">
          <input type="checkbox" checked={snapOn} onChange={(e) => setSnapOn(e.target.checked)} />
          Snap an input to an output port
        </label>
        {components.pick?.kind === 'place' ? (
          <p className="mt-1 text-info" role="status">
            Click in the world where the box’s corner goes{snapOn ? ' (near the output to join)' : ''}; right click
            cancels.
          </p>
        ) : null}
        <div className="mt-1 flex items-center gap-1">
          <Button size="sm" variant="primary" disabled={!def} onClick={placeHere}>
            <MousePointerClick className="size-3" /> Place {def ? def.name : '…'}
          </Button>
          <PlaceAt onPlace={(at) => def && c.place(def.id, at, { turns, mirror, snap: snapOn })} disabled={!def} />
        </div>
      </section>
      <BuiltIns defs={defs} chosen={chosen} setChosen={setChosen} version={version} />
      <Placed components={components} defs={defs} />
      <Own components={components} defs={defs} chosen={chosen} setChosen={setChosen} studio={studio} />
      <InsertDesign components={components} studio={studio} load={p.loadDesign ?? loadSaved} />
    </div>
  );
}

function PlaceAt({ onPlace, disabled }: { onPlace: (at: Pos) => void; disabled: boolean }) {
  const [text, setText] = useState('');
  const at = parsePos(text);
  return (
    <span className="flex items-center gap-1">
      <input
        aria-label="Place at x y z"
        placeholder="x y z"
        value={text}
        onChange={(e) => setText(e.target.value)}
        className="h-6 w-20 rounded border border-border bg-surface px-1 font-mono text-fg"
      />
      <Button size="sm" disabled={disabled || !at} onClick={() => at && onPlace(at)}>
        Place at
      </Button>
    </span>
  );
}

/** Three whole numbers, by spaces or commas. */
export function parsePos(text: string): Pos | null {
  const parts = text.trim().split(/[\s,]+/);
  if (parts.length !== 3 || !parts.every((s) => /^-?\d+$/.test(s))) return null;
  return [Number(parts[0]), Number(parts[1]), Number(parts[2])];
}

function BuiltIns(p: {
  defs: readonly PlaceableDef[];
  chosen: string | null;
  setChosen: (id: string) => void;
  version: RedstoneVersion;
}) {
  const builtIn = p.defs.filter((d) => d.builtIn);
  return (
    <section className={sectionClass} aria-label="Proven gates">
      <h3 className={headClass}>Proven gates ({p.version})</h3>
      <p className="text-muted">
        Each one is checked by the simulator against its truth table or timing, turned and mirrored.
      </p>
      {CATEGORIES.map((cat) => {
        const list = builtIn.filter((d) => d.builtIn!.category === cat.id);
        if (list.length === 0) return null;
        return (
          <div key={cat.id} className="mt-1">
            <h4 className="text-[10px] uppercase tracking-wide text-muted">{cat.label}</h4>
            <ul className="mt-0.5 flex flex-wrap gap-1">
              {list.map((d) => (
                <li key={d.id}>
                  <button
                    type="button"
                    aria-pressed={p.chosen === d.id}
                    title={d.builtIn!.description}
                    onClick={() => p.setChosen(d.id)}
                    className={cn(
                      'rounded border px-1.5 py-0.5',
                      p.chosen === d.id
                        ? 'border-accent bg-accent-soft text-fg'
                        : 'border-border text-fg hover:bg-surface-2',
                    )}
                  >
                    {d.name}
                  </button>
                </li>
              ))}
            </ul>
          </div>
        );
      })}
    </section>
  );
}

function Placed({ components, defs }: { components: RedstoneComponents; defs: readonly PlaceableDef[] }) {
  const c = components.controller!;
  const list = components.snap.components;
  return (
    <section className={sectionClass} aria-label="Placed components">
      <h3 className={headClass}>In this design ({list.length})</h3>
      {list.length === 0 ? <p className="text-muted">Nothing placed yet.</p> : null}
      <ul className="mt-1 space-y-1">
        {list.map((inst) => (
          <PlacedRow key={inst.id} inst={inst} name={defs.find((d) => d.id === inst.def)?.name ?? inst.def} c={c} />
        ))}
      </ul>
    </section>
  );
}

function PlacedRow({
  inst,
  name,
  c,
}: {
  inst: ComponentInstance;
  name: string;
  c: NonNullable<RedstoneComponents['controller']>;
}) {
  const [at, setAt] = useState(inst.at.join(' '));
  const ports = c.placedOf(inst.id)?.ports ?? [];
  return (
    <li className="rounded border border-border px-1.5 py-1" data-testid="placed-component">
      <div className="flex items-center gap-1">
        <input
          aria-label={`Label of ${inst.id}`}
          defaultValue={inst.label ?? ''}
          placeholder={name}
          onBlur={(e) => e.target.value !== (inst.label ?? '') && c.rename(inst.id, e.target.value)}
          className="h-5 min-w-0 flex-1 rounded border border-transparent bg-transparent px-1 text-fg hover:border-border"
        />
        <button type="button" className={smallButton} aria-label={`Turn ${inst.id}`} onClick={() => c.turn(inst.id)}>
          <RotateCw className="size-3.5" />
        </button>
        <button
          type="button"
          className={smallButton}
          aria-label={`Mirror ${inst.id} east-west`}
          onClick={() => c.mirror(inst.id, 'x')}
        >
          <FlipHorizontal2 className="size-3.5" />
        </button>
        <button
          type="button"
          className={smallButton}
          aria-label={`Mirror ${inst.id} north-south`}
          onClick={() => c.mirror(inst.id, 'z')}
        >
          <FlipVertical2 className="size-3.5" />
        </button>
        <button
          type="button"
          className={smallButton}
          aria-label={`Unlink ${inst.id}`}
          title="Keep the blocks as plain cells"
          onClick={() => c.detach(inst.id)}
        >
          <Unlink className="size-3.5" />
        </button>
        <button
          type="button"
          className={smallButton}
          aria-label={`Remove ${inst.id}`}
          onClick={() => c.remove(inst.id)}
        >
          <Trash2 className="size-3.5" />
        </button>
      </div>
      <div className="mt-0.5 flex items-center gap-1 text-muted">
        <span>{name} at</span>
        <input
          aria-label={`Corner of ${inst.id}`}
          value={at}
          onChange={(e) => setAt(e.target.value)}
          onBlur={() => {
            const to = parsePos(at);
            if (!to || to.join(' ') === inst.at.join(' ') || !c.move(inst.id, to)) setAt(inst.at.join(' '));
          }}
          className="h-5 w-20 rounded border border-border bg-surface px-1 font-mono text-fg"
        />
      </div>
      {ports.length > 0 ? (
        <p className="mt-0.5 font-mono text-[10px] text-muted">
          {ports.map((port) => `${port.kind === 'in' ? '→' : '←'}${port.name} ${port.at.join(',')}`).join('  ')}
        </p>
      ) : null}
    </li>
  );
}

/** The user's own components: the design's, the quick library's, and saving a selection as a new one. */
function Own(p: {
  components: RedstoneComponents;
  defs: readonly PlaceableDef[];
  chosen: string | null;
  setChosen: (id: string) => void;
  studio: Studio;
}) {
  const { components, studio } = p;
  const c = components.controller!;
  const owner = studio.session.owner;
  const [quick, setQuick] = useState<CustomDef[]>(() => loadQuickLibrary(browserStorage(), owner));
  const [note, setNote] = useState<string | null>(null);
  const [io, setIo] = useState('');
  const keep = (next: CustomDef[]) => {
    setQuick(next);
    if (!saveQuickLibrary(browserStorage(), owner, next)) setNote('This browser would not keep the library.');
  };
  const remember = (d: CustomDef) => keep([...quick.filter((q) => q.id !== d.id), d]);
  const own = components.snap.customDefs;
  const elsewhere = quick.filter((q) => !own.some((d) => d.id === q.id));
  return (
    <section className={sectionClass} aria-label="Your components">
      <h3 className={headClass}>Your components</h3>
      {note ? <p className="text-warn">{note}</p> : null}
      <ul className="mt-1 space-y-0.5">
        {own.map((d) => {
          const placeable = p.defs.find((x) => x.id === d.id);
          return (
            <li key={d.id} className="flex items-center gap-1">
              <button
                type="button"
                aria-pressed={p.chosen === d.id}
                onClick={() => p.setChosen(d.id)}
                className={cn(
                  'min-w-0 flex-1 truncate rounded px-1 text-left',
                  p.chosen === d.id ? 'bg-accent-soft' : 'hover:bg-surface-2',
                )}
              >
                {d.name} <span className="text-muted">{d.layout.size.join('×')}</span>
                {placeable?.otherVersion ? (
                  <span className="text-warn"> drawn for {placeable.otherVersion}</span>
                ) : null}
              </button>
              <button
                type="button"
                className={smallButton}
                onClick={() => remember(d)}
                title="Keep in this browser’s library"
              >
                Keep
              </button>
              <button
                type="button"
                className={smallButton}
                aria-label={`Drop ${d.name}`}
                onClick={() => c.removeDef(d.id)}
              >
                <Trash2 className="size-3.5" />
              </button>
            </li>
          );
        })}
        {elsewhere.map((d) => (
          <li key={d.id} className="flex items-center gap-1 text-muted">
            <span className="min-w-0 flex-1 truncate">{d.name} (library)</span>
            <button
              type="button"
              className={smallButton}
              onClick={() => {
                const id = c.addDef(d);
                if (id) p.setChosen(id);
              }}
            >
              Use
            </button>
            <button
              type="button"
              className={smallButton}
              aria-label={`Forget ${d.name}`}
              onClick={() => keep(quick.filter((q) => q.id !== d.id))}
            >
              <Trash2 className="size-3.5" />
            </button>
          </li>
        ))}
      </ul>
      {own.length === 0 && elsewhere.length === 0 ? <p className="text-muted">None yet.</p> : null}
      <SaveSelection components={components} studio={studio} onSaved={(d) => (remember(d), p.setChosen(d.id))} />
      <details className="mt-1">
        <summary className="cursor-pointer text-muted">Export or import the library</summary>
        <textarea
          aria-label="Library as JSON"
          value={io}
          onChange={(e) => setIo(e.target.value)}
          rows={3}
          className="mt-1 w-full rounded border border-border bg-surface p-1 font-mono text-[10px] text-fg"
        />
        <div className="flex gap-1">
          <Button size="sm" onClick={() => setIo(exportLibrary(quick))}>
            Export
          </Button>
          <Button
            size="sm"
            onClick={() => {
              const defs = importLibrary(io);
              if (!defs) return setNote('That is not an exported library.');
              keep([...quick.filter((q) => !defs.some((d) => d.id === q.id)), ...defs]);
              setNote(`${defs.length} component${defs.length === 1 ? '' : 's'} imported.`);
            }}
          >
            Import
          </Button>
        </div>
      </details>
    </section>
  );
}

function SaveSelection(p: { components: RedstoneComponents; studio: Studio; onSaved: (d: CustomDef) => void }) {
  const { components, studio } = p;
  const snap = useSyncExternalStore(studio.subscribe, studio.snapshot, studio.snapshot);
  const [name, setName] = useState('');
  const sel = snap.selection;
  const picked = components.picked;
  const named = picked.map((x, i) => {
    const same = picked.slice(0, i).filter((y) => y.kind === x.kind).length;
    return { ...x, name: (x.kind === 'in' ? IN_NAMES : OUT_NAMES)[same] ?? `${x.kind}${same}` };
  });
  const picking = components.pick?.kind === 'port' ? components.pick.port : null;
  const save = () => {
    if (!sel) return;
    const def = components.controller!.saveSelection(name, sel, named);
    if (!def) return;
    components.clearPicked();
    components.setPick(null);
    setName('');
    p.onSaved(def);
  };
  return (
    <details className="mt-1">
      <summary className="cursor-pointer text-muted">Save the selection as a component</summary>
      {!sel ? (
        <p className="text-muted">Select the circuit first (the Select tool).</p>
      ) : (
        <div className="mt-1 space-y-1">
          <input
            aria-label="Component name"
            placeholder="name"
            value={name}
            onChange={(e) => setName(e.target.value)}
            className="h-6 w-full rounded border border-border bg-surface px-1 text-fg"
          />
          <p className="text-muted">
            Ports are cells just outside the selection where wires join. Click them in the world.
          </p>
          <div className="flex gap-1">
            {(['in', 'out'] as const).map((kind) => (
              <Button
                key={kind}
                size="sm"
                variant={picking === kind ? 'primary' : 'secondary'}
                onClick={() => components.setPick(picking === kind ? null : { kind: 'port', port: kind })}
              >
                Pick {kind === 'in' ? 'inputs' : 'outputs'}
              </Button>
            ))}
          </div>
          {named.length > 0 ? (
            <p className="font-mono text-[10px] text-muted">
              {named.map((x) => `${x.name} ${x.at.join(',')}`).join('  ')}{' '}
              <button type="button" className="underline" onClick={components.clearPicked}>
                clear
              </button>
            </p>
          ) : null}
          <Button size="sm" variant="primary" onClick={save}>
            Save component
          </Button>
        </div>
      )}
    </details>
  );
}

function InsertDesign(p: { components: RedstoneComponents; studio: Studio; load: LoadDesign }) {
  const [open, setOpen] = useState(false);
  const [busy, setBusy] = useState(false);
  const [note, setNote] = useState<string | null>(null);
  const designs = useApi('designs.list', { query: { limit: 200 } }, { enabled: open });
  const own = p.studio.designId;
  const list = (designs.data?.items ?? []).filter((d) => d.kind === 'redstone' && d.id !== own);
  const insert = async (id: number) => {
    setBusy(true);
    setNote(null);
    try {
      const d = await p.load(id);
      const def = p.components.controller!.insertDesign(d.name, d.cells, d.version);
      if (def) setNote(`${def.name} is now one of your components: choose it and place it.`);
    } catch (e) {
      setNote(errorText(e));
    } finally {
      setBusy(false);
    }
  };
  return (
    <section className={sectionClass} aria-label="Insert a saved design">
      <details onToggle={(e) => setOpen(e.currentTarget.open)}>
        <summary className={cn(headClass, 'cursor-pointer')}>Insert a saved redstone design</summary>
        {designs.isLoading ? <p className="text-muted">Loading…</p> : null}
        {open && !designs.isLoading && list.length === 0 ? (
          <p className="text-muted">No other redstone designs.</p>
        ) : null}
        <ul className="mt-1 space-y-0.5">
          {list.map((d) => (
            <li key={d.id} className="flex items-center gap-1">
              <span className="min-w-0 flex-1 truncate">
                #{d.id} {d.name}
                {d.version && d.version !== p.studio.session.version ? (
                  <span className="text-warn"> ({d.version})</span>
                ) : null}
              </span>
              <Button size="sm" disabled={busy} onClick={() => void insert(d.id)}>
                Insert
              </Button>
            </li>
          ))}
        </ul>
        {note ? <p className="mt-1 text-info">{note}</p> : null}
      </details>
    </section>
  );
}
