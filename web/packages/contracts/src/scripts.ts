/**
 * Scripts library (explorer-spec §9) and the live script debugger (§12.8).
 * C# sources: `src/MineClaw.Agent/Routines/ScriptLibraryReport.cs`, `src/MineClaw.Scripting/ScriptDebugSnapshot.cs`.
 */
import { z } from 'zod';
import { Double, IsoDateTime, JsonValue, QBool, QInt } from './common.js';

/** `live` = the running bot's library (real lint); `db` = read from the db by the bridge (offline lint). */
export const ScriptSource = z.enum(['live', 'db']);
export type ScriptSource = z.infer<typeof ScriptSource>;

export const ScriptParamInfo = z.object({
  name: z.string(),
  default: z.string().nullish(),
  tuned: z.string().nullish(),
});
export type ScriptParamInfo = z.infer<typeof ScriptParamInfo>;

export const ScriptLintLine = z.object({
  severity: z.string().describe('error | warn'),
  line: z.number().int(),
  rule: z.string(),
  message: z.string(),
});
export type ScriptLintLine = z.infer<typeof ScriptLintLine>;

export const ScriptLintInfo = z.object({
  status: z.enum(['ok', 'warnings', 'errors', 'unknown']),
  errors: z.number().int(),
  warnings: z.number().int(),
  findings: z.array(ScriptLintLine),
  note: z.string().nullish(),
});
export type ScriptLintInfo = z.infer<typeof ScriptLintInfo>;

export const ScriptRunInfo = z.object({
  at: IsoDateTime,
  ok: z.boolean(),
  seconds: Double,
  source: z.enum(['live', 'saved']),
  script: z.string().nullish(),
  outcome: z.string().nullish(),
  actions: z.number().int().nullish(),
  failLine: z.number().int().nullish(),
  version: z.number().int().nullish(),
  params: z.string().nullish().describe('JSON text'),
  timings: z.string().nullish(),
  body: z.string().nullish(),
  yield: z.number().int().nullish(),
  status: z.string().nullish(),
});
export type ScriptRunInfo = z.infer<typeof ScriptRunInfo>;

export const ScriptVersionInfo = z.object({
  v: z.number().int(),
  lang: z.string(),
  author: z.string().nullish(),
  authorKind: z.string(),
  savedAt: IsoDateTime.nullish(),
  active: z.boolean(),
  runs: z.number().int(),
  successes: z.number().int(),
  failures: z.number().int(),
  avgSeconds: Double,
  lastRun: IsoDateTime.nullish(),
  topFailures: z.array(z.string()),
  lines: z.number().int(),
});
export type ScriptVersionInfo = z.infer<typeof ScriptVersionInfo>;

export const ScriptHandlerInfo = z.object({
  kind: z.string(),
  signature: z.string().describe('"kind|situation|where"'),
  js: z.string(),
  then: z.string(),
  note: z.string().nullish(),
  author: z.string().nullish(),
  version: z.number().int().nullish(),
  learnedAt: IsoDateTime.nullish(),
  lastUsed: IsoDateTime.nullish(),
  successes: z.number().int(),
  failures: z.number().int(),
  demoted: z.boolean(),
});
export type ScriptHandlerInfo = z.infer<typeof ScriptHandlerInfo>;

export const ScriptLibraryEntry = z.object({
  name: z.string(),
  version: z.number().int(),
  versions: z.number().int(),
  lang: z.string(),
  author: z.string().nullish(),
  authorKind: z.string(),
  purpose: z.string().nullish(),
  params: z.array(ScriptParamInfo),
  lastRun: IsoDateTime.nullish(),
  runs: z.number().int(),
  successRate: Double,
  avgSeconds: Double,
  timings: z.string().nullish(),
  usedBy: z.array(z.string()),
  lint: ScriptLintInfo,
  migration: z.string().nullish(),
  archived: z.boolean(),
  promotedAs: z.string().nullish(),
  lastOutcome: z.string().nullish(),
  duplicateOf: z.string().nullish(),
  updated: IsoDateTime,
  hasLuaHistory: z.boolean(),
  handlers: z.array(ScriptHandlerInfo).nullish(),
});
export type ScriptLibraryEntry = z.infer<typeof ScriptLibraryEntry>;

