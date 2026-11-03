/**
 * Mock Administration node: the bot's settings registry (the same keys as the bot's SettingsRegistry), an override layer the
 * save writes into, live vs restart, secrets kept server-side (only `set` + the last four go out), a settings audit, provider
 * tests, Ollama tags and the restart safety check. `sessionStorage['mcx-mock-restart-unsafe'] = '1'` makes a restart unsafe.
 */
import type {
  AdminNode,
  AdminSetting,
  AdminSettingGroup,
  AdminSettingsResponse,
  OllamaModelsResponse,
  ProviderTestResponse,
  RestartSafety,
  SettingChange,
  SettingSource,
  SettingValue,
  SettingsAuditEntry,
} from '@mineclaw/contracts';
import { isoAgo, nowIso } from '../util';

export const ALLOWED_CLAUDE = [
  'claude-opus-5-5',
  'claude-sonnet-5-5',
  'claude-haiku-5-5',
  'claude-fable-5-1',
  'claude-mythos-5-1',
];

const GROUPS: AdminSettingGroup[] = [
  {
    id: 'models',
    title: 'Models & providers',
    description: 'Which models each tier runs on, the Anthropic key, Ollama and vision.',
  },
  { id: 'bot', title: 'Bot & chat', description: 'What the bot answers to and who it trusts.' },
  { id: 'viewer', title: 'Viewer access', description: 'What web users may do through the viewer routes.' },
  { id: 'hive', title: 'Hive & accounts', description: 'How many bodies the hive may run and its memory ceiling.' },
  { id: 'story', title: 'Story & designer', description: 'Design Studio limits.' },
  { id: 'logging', title: 'Logging', description: 'How much the bot writes to its console log.' },
  {
    id: 'system',
    title: 'System',
    description: 'Shown for reference; changing these from the web could lock the explorer out.',
  },
];

interface Def {
  key: string;
  group: string;
  section: string;
  label: string;
  description: string;
  type: AdminSetting['type'];
  apply: AdminSetting['apply'];
  def: SettingValue;
  base?: SettingValue;
  source?: SettingSource;
  secret?: boolean;
  readOnly?: string;
  options?: string[];
  min?: number;
  max?: number;
  unit?: string;
  providerKey?: string;
  nullable?: boolean;
}

const EFFORT = ['low', 'medium', 'high', ''];
const THINKING = ['off', 'adaptive', 'omit'];
const PROVIDERS = ['anthropic', 'ollama'];

function tier(name: 'Main' | 'Worker' | 'Cheap', label: string, model: string, tokens: number, effort: string): Def[] {
  const section = `${label} tier`;
  return [
    {
      key: `Agent:Llm:${name}Provider`,
      group: 'models',
      section,
      label: 'Provider',
      description: `Where the ${label.toLowerCase()} tier runs.`,
      type: 'enum',
      apply: 'restart',
      def: 'anthropic',
      options: PROVIDERS,
    },
    {
      key: `Agent:Llm:${name}Model`,
      group: 'models',
      section,
      label: 'Model',
      description: `The ${label.toLowerCase()} tier's model: a 5.5-generation Claude id, or an Ollama tag.`,
      type: 'model',
      apply: 'restart',
      def: model,
      providerKey: `Agent:Llm:${name}Provider`,
    },
    {
      key: `Agent:Llm:${name}MaxTokens`,
      group: 'models',
      section,
      label: 'Max output tokens',
      description: 'Output ceiling of one call.',
      type: 'int',
      apply: 'live',
      def: tokens,
      min: 64,
      max: 128000,
      unit: 'tokens',
    },
    {
      key: `Agent:Llm:${name}Effort`,
      group: 'models',
      section,
      label: 'Effort',
      description: 'output_config.effort ("" = omit).',
      type: 'enum',
      apply: 'live',
      def: effort,
      options: EFFORT,
    },
    {
      key: `Agent:Llm:${name}Thinking`,
      group: 'models',
      section,
      label: 'Thinking',
      description: 'off = the cheapest setting the model accepts.',
      type: 'enum',
      apply: 'live',
      def: 'off',
      options: THINKING,
    },
  ];
}

