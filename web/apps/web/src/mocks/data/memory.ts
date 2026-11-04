/** Mock memory db: the 16 browser kinds with deterministic rows, server-side filter/sort/paging, and detail records. */
import {
  MEMORY_EDIT_KIND,
  type MemoryDetail,
  type MemoryKindInfo,
  type MemoryKindKey,
  type MemoryListQuery,
  type MemoryPage,
  type MemoryRecord,
  type MemoryRow,
} from '@mineclaw/contracts';
import { int, minsAgo, pick, rng } from '../util';

const LABELS: Record<MemoryKindKey, [string, string, number]> = {
  facts: ['Facts', 'Knowledge', 214],
  claims: ['Claims', 'Knowledge', 96],
  beliefs: ['Beliefs', 'Knowledge', 58],
  personality: ['Self-profile', 'Self', 41],
  structures: ['Structures', 'World', 18],
  places: ['Places', 'World', 27],
  chests: ['Chests', 'World', 34],
  gencells: ['Gen cells', 'World', 6],
  observed: ['Observed places', 'World', 120],
  lessons: ['Lessons', 'Knowledge', 23],
  episodes: ['Episodes', 'History', 310],
  activity: ['Activity log', 'History', 8420],
  chat: ['Chat log', 'History', 12650],
  routines: ['Routines & scripts', 'Library', 14],
  recipes: ['Recipes & game data', 'Library', 640],
  knowledge: ['Knowledge & skills', 'Library', 155],
};

const PEOPLE = ['poofimafox', 'kiwi', 'maid_chan_67', 'maid_chan'];
const FACT_TEXT = [
  'likes melons and asks for bone meal',
  'prefers it to be day while building',
  'calls the bot claude',
  'keeps spare torches in chest 3',
  'built the melon farm east of home',
  'wants a cobblestone tower later',
  'is usually on in the evening',
  'does not like creepers near the farm',
];
const CHAT_TEXT = ['claude, set it to day', 'thank you!!', 'how is the cobble going?', 'good night maid', 'can you save me some bone meal?', 'nice work on the farm', 'where are you?', 'brb'];
const ACT_TEXT = ['mined 64 cobblestone', 'deposited stone in the main chest', 'harvested wheat row 2', 'crafted 4 torches', 'ate bread', 'smelted 32 stone', 'replanted seeds', 'fixed the water channel'];
const STATES: Partial<Record<MemoryKindKey, string[]>> = {
  claims: ['unverified', 'verified', 'disputed', 'refuted', 'provisional'],
  beliefs: ['verified', 'provisional'],
  personality: ['trait', 'like', 'dislike', 'opinion', 'joke', 'nickname', 'relationship', 'milestone'],
  chests: ['chest', 'furnace', 'trapped_chest'],
  observed: ['portal', 'farm', 'tree', 'spawner', 'lava'],
  chat: ['chat', 'whisper', 'join', 'leave', 'bot', 'viewer_chat'],
  activity: ['mine', 'build', 'farm', 'craft', 'move'],
};

const cache = new Map<MemoryKindKey, MemoryRow[]>();

/** One row being made: the kind's rng, its index, the person and state already drawn for it. */
interface RowDraw {
  r: () => number;
  i: number;
  kind: MemoryKindKey;
  person: string;
  state: string | null;
}

/** The kind-specific part of a row (id defaults to the 1-based index, type to null). */
type RowFields = { name: string; preview: string; type?: string | null; id?: string };

/** A picked name, numbered after the seventh row (`sep` between). */
const numbered = (d: RowDraw, names: string[], sep = ' ') => pick(d.r, names) + (d.i > 6 ? `${sep}${d.i}` : '');

const personFact = (d: RowDraw): RowFields => ({ name: d.person, preview: `${d.person} ${pick(d.r, FACT_TEXT)}` });

