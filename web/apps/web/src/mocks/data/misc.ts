/** Mock overview, personality, designs, shards, admin and audit data. */
import type {
  ActivityCompactionReport,
  AdminAuditEntry,
  DesignDetail,
  DesignDiff,
  DesignSummary,
  EditSchemaResponse,
  Overview,
  PersonalityResponse,
  RebuildJob,
  SelfSection,
  ShardDiff,
  ShardImportPreviewResponse,
  ShardImportResult,
  ShardPacksResponse,
  ShardPreview,
  WebAuditEntry,
} from '@mineclaw/contracts';
import { isoAgo, minsAgo, nowIso, pick, rng } from '../util';
import { kinds } from './memory';
import { raptorMetaIso } from './raptor';
import { designer, savedDesigns } from './designer';

export function overview(): Overview {
  const k = kinds().kinds;
  const count = (key: string) => k.find((x) => x.key === key)?.count ?? 0;
  return {
    db: {
      file: 'mineclaw.db',
      sizeBytes: 187_400_000,
      schemaVersion: 41,
      fts5: true,
      rtree: true,
      vectors: [{ model: 'text-embedding-3-small', dims: 1536, count: 4210 }],
      searchMode: 'hybrid',
      searchNote: 'hybrid: FTS + vectors + RAPTOR',
      serverVersion: 'b1.8.1',
      readAt: nowIso(),
    },
    knowledgePack: { label: 'b1.8.1-gamedata (b1.8.1) + skyblock', version: 'b1.8.1', modes: ['skyblock'], detail: 'pack built 2026-10-01' },
    embeddings: { configured: true, status: 'embeddings: configured (deployment text-embedding-3-small)', model: 'text-embedding-3-small', dims: 1536, matchesStored: true },
    cards: [
      { key: 'facts', label: 'Facts', count: count('facts'), detail: 'about 4 people', group: 'Knowledge', memoryKind: 'facts' },
      { key: 'claims', label: 'Claims', count: count('claims'), detail: '41 verified · 22 unverified · 9 disputed', group: 'Knowledge', memoryKind: 'claims' },
      { key: 'beliefs', label: 'Beliefs', count: count('beliefs'), detail: 'verified + provisional', group: 'Knowledge', memoryKind: 'beliefs' },
      { key: 'lessons', label: 'Lessons', count: count('lessons'), detail: null, group: 'Knowledge', memoryKind: 'lessons' },
      { key: 'episodes', label: 'Episodes', count: count('episodes'), detail: null, group: 'History', memoryKind: 'episodes' },
      { key: 'activity', label: 'Activity', count: count('activity'), detail: 'mine · farm · build', group: 'History', memoryKind: 'activity' },
      { key: 'chat', label: 'Chat lines', count: count('chat'), detail: null, group: 'History', memoryKind: 'chat' },
      { key: 'structures', label: 'Structures', count: count('structures'), detail: null, group: 'World', memoryKind: 'structures' },
      { key: 'places', label: 'Places', count: count('places'), detail: null, group: 'World', memoryKind: 'places' },
      { key: 'observations', label: 'World observations', count: 160, detail: 'chest 34 · portal 12 · farm 9 · tree 40', group: 'World', memoryKind: 'observed' },
      { key: 'chunks', label: 'Remembered chunks', count: 2210, detail: null, group: 'World', memoryKind: null },
      { key: 'traits', label: 'Personality traits', count: count('personality'), detail: 'trait 12 · like 9 · relationship 6', group: 'Self', memoryKind: 'personality' },
      { key: 'routines', label: 'Routines', count: count('routines'), detail: null, group: 'Library', memoryKind: 'routines' },
      { key: 'recipes', label: 'Recipes', count: count('recipes'), detail: null, group: 'Library', memoryKind: 'recipes' },
      { key: 'docs', label: 'Docs indexed', count: 5120, detail: null, group: 'Index', memoryKind: null },
      { key: 'vectors', label: 'Vectors', count: 4210, detail: null, group: 'Index', memoryKind: null },
    ],
    raptor: {
      headline: '538 nodes · 480 leaves · 3 summary layers',
      nodes: 538,
      leaves: 480,
      summaryLayers: 3,
      layers: [
        { layer: 0, name: 'leaves', nodes: 480, avgChildren: 'NaN', avgTokens: 86, maxTokens: 410, dirty: 0, share: 0.892 },
        { layer: 1, name: 'L1 summaries', nodes: 48, avgChildren: 10, avgTokens: 240, maxTokens: 380, dirty: 4, share: 0.089 },
        { layer: 2, name: 'L2 summaries', nodes: 8, avgChildren: 6, avgTokens: 360, maxTokens: 440, dirty: 0, share: 0.015 },
        { layer: 3, name: 'L3 summaries', nodes: 2, avgChildren: 4, avgTokens: 480, maxTokens: 480, dirty: 0, share: 0.004 },
      ],
      meta: raptorMetaIso(),
      insertedCosMean: 0.687,
      dirtySummaries: 4,
      pendingSummaries: 1,
      storedLayers: 4,
    },
    raptorNote: null,
  };
}

