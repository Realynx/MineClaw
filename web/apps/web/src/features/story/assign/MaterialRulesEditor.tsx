'use client';

/**
 * Material tool rules: per material or group, the tool and the tier pawns use (never below the version's floor: those tiers are
 * greyed out and refused), and for marked materials an enchantment (Fortune for gem ores, Silk Touch) with what to do when none
 * is in storage (use a plain tool, or wait for one).
 */
import {
  STORY_TOOL_KINDS,
  type StoryMaterialInfo,
  type StoryMaterialRule,
  type StoryMaterialRuleSpec,
} from '@mineclaw/contracts';
import { Plus, Trash2 } from 'lucide-react';
import { useMemo, useState } from 'react';
import { Button, Select } from '@/components/ui/primitives';
import { cn } from '@/lib/utils';
import {
  ENCHANT_LABELS,
  enchantsSupported,
  ruleProblem,
  ruleSummary,
  TIER_LABELS,
  tierChoices,
  TOOL_LABELS,
} from './gear';

export interface RuleWrite {
  set: (rule: StoryMaterialRuleSpec) => void;
  remove: (material: string) => void;
}

export function MaterialRulesEditor({
  materials,
  rules,
  version,
  canWrite,
  write,
}: {
  materials: StoryMaterialInfo[];
  rules: StoryMaterialRule[];
  version: string | number | null;
  canWrite: boolean;
  write: RuleWrite;
}) {
  const [adding, setAdding] = useState('');
  const byId = useMemo(() => new Map(materials.map((m) => [m.id, m])), [materials]);
  const free = materials.filter((m) => !rules.some((r) => r.material === m.id));
  const ench = enchantsSupported(version);
  return (
    <div className="space-y-2" data-testid="material-rules">
      <p className="text-muted">
        Pawns always use at least the tool a material needs to drop (a stone pickaxe for iron ore). Raise it here, or
        mark a material to prefer an enchanted tool.
      </p>
      {rules.length === 0 ? <p className="text-subtle">No rules: every material uses its minimum tool.</p> : null}
      <ul className="space-y-1">
        {rules.map((r) => (
          <RuleRow
            key={r.material}
            rule={r}
            info={byId.get(r.material)}
            version={version}
            enchants={ench}
            canWrite={canWrite}
            write={write}
          />
        ))}
      </ul>
      <div className="flex items-center gap-2">
        <Select
          aria-label="Material to add a rule for"
          className="h-7 w-56 text-xs"
          value={adding}
          disabled={!canWrite || free.length === 0}
          onChange={(e) => setAdding(e.target.value)}
        >
          <option value="">Add a rule for…</option>
          {free.map((m) => (
            <option key={m.id} value={m.id}>
              {m.group ? '  ' : ''}
              {m.label}
              {m.floor ? ` (min ${TIER_LABELS[m.floor]?.toLowerCase() ?? m.floor})` : ''}
            </option>
          ))}
        </Select>
        <Button
          size="sm"
          disabled={!canWrite || !adding}
          onClick={() => {
            const m = byId.get(adding);
            if (!m) return;
            write.set({ material: m.id, ...((m.tool ?? m.keepTool) ? { tool: (m.tool ?? m.keepTool) as never } : {}) });
            setAdding('');
          }}
        >
          <Plus className="size-3.5" /> Add
        </Button>
      </div>
    </div>
  );
}