function claimFields(d: RowDraw): RowFields {
  const preview = pick(d.r, ['there are diamonds under the island', 'the generator needs a fence', 'zombies spawn near the farm at night', 'the nether portal is south']);
  return { name: d.person, preview, type: pick(d.r, ['world', 'safety', 'people']) };
}

function structureFields(d: RowDraw): RowFields {
  const id = numbered(d, ['storage room', 'cobble generator', 'wheat farm', 'melon farm', 'home platform', 'nether portal', 'tower']);
  const type = pick(d.r, ['room', 'farm', 'machine', 'portal']);
  return { id, name: id, type, preview: `${id}: built by ${d.person}` };
}

function placeFields(d: RowDraw): RowFields {
  const id = numbered(d, ['home', 'generator', 'farm', 'portal', 'lookout', 'chest room', 'robin base']);
  return { id, name: id, preview: `${int(d.r, -40, 40)},${int(d.r, 60, 70)},${int(d.r, -40, 40)}` };
}

function observationFields(d: RowDraw): RowFields {
  const type = d.state ?? 'cobble_generator';
  const preview = d.kind === 'chests' ? `${int(d.r, 1, 27)} stacks: cobblestone, bread, torches` : 'seen near home';
  return { name: `${type} #${d.i + 1}`, type, preview };
}

function lessonFields(d: RowDraw): RowFields {
  const name = pick(d.r, ['Water + lava makes cobble, not obsidian, when flowing', 'Do not mine the generator output block twice', 'Bread restores 5 hunger in b1.8.1', 'Creepers cannot see through glass']);
  return { id: `lesson-${d.i + 1}`, name, preview: name };
}

function episodeFields(d: RowDraw): RowFields {
  const name = pick(d.r, ['Built the cobble generator with Fox', 'First night on the island', 'Helped Robin plant melons', 'Expanded the platform east']);
  return { name, preview: `${name}. It went well; Fox was happy.` };
}

function routineFields(d: RowDraw): RowFields {
  const id = numbered(d, ['cobble_loop', 'farm_tend', 'sort_chests', 'nether_scout', 'greet_player', 'build_tower', 'night_shelter'], '_');
  return { id, name: id, type: d.i % 3 ? 'script' : 'routine', preview: 'saved routine' };
}

function recipeFields(d: RowDraw): RowFields {
  const name = pick(d.r, ['furnace', 'torch', 'bread', 'chest', 'stone pickaxe', 'ladder', 'bucket']);
  return { name, type: pick(d.r, ['recipe', 'smelt']), preview: `${name}: shaped recipe` };
}

function knowledgeFields(d: RowDraw): RowFields {
  const name = pick(d.r, ['How cobble generators work', 'Skyblock basics', 'Mob spawning light levels', 'Farming in b1.8.1']);
  return { name, type: pick(d.r, ['knowledge', 'skill']), preview: `${name} — reference notes` };
}

/** Each kind's row fields (drawn from the kind's rng in this order). */
const ROW_FIELDS: Record<MemoryKindKey, (d: RowDraw) => RowFields> = {
  facts: personFact,
  claims: claimFields,
  beliefs: claimFields,
  personality: (d) => ({
    name: d.state ?? 'trait',
    preview: pick(d.r, ['I like keeping chests tidy', 'Fox is my best friend here', 'Robin is fun to build with', 'I enjoy sunrise on the island', 'Calls the cobble "the gravel river"']),
  }),
  structures: structureFields,
  places: placeFields,
  chests: observationFields,
  gencells: observationFields,
  observed: observationFields,
  lessons: lessonFields,
  episodes: episodeFields,
  activity: (d) => ({ name: d.person, type: d.state, preview: pick(d.r, ACT_TEXT) }),
  chat: (d) => ({ name: d.person, preview: pick(d.r, CHAT_TEXT) }),
  routines: routineFields,
  recipes: recipeFields,
  knowledge: knowledgeFields,
};

