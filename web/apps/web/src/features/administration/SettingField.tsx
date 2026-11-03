'use client';

/** One setting row: label, description, default, Live/Restart and source badges, the control, Reset, inline errors and tests. */
import type { AdminSetting, ProviderTestResponse, SettingValue } from '@mineclaw/contracts';
import { CheckCircle2, KeyRound, Lock, PlugZap, RotateCcw, XCircle } from 'lucide-react';
import { useEffect, useState } from 'react';
import { Switch } from '@/components/ui/overlays';
import { Badge, Button, InlineError, Input, Select, Spinner, Textarea, type Tone } from '@/components/ui/primitives';
import { api, type RouteBody } from '@/lib/api/client';
import { useApi } from '@/lib/api/query';
import { cn } from '@/lib/utils';
import { fieldId, useAdminForm } from './context';
import { formatValue } from './diff';
import { hasDraft, isHttpUrl, parseList, parseNumber, providerOf, secretView, shownValue } from './fieldState';
import { StepUpCancelled } from './useStepUp';

const SOURCE_TONE: Record<AdminSetting['source'], Tone> = {
  default: 'neutral',
  config: 'neutral',
  env: 'info',
  override: 'accent',
  cli: 'warn',
};
const SOURCE_TEXT: Record<AdminSetting['source'], string> = {
  default: 'default',
  config: 'config file',
  env: 'environment',
  override: 'set here',
  cli: 'command line',
};

export const ANTHROPIC_KEY = 'Agent:Llm:ApiKey';
export const OLLAMA_ENDPOINT = 'Agent:Llm:Ollama:Endpoint';

function useDebounced<T>(value: T, ms: number): T {
  const [v, setV] = useState(value);
  useEffect(() => {
    const t = setTimeout(() => setV(value), ms);
    return () => clearTimeout(t);
  }, [value, ms]);
  return v;
}

export function SettingField({ s }: { s: AdminSetting }) {
  const f = useAdminForm();
  const id = fieldId(s.key);
  const edited = hasDraft(f.draft, s.key);
  const error = f.errors[s.key];
  const name = `${s.section} ${s.label}`;
  return (
    <div
      className={cn(
        'grid grid-cols-1 gap-x-6 gap-y-2 px-4 py-3.5 md:grid-cols-[minmax(0,1fr)_minmax(0,1.15fr)]',
        edited && 'bg-accent-soft/40',
      )}
      data-setting={s.key}
    >
      <div className="min-w-0 space-y-1">
        <div className="flex flex-wrap items-center gap-1.5">
          <label htmlFor={id} className="text-sm font-medium text-fg">
            {s.label}
          </label>
          {s.editable ? (
            <Badge
              tone={s.apply === 'live' ? 'ok' : 'warn'}
              title={s.apply === 'live' ? 'Applied the moment it is saved' : 'Saved now, used after the bot restarts'}
            >
              {s.apply === 'live' ? 'Live' : 'Restart'}
            </Badge>
          ) : (
            <Badge tone="neutral">
              <Lock className="size-3" /> Read-only
            </Badge>
          )}
          <Badge tone={SOURCE_TONE[s.source]} title="Where the running value comes from">
            {SOURCE_TEXT[s.source]}
          </Badge>
          {s.pendingRestart ? <Badge tone="warn">waiting for restart</Badge> : null}
        </div>
        <p className="text-xs leading-relaxed text-muted">{s.description}</p>
        <p className="font-mono text-[11px] text-subtle">
          {s.key}
          {!s.secret && s.default !== undefined ? (
            <span className="font-sans"> · default {formatValue(s.default, s)}</span>
          ) : null}
        </p>
        {s.readOnlyReason ? <p className="text-[11px] text-subtle">{s.readOnlyReason}</p> : null}
      </div>
      <div className="min-w-0 space-y-1.5">
        <div className="flex items-start gap-2">
          <div className="min-w-0 flex-1">
            {s.editable ? <Control s={s} id={id} name={name} invalid={!!error} /> : <ReadOnlyValue s={s} id={id} />}
          </div>
          {s.editable && edited ? (
            <Button
              size="sm"
              variant="ghost"
              onClick={() => f.setValue(s.key, undefined)}
              aria-label={`Undo ${name}`}
              title="Undo this edit"
            >
              Undo
            </Button>
          ) : s.editable && s.overridden && !s.secret ? (
            <Button
              size="sm"
              variant="ghost"
              onClick={() => f.setValue(s.key, null)}
              aria-label={`Reset ${name}`}
              title="Drop the value set here and go back to the base config"
            >
              <RotateCcw className="size-3.5" /> Reset
            </Button>
          ) : null}
        </div>
        {error ? (
          <p role="alert" className="text-xs text-danger" id={`${id}-error`}>
            {error}
          </p>
        ) : null}
        {s.key === ANTHROPIC_KEY ? <AnthropicTest /> : null}
        {s.key === OLLAMA_ENDPOINT ? <OllamaTest /> : null}
      </div>
    </div>
  );
}