export function RuleRow({
  rule,
  info,
  version,
  enchants,
  canWrite,
  write,
}: {
  rule: StoryMaterialRule;
  info: StoryMaterialInfo | undefined;
  version: string | number | null;
  enchants: boolean;
  canWrite: boolean;
  write: RuleWrite;
}) {
  const [problem, setProblem] = useState<string | null>(null);
  const floorInfo = info ?? { id: rule.material, floor: rule.floor ?? null, tool: rule.tool };
  const tiers = tierChoices(floorInfo);
  const send: RuleSend = (patch) => {
    const next = {
      material: rule.material,
      tool: rule.tool,
      tier: rule.tier ?? null,
      enchant: rule.enchant ?? null,
      fallback: rule.fallback,
      ...patch,
    };
    const p = info ? ruleProblem(next, info, version) : null;
    setProblem(p);
    if (p) return;
    write.set(next as StoryMaterialRuleSpec);
  };
  return (
    <li className="rounded-md bg-surface-2 px-2 py-1.5" data-rule={rule.material}>
      <div className="flex flex-wrap items-center gap-2">
        <span className="w-28 truncate font-semibold" title={rule.material}>
          {ruleLabel(rule, info)}
        </span>
        <Select
          aria-label={`Tool for ${rule.material}`}
          className="h-7 w-24 text-xs"
          value={rule.tool}
          disabled={!canWrite}
          onChange={(e) => send({ tool: e.target.value as never })}
        >
          {ruleKinds(info).map((k) => (
            <option key={k} value={k}>
              {TOOL_LABELS[k] ?? k}
            </option>
          ))}
        </Select>
        <TierSelect rule={rule} tiers={tiers} canWrite={canWrite} send={send} />
        {enchants ? <EnchantSelects rule={rule} canWrite={canWrite} send={send} /> : null}
        <button
          type="button"
          className="ml-auto rounded p-0.5 text-danger hover:bg-danger-soft disabled:opacity-50"
          aria-label={`Remove the rule for ${rule.material}`}
          disabled={!canWrite}
          onClick={() => write.remove(rule.material)}
        >
          <Trash2 className="size-3.5" />
        </button>
      </div>
      <div className={cn('mt-0.5 text-[10.5px]', problem ? 'text-danger' : 'text-muted')} data-testid="rule-summary">
        {problem ?? ruleSummary(rule)}
      </div>
    </li>
  );
}

type RuleSend = (patch: Partial<StoryMaterialRuleSpec>) => void;

/** A material that needs a tool keeps that kind (cobweb: a sword or shears); one the hand harvests may take any (shears keep leaves). */
function ruleKinds(info: StoryMaterialInfo | undefined): string[] {
  return info?.floor ? [info.tool, info.keepTool].filter((k): k is string => !!k) : [...STORY_TOOL_KINDS];
}

function ruleLabel(rule: StoryMaterialRule, info: StoryMaterialInfo | undefined): string {
  return rule.label ?? info?.label ?? rule.material;
}

/** The tier: the minimum (or any), then each tier, the ones below the version's floor greyed out. */
function TierSelect({
  rule,
  tiers,
  canWrite,
  send,
}: {
  rule: StoryMaterialRule;
  tiers: ReturnType<typeof tierChoices>;
  canWrite: boolean;
  send: RuleSend;
}) {
  return (
    <Select
      aria-label={`Tier for ${rule.material}`}
      className="h-7 w-28 text-xs"
      value={rule.tier ?? ''}
      disabled={!canWrite}
      onChange={(e) => send({ tier: (e.target.value || null) as never })}
    >
      <option value="">{rule.floor ? `Minimum (${TIER_LABELS[rule.floor]?.toLowerCase()})` : 'Any (hand ok)'}</option>
      {tiers.map((t) => (
        <option
          key={t.tier}
          value={t.tier}
          disabled={t.disabled}
          title={t.why ?? undefined}
          data-floor-blocked={t.disabled || undefined}
        >
          {t.label}
          {t.disabled ? ' — below the minimum' : ''}
        </option>
      ))}
    </Select>
  );
}

/** The enchantment to prefer, and (with one chosen) what to do when none is in storage. */
function EnchantSelects({ rule, canWrite, send }: { rule: StoryMaterialRule; canWrite: boolean; send: RuleSend }) {
  return (
    <>
      <Select
        aria-label={`Enchantment for ${rule.material}`}
        className="h-7 w-32 text-xs"
        value={rule.enchant ?? ''}
        disabled={!canWrite}
        onChange={(e) => send({ enchant: (e.target.value || null) as never })}
      >
        <option value="">No enchantment</option>
        {Object.entries(ENCHANT_LABELS).map(([k, l]) => (
          <option key={k} value={k}>
            {l}
          </option>
        ))}
      </Select>
      {rule.enchant ? (
        <Select
          aria-label={`Without one, for ${rule.material}`}
          className="h-7 w-32 text-xs"
          value={rule.fallback}
          disabled={!canWrite}
          onChange={(e) => send({ fallback: e.target.value as never })}
        >
          <option value="plain">Else a plain tool</option>
          <option value="wait">Else wait for one</option>
        </Select>
      ) : null}
    </>
  );
}