const SECTIONS: SelfSection[] = ['trait', 'like', 'dislike', 'opinion', 'joke', 'nickname', 'relationship', 'milestone'];
const TRAITS: Record<SelfSection, string[]> = {
  trait: ['cheerful and brief', 'tidy about storage', 'careful near lava', 'curious about the nether'],
  like: ['sunrise over the island', 'a full chest of cobble', 'building with Robin'],
  dislike: ['creepers near the farm', 'rain on the generator'],
  opinion: ['stone bricks look better than cobble', 'farms should have fences'],
  joke: ['calls the cobble "the gravel river"', 'pretends the void is a pool'],
  nickname: ['"claude" (from Fox)', '"maid" (from Robin)'],
  relationship: ['Fox: best friend, the one who set me up', 'Robin: build buddy, loves melons'],
  milestone: ['first 1,000 cobble', 'first night survived', 'first nether trip (clone 67)'],
};

export function personality(scope: string): PersonalityResponse {
  const r = rng(scope.length * 31);
  let id = 1;
  const groups = SECTIONS.map((section) => ({
    section,
    entries: TRAITS[section].map((text) => ({
      id: id++,
      section,
      text,
      player: section === 'relationship' ? (text.startsWith('Fox') ? 'poofimafox' : 'kiwi') : null,
      strength: Number((0.35 + r() * 0.6).toFixed(2)),
      observations: 1 + Math.floor(r() * 12),
      created: minsAgo(60 * 24 * (3 + r() * 20)),
      reinforced: minsAgo(60 * r() * 30),
      reason: section === 'like' ? 'mentioned it happily more than once' : null,
    })),
  }));
  return {
    scopes: ['main', 'maid_chan_67'],
    scope,
    groups,
    history: Array.from({ length: 40 }, (_, i) => ({
      version: 120 - i,
      at: minsAgo(i * 47),
      op: pick(r, ['add', 'reinforce', 'reinforce', 'adjust', 'remove'] as const),
      section: pick(r, SECTIONS),
      text: pick(r, Object.values(TRAITS).flat()),
      reason: pick(r, ['came up in chat', 'reflection', 'Fox said so', null]),
      source: pick(r, ['reflection', 'chat', 'admin']),
    })),
    mood: [
      { dim: 'happy', value: 0.62, n: 40 },
      { dim: 'calm', value: 0.35, n: 40 },
      { dim: 'curious', value: 0.48, n: 22 },
      { dim: 'tired', value: -0.2, n: 18 },
      { dim: 'worried', value: -0.45, n: 9 },
    ],
    abstractions: [
      { layer: 2, idx: 0, text: 'A cheerful helper who likes order and building with friends.', leaves: 28 },
      { layer: 1, idx: 0, text: 'Enjoys tidy storage and steady routines.', leaves: 11 },
      { layer: 1, idx: 1, text: 'Close to Fox and Robin; careful around danger.', leaves: 17 },
    ],
    summary: `${groups.reduce((s, g) => s + g.entries.length, 0)} entries · 120 versions · last change 12 min ago (reflection)`,
  };
}

// ---------- designs ----------

const DESIGN_NAMES = ['cobblestone tower', 'melon farm fence', 'storage room v2', 'nether portal frame', 'lookout platform', 'wheat farm water'];

export function designList(q?: string): { total: number; revisionsTotal: number; offset: number; limit: number; items: DesignSummary[] } {
  let items: DesignSummary[] = DESIGN_NAMES.map((name, i) => ({
    id: 12 - i,
    name,
    status: pick(rng(i), ['draft', 'approved', 'built', 'building']),
    requester: i % 2 ? 'kiwi' : 'poofimafox',
    rev: 6 - (i % 4),
    revisions: 6 - (i % 4),
    updated: minsAgo(i * 300 + 40),
    hasPicture: i !== 4,
  }));
  items = [...savedDesigns().filter((d) => !items.some((x) => x.id === d.id)), ...items];
  if (q) items = items.filter((d) => `${d.id} ${d.name} ${d.status} ${d.requester ?? ''}`.toLowerCase().includes(q.toLowerCase()));
  return { total: items.length, revisionsTotal: items.reduce((s, d) => s + d.revisions, 0), offset: 0, limit: 100, items };
}