const CONF_KINDS: MemoryKindKey[] = ['claims', 'beliefs', 'structures', 'observed'];
const DOC_KINDS: MemoryKindKey[] = ['facts', 'structures', 'places', 'episodes', 'recipes', 'knowledge', 'claims'];
const LEAF_KINDS: MemoryKindKey[] = ['facts', 'episodes', 'recipes', 'structures'];

/** The fields every row draws first, in this order: person, dimension, state, age. */
function drawCommon(r: () => number, kind: MemoryKindKey, i: number) {
  const person = pick(r, PEOPLE);
  const dim: MemoryRow['dim'] = r() < 0.85 ? 'overworld' : r() < 0.6 ? 'nether' : '';
  const states = STATES[kind];
  const state = states ? pick(r, states) : null;
  const updated = minsAgo(i * (kind === 'chat' || kind === 'activity' ? 1.7 : 37) + r() * 20);
  return { person, dim, state, updated };
}

/** Row `i` of a kind: person, dimension, state and age first, then the kind's fields, then the confidence. */
function makeRow(r: () => number, kind: MemoryKindKey, i: number): MemoryRow {
  const { person, dim, state, updated } = drawCommon(r, kind, i);
  const f = ROW_FIELDS[kind]({ r, i, kind, person, state });
  const id = f.id ?? String(i + 1);
  return {
    id,
    name: f.name,
    type: f.type ?? null,
    state,
    dim: dim,
    conf: CONF_KINDS.includes(kind) ? Number((0.3 + r() * 0.7).toFixed(2)) : null,
    updated,
    preview: f.preview,
    docRef: DOC_KINDS.includes(kind) ? `${kind.slice(0, 2)}:${id}` : null,
    raptorLeaf: LEAF_KINDS.includes(kind) ? (i * 7) % 480 : null,
  };
}

function rows(kind: MemoryKindKey): MemoryRow[] {
  const hit = cache.get(kind);
  if (hit) return hit;
  const r = rng(kind.length * 977 + kind.charCodeAt(0));
  const n = LABELS[kind][2];
  const out: MemoryRow[] = [];
  for (let i = 0; i < n; i++) out.push(makeRow(r, kind, i));
  cache.set(kind, out);
  return out;
}

export function kinds(): { kinds: MemoryKindInfo[] } {
  return {
    kinds: (Object.keys(LABELS) as MemoryKindKey[]).map((k) => ({
      key: k,
      label: LABELS[k][0],
      group: LABELS[k][1],
      count: LABELS[k][2],
      available: true,
      editKind: MEMORY_EDIT_KIND[k],
      states: STATES[k] ?? [],
    })),
  };
}

export function list(kind: MemoryKindKey, q: MemoryListQuery): MemoryPage {
  let items = rows(kind);
  if (q.q) {
    const words = q.q.toLowerCase().split(/\s+/).filter(Boolean);
    items = items.filter((r) => {
      const hay = `${r.id} ${r.name} ${r.type ?? ''} ${r.state ?? ''} ${r.preview}`.toLowerCase();
      return words.every((w) => hay.includes(w));
    });
  }
  if (q.dim !== 'any') items = items.filter((r) => (q.dim === 'none' ? r.dim === '' : r.dim === q.dim));
  if (q.state) items = items.filter((r) => r.state === q.state);
  const key = q.sort;
  const val = (r: MemoryRow): string | number =>
    key === 'updated' ? (r.updated ?? 0) : key === 'conf' ? (typeof r.conf === 'number' ? r.conf : -1) : key === 'id' ? r.id : String(r[key] ?? '');
  items = [...items].sort((a, b) => {
    const x = val(a);
    const y = val(b);
    const c = typeof x === 'number' && typeof y === 'number' ? x - y : String(x).localeCompare(String(y));
    return q.dir === 'asc' ? c : -c;
  });
  return { kind, total: items.length, offset: q.offset, limit: q.limit, items: items.slice(q.offset, q.offset + q.limit) };
}