function ReadOnlyValue({ s, id }: { s: AdminSetting; id: string }) {
  if (s.secret) {
    const v = secretView(s, {});
    return (
      <p id={id} className="flex h-9 items-center gap-2 text-sm text-muted">
        <KeyRound className="size-3.5" /> {v.text}
      </p>
    );
  }
  return (
    <p id={id} className="flex min-h-9 items-center text-sm break-all text-fg">
      {formatValue(s.value, s)}
    </p>
  );
}

function Control({ s, id, name, invalid }: { s: AdminSetting; id: string; name: string; invalid: boolean }) {
  const f = useAdminForm();
  const v = shownValue(s, f.draft);
  const set = (nv: SettingValue) => f.setValue(s.key, nv);
  const common = {
    id,
    'aria-label': name,
    'aria-invalid': invalid || undefined,
    'aria-describedby': invalid ? `${id}-error` : undefined,
  };
  switch (s.type) {
    case 'bool':
      if (s.nullable)
        return (
          <Select
            {...common}
            className="w-full"
            value={v === null ? '' : v ? 'on' : 'off'}
            onChange={(e) => set(e.target.value === '' ? null : e.target.value === 'on')}
          >
            <option value="">Inherit</option>
            <option value="on">On</option>
            <option value="off">Off</option>
          </Select>
        );
      return (
        <Switch
          id={id}
          checked={v === true}
          onCheckedChange={(c) => set(c)}
          label={<span className="sr-only">{name}</span>}
        />
      );
    case 'int':
    case 'number':
      return (
        <div className="flex items-center gap-2">
          <Input
            {...common}
            type="number"
            inputMode={s.type === 'int' ? 'numeric' : 'decimal'}
            step={s.type === 'int' ? 1 : 'any'}
            min={s.min ?? undefined}
            max={s.max ?? undefined}
            value={typeof v === 'number' && Number.isFinite(v) ? String(v) : ''}
            onChange={(e) => set(parseNumber(e.target.value))}
            className="max-w-48"
          />
          {s.unit ? <span className="text-xs text-muted">{s.unit}</span> : null}
        </div>
      );
    case 'enum':
      return (
        <Select
          {...common}
          className="w-full"
          value={typeof v === 'string' ? v : ''}
          onChange={(e) => set(e.target.value)}
        >
          {(s.options ?? []).map((o) => (
            <option key={o} value={o}>
              {o === '' ? '(none)' : o}
            </option>
          ))}
        </Select>
      );
    case 'list':
      return <ListControl s={s} common={common} value={Array.isArray(v) ? v : []} onChange={set} />;
    case 'text':
      return (
        <Textarea {...common} value={typeof v === 'string' ? v : ''} onChange={(e) => set(e.target.value)} rows={4} />
      );
    case 'secret':
      return <SecretControl s={s} common={common} />;
    case 'model':
      return <ModelControl s={s} common={common} value={typeof v === 'string' ? v : ''} onChange={set} />;
    default:
      return (
        <Input
          {...common}
          type={s.type === 'url' ? 'url' : 'text'}
          spellCheck={false}
          placeholder={s.type === 'url' ? 'https://…' : undefined}
          value={typeof v === 'string' ? v : ''}
          onChange={(e) => set(e.target.value)}
        />
      );
  }
}

type Common = { id: string; 'aria-label': string; 'aria-invalid'?: boolean; 'aria-describedby'?: string };

