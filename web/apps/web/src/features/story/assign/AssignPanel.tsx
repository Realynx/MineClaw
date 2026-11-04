'use client';

/**
 * The Assign tab (the genre's outfit/assign window): which gear policy each pawn follows, the policies themselves (tools by
 * work, extra tools, weapon, armour, food), the material tool rules, and the colony's tool options (craft missing tools, keep
 * spare tools).
 */
import {
  STORY_ARMOR_MODES,
  STORY_TOOL_KINDS,
  STORY_WEAPON_PREFS,
  type StoryColonyState,
  type StoryPawn,
  type StoryPolicy,
} from '@mineclaw/contracts';
import { Plus, Trash2 } from 'lucide-react';
import { useState } from 'react';
import { Button, Checkbox, Input, Select } from '@/components/ui/primitives';
import { cn } from '@/lib/utils';
import { panel } from '../StoryHud';
import { Avatar } from '../visuals';
import { ARMOR_LABELS, durability, policyOf, TOOL_LABELS, WEAPON_LABELS } from './gear';
import { MaterialRulesEditor, type RuleWrite } from './MaterialRulesEditor';

type View = 'pawns' | 'policies' | 'rules' | 'options';

export interface AssignWrites {
  assign: (policy: string, pawns: string[]) => void;
  setPolicy: (p: Partial<StoryPolicy> & { name: string }) => void;
  removePolicy: (id: string) => void;
  rule: RuleWrite;
  options: (o: { autoCraftTools?: boolean; spareTools?: { count: number; kinds: string[] } }) => void;
}

export function AssignPanel({
  state,
  pawns,
  canWrite,
  writes,
}: {
  state: StoryColonyState | undefined;
  pawns: StoryPawn[];
  canWrite: boolean;
  writes: AssignWrites;
}) {
  const [view, setView] = useState<View>('pawns');
  const policies = state?.policies ?? [];
  return (
    <section
      className={cn(
        panel,
        'pointer-events-auto max-h-[52vh] w-[44rem] max-w-[calc(100vw-1rem)] overflow-auto p-2 text-xs',
      )}
      aria-label="Assign"
      data-testid="assign-panel"
    >
      <div className="mb-2 flex gap-1" role="tablist">
        {(
          [
            ['pawns', 'Pawns'],
            ['policies', 'Policies'],
            ['rules', 'Tool rules'],
            ['options', 'Spare tools'],
          ] as const
        ).map(([v, label]) => (
          <button
            key={v}
            type="button"
            role="tab"
            aria-selected={view === v}
            onClick={() => setView(v)}
            className={cn(
              'rounded px-2 py-1 font-semibold',
              view === v ? 'bg-accent-soft text-fg' : 'text-muted hover:bg-surface-2 hover:text-fg',
            )}
          >
            {label}
          </button>
        ))}
      </div>
      {view === 'pawns' ? (
        <PawnPolicies pawns={pawns} policies={policies} canWrite={canWrite} onAssign={writes.assign} />
      ) : null}
      {view === 'policies' ? (
        <PolicyEditor policies={policies} canWrite={canWrite} onSet={writes.setPolicy} onRemove={writes.removePolicy} />
      ) : null}
      {view === 'rules' ? (
        <MaterialRulesEditor
          materials={state?.materials ?? []}
          rules={state?.materialRules ?? []}
          version={state?.version ?? null}
          canWrite={canWrite}
          write={writes.rule}
        />
      ) : null}
      {view === 'options' ? <ToolOptions state={state} canWrite={canWrite} onOptions={writes.options} /> : null}
    </section>
  );
}