const DEFS: Def[] = [
  {
    key: 'Agent:Llm:ApiKey',
    group: 'models',
    section: 'Anthropic',
    label: 'API key',
    description: 'The Anthropic key every Claude tier uses. Write-only: it never comes back.',
    type: 'secret',
    apply: 'restart',
    def: null,
    base: 'sk-ant-api03-mockmockmockab12',
    source: 'env',
    secret: true,
  },
  {
    key: 'Agent:Llm:BaseUrl',
    group: 'models',
    section: 'Anthropic',
    label: 'Base URL',
    description: 'Empty = api.anthropic.com. A proxy or gateway URL otherwise.',
    type: 'url',
    apply: 'restart',
    def: '',
  },
  {
    key: 'Agent:Llm:MaxRetries',
    group: 'models',
    section: 'Anthropic',
    label: 'Retries',
    description: 'SDK retries per call.',
    type: 'int',
    apply: 'restart',
    def: 2,
    min: 0,
    max: 10,
  },
  {
    key: 'Agent:Llm:TimeoutSeconds',
    group: 'models',
    section: 'Anthropic',
    label: 'Timeout',
    description: 'Per small call.',
    type: 'int',
    apply: 'restart',
    def: 60,
    min: 5,
    max: 600,
    unit: 's',
  },
  ...tier('Main', 'Brain', 'claude-opus-5-5', 1200, 'medium'),
  ...tier('Worker', 'Worker', 'claude-sonnet-5-5', 800, 'low'),
  ...tier('Cheap', 'Memory', 'claude-haiku-5-5', 400, 'low'),
  {
    key: 'Agent:Llm:Ollama:Endpoint',
    group: 'models',
    section: 'Ollama',
    label: 'Endpoint',
    description: 'The Ollama server ("http://10.1.2.50:11434"). Empty = no Ollama anywhere.',
    type: 'url',
    apply: 'restart',
    def: '',
    base: 'http://10.1.2.50:11434',
    source: 'config',
  },
  {
    key: 'Agent:Llm:Ollama:ApiKey',
    group: 'models',
    section: 'Ollama',
    label: 'Bearer token',
    description: 'Only for a proxy in front of Ollama.',
    type: 'secret',
    apply: 'restart',
    def: null,
    secret: true,
  },
  {
    key: 'Agent:Llm:Ollama:DefaultNumCtx',
    group: 'models',
    section: 'Ollama',
    label: 'Context size',
    description: 'options.num_ctx (0 = server default).',
    type: 'int',
    apply: 'restart',
    def: 0,
    min: 0,
    max: 1048576,
    unit: 'tokens',
  },
  {
    key: 'Agent:Llm:Ollama:KeepAlive',
    group: 'models',
    section: 'Ollama',
    label: 'Keep alive',
    description: '"30m", "-1"… empty = server default.',
    type: 'string',
    apply: 'restart',
    def: '',
  },
  {
    key: 'Agent:Llm:Ollama:TimeoutSeconds',
    group: 'models',
    section: 'Ollama',
    label: 'Timeout',
    description: 'Per call, streams included (0 = 120 s).',
    type: 'int',
    apply: 'restart',
    def: 120,
    min: 0,
    max: 3600,
    unit: 's',
  },
  {
    key: 'Agent:Llm:Ollama:VisionModels',
    group: 'models',
    section: 'Ollama',
    label: 'Vision models',
    description: 'Models that accept images besides those whose capabilities say so.',
    type: 'list',
    apply: 'restart',
    def: [],
  },
  {
    key: 'Agent:Llm:Perception:Provider',
    group: 'models',
    section: 'Vision',
    label: 'Provider',
    description: 'The model that looks at pictures for the brain.',
    type: 'enum',
    apply: 'restart',
    def: 'anthropic',
    options: ['anthropic', 'azure-openai', 'openai', 'ollama'],
  },
  {
    key: 'Agent:Llm:Perception:Model',
    group: 'models',
    section: 'Vision',
    label: 'Model',
    description: 'Perception model id.',
    type: 'model',
    apply: 'restart',
    def: 'claude-sonnet-5-5',
    providerKey: 'Agent:Llm:Perception:Provider',
  },
  {
    key: 'Agent:Llm:Perception:ApiKey',
    group: 'models',
    section: 'Vision',
    label: 'API key',
    description: 'Azure/OpenAI/Ollama key; empty on Anthropic = the shared key.',
    type: 'secret',
    apply: 'restart',
    def: null,
    secret: true,
  },
  {
    key: 'Agent:Budget:DailyCapUsd',
    group: 'models',
    section: 'Budget',
    label: 'Daily cap',
    description: 'Model spend per day before the bot pauses.',
    type: 'number',
    apply: 'live',
    def: 50,
    min: 0,
    max: 10000,
    unit: 'USD',
  },
  {
    key: 'Agent:Budget:MonthlyCapUsd',
    group: 'models',
    section: 'Budget',
    label: 'Monthly cap',
    description: 'Model spend per month.',
    type: 'number',
    apply: 'live',
    def: 200,
    min: 0,
    max: 100000,
    unit: 'USD',
  },
  {
    key: 'Agent:Chat:DisplayName',
    group: 'bot',
    section: 'Identity',
    label: 'Display name',
    description: 'The name the bot answers to (a rename in game wins).',
    type: 'string',
    apply: 'restart',
    def: 'claudechan',
  },
  {
    key: 'Agent:Chat:Aliases',
    group: 'bot',
    section: 'Identity',
    label: 'Aliases',
    description: 'Other names it answers to.',
    type: 'list',
    apply: 'restart',
    def: ['claude chan', 'claude'],
  },
  {
    key: 'Agent:Self:TrustedPlayers',
    group: 'bot',
    section: 'Trust',
    label: 'Trusted players',
    description: 'Players whose requests the bot treats as trusted (and the only ones a viewer line may speak as).',
    type: 'list',
    apply: 'restart',
    def: [],
    base: ['poofimafox', 'kiwi'],
    source: 'config',
  },
  {
    key: 'Supervisor:Viewer:Enabled',
    group: 'viewer',
    section: 'Viewer',
    label: 'Viewer routes',
    description: "Chat, feed, now and Story Mode for the web. Off cuts the explorer's live pages.",
    type: 'bool',
    apply: 'restart',
    def: false,
    base: true,
    source: 'config',
  },
  {
    key: 'Supervisor:Viewer:AllowCommands',
    group: 'viewer',
    section: 'Viewer',
    label: 'Allow commands',
    description: 'Web users with the commands role may arm command grants.',
    type: 'bool',
    apply: 'live',
    def: false,
  },
  {
    key: 'Supervisor:Viewer:PerMinute',
    group: 'viewer',
    section: 'Viewer',
    label: 'Lines per minute',
    description: 'Viewer chat lines per player per minute.',
    type: 'int',
    apply: 'live',
    def: 12,
    min: 1,
    max: 120,
  },
  {
    key: 'Supervisor:Viewer:AllowStory',
    group: 'viewer',
    section: 'Viewer',
    label: 'Story Mode writes',
    description: 'Empty = on whenever the viewer is.',
    type: 'bool',
    apply: 'live',
    def: null,
    nullable: true,
  },
  {
    key: 'Supervisor:Viewer:OpAccounts',
    group: 'viewer',
    section: 'Viewer',
    label: 'Op accounts',
    description: 'Accounts that are op on a b1.8.1 server (Story Mode time control).',
    type: 'list',
    apply: 'live',
    def: [],
  },
  {
    key: 'Supervisor:Viewer:Designer:Enabled',
    group: 'story',
    section: 'Design Studio',
    label: 'Design Studio',
    description: 'The /api/designer routes.',
    type: 'bool',
    apply: 'restart',
    def: true,
  },
  {
    key: 'Supervisor:Viewer:Designer:MaxRounds',
    group: 'story',
    section: 'Design Studio',
    label: 'Rounds per message',
    description: 'Model calls per message.',
    type: 'int',
    apply: 'live',
    def: 8,
    min: 1,
    max: 40,
  },
  {
    key: 'Supervisor:Viewer:Designer:MaxUsdPerMessage',
    group: 'story',
    section: 'Design Studio',
    label: 'Cost cap per message',
    description: 'A message stops calling the model past this.',
    type: 'number',
    apply: 'live',
    def: 1.5,
    min: 0.01,
    max: 50,
    unit: 'USD',
  },
  {
    key: 'Hive:Enabled',
    group: 'hive',
    section: 'Hive',
    label: 'Hive',
    description: 'Off = no clones, no hive tools.',
    type: 'bool',
    apply: 'restart',
    def: true,
  },
  {
    key: 'Hive:MaxClones',
    group: 'hive',
    section: 'Hive',
    label: 'Max clones',
    description: 'Hard cap on accounts out at once (0 = the resource ceiling decides).',
    type: 'int',
    apply: 'restart',
    def: 0,
    min: 0,
    max: 200,
  },
  {
    key: 'Hive:Resources:LimitMb',
    group: 'hive',
    section: 'Resources',
    label: 'Memory limit',
    description: 'The ceiling the hive plans clones into (0 = the cgroup limit).',
    type: 'number',
    apply: 'restart',
    def: 0,
    base: 14336,
    source: 'config',
    min: 0,
    max: 262144,
    unit: 'MB',
  },
  {
    key: 'Host:ConsoleLogLevel',
    group: 'logging',
    section: 'Console',
    label: 'Console level',
    description: 'Lowest level written to the console / journal (the file log keeps Debug).',
    type: 'enum',
    apply: 'live',
    def: 'Information',
    options: ['Trace', 'Debug', 'Information', 'Warning', 'Error'],
  },
  {
    key: 'Host:LogFormat',
    group: 'logging',
    section: 'Console',
    label: 'Format',
    description: 'auto = systemd under journald, else simple.',
    type: 'enum',
    apply: 'restart',
    def: 'auto',
    options: ['auto', 'simple', 'systemd', 'json'],
  },
  {
    key: 'Supervisor:Token',
    group: 'system',
    section: 'Supervisor',
    label: 'Supervisor token',
    description: 'The bridge proxies the bot with it.',
    type: 'secret',
    apply: 'restart',
    def: null,
    base: 'f00dfeedf00dfeedf00dfeed9c3e',
    source: 'config',
    secret: true,
    readOnly:
      'The bridge reads it at start; changing it here would cut the explorer off. Edit /etc/mineclaw/appsettings.json.',
  },
  {
    key: 'Supervisor:AllowAdminWrites',
    group: 'system',
    section: 'Supervisor',
    label: 'Admin writes',
    description: 'Every admin route, this page included.',
    type: 'bool',
    apply: 'restart',
    def: false,
    base: true,
    source: 'config',
    readOnly: 'Turning it off from here would lock the Administration page out.',
  },
  {
    key: 'Supervisor:Port',
    group: 'system',
    section: 'Supervisor',
    label: 'Port',
    description: 'The supervisor listener.',
    type: 'int',
    apply: 'restart',
    def: 8765,
    readOnly: 'The bridge connects here.',
  },
];