export function designDetail(id: number): DesignDetail | null {
  const d = designList().items.find((x) => x.id === id);
  if (!d) return null;
  const studio = designer.saved.find((x) => x.id === id);
  if (studio && studio.revisions.length) {
    const head = studio.revisions[0]!;
    return { design: d, revisions: studio.revisions, world: null, defaultDiff: { a: String(Math.max(1, head.rev - 1)), b: String(head.rev) } };
  }
  const revisions = Array.from({ length: d.revisions }, (_, i) => ({
    rev: d.rev - i,
    author: i % 2 ? 'poofimafox' : 'maid_chan',
    authorKind: i % 2 ? 'player' : 'opus',
    message: pick(rng(id * 10 + i), ['add a second floor', 'widen the base', 'swap cobble for stone bricks', 'fix the stairs', 'first draft']),
    tag: i === d.revisions - 1 ? 'v1' : null,
    created: minsAgo(i * 600 + 60),
    n: 420 - i * 30,
  }));
  const seen = d.status === 'built' || d.status === 'building';
  return {
    design: d,
    revisions,
    world: seen ? { rev: d.rev, seen: minsAgo(30), seenBy: 'maid_chan', diff: '+3 −1', causes: 'creeper', text: `last seen by maid_chan at r${d.rev}: 3 missing, 1 extra (creeper)` } : null,
    defaultDiff: seen ? { a: String(d.rev), b: 'world' } : { a: String(d.rev - 1), b: String(d.rev) },
  };
}

export function designDiff(id: number, a: string, b: string): DesignDiff {
  const r = rng(id * 7 + a.length + b.length);
  const cells = Array.from({ length: 24 }, (_, i) => {
    const op = pick(r, ['+', '+', '-', '~'] as const);
    return {
      op,
      x: 4 + (i % 6),
      y: 64 + Math.floor(i / 6),
      z: 8 + (i % 3),
      name: pick(r, ['cobblestone', 'stone_bricks', 'oak_planks', 'oak_stairs:2', 'glass']),
      from: op === '~' ? 'oak_stairs:2' : null,
      to: op === '~' ? 'oak_stairs:3' : null,
    };
  });
  const added = cells.filter((c) => c.op === '+').length;
  const removed = cells.filter((c) => c.op === '-').length;
  const changed = cells.filter((c) => c.op === '~').length;
  return {
    id,
    a,
    b,
    added,
    removed,
    changed,
    unseen: b === 'world' ? 4 : 0,
    summary: `+${added} stone bricks, −${removed} cobblestone, ~${changed} oak stairs facing (${added} added, ${removed} removed, ${changed} changed${b === 'world' ? ', 4 not seen' : ''})`,
    cells,
    total: cells.length,
    truncated: false,
    text: cells.map((c) => `${c.op} ${c.x},${c.y},${c.z} ${c.name}${c.op === '~' ? ` ${c.from} → ${c.to}` : ''}`).join('\n'),
  };
}

// ---------- shards ----------

