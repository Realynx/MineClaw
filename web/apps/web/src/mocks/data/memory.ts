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

function rows(kind: MemoryKindKey): MemoryRow[] {
  const hit = cache.get(kind);
  if (hit) return hit;
  const r = rng(kind.length * 977 + kind.charCodeAt(0));
  const n = LABELS[kind][2];
  const out: MemoryRow[] = [];
  for (let i = 0; i < n; i++) {
    const person = pick(r, PEOPLE);
    const dim = r() < 0.85 ? 'overworld' : r() < 0.6 ? 'nether' : '';
    const state = STATES[kind] ? pick(r, STATES[kind]) : null;
    const updated = minsAgo(i * (kind === 'chat' || kind === 'activity' ? 1.7 : 37) + r() * 20);
    let name = '';
    let preview = '';
    let type: string | null = null;
    let id = String(i + 1);
    switch (kind) {
      case 'facts':
        name = person;
        preview = `${person} ${pick(r, FACT_TEXT)}`;
        break;
      case 'claims':
      case 'beliefs':
        name = person;
        preview = pick(r, ['there are diamonds under the island', 'the generator needs a fence', 'zombies spawn near the farm at night', 'the nether portal is south']);
        type = pick(r, ['world', 'safety', 'people']);
        break;
      case 'personality':
        name = state ?? 'trait';
        preview = pick(r, ['I like keeping chests tidy', 'Fox is my best friend here', 'Robin is fun to build with', 'I enjoy sunrise on the island', 'Calls the cobble "the gravel river"']);
        break;
      case 'structures':
        id = pick(r, ['storage room', 'cobble generator', 'wheat farm', 'melon farm', 'home platform', 'nether portal', 'tower']) + (i > 6 ? ` ${i}` : '');
        name = id;
        type = pick(r, ['room', 'farm', 'machine', 'portal']);
        preview = `${name}: built by ${person}`;
        break;
      case 'places':
        id = pick(r, ['home', 'generator', 'farm', 'portal', 'lookout', 'chest room', 'robin base']) + (i > 6 ? ` ${i}` : '');
        name = id;
        preview = `${int(r, -40, 40)},${int(r, 60, 70)},${int(r, -40, 40)}`;
        break;
      case 'chests':
      case 'gencells':
      case 'observed':
        name = `${state ?? 'cobble_generator'} #${i + 1}`;
        type = state ?? 'cobble_generator';
        preview = kind === 'chests' ? `${int(r, 1, 27)} stacks: cobblestone, bread, torches` : 'seen near home';
        break;
      case 'lessons':
        id = `lesson-${i + 1}`;
        name = pick(r, ['Water + lava makes cobble, not obsidian, when flowing', 'Do not mine the generator output block twice', 'Bread restores 5 hunger in b1.8.1', 'Creepers cannot see through glass']);
        preview = name;
        break;
      case 'episodes':
        name = pick(r, ['Built the cobble generator with Fox', 'First night on the island', 'Helped Robin plant melons', 'Expanded the platform east']);
        preview = `${name}. It went well; Fox was happy.`;
        break;
      case 'activity':
        name = person;
        type = state;
        preview = pick(r, ACT_TEXT);
        break;
      case 'chat':
        name = person;
        preview = pick(r, CHAT_TEXT);
        break;
      case 'routines':
        id = pick(r, ['cobble_loop', 'farm_tend', 'sort_chests', 'nether_scout', 'greet_player', 'build_tower', 'night_shelter']) + (i > 6 ? `_${i}` : '');
        name = id;
        type = i % 3 ? 'script' : 'routine';
        preview = 'saved routine';
        break;
      case 'recipes':
        name = pick(r, ['furnace', 'torch', 'bread', 'chest', 'stone pickaxe', 'ladder', 'bucket']);
        type = pick(r, ['recipe', 'smelt']);
        preview = `${name}: shaped recipe`;
        break;
      case 'knowledge':
        name = pick(r, ['How cobble generators work', 'Skyblock basics', 'Mob spawning light levels', 'Farming in b1.8.1']);
        type = pick(r, ['knowledge', 'skill']);
        preview = `${name} — reference notes`;
        break;
    }
    out.push({
      id,
      name,
      type,
      state,
      dim: dim as MemoryRow['dim'],
      conf: ['claims', 'beliefs', 'structures', 'observed'].includes(kind) ? Number((0.3 + r() * 0.7).toFixed(2)) : null,
      updated,
      preview,
      docRef: ['facts', 'structures', 'places', 'episodes', 'recipes', 'knowledge', 'claims'].includes(kind) ? `${kind.slice(0, 2)}:${id}` : null,
      raptorLeaf: ['facts', 'episodes', 'recipes', 'structures'].includes(kind) ? (i * 7) % 480 : null,
    });
  }
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

export function get(kind: MemoryKindKey, id: string): MemoryDetail | null {
  const row = rows(kind).find((r) => r.id === id);
  if (!row) return null;
  const n = Number(id) || 1;
  const pos = { dimension: row.dim === 'nether' ? -1 : 0, x: 4 + n, y: 64, z: 6 - n };
  let record: MemoryRecord;
  const doc = { id: n, ref: row.docRef ?? `doc:${n}`, tag: row.type, title: row.name, body: row.preview, source: 'bot', updated: row.updated, dimension: 0, minX: null, raptorIdx: row.raptorLeaf };
  switch (kind) {
    case 'facts':
      record = { kind, id: n, subject: row.name, text: row.preview, created: row.updated };
      break;
    case 'claims':
    case 'beliefs':
      record = {
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
        evidence: [
          { at: minsAgo(80), kind: 'sighting', polarity: 1, weight: 0.6, ref: 'act:120', note: 'saw it while mining' },
          { at: minsAgo(30), kind: 'chat', polarity: 1, weight: 0.3, ref: 'chat:5521', note: 'Fox agreed' },
        ],
      };
      break;
    case 'personality':
      record = { kind, id: n, section: row.state ?? 'trait', player: n % 2 ? 'kiwi' : null, text: row.preview, strength: 0.72, created: row.updated, reinforced: row.updated, reason: 'came up several times', scope: 'main', observations: 4 };
      break;
    case 'structures':
      record = { kind, name: row.name, structureKind: row.type, taughtBy: 'poofimafox', confidence: row.conf, data: { min: [2, 63, 4], max: [8, 67, 10], note: 'walls are cobble', steps: ['clear the area', 'lay the floor'], keyBlocks: [] }, updated: row.updated, dimension: 0 };
      break;
    case 'places':
      record = { kind, name: row.name, x: 4 + n, y: 64, z: 6 - n, note: 'marked by Fox', updated: row.updated, dimension: 0 };
      break;
    case 'chests':
    case 'gencells':
    case 'observed': {
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
      record =
        kind === 'chests'
          ? { kind, ...base, contents: [{ name: 'cobblestone', count: 640, damage: 0 }, { name: 'bread', count: 12 }, { name: 'torch', count: 33 }] }
          : { kind, ...base };
      break;
    }
    case 'lessons':
      record = { kind, id: row.id, title: row.name, correction: row.preview, source: 'Fox', updated: row.updated };
      break;
    case 'episodes':
    case 'recipes':
    case 'knowledge':
      record = { kind, ...doc, docKind: kind === 'episodes' ? 'episode' : kind === 'recipes' ? 'gamedata' : 'knowledge' };
      break;
    case 'activity':
      record = { kind, id: n, at: row.updated ?? 0, player: row.name, activityKind: row.type ?? 'mine', category: 'work', text: row.preview, placeName: 'generator', ...pos, confidence: 0.9 };
      break;
    case 'chat':
      record = { kind, id: n, at: row.updated ?? 0, chatKind: row.state ?? 'chat', player: row.name, text: row.preview };
      break;
    case 'routines':
      record = { kind, name: row.name, routineType: row.type === 'script' ? 'script' : 'routine', data: { script: { code: '-- …' } }, updated: row.updated };
      break;
  }
  const editKind = MEMORY_EDIT_KIND[kind];
  const props = [
    { key: 'id', label: 'Id', value: row.id },
    { key: 'name', label: 'Name', value: row.name },
    { key: 'type', label: 'Type', value: row.type ?? null },
    { key: 'state', label: 'State', value: row.state ?? null },
    { key: 'dim', label: 'Dimension', value: row.dim || null },
    { key: 'conf', label: 'Confidence', value: row.conf !== null && row.conf !== undefined ? String(row.conf) : null },
    { key: 'text', label: 'Text', value: row.preview },
    { key: 'ref', label: 'Doc ref', value: row.docRef ?? null },
  ];
  const prefill: Record<string, string | null> | null = editKind
    ? editKind === 'fact'
      ? { subject: row.name, text: row.preview }
      : editKind === 'place'
        ? { name: row.name, x: String(4 + n), y: '64', z: String(6 - n), dim: 'overworld', note: 'marked by Fox' }
        : editKind === 'claim'
          ? { status: row.state ?? 'unverified', confidence: String(row.conf ?? ''), reason: 'two independent sightings' }
          : editKind === 'trait'
            ? { section: row.state ?? 'trait', text: row.preview, player: '', reason: '' }
            : editKind === 'lesson'
              ? { title: row.name, correction: row.preview, patterns: 'cobble && obsidian' }
              : editKind === 'structure'
                ? { name: row.name, kind: row.type ?? '', note: 'walls are cobble', confidence: String(row.conf ?? '') }
                : { text: row.preview, annotation: '' }
    : null;
  return {
    kind,
    id: row.id,
    row,
    props,
    record,
    editKind,
    editPrefill: prefill,
    people: PEOPLE.includes(row.name) ? [row.name] : [],
  };
}