interface State {
  override: Map<string, SettingValue>;
  /** Values the running process uses (live keys follow the override at once; restart keys only after a restart). */
  running: Map<string, SettingValue>;
  audit: SettingsAuditEntry[];
  backups: number;
  updatedAt: string | null;
}

let state: State | null = null;

function baseOf(d: Def): SettingValue {
  return d.base !== undefined ? d.base : d.def;
}

function s(): State {
  if (state) return state;
  const running = new Map<string, SettingValue>();
  for (const d of DEFS) running.set(d.key, baseOf(d));
  state = {
    override: new Map(),
    running,
    backups: 2,
    updatedAt: isoAgo(60 * 26),
    audit: [
      {
        at: isoAgo(60 * 26),
        who: 'fox (web)',
        action: 'update',
        keys: ['Agent:Llm:WorkerModel'],
        reason: 'sonnet for workers again',
        ok: true,
        note: null,
      },
      {
        at: isoAgo(60 * 25),
        who: 'fox (web)',
        action: 'restart',
        keys: [],
        reason: 'pick up the worker model',
        ok: true,
        note: null,
      },
      {
        at: isoAgo(60 * 3),
        who: 'fox (web)',
        action: 'test',
        keys: ['Agent:Llm:Ollama:Endpoint'],
        reason: null,
        ok: true,
        note: 'ollama: 4 models',
      },
    ],
  };
  return state;
}