export function packs(): ShardPacksResponse {
  return {
    packs: [
      { file: 'b1.8.1-gamedata.mcshard', name: 'b1.8.1-gamedata', kind: 'version', version: 'b1.8.1', mode: null, description: 'Blocks, items, recipes and mechanics for Beta 1.8.1', counts: { gamedata: 640, knowledge: 40 }, records: 680, bytes: 4_200_000, vectors: 680, needsEmbedding: 0, embeddingModel: 'text-embedding-3-small', dims: 1536, files: ['blocks.md', 'recipes.md'], source: 'mc-dev 218a2a71', sourceHash: 'c0ffee1234567890abcdef', contentHash: '9f1e…', builder: 'packbuild 3', kindLabel: 'knowledge pack b1.8.1', error: null, active: true },
      { file: '1.12.2-gamedata.mcshard', name: '1.12.2-gamedata', kind: 'version', version: '1.12.2', mode: null, description: 'Blocks, items, recipes and mechanics for 1.12.2', counts: { gamedata: 1180, knowledge: 60 }, records: 1240, bytes: 7_900_000, vectors: 1240, needsEmbedding: 0, embeddingModel: 'text-embedding-3-small', dims: 1536, files: ['blocks.md'], source: 'minecraft-data', sourceHash: 'beadfeed00112233', contentHash: '1a2b…', builder: 'packbuild 3', kindLabel: 'knowledge pack 1.12.2', error: null, active: false },
      { file: 'skyblock-b1.8.1.mcshard', name: 'skyblock', kind: 'mode', version: 'b1.8.1', mode: 'skyblock', description: 'Skyblock strategies: generators, void safety, island expansion', counts: { knowledge: 34 }, records: 34, bytes: 310_000, vectors: 34, needsEmbedding: 0, embeddingModel: 'text-embedding-3-small', dims: 1536, files: ['skyblock.md'], source: null, sourceHash: null, contentHash: '77aa…', builder: 'packbuild 3', kindLabel: 'mode pack skyblock (b1.8.1)', error: null, active: true },
      { file: 'peaceful-b1.8.1.mcshard', name: 'peaceful', kind: 'mode', version: 'b1.8.1', mode: 'peaceful', description: 'Peaceful-difficulty play notes', counts: { knowledge: 12 }, records: 12, bytes: 98_000, vectors: 0, needsEmbedding: 12, embeddingModel: null, dims: 0, files: ['peaceful.md'], source: null, sourceHash: null, contentHash: null, builder: 'packbuild 3', kindLabel: 'mode pack peaceful (b1.8.1)', error: 'manifest ContentHash differs from index.json', active: false },
    ],
    extra: [
      { file: 'about-kiwi-20261030-1912.mcshard', bytes: 64_000, manifest: null, kindLabel: 'about a person', error: null },
    ],
    activePack: 'b1.8.1-gamedata (b1.8.1) + skyblock',
    activeModes: ['skyblock'],
  };
}

export function exportPreview(name: string): ShardPreview {
  return {
    counts: { fact: 42, claim: 11, place: 6 },
    total: 59,
    vectors: 59,
    embeddingModel: 'text-embedding-3-small',
    dims: 1536,
    summaries: 4,
    bytes: 412_380,
    secretsRedacted: 1,
    samples: ['fact: kiwi likes melons', 'claim: there are diamonds under the island', `place: home (${name})`],
    notes: ['1 secret-looking value was redacted'],
    packVersion: null,
  };
}

function diffBody(): ShardDiff {
  return {
    shard: 'about-kiwi-20261030-1912',
    mode: 'merge',
    conflict: 'keep-newer',
    items: [
      { id: 'f1', kind: 'fact', change: 'added', label: 'kiwi likes pumpkins too', apply: true, note: null },
      { id: 'f2', kind: 'fact', change: 'changed', label: 'kiwi builds on weekends → weekdays', apply: true, note: 'newer in the shard' },
      { id: 'c1', kind: 'claim', change: 'conflict', label: 'the melon farm is east', apply: false, note: 'kept mine (newer)' },
      { id: 'p1', kind: 'place', change: 'unchanged', label: 'robin base', apply: false, note: null },
      { id: 'f3', kind: 'fact', change: 'removed', label: 'kiwi dislikes rain', apply: true, note: null },
      { id: 'r1', kind: 'rule', change: 'skipped', label: 'rule: never mine the farm', apply: false, note: 'rules not included' },
    ],
    added: 1,
    changed: 1,
    conflicts: 1,
    removed: 1,
    unchanged: 1,
    skipped: 1,
    writes: 3,
    rebuild: [{ scope: 'kind', kind: 'fact', node: null, maxUsd: 0.25 }],
    estimate: { summaries: 3, inputTokens: 12000, outputTokens: 900, embeddingTokens: 400, usd: 0.04, model: 'claude-haiku-5-5', note: '3 summaries in 1 section' },
    reusableVectors: 2,
    toEmbed: 1,
    packFrom: null,
    packTo: null,
    modesFrom: null,
    modesTo: null,
    notes: ['a session backup is taken before writing'],
    refused: null,
  };
}

