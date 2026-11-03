/**
 * Administration: the bot's own settings (docs/web/api.md §9, docs/web/deploy.md "Bot settings from the web").
 *
 * Everything is scoped to a **node**: one bot instance (its supervisor) the bridge can reach. Today there is exactly one,
 * `local` (the bridge's `SupervisorUrl`); the routes already carry `:node` so more nodes plug in later without a new API.
 *
 * Secrets never travel back: a secret setting's `value` and `default` are always null and `secretState` says only whether it
 * is set and its last four characters. Writes need the `admin` role and a fresh step-up (`POST /api/auth/step-up`, 5 min).
 */
import { z } from 'zod';
import { IsoDateTime, QInt } from './common.js';

/** A node id: `local` today (lower-case letters, digits, `-`). */
export const NodeId = z.string().regex(/^[a-z0-9][a-z0-9-]{0,31}$/);
export type NodeId = z.infer<typeof NodeId>;

export const AdminNodeParams = z.object({ node: NodeId }).strict();
export type AdminNodeParams = z.infer<typeof AdminNodeParams>;

/** One bot instance (supervisor) the explorer can administer. */
export const AdminNode = z.object({
  id: NodeId,
  name: z.string().describe("the bot's main account (maid_chan)"),
  version: z.string().nullish().describe("the bot build's version"),
  startedAt: IsoDateTime.nullish(),
  gameVersion: z.string().nullish().describe('b1.8.1 | 1.12.2'),
  supervisorUrl: z.string().nullish().describe('where the bridge reaches it (loopback)'),
  reachable: z.boolean().nullish().describe('false = the bridge could not reach it just now'),
});
export type AdminNode = z.infer<typeof AdminNode>;

export const AdminNodesResponse = z.object({ nodes: z.array(AdminNode) });
export type AdminNodesResponse = z.infer<typeof AdminNodesResponse>;

/** A setting's editor. `model` = a model id (Claude ids from `allowedClaudeModels` or an Ollama tag, see `providerKey`). */
export const SettingType = z.enum(['bool', 'int', 'number', 'string', 'text', 'enum', 'url', 'list', 'secret', 'model']);
export type SettingType = z.infer<typeof SettingType>;

/** `live` = applied the moment it is saved; `restart` = saved now, used after the bot restarts. */
export const SettingApply = z.enum(['live', 'restart']);
export type SettingApply = z.infer<typeof SettingApply>;

/** Where the running value came from (lowest to highest): built-in default, the base config file, env, the override file. */
export const SettingSource = z.enum(['default', 'config', 'env', 'override', 'cli']);
export type SettingSource = z.infer<typeof SettingSource>;

/** A scalar setting value (or a list of strings); null = unset / reset. */
export const SettingValue = z.union([z.string(), z.number(), z.boolean(), z.array(z.string()), z.null()]);
export type SettingValue = z.infer<typeof SettingValue>;

export const SecretState = z.object({
  set: z.boolean(),
  hint: z.string().nullish().describe('"…ab12": the last four characters, only when at least 12 long; never more'),
});
export type SecretState = z.infer<typeof SecretState>;

export const AdminSettingGroup = z.object({
  id: z.string(),
  title: z.string(),
  description: z.string(),
});
export type AdminSettingGroup = z.infer<typeof AdminSettingGroup>;

/** One configurable (or shown read-only) setting with its running value. */
export const AdminSetting = z.object({
  key: z.string().describe('config path, "Agent:Llm:MainModel"'),
  group: z.string(),
  section: z.string().describe('sub-heading inside the group ("Anthropic", "Tiers", "Ollama"…)'),
  label: z.string(),
  description: z.string(),
  type: SettingType,
  apply: SettingApply,
  secret: z.boolean(),
  editable: z.boolean(),
  readOnlyReason: z.string().nullish(),
  default: SettingValue.optional().describe('null for secrets'),
  value: SettingValue.optional().describe('the running value; null for secrets'),
  secretState: SecretState.nullish(),
  source: SettingSource,
  /** The override file holds a value for this key (it can be reset to the base config). */
  overridden: z.boolean(),
  /** A restart setting whose saved value is not running yet. */
  pendingRestart: z.boolean(),
  nullable: z.boolean().nullish().describe('null is a meaningful value ("inherit")'),
  options: z.array(z.string()).nullish().describe('enum choices, or the allowed Claude ids for a model'),
  min: z.number().nullish(),
  max: z.number().nullish(),
  unit: z.string().nullish(),
  /** For a `model`: the key of the provider setting that decides whether it is a Claude id or an Ollama tag. */
  providerKey: z.string().nullish(),
});
export type AdminSetting = z.infer<typeof AdminSetting>;

export const OverrideFileInfo = z.object({
  path: z.string(),
  exists: z.boolean(),
  updatedAt: IsoDateTime.nullish(),
  backups: z.number().int().nonnegative(),
});
export type OverrideFileInfo = z.infer<typeof OverrideFileInfo>;

/** GET /api/admin/nodes/:node/settings. */
export const AdminSettingsResponse = z.object({
  node: AdminNode,
  groups: z.array(AdminSettingGroup),
  settings: z.array(AdminSetting),
  /** The Claude ids the bot may run (the startup 5.5-generation rule); a Claude model field offers only these. */
  allowedClaudeModels: z.array(z.string()),
  restartPending: z.array(z.string()).describe('keys saved but not running until a restart'),
  override: OverrideFileInfo,
});
export type AdminSettingsResponse = z.infer<typeof AdminSettingsResponse>;

export const SettingChange = z
  .object({
    key: z.string().min(1).max(200),
    /** The new value; null = drop the override (back to the base config). A secret takes its new text ("" = clear it). */
    value: SettingValue,
  })
  .strict();
export type SettingChange = z.infer<typeof SettingChange>;