/** For tests: back to the seed. */
export function resetAdministration(): void {
  state = null;
}

function hint(secret: SettingValue): string | null {
  return typeof secret === 'string' && secret.length >= 12 ? `…${secret.slice(-4)}` : null;
}

function savedValue(d: Def): SettingValue {
  const st = s();
  return st.override.has(d.key) ? st.override.get(d.key)! : baseOf(d);
}

function same(a: SettingValue, b: SettingValue): boolean {
  return JSON.stringify(a) === JSON.stringify(b);
}

function setting(d: Def): AdminSetting {
  const st = s();
  const overridden = st.override.has(d.key);
  const running = st.running.get(d.key) ?? null;
  const saved = savedValue(d);
  return {
    key: d.key,
    group: d.group,
    section: d.section,
    label: d.label,
    description: d.description,
    type: d.type,
    apply: d.apply,
    secret: !!d.secret,
    editable: !d.readOnly,
    readOnlyReason: d.readOnly ?? null,
    default: d.secret ? null : d.def,
    value: d.secret ? null : running,
    secretState: d.secret ? { set: typeof running === 'string' && running.length > 0, hint: hint(running) } : null,
    source: overridden ? 'override' : (d.source ?? 'default'),
    overridden,
    pendingRestart: d.apply === 'restart' && !same(saved, running),
    nullable: d.nullable ?? null,
    options: d.type === 'model' ? ALLOWED_CLAUDE : (d.options ?? null),
    min: d.min ?? null,
    max: d.max ?? null,
    unit: d.unit ?? null,
    providerKey: d.providerKey ?? null,
  };
}