function PawnPolicies({
  pawns,
  policies,
  canWrite,
  onAssign,
}: {
  pawns: StoryPawn[];
  policies: StoryPolicy[];
  canWrite: boolean;
  onAssign: (policy: string, pawns: string[]) => void;
}) {
  return (
    <table className="w-full border-separate border-spacing-y-0.5" data-testid="assign-grid">
      <thead>
        <tr className="text-left text-[11px] text-muted">
          <th className="px-1 font-semibold">Pawn</th>
          <th className="px-1 font-semibold">Policy</th>
          <th className="px-1 font-semibold">Holding</th>
          <th className="px-1 font-semibold">Tools</th>
          <th className="px-1 font-semibold">Armour</th>
        </tr>
      </thead>
      <tbody>
        {pawns.map((p) => {
          const pol = policyOf(p, policies);
          const e = p.equipment;
          return (
            <tr key={p.name} data-assign-pawn={p.name} className="bg-surface-2">
              <td className="rounded-l px-1 py-0.5">
                <span className="flex items-center gap-1.5">
                  <Avatar name={p.name} size={18} />
                  <span className="font-semibold">{p.display}</span>
                </span>
              </td>
              <td className="px-1">
                <Select
                  aria-label={`Policy of ${p.display}`}
                  className="h-7 w-32 text-xs"
                  value={pol?.id ?? ''}
                  disabled={!canWrite || policies.length === 0}
                  onChange={(ev) => onAssign(ev.target.value, [p.name])}
                >
                  {policies.map((x) => (
                    <option key={x.id} value={x.id}>
                      {x.name}
                    </option>
                  ))}
                </Select>
              </td>
              <td className="px-1 text-muted">{e?.held?.label ?? p.held ?? '—'}</td>
              <td className="px-1">
                {(e?.tools ?? []).map((t, i) => {
                  const d = durability(t);
                  return (
                    <span
                      key={i}
                      className={cn(
                        'mr-1 rounded px-1',
                        d.tone === 'worn' ? 'bg-danger-soft text-danger' : 'bg-surface-3',
                      )}
                      title={d.pct !== null ? `${d.pct}% left` : undefined}
                    >
                      {t.label ?? t.kind}
                    </span>
                  );
                })}
              </td>
              <td className="rounded-r px-1 text-muted">{(e?.armor ?? []).map((a) => a.label).join(', ') || '—'}</td>
            </tr>
          );
        })}
      </tbody>
    </table>
  );
}

const blank = (): Partial<StoryPolicy> & { name: string } => ({
  name: '',
  toolsByWork: true,
  tools: [],
  weapon: 'best',
  armor: 'drafted',
  food: 0,
});