function ListControl({
  s,
  common,
  value,
  onChange,
}: {
  s: AdminSetting;
  common: Common;
  value: string[];
  onChange: (v: string[]) => void;
}) {
  const f = useAdminForm();
  const edited = hasDraft(f.draft, s.key);
  const [text, setText] = useState(value.join('\n'));
  // Undo / a refreshed running value: show it again.
  useEffect(() => {
    if (!edited) setText(value.join('\n'));
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [edited, JSON.stringify(value)]);
  return (
    <Textarea
      {...common}
      rows={Math.min(6, Math.max(2, value.length + 1))}
      spellCheck={false}
      placeholder="One per line"
      value={text}
      onChange={(e) => {
        setText(e.target.value);
        onChange(parseList(e.target.value));
      }}
    />
  );
}

function SecretControl({ s, common }: { s: AdminSetting; common: Common }) {
  const f = useAdminForm();
  const view = secretView(s, f.draft);
  const [editing, setEditing] = useState(false);
  const typed = typeof f.draft[s.key] === 'string' ? (f.draft[s.key] as string) : '';
  if (editing || view.mode === 'replacing') {
    return (
      <div className="flex items-center gap-2">
        <Input
          {...common}
          type="password"
          autoComplete="new-password"
          spellCheck={false}
          autoFocus={editing}
          placeholder={s.secretState?.set ? 'New value (replaces the current one)' : 'Value'}
          value={typed}
          onChange={(e) => f.setValue(s.key, e.target.value === '' ? undefined : e.target.value)}
        />
        <Button
          size="sm"
          variant="ghost"
          onClick={() => {
            setEditing(false);
            f.setValue(s.key, undefined);
          }}
        >
          Cancel
        </Button>
      </div>
    );
  }
  return (
    <div className="flex min-h-9 flex-wrap items-center gap-x-2 gap-y-1 text-sm">
      <span
        id={common.id}
        className={cn(
          'inline-flex items-center gap-1.5',
          view.mode === 'unset' ? 'text-muted' : view.mode === 'clearing' ? 'text-danger' : 'text-fg',
        )}
      >
        <KeyRound className="size-3.5" /> {view.text}
      </span>
      <span className="text-subtle" aria-hidden>
        ·
      </span>
      <Button
        size="sm"
        variant="secondary"
        onClick={() => setEditing(true)}
        aria-label={`${s.secretState?.set ? 'Replace' : 'Set'} ${common['aria-label']}`}
      >
        {s.secretState?.set ? 'Replace' : 'Set'}
      </Button>
      {s.secretState?.set && view.mode !== 'clearing' ? (
        <Button
          size="sm"
          variant="ghost"
          onClick={() => f.setValue(s.key, '')}
          aria-label={`Clear ${common['aria-label']}`}
        >
          Clear
        </Button>
      ) : null}
      {s.overridden && view.mode === 'set' ? (
        <Button
          size="sm"
          variant="ghost"
          onClick={() => f.setValue(s.key, null)}
          aria-label={`Reset ${common['aria-label']}`}
        >
          <RotateCcw className="size-3.5" /> Reset
        </Button>
      ) : null}
    </div>
  );
}

function ModelControl({
  s,
  common,
  value,
  onChange,
}: {
  s: AdminSetting;
  common: Common;
  value: string;
  onChange: (v: string) => void;
}) {
  const f = useAdminForm();
  const provider = providerOf(s, f.all, f.draft);
  if (provider === 'anthropic') {
    const known = f.allowedClaude.includes(value);
    return (
      <Select {...common} className="w-full" value={value} onChange={(e) => onChange(e.target.value)}>
        {!known ? <option value={value}>{value ? `${value} (not allowed)` : 'Pick a Claude model…'}</option> : null}
        {f.allowedClaude.map((m) => (
          <option key={m} value={m}>
            {m}
          </option>
        ))}
      </Select>
    );
  }
  if (provider === 'ollama') return <OllamaModelSelect common={common} value={value} onChange={onChange} />;
  return (
    <Input
      {...common}
      spellCheck={false}
      value={value}
      onChange={(e) => onChange(e.target.value)}
      placeholder="Model id"
    />
  );
}

/** The Ollama endpoint the form means right now (the drafted one, else the running one). */
function useOllamaEndpoint(): string {
  const f = useAdminForm();
  const s = f.all.find((x) => x.key === OLLAMA_ENDPOINT);
  if (!s) return '';
  const v = shownValue(s, f.draft);
  return typeof v === 'string' ? v.trim() : '';
}

function OllamaModelSelect({
  common,
  value,
  onChange,
}: {
  common: Common;
  value: string;
  onChange: (v: string) => void;
}) {
  const f = useAdminForm();
  const endpoint = useDebounced(useOllamaEndpoint(), 500);
  const ok = endpoint !== '' && isHttpUrl(endpoint);
  const q = useApi(
    'admin.ollamaModels',
    { params: { node: f.node }, query: { endpoint } },
    { enabled: ok, staleTime: 60_000 },
  );
  if (!ok) return <p className="flex h-9 items-center text-xs text-muted">Set the Ollama endpoint first.</p>;
  if (q.isPending)
    return (
      <p className="flex h-9 items-center gap-2 text-xs text-muted">
        <Spinner /> Loading the endpoint's models…
      </p>
    );
  const models = q.data?.ok ? q.data.models : [];
  if (!models.length)
    return (
      <div className="space-y-1">
        <Input
          {...common}
          spellCheck={false}
          value={value}
          onChange={(e) => onChange(e.target.value)}
          placeholder="Ollama model tag"
        />
        <p className="text-[11px] text-warn">
          {q.data?.error ?? (q.error ? 'Could not list the models.' : 'The endpoint lists no models.')} Type a tag
          instead.
        </p>
      </div>
    );
  const known = models.some((m) => m.name === value);
  return (
    <Select {...common} className="w-full" value={value} onChange={(e) => onChange(e.target.value)}>
      {!known ? <option value={value}>{value ? `${value} (not on this endpoint)` : 'Pick a model…'}</option> : null}
      {models.map((m) => (
        <option key={m.name} value={m.name}>
          {m.name}
          {m.parameterSize ? ` · ${m.parameterSize}` : ''}
          {m.capabilities?.length ? ` · ${m.capabilities.filter((c) => c !== 'completion').join(', ')}` : ''}
        </option>
      ))}
    </Select>
  );
}

function TestResult({ r }: { r: ProviderTestResponse }) {
  return (
    <p role="status" className={cn('flex items-start gap-1.5 text-xs', r.ok ? 'text-ok' : 'text-danger')}>
      {r.ok ? <CheckCircle2 className="mt-px size-3.5 shrink-0" /> : <XCircle className="mt-px size-3.5 shrink-0" />}
      <span>
        {r.message}
        {r.latencyMs !== null && r.latencyMs !== undefined ? (
          <span className="text-muted"> · {Math.round(r.latencyMs)} ms</span>
        ) : null}
      </span>
    </p>
  );
}

function useTester() {
  const f = useAdminForm();
  const [busy, setBusy] = useState(false);
  const [result, setResult] = useState<ProviderTestResponse | null>(null);
  const [error, setError] = useState<unknown>(null);
  const test = async (body: RouteBody<'admin.settingsTest'>) => {
    setBusy(true);
    setError(null);
    setResult(null);
    try {
      setResult(await f.stepUp.run(() => api('admin.settingsTest', { params: { node: f.node }, body })));
    } catch (e) {
      if (!(e instanceof StepUpCancelled)) setError(e);
    } finally {
      setBusy(false);
    }
  };
  return { busy, result, error, test };
}

function AnthropicTest() {
  const f = useAdminForm();
  const t = useTester();
  const draftKey = f.draft[ANTHROPIC_KEY];
  const baseUrl = f.draft['Agent:Llm:BaseUrl'];
  const main = f.all.find((x) => x.key === 'Agent:Llm:MainModel');
  const mainModel = main ? shownValue(main, f.draft) : null;
  const model =
    main &&
    providerOf(main, f.all, f.draft) === 'anthropic' &&
    typeof mainModel === 'string' &&
    f.allowedClaude.includes(mainModel)
      ? mainModel
      : 'claude-haiku-5-5';
  return (
    <div className="space-y-1">
      <Button
        size="sm"
        variant="secondary"
        loading={t.busy}
        onClick={() =>
          void t.test({
            kind: 'anthropic',
            ...(typeof draftKey === 'string' && draftKey ? { apiKey: draftKey } : {}),
            ...(typeof baseUrl === 'string' ? { baseUrl } : {}),
            model,
          })
        }
      >
        <PlugZap className="size-3.5" /> {typeof draftKey === 'string' && draftKey ? 'Test the new key' : 'Test key'}
      </Button>
      {t.result ? <TestResult r={t.result} /> : null}
      <InlineError error={t.error} />
    </div>
  );
}

function OllamaTest() {
  const f = useAdminForm();
  const t = useTester();
  const endpoint = useOllamaEndpoint();
  const token = f.draft['Agent:Llm:Ollama:ApiKey'];
  return (
    <div className="space-y-1">
      <Button
        size="sm"
        variant="secondary"
        loading={t.busy}
        disabled={!endpoint || !isHttpUrl(endpoint)}
        onClick={() =>
          void t.test({
            kind: 'ollama',
            endpoint,
            ...(typeof token === 'string' && token ? { ollamaApiKey: token } : {}),
          })
        }
        aria-label="Test the Ollama endpoint"
      >
        <PlugZap className="size-3.5" /> Test
      </Button>
      {t.result ? <TestResult r={t.result} /> : null}
      <InlineError error={t.error} />
    </div>
  );
}