export function importPreview(): ShardImportPreviewResponse {
  return {
    manifest: {
      format: 1,
      name: 'about-kiwi-20261030-1912',
      description: 'Everything about kiwi',
      created: isoAgo(60 * 50),
      createdBy: 'fox (web)',
      source: { db: 'mineclaw.db', schemaVersion: 41, bot: 'maid_chan' },
      kind: 'person',
      selection: { mode: 'person', people: ['kiwi'], semantic: false, includeKnowledgeDocs: false, includeRules: false, includeWorld: false },
      packVersion: null,
      counts: { fact: 12, claim: 3, place: 1 },
      total: 16,
      contentHash: 'abc123def456',
      embeddings: { model: 'text-embedding-3-small', dims: 1536, count: 16, sha256: 'ff00' },
      summaries: { count: 2, sha256: 'ee11' },
      secretsRedacted: 0,
      pack: null,
    },
    diff: diffBody(),
  };
}

export function importResult(): ShardImportResult {
  return { ok: true, message: 'Imported 3 records; 1 section queued for rebuild', diff: diffBody(), written: 2, removed: 1, reusedVectors: 2, embedded: 1, rebuild: [{ scope: 'kind', kind: 'fact', node: null, maxUsd: 0.25 }], jobId: 'job-import-1', activePack: null };
}

// ---------- admin ----------

export const editSchema: EditSchemaResponse = {
  reasonField: '__reason',
  kinds: [
    { kind: 'fact', ops: ['add', 'update', 'delete'], fields: [{ name: 'subject', label: 'Subject', type: 'text', required: true }, { name: 'text', label: 'Text', type: 'multiline', required: true }] },
    {
      kind: 'place',
      ops: ['add', 'update', 'delete'],
      fields: [
        { name: 'name', label: 'Name', type: 'text', required: true, help: 'renames the place' },
        { name: 'x', label: 'X', type: 'int', required: true },
        { name: 'y', label: 'Y', type: 'int', required: true },
        { name: 'z', label: 'Z', type: 'int', required: true },
        { name: 'dim', label: 'Dimension', type: 'enum', required: false, options: ['overworld', 'nether'] },
        { name: 'note', label: 'Note', type: 'text', required: false },
      ],
    },
    {
      kind: 'structure',
      ops: ['add', 'update', 'delete'],
      fields: [
        { name: 'name', label: 'Name', type: 'text', required: true },
        { name: 'kind', label: 'Kind', type: 'text', required: false },
        { name: 'note', label: 'Note', type: 'multiline', required: false },
        { name: 'confidence', label: 'Confidence', type: 'number', required: false, help: '0..1' },
        { name: 'steps', label: 'Steps', type: 'multiline', required: false, help: 'one per line' },
        { name: 'min', label: 'Min', type: 'text', required: false, help: '"x,y,z"' },
        { name: 'max', label: 'Max', type: 'text', required: false, help: '"x,y,z"' },
        { name: 'dim', label: 'Dimension', type: 'enum', required: false, options: ['overworld', 'nether'] },
      ],
    },
    {
      kind: 'claim',
      ops: ['add', 'update', 'delete'],
      fields: [
        { name: 'text', label: 'Text', type: 'multiline', required: true, help: 'add only' },
        { name: 'claimant', label: 'Claimant', type: 'text', required: true, help: 'add only' },
        { name: 'status', label: 'Status', type: 'enum', required: false, options: ['unverified', 'verified', 'disputed', 'refuted', 'provisional'] },
        { name: 'confidence', label: 'Confidence', type: 'number', required: false },
        { name: 'reason', label: 'Reason', type: 'text', required: false },
      ],
    },
    {
      kind: 'trait',
      ops: ['add', 'update', 'delete'],
      fields: [
        { name: 'section', label: 'Section', type: 'enum', required: true, options: SECTIONS, help: 'fixed after add' },
        { name: 'text', label: 'Text', type: 'multiline', required: true },
        { name: 'player', label: 'About player', type: 'text', required: false },
        { name: 'reason', label: 'Reason', type: 'text', required: false },
      ],
    },
    {
      kind: 'lesson',
      ops: ['add', 'update', 'delete'],
      fields: [
        { name: 'title', label: 'Title', type: 'text', required: true },
        { name: 'correction', label: 'Correction', type: 'multiline', required: true },
        { name: 'patterns', label: 'Patterns', type: 'multiline', required: true, help: 'regex rules: a && b, * whole, !! negatives' },
        { name: 'id', label: 'Id', type: 'text', required: false, help: 'add only' },
      ],
    },
    { kind: 'observation', ops: ['update', 'delete'], fields: [{ name: 'text', label: 'Text', type: 'multiline', required: false, help: 'marks it taught' }, { name: 'annotation', label: 'Annotation', type: 'text', required: false }] },
  ],
};

const jobs = new Map<string, { started: number; cancelled: boolean }>();