export const ScriptLibraryQuery = z.object({ source: ScriptSource.default('live') });
export const ScriptLibraryResponse = z.object({
  at: IsoDateTime,
  source: ScriptSource.optional().describe('added by Nest: which source answered'),
  scripts: z.array(ScriptLibraryEntry),
});
export type ScriptLibraryResponse = z.infer<typeof ScriptLibraryResponse>;

export const ScriptNameParams = z.object({ name: z.string().min(1).max(128) });
export const ScriptDetailQuery = z.object({
  source: ScriptSource.default('live'),
  v: QInt.min(1).optional().describe('version; omitted = active'),
});

/** C# ScriptVersionDetail. */
export const ScriptVersionDetail = z.object({
  script: ScriptLibraryEntry,
  version: ScriptVersionInfo,
  code: z.string(),
  lint: ScriptLintInfo,
  params: z.array(ScriptParamInfo),
  history: z.array(ScriptVersionInfo),
  runs: z.array(ScriptRunInfo),
  revertNote: z.string().nullish(),
  tunedParams: z.string().nullish(),
});
export type ScriptVersionDetail = z.infer<typeof ScriptVersionDetail>;

/** GET /api/scripts/:name/diff?a&b (LCS line diff, 3 lines of context). */
export const ScriptDiffQuery = z.object({
  source: ScriptSource.default('db'),
  a: QInt.min(1),
  b: QInt.min(1),
  context: QInt.min(0).max(20).default(3),
});
export const DiffLine = z.object({
  op: z.enum([' ', '+', '-']),
  text: z.string(),
  aLine: z.number().int().nullish(),
  bLine: z.number().int().nullish(),
});
export type DiffLine = z.infer<typeof DiffLine>;
export const DiffHunk = z.object({
  aStart: z.number().int(),
  aLines: z.number().int(),
  bStart: z.number().int(),
  bLines: z.number().int(),
  lines: z.array(DiffLine),
});
export type DiffHunk = z.infer<typeof DiffHunk>;
export const ScriptDiff = z.object({
  name: z.string(),
  a: z.number().int(),
  b: z.number().int(),
  added: z.number().int(),
  removed: z.number().int(),
  hunks: z.array(DiffHunk),
  unified: z.string().describe('the unified diff text (Copy)'),
});
export type ScriptDiff = z.infer<typeof ScriptDiff>;

/** GET /api/scripts/:name/lint?v. */
export const ScriptLintQuery = z.object({ source: ScriptSource.default('db'), v: QInt.min(1).optional() });
export const ScriptLintResponse = z.object({
  name: z.string(),
  version: z.number().int(),
  lint: ScriptLintInfo,
  params: z.array(ScriptParamInfo),
  offline: z.boolean().describe('true = offline lint: game function names not checked'),
});
export type ScriptLintResponse = z.infer<typeof ScriptLintResponse>;

// ---- live: who runs what, and the debugger ----

export const BodyScriptRow = z.object({
  body: z.string(),
  short: z.string(),
  script: z.string().nullish(),
  status: z.string().nullish(),
  line: z.number().int().nullish(),
  paused: z.boolean().nullish(),
  version: z.number().int().nullish(),
  role: z.string().nullish(),
});
export type BodyScriptRow = z.infer<typeof BodyScriptRow>;

/** GET /api/bodies/scripts (supervisor LiveScriptsDto). */
export const LiveScriptsResponse = z.object({ at: IsoDateTime, bodies: z.array(BodyScriptRow) });
export type LiveScriptsResponse = z.infer<typeof LiveScriptsResponse>;