/** POST /api/admin/nodes/:node/settings: all-or-nothing. */
export const AdminSettingsSaveRequest = z
  .object({
    changes: z.array(SettingChange).min(1).max(100),
    reason: z.string().trim().min(3, 'say why (at least 3 characters)').max(300),
  })
  .strict();
export type AdminSettingsSaveRequest = z.infer<typeof AdminSettingsSaveRequest>;

export const AdminSettingsSaveResponse = z.object({
  applied: z.array(z.string()).describe('keys now running'),
  restartRequired: z.array(z.string()).describe('keys saved that need a restart'),
  backup: z.string().nullish().describe('the previous override file, kept as this backup'),
});
export type AdminSettingsSaveResponse = z.infer<typeof AdminSettingsSaveResponse>;

/** POST /api/admin/nodes/:node/settings/test: checks a provider without saving. Omitted fields use the running values. */
export const ProviderTestRequest = z
  .object({
    kind: z.enum(['anthropic', 'ollama']),
    apiKey: z.string().max(400).optional().describe('anthropic: a candidate key (else the running one)'),
    baseUrl: z.string().max(400).optional(),
    model: z.string().max(200).optional().describe('anthropic: a 5.5-generation id; ollama: a tag to look for'),
    endpoint: z.string().max(400).optional().describe('ollama: a candidate endpoint'),
    ollamaApiKey: z.string().max(400).optional(),
  })
  .strict();
export type ProviderTestRequest = z.infer<typeof ProviderTestRequest>;

export const ProviderTestResponse = z.object({
  ok: z.boolean(),
  message: z.string(),
  latencyMs: z.number().nullish(),
  models: z.array(z.string()).nullish(),
});
export type ProviderTestResponse = z.infer<typeof ProviderTestResponse>;

/** GET /api/admin/nodes/:node/ollama/models?endpoint=. */
export const OllamaModelsQuery = z.object({ endpoint: z.string().max(400).optional() });
export type OllamaModelsQuery = z.infer<typeof OllamaModelsQuery>;

export const OllamaModel = z.object({
  name: z.string(),
  sizeBytes: z.number().nullish(),
  modifiedAt: z.string().nullish(),
  family: z.string().nullish(),
  parameterSize: z.string().nullish(),
  capabilities: z.array(z.string()).nullish().describe('from /api/show: tools, vision, thinking, completion…'),
});
export type OllamaModel = z.infer<typeof OllamaModel>;

export const OllamaModelsResponse = z.object({
  endpoint: z.string(),
  ok: z.boolean(),
  error: z.string().nullish(),
  models: z.array(OllamaModel),
});
export type OllamaModelsResponse = z.infer<typeof OllamaModelsResponse>;

export const RestartBodyCheck = z.object({
  name: z.string(),
  dim: z.number().int().nullish(),
  y: z.number().nullish(),
  exposed: z.boolean().describe('in the overworld at night under open sky'),
  highBuild: z.boolean().describe('standing at a ledge with a long drop'),
  drop: z.number().int().nullish().describe('blocks of air below the ledge'),
  reason: z.string().nullish(),
});
export type RestartBodyCheck = z.infer<typeof RestartBodyCheck>;

/** GET …/restart/safety: whether a restart is safe right now (never while a body is exposed at night or on a high build). */
export const RestartSafety = z.object({
  safe: z.boolean(),
  checkedAt: IsoDateTime,
  night: z.boolean().nullish().describe('null = not in game'),
  timeOfDay: z.number().int().nullish(),
  reasons: z.array(z.string()),
  bodies: z.array(RestartBodyCheck),
});
export type RestartSafety = z.infer<typeof RestartSafety>;

/** POST …/restart: refused (409) while unsafe unless `force` with a reason. */
export const RestartRequest = z
  .object({
    reason: z.string().trim().min(3).max(300),
    force: z.boolean().optional(),
  })
  .strict();
export type RestartRequest = z.infer<typeof RestartRequest>;

export const RestartResponse = z.object({
  accepted: z.boolean(),
  forced: z.boolean().nullish(),
  safety: RestartSafety,
  message: z.string().nullish(),
});
export type RestartResponse = z.infer<typeof RestartResponse>;

export const SettingsAuditQuery = z.object({ max: QInt.min(1).max(500).default(100) });
export type SettingsAuditQuery = z.infer<typeof SettingsAuditQuery>;

/** One admin action on the bot's settings. Values are never recorded (secrets would leak); keys only. */
export const SettingsAuditEntry = z.object({
  at: IsoDateTime,
  who: z.string(),
  action: z.enum(['update', 'restart', 'restart_refused', 'test']),
  keys: z.array(z.string()),
  reason: z.string().nullish(),
  ok: z.boolean(),
  note: z.string().nullish(),
});
export type SettingsAuditEntry = z.infer<typeof SettingsAuditEntry>;

export const SettingsAuditResponse = z.object({ entries: z.array(SettingsAuditEntry) });
export type SettingsAuditResponse = z.infer<typeof SettingsAuditResponse>;

/** The web audit summary of an admin settings call: the keys (and the reason), never a value. */
export function adminSettingsAuditSummary(body: unknown): string {
  if (!body || typeof body !== 'object') return '';
  const b = body as { changes?: Array<{ key?: unknown }>; reason?: unknown; kind?: unknown; force?: unknown };
  const parts: string[] = [];
  if (Array.isArray(b.changes)) parts.push(`keys: ${b.changes.map((c) => String(c?.key ?? '?')).join(', ')}`);
  if (typeof b.kind === 'string') parts.push(`test ${b.kind}`);
  if (b.force === true) parts.push('forced');
  if (typeof b.reason === 'string') parts.push(`reason: ${b.reason}`);
  return parts.join('; ');
}