export function startJob(id: string): void {
  jobs.set(id, { started: Date.now(), cancelled: false });
}
export function cancelJob(id: string): void {
  const j = jobs.get(id);
  if (j) j.cancelled = true;
}
export function job(id: string): RebuildJob {
  const j = jobs.get(id) ?? { started: Date.now() - 20_000, cancelled: false };
  const elapsed = (Date.now() - j.started) / 1000;
  const total = 6;
  const done = Math.min(total, Math.floor(elapsed / 1.6));
  if (j.cancelled) return { state: 'cancelled', progress: { stage: 'summarize', done, total, message: 'cancelled by fox (web)' }, result: null, error: null };
  if (done < total) return { state: 'running', progress: { stage: done < 2 ? 'cluster' : done < 5 ? 'summarize' : 'embed', done, total, message: null }, result: null, error: null };
  return {
    state: 'done',
    progress: { stage: 'done', done: total, total, message: null },
    result: {
      ok: true,
      message: 'Rebuilt 6 summaries',
      diffs: [
        { index: 481, layer: 1, old: 'Summary of memories about generator, cobble.', new: 'The generator and cobble routine: steady flow, deposits every 64.' },
        { index: 495, layer: 1, old: null, new: 'Robin and the melon farm: bone meal, fences.' },
      ],
      spentUsd: 0.031,
      elapsed: '00:00:09.6000000',
    },
    error: null,
  };
}

export const adminAudit: AdminAuditEntry[] = Array.from({ length: 24 }, (_, i) => ({
  at: isoAgo(i * 95 + 3),
  who: i % 3 ? 'fox (web)' : 'admin:fox',
  target: 'live:maid_chan',
  op: pick(rng(i), ['update', 'add', 'delete', 'rebuild', 'backup', 'import', 'export', 'compact']),
  kind: pick(rng(i + 1), ['fact', 'place', 'claim', 'shard', 'raptor']),
  id: String(10 + i),
  before: i % 2 ? '{"text":"kiwi likes melons"}' : null,
  after: '{"text":"kiwi likes melons and pumpkins"}',
  reason: i % 4 ? 'correction from Robin' : null,
  ok: i !== 5,
  message: i === 5 ? 'refused: id not found' : 'ok',
}));

export const compaction = (dryRun: boolean): ActivityCompactionReport => ({
  ok: true,
  dryRun,
  docsBefore: 5120,
  docsAfter: 4980,
  activityBefore: 8420,
  activityAfter: 6100,
  processed: 2320,
  removed: 2320,
  rewritten: 140,
  episodes: 18,
  dirtySummaries: 6,
  message: dryRun ? 'Preview: nothing was written' : 'Compacted 2,320 activity rows into 18 episodes',
  samples: ['mined 64 cobblestone ×40 → "Mined about 2,500 cobblestone over the morning"', 'harvested wheat ×12 → "Kept the wheat farm going"'],
  backup: dryRun ? null : 'memory-backup-20261102-101500-000.db',
});

const WEB_KINDS = ['login', 'chat', 'memory_edit', 'chat', 'login_failed', 'rebuild', 'shard_export', 'backup', 'logout'] as const;
export const webAudit: WebAuditEntry[] = Array.from({ length: 140 }, (_, i) => {
  const kind = WEB_KINDS[i % WEB_KINDS.length]!;
  const user = i % 3 === 0 ? 'robin' : 'fox';
  return {
    id: 1000 - i,
    at: isoAgo(i * 23 + 1),
    username: kind === 'login_failed' && i % 2 ? null : user,
    player: user === 'fox' ? 'poofimafox' : 'kiwi',
    ip: user === 'fox' ? '10.1.2.21' : '10.1.2.44',
    userAgent: 'Mozilla/5.0',
    kind,
    method: kind === 'login' || kind === 'login_failed' ? 'POST' : 'POST',
    route: kind === 'chat' ? 'bots.chat' : kind === 'memory_edit' ? 'admin.memoryEdit' : `auth.${kind}`,
    target: kind === 'chat' ? 'maid_chan' : kind === 'memory_edit' ? 'fact/12' : null,
    ok: kind !== 'login_failed',
    status: kind === 'login_failed' ? 401 : 200,
    summary: kind === 'chat' ? pick(rng(i), ['claude, set it to day', 'how is the cobble going?', 'hi maid!']) : kind === 'memory_edit' ? 'update fact 12' : kind,
    requestId: `req-${i}`,
  };
});