export const NODE: AdminNode = {
  id: 'local',
  name: 'maid_chan',
  version: 'mock-0.1.0',
  startedAt: isoAgo(60 * 25),
  gameVersion: 'b1.8.1',
  supervisorUrl: 'http://127.0.0.1:8765',
  reachable: true,
};

export function nodes(): { nodes: AdminNode[] } {
  return { nodes: [NODE] };
}

export function settings(): AdminSettingsResponse {
  const list = DEFS.map(setting);
  const st = s();
  return {
    node: NODE,
    groups: GROUPS,
    settings: list,
    allowedClaudeModels: ALLOWED_CLAUDE,
    restartPending: list.filter((x) => x.pendingRestart).map((x) => x.key),
    override: {
      path: '/var/lib/mineclaw/settings.override.json',
      exists: st.override.size > 0 || st.updatedAt !== null,
      updatedAt: st.updatedAt,
      backups: st.backups,
    },
  };
}

export class AdminMockError extends Error {
  constructor(
    readonly status: number,
    message: string,
    readonly code: string,
    readonly details?: Array<{ path: string; message: string }>,
  ) {
    super(message);
  }
}

function check(d: Def | undefined, v: SettingValue, provider: (k: string) => SettingValue): string | null {
  if (!d) return 'no such setting';
  if (d.readOnly) return 'read-only: ' + d.readOnly;
  if (v === null) return null; // reset to the base config
  switch (d.type) {
    case 'bool':
      return typeof v === 'boolean' ? null : 'must be true or false';
    case 'int':
    case 'number': {
      if (typeof v !== 'number' || !Number.isFinite(v)) return 'must be a number';
      if (d.type === 'int' && !Number.isInteger(v)) return 'must be a whole number';
      if (d.min !== undefined && v < d.min) return `at least ${d.min}`;
      if (d.max !== undefined && v > d.max) return `at most ${d.max}`;
      return null;
    }
    case 'enum':
      return typeof v === 'string' && (d.options ?? []).includes(v) ? null : `one of ${(d.options ?? []).join(', ')}`;
    case 'list':
      return Array.isArray(v) ? null : 'must be a list';
    case 'url':
      if (typeof v !== 'string') return 'must be text';
      return v === '' || /^https?:\/\/[^\s/]+/i.test(v) ? null : 'must be an http(s) URL';
    case 'model': {
      if (typeof v !== 'string' || !v.trim()) return 'pick a model';
      const p = d.providerKey ? provider(d.providerKey) : 'anthropic';
      if ((p ?? 'anthropic') === 'anthropic' && !ALLOWED_CLAUDE.includes(v))
        return `Claude models must be 5.5-generation (${ALLOWED_CLAUDE.join(', ')})`;
      return null;
    }
    default:
      return typeof v === 'string' ? null : 'must be text';
  }
}