/** What a detail record is made from: the row, its numeric id and its position. */
interface DetailOf {
  kind: MemoryKindKey;
  row: MemoryRow;
  n: number;
  pos: { dimension: number; x: number; y: number; z: number };
}

const CLAIM_EVIDENCE = () => [
  { at: minsAgo(80), kind: 'sighting', polarity: 1, weight: 0.6, ref: 'act:120', note: 'saw it while mining' },
  { at: minsAgo(30), kind: 'chat', polarity: 1, weight: 0.3, ref: 'chat:5521', note: 'Fox agreed' },
];

function claimRecord({ kind, row, n, pos }: DetailOf): MemoryRecord {
  return {
    kind,
    id: n,
    claimant: row.name,
    text: row.preview,
    category: row.type,
    stakes: 'low',
    confidence: row.conf,
    status: row.state ?? 'unverified',
    reason: 'two independent sightings',
    plan: 'check the next time near the spot',
    risk: null,
    directive: false,
    attempts: 1,
    checked: row.updated,
    created: row.updated,
    updated: row.updated,
    cloneId: null,
    ...pos,
    evidence: CLAIM_EVIDENCE(),
  } as MemoryRecord;
}

function observationRecord({ kind, row, n, pos }: DetailOf): MemoryRecord {
  const base = {
    id: n,
    observationKind: row.type ?? 'chest',
    key: `${pos.x},${pos.y},${pos.z}`,
    minX: pos.x,
    minY: 64,
    minZ: pos.z,
    maxX: pos.x,
    maxY: 64,
    maxZ: pos.z,
    text: row.preview,
    source: 'sight',
    confidence: 0.9,
    annotation: null,
    firstSeen: minsAgo(600),
    lastSeen: row.updated,
    seenCount: 12,
    removed: null,
    dimension: pos.dimension,
    contentsAt: row.updated,
    contentsBy: 'maid_chan',
  };
  return (
    kind === 'chests'
      ? { kind, ...base, contents: [{ name: 'cobblestone', count: 640, damage: 0 }, { name: 'bread', count: 12 }, { name: 'torch', count: 33 }] }
      : { kind, ...base }
  ) as MemoryRecord;
}

const DOC_KIND_OF: Partial<Record<MemoryKindKey, string>> = { episodes: 'episode', recipes: 'gamedata' };

function docRecord({ kind, row, n }: DetailOf): MemoryRecord {
  const doc = { id: n, ref: row.docRef ?? `doc:${n}`, tag: row.type, title: row.name, body: row.preview, source: 'bot', updated: row.updated, dimension: 0, minX: null, raptorIdx: row.raptorLeaf };
  return { kind, ...doc, docKind: DOC_KIND_OF[kind] ?? 'knowledge' } as MemoryRecord;
}