export function PolicyEditor({
  policies,
  canWrite,
  onSet,
  onRemove,
}: {
  policies: StoryPolicy[];
  canWrite: boolean;
  onSet: (p: Partial<StoryPolicy> & { name: string }) => void;
  onRemove: (id: string) => void;
}) {
  const [pick, setPick] = useState<string | null>(policies[0]?.id ?? null);
  const [draft, setDraft] = useState<(Partial<StoryPolicy> & { name: string }) | null>(null);
  const cur = draft ?? policies.find((x) => x.id === pick) ?? null;
  const edit = (patch: Partial<StoryPolicy>) => setDraft({ ...(cur ?? blank()), ...patch });
  const nameOk = !!cur && cur.name.trim().length > 0 && cur.name.trim().length <= 40;
  return (
    <div className="flex gap-3" data-testid="policy-editor">
      <ul className="w-36 shrink-0 space-y-0.5">
        {policies.map((x) => (
          <li key={x.id}>
            <button
              type="button"
              onClick={() => (setPick(x.id), setDraft(null))}
              className={cn(
                'w-full rounded px-1.5 py-1 text-left font-semibold',
                pick === x.id ? 'bg-accent-soft' : 'hover:bg-surface-2',
              )}
            >
              {x.name}
              {x.default ? <span className="ml-1 text-[10px] text-subtle">default</span> : null}
            </button>
          </li>
        ))}
        <li>
          <Button size="sm" variant="ghost" disabled={!canWrite} onClick={() => (setPick(null), setDraft(blank()))}>
            <Plus className="size-3.5" /> New policy
          </Button>
        </li>
      </ul>
      {cur ? (
        <div className="min-w-0 flex-1 space-y-2">
          <label className="block">
            <span className="mb-0.5 block text-[11px] text-muted">Name</span>
            <Input
              aria-label="Policy name"
              className="h-7 text-xs"
              value={cur.name}
              maxLength={40}
              onChange={(e) => edit({ name: e.target.value })}
            />
          </label>
          <Checkbox
            label="Tools for their work (mine: pickaxe + shovel, chop: axe, farm: hoe, build: pickaxe + axe)"
            checked={cur.toolsByWork ?? true}
            onChange={(v) => edit({ toolsByWork: v })}
          />
          <fieldset>
            <legend className="mb-0.5 text-[11px] text-muted">Also carry</legend>
            <div className="flex flex-wrap gap-2">
              {STORY_TOOL_KINDS.map((k) => (
                <Checkbox
                  key={k}
                  label={TOOL_LABELS[k]}
                  checked={(cur.tools ?? []).includes(k)}
                  onChange={(v) =>
                    edit({ tools: v ? [...(cur.tools ?? []), k] : (cur.tools ?? []).filter((t) => t !== k) })
                  }
                />
              ))}
            </div>
          </fieldset>
          <div className="flex flex-wrap gap-3">
            <label>
              <span className="mb-0.5 block text-[11px] text-muted">Weapon</span>
              <Select
                aria-label="Weapon"
                className="h-7 w-32 text-xs"
                value={cur.weapon ?? 'best'}
                onChange={(e) => edit({ weapon: e.target.value })}
              >
                {STORY_WEAPON_PREFS.map((w) => (
                  <option key={w} value={w}>
                    {WEAPON_LABELS[w]}
                  </option>
                ))}
              </Select>
            </label>
            <label>
              <span className="mb-0.5 block text-[11px] text-muted">Armour</span>
              <Select
                aria-label="Armour"
                className="h-7 w-32 text-xs"
                value={cur.armor ?? 'drafted'}
                onChange={(e) => edit({ armor: e.target.value })}
              >
                {STORY_ARMOR_MODES.map((a) => (
                  <option key={a} value={a}>
                    {ARMOR_LABELS[a]}
                  </option>
                ))}
              </Select>
            </label>
            <label>
              <span className="mb-0.5 block text-[11px] text-muted">Food to carry</span>
              <Input
                aria-label="Food to carry"
                type="number"
                min={0}
                max={16}
                className="h-7 w-20 text-xs"
                value={cur.food ?? 0}
                onChange={(e) => edit({ food: Math.max(0, Math.min(16, Math.floor(Number(e.target.value) || 0))) })}
              />
            </label>
          </div>
          <div className="flex gap-2">
            <Button
              size="sm"
              disabled={!canWrite || !draft || !nameOk}
              onClick={() => {
                if (!cur) return;
                onSet({ ...cur, name: cur.name.trim() });
                setDraft(null);
              }}
            >
              Save policy
            </Button>
            {draft ? (
              <Button size="sm" variant="ghost" onClick={() => setDraft(null)}>
                Discard
              </Button>
            ) : null}
            {cur.id && !cur.default ? (
              <Button size="sm" variant="ghost" disabled={!canWrite} onClick={() => onRemove(cur.id!)}>
                <Trash2 className="size-3.5" /> Delete
              </Button>
            ) : null}
          </div>
          {!nameOk && draft ? <p className="text-danger">A policy needs a name (1–40 characters)</p> : null}
        </div>
      ) : (
        <p className="text-muted">Pick a policy, or make a new one.</p>
      )}
    </div>
  );
}

function ToolOptions({
  state,
  canWrite,
  onOptions,
}: {
  state: StoryColonyState | undefined;
  canWrite: boolean;
  onOptions: AssignWrites['options'];
}) {
  const o = state?.options;
  const spare = o?.spareTools ?? { count: 0, kinds: [] };
  return (
    <div className="space-y-2" data-testid="tool-options">
      <Checkbox
        label="Craft a tool a job is waiting for (an auto bill “for tools” at the colony bench)"
        checked={o?.autoCraftTools ?? true}
        disabled={!canWrite}
        onChange={(v) => onOptions({ autoCraftTools: v })}
      />
      <div className="flex items-center gap-2">
        <span>Keep</span>
        <Input
          aria-label="Spare tools of each kind"
          type="number"
          min={0}
          max={8}
          className="h-7 w-16 text-xs"
          disabled={!canWrite}
          value={spare.count}
          onChange={(e) =>
            onOptions({
              spareTools: {
                count: Math.max(0, Math.min(8, Math.floor(Number(e.target.value) || 0))),
                kinds: spare.kinds,
              },
            })
          }
        />
        <span>spare tools of each kind in storage (wood / stone first, iron when there are ingots)</span>
      </div>
      <div className="flex flex-wrap gap-2">
        {STORY_TOOL_KINDS.map((k) => (
          <Checkbox
            key={k}
            label={TOOL_LABELS[k]}
            disabled={!canWrite}
            checked={spare.kinds.includes(k)}
            onChange={(v) =>
              onOptions({
                spareTools: { count: spare.count, kinds: v ? [...spare.kinds, k] : spare.kinds.filter((x) => x !== k) },
              })
            }
          />
        ))}
      </div>
    </div>
  );
}