export function save(
  changes: SettingChange[],
  reason: string,
  who: string,
): { applied: string[]; restartRequired: string[]; backup: string | null } {
  const st = s();
  const pending = new Map(changes.map((c) => [c.key, c.value] as const));
  const provider = (k: string) =>
    pending.has(k)
      ? (pending.get(k) ?? baseOf(DEFS.find((d) => d.key === k)!))
      : savedValue(DEFS.find((d) => d.key === k)!);
  const details: Array<{ path: string; message: string }> = [];
  for (const c of changes) {
    const msg = check(
      DEFS.find((d) => d.key === c.key),
      c.value,
      provider,
    );
    if (msg) details.push({ path: c.key, message: msg });
  }
  if (details.length) {
    st.audit.push({
      at: nowIso(),
      who,
      action: 'update',
      keys: changes.map((c) => c.key),
      reason,
      ok: false,
      note: `refused: ${details.length} invalid`,
    });
    throw new AdminMockError(
      400,
      `${details.length} setting${details.length === 1 ? '' : 's'} refused; nothing was saved`,
      'bad_request',
      details,
    );
  }
  const applied: string[] = [];
  const restartRequired: string[] = [];
  for (const c of changes) {
    const d = DEFS.find((x) => x.key === c.key)!;
    if (c.value === null) st.override.delete(c.key);
    else st.override.set(c.key, c.value);
    if (d.apply === 'live') {
      st.running.set(c.key, savedValue(d));
      applied.push(c.key);
    } else if (!same(savedValue(d), st.running.get(c.key) ?? null)) restartRequired.push(c.key);
  }
  st.backups += 1;
  st.updatedAt = nowIso();
  st.audit.push({ at: nowIso(), who, action: 'update', keys: changes.map((c) => c.key), reason, ok: true, note: null });
  return {
    applied,
    restartRequired,
    backup: `settings.override.${new Date().toISOString().replace(/\D/g, '').slice(0, 14)}.json`,
  };
}

const TAGS = [
  {
    name: 'qwen3.5:32b',
    sizeBytes: 19_800_000_000,
    family: 'qwen3',
    parameterSize: '32B',
    capabilities: ['completion', 'tools', 'thinking'],
  },
  {
    name: 'gemma4:27b',
    sizeBytes: 17_000_000_000,
    family: 'gemma4',
    parameterSize: '27B',
    capabilities: ['completion', 'vision', 'tools'],
  },
  {
    name: 'deepseek-v4.1-flash:cloud',
    sizeBytes: null,
    family: 'deepseek',
    parameterSize: null,
    capabilities: ['completion', 'tools'],
  },
  { name: 'llama4:8b', sizeBytes: 4_900_000_000, family: 'llama', parameterSize: '8B', capabilities: ['completion'] },
];

function reachable(endpoint: string): boolean {
  return /^https?:\/\//i.test(endpoint) && !/bad|unreachable|nowhere/i.test(endpoint);
}

function currentEndpoint(): string {
  const v = s().running.get('Agent:Llm:Ollama:Endpoint');
  return typeof v === 'string' ? v : '';
}

export function ollamaModels(endpoint?: string): OllamaModelsResponse {
  const e = (endpoint ?? '').trim() || currentEndpoint();
  if (!e) return { endpoint: '', ok: false, error: 'no Ollama endpoint is set', models: [] };
  if (!reachable(e))
    return { endpoint: e, ok: false, error: 'the endpoint did not answer (connection refused)', models: [] };
  return { endpoint: e, ok: true, error: null, models: TAGS.map((t) => ({ ...t, modifiedAt: isoAgo(60 * 24 * 3) })) };
}