/** Each kind's detail record. */
const RECORDS: Record<MemoryKindKey, (d: DetailOf) => MemoryRecord> = {
  facts: ({ kind, row, n }) => ({ kind, id: n, subject: row.name, text: row.preview, created: row.updated }) as MemoryRecord,
  claims: claimRecord,
  beliefs: claimRecord,
  personality: ({ kind, row, n }) =>
    ({ kind, id: n, section: row.state ?? 'trait', player: n % 2 ? 'kiwi' : null, text: row.preview, strength: 0.72, created: row.updated, reinforced: row.updated, reason: 'came up several times', scope: 'main', observations: 4 }) as MemoryRecord,
  structures: ({ kind, row }) =>
    ({ kind, name: row.name, structureKind: row.type, taughtBy: 'poofimafox', confidence: row.conf, data: { min: [2, 63, 4], max: [8, 67, 10], note: 'walls are cobble', steps: ['clear the area', 'lay the floor'], keyBlocks: [] }, updated: row.updated, dimension: 0 }) as MemoryRecord,
  places: ({ kind, row, n }) => ({ kind, name: row.name, x: 4 + n, y: 64, z: 6 - n, note: 'marked by Fox', updated: row.updated, dimension: 0 }) as MemoryRecord,
  chests: observationRecord,
  gencells: observationRecord,
  observed: observationRecord,
  lessons: ({ kind, row }) => ({ kind, id: row.id, title: row.name, correction: row.preview, source: 'Fox', updated: row.updated }) as MemoryRecord,
  episodes: docRecord,
  recipes: docRecord,
  knowledge: docRecord,
  activity: ({ kind, row, n, pos }) =>
    ({ kind, id: n, at: row.updated ?? 0, player: row.name, activityKind: row.type ?? 'mine', category: 'work', text: row.preview, placeName: 'generator', ...pos, confidence: 0.9 }) as MemoryRecord,
  chat: ({ kind, row, n }) => ({ kind, id: n, at: row.updated ?? 0, chatKind: row.state ?? 'chat', player: row.name, text: row.preview }) as MemoryRecord,
  routines: ({ kind, row }) =>
    ({ kind, name: row.name, routineType: row.type === 'script' ? 'script' : 'routine', data: { script: { code: '-- …' } }, updated: row.updated }) as MemoryRecord,
};

/** The detail's property list. */
function propsOf(row: MemoryRow) {
  const conf = row.conf !== null && row.conf !== undefined ? String(row.conf) : null;
  return [
    { key: 'id', label: 'Id', value: row.id },
    { key: 'name', label: 'Name', value: row.name },
    { key: 'type', label: 'Type', value: row.type ?? null },
    { key: 'state', label: 'State', value: row.state ?? null },
    { key: 'dim', label: 'Dimension', value: row.dim || null },
    { key: 'conf', label: 'Confidence', value: conf },
    { key: 'text', label: 'Text', value: row.preview },
    { key: 'ref', label: 'Doc ref', value: row.docRef ?? null },
  ];
}

type Prefill = Record<string, string | null>;

/** The edit form's prefill per edit kind; any other kind edits the text and an annotation. */
const PREFILLS: Record<string, (row: MemoryRow, n: number) => Prefill> = {
  fact: (row) => ({ subject: row.name, text: row.preview }),
  place: (row, n) => ({ name: row.name, x: String(4 + n), y: '64', z: String(6 - n), dim: 'overworld', note: 'marked by Fox' }),
  claim: (row) => ({ status: row.state ?? 'unverified', confidence: String(row.conf ?? ''), reason: 'two independent sightings' }),
  trait: (row) => ({ section: row.state ?? 'trait', text: row.preview, player: '', reason: '' }),
  lesson: (row) => ({ title: row.name, correction: row.preview, patterns: 'cobble && obsidian' }),
  structure: (row) => ({ name: row.name, kind: row.type ?? '', note: 'walls are cobble', confidence: String(row.conf ?? '') }),
};

function prefillOf(editKind: string | null | undefined, row: MemoryRow, n: number): Prefill | null {
  if (!editKind) return null;
  const f = Object.hasOwn(PREFILLS, editKind) ? PREFILLS[editKind] : undefined;
  return f ? f(row, n) : { text: row.preview, annotation: '' };
}

export function get(kind: MemoryKindKey, id: string): MemoryDetail | null {
  const row = rows(kind).find((r) => r.id === id);
  if (!row) return null;
  const n = Number(id) || 1;
  const pos = { dimension: row.dim === 'nether' ? -1 : 0, x: 4 + n, y: 64, z: 6 - n };
  const record = RECORDS[kind]({ kind, row, n, pos });
  const editKind = MEMORY_EDIT_KIND[kind];
  return {
    kind,
    id: row.id,
    row,
    props: propsOf(row),
    record,
    editKind,
    editPrefill: prefillOf(editKind, row, n),
    people: PEOPLE.includes(row.name) ? [row.name] : [],
  };
}