export const ScriptFrame = z.object({
  function: z.string(),
  file: z.string(),
  line: z.number().int(),
  column: z.number().int(),
  script: z.boolean().describe('true = a script frame; false = host/SDK'),
});
export const ScriptLocal = z.object({ name: z.string(), value: z.string(), scope: z.string() });
export const ScriptTimingRow = z.object({
  action: z.string(),
  calls: z.number().int(),
  fails: z.number().int(),
  seconds: Double,
  avgMs: Double,
});
export const ScriptActionView = z.object({
  label: z.string(),
  args: z.string().nullish(),
  seconds: Double,
  atomic: z.boolean(),
  running: z.boolean(),
  ok: z.boolean().nullish(),
});
export const MachineStep = z.object({ from: z.string(), to: z.string(), why: z.string(), at: Double });
export const ScriptMachineView = z.object({
  id: z.string(),
  state: z.string(),
  transitions: z.number().int(),
  history: z.array(MachineStep),
  blackboard: JsonValue.describe('opaque JSON'),
  blackboardAgeSeconds: Double.nullish(),
});
export const ScriptLaneView = z.object({ id: z.number().int(), body: z.string(), status: z.string() });

export const ScriptStatus = z.enum(['starting', 'running', 'paused', 'done', 'failed', 'cancelled']);
export type ScriptStatus = z.infer<typeof ScriptStatus>;

/** C# ScriptDebugSnapshot — every field. */
export const ScriptDebugSnapshot = z.object({
  // identity
  sessionId: z.number().int(),
  name: z.string(),
  version: z.number().int().nullish(),
  lang: z.enum(['js', 'lua']),
  status: ScriptStatus,
  live: z.boolean(),
  body: z.string().nullish(),
  author: z.string().nullish(),
  role: z.string().nullish(),
  crew: z.string().nullish(),
  coordinator: z.string().nullish(),
  at: IsoDateTime,
  started: IsoDateTime,
  seq: z.number().int(),
  // position
  line: z.number().int().describe('1-based; 0 = unknown'),
  column: z.number().int(),
  lineLive: z.boolean(),
  inSdk: z.string().nullish(),
  snippet: z.string().nullish(),
  stack: z.array(ScriptFrame).describe('innermost first'),
  stackAgeSeconds: Double.nullish(),
  // action
  action: ScriptActionView.nullish(),
  actions: z.number().int(),
  elapsedSeconds: Double,
  timings: z.array(ScriptTimingRow),
  // inputs
  params: JsonValue.describe('effective params, opaque JSON or null'),
  args: JsonValue.describe("this run's args, opaque JSON"),
  checkpoint: z.string().nullish(),
  checkpointsPassed: z.number().int(),
  checkpointTotal: z.number().int(),
  // pause
  paused: z.boolean(),
  pausePending: z.boolean(),
  pauseKind: z.enum(['chat', 'script', 'brain', 'danger', 'stop']).nullish(),
  pauseReason: z.string().nullish(),
  pausedSeconds: Double.nullish(),
  // patching
  patching: z.string().nullish(),
  patchNote: z.string().nullish(),
  patches: z.number().int(),
  // state
  machine: ScriptMachineView.nullish(),
  locals: z.array(ScriptLocal).nullish(),
  localsNote: z.string().nullish(),
  localsAgeSeconds: Double.nullish(),
  lanes: z.array(ScriptLaneView),
  // progress
  progressPct: Double.nullish(),
  progressNote: z.string().nullish(),
  lastFailure: z.string().nullish(),
  outcome: z.string().nullish(),
  logs: z.array(z.string()),
  // source and controls
  code: z.string().nullish().describe('only with ?code=1'),
  controls: z.array(z.string()).describe('always empty for now'),
});
export type ScriptDebugSnapshot = z.infer<typeof ScriptDebugSnapshot>;

export const BodyParams = z.object({ body: z.string().min(1).max(64) });
export const BodyScriptQuery = z.object({ code: QBool.optional() });

/** GET /api/bodies/:body/script (supervisor BodyScriptReply). Asking keeps ~5 Hz sampling on for a few seconds. */
export const BodyScriptReply = z.object({
  body: z.string(),
  running: z.boolean(),
  snapshot: ScriptDebugSnapshot.nullish(),
  note: z.string().nullish(),
});
export type BodyScriptReply = z.infer<typeof BodyScriptReply>;