export function test(
  body: { kind: 'anthropic' | 'ollama'; apiKey?: string; model?: string; endpoint?: string },
  who: string,
): ProviderTestResponse {
  const st = s();
  let out: ProviderTestResponse;
  if (body.kind === 'ollama') {
    const r = ollamaModels(body.endpoint);
    out = r.ok
      ? {
          ok: true,
          message: `${r.endpoint} answered: ${r.models.length} models`,
          latencyMs: 42,
          models: r.models.map((m) => m.name),
        }
      : { ok: false, message: r.error ?? 'failed', latencyMs: null, models: null };
  } else {
    const model = body.model ?? 'claude-haiku-5-5';
    if (!ALLOWED_CLAUDE.includes(model))
      out = { ok: false, message: `${model} is not a 5.5-generation model`, latencyMs: null, models: null };
    else if (body.apiKey !== undefined && !body.apiKey.startsWith('sk-ant-'))
      out = { ok: false, message: 'the API key was rejected (401)', latencyMs: 180, models: null };
    else out = { ok: true, message: `key ok: ${model}`, latencyMs: 210, models: [model] };
  }
  st.audit.push({
    at: nowIso(),
    who,
    action: 'test',
    keys: [body.kind === 'ollama' ? 'Agent:Llm:Ollama:Endpoint' : 'Agent:Llm:ApiKey'],
    reason: null,
    ok: out.ok,
    note: out.ok ? `${body.kind}: ok` : `${body.kind}: ${out.message}`,
  });
  return out;
}

function unsafe(): boolean {
  try {
    return sessionStorage.getItem('mcx-mock-restart-unsafe') === '1';
  } catch {
    return false;
  }
}

export function safety(): RestartSafety {
  if (unsafe())
    return {
      safe: false,
      checkedAt: nowIso(),
      night: true,
      timeOfDay: 15200,
      reasons: ['maid_chan_101 is outside at night', 'maid_chan_102 is at the edge of a high build (a 14-block drop)'],
      bodies: [
        { name: 'maid_chan', dim: 0, y: 66, exposed: false, highBuild: false, drop: 1, reason: null },
        { name: 'maid_chan_101', dim: 0, y: 70, exposed: true, highBuild: false, drop: 1, reason: 'outside at night' },
        {
          name: 'maid_chan_102',
          dim: 0,
          y: 92,
          exposed: false,
          highBuild: true,
          drop: 14,
          reason: 'at the edge of a high build (a 14-block drop)',
        },
      ],
    };
  return {
    safe: true,
    checkedAt: nowIso(),
    night: false,
    timeOfDay: 6200,
    reasons: [],
    bodies: [
      { name: 'maid_chan', dim: 0, y: 66, exposed: false, highBuild: false, drop: 1, reason: null },
      { name: 'maid_chan_101', dim: 0, y: 64, exposed: false, highBuild: false, drop: 1, reason: null },
    ],
  };
}

export function restart(
  body: { reason: string; force?: boolean },
  who: string,
): { accepted: boolean; forced: boolean; safety: RestartSafety; message: string } {
  const st = s();
  const sf = safety();
  if (!sf.safe && !body.force) {
    st.audit.push({
      at: nowIso(),
      who,
      action: 'restart_refused',
      keys: [],
      reason: body.reason,
      ok: false,
      note: sf.reasons.join('; '),
    });
    throw new AdminMockError(409, `not safe to restart now: ${sf.reasons.join('; ')}`, 'conflict');
  }
  st.audit.push({
    at: nowIso(),
    who,
    action: 'restart',
    keys: [],
    reason: body.reason,
    ok: true,
    note: body.force && !sf.safe ? 'forced' : null,
  });
  // The mock "restarts" at once: every saved value is now running.
  for (const d of DEFS) st.running.set(d.key, savedValue(d));
  return {
    accepted: true,
    forced: !!body.force && !sf.safe,
    safety: sf,
    message: 'restarting: systemd brings the bot back in about 10 s',
  };
}

export function audit(max: number): { entries: SettingsAuditEntry[] } {
  return { entries: [...s().audit].reverse().slice(0, max) };
}
