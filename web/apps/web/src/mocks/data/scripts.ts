/** Mock script library: realistic Lua sources with versions, runs, handlers and lint. */
import type {
  ScriptDiff,
  ScriptHandlerInfo,
  ScriptLibraryEntry,
  ScriptLintInfo,
  ScriptRunInfo,
  ScriptVersionDetail,
  ScriptVersionInfo,
} from '@mineclaw/contracts';
import { isoAgo, lineDiff } from '../util';

const cobbleV7 = `-- cobble_loop: mine the generator until the target, deposit when full
local p = params({ n = 64, store_at = "home", target = 2000 })

local mined = 0
local gen = locate("cobble generator")

local function deposit()
  checkpoint("deposit")
  goto(p.store_at)
  local chest = nearest("chest")
  put_all(chest, "cobblestone")
  log("deposited " .. count_moved() .. " cobblestone")
end

local function mine_once()
  local spot = gen.output
  if not is_block(spot, "cobblestone") then
    wait_for(function() return is_block(spot, "cobblestone") end, 3)
  end
  mine(spot)
  mined = mined + 1
  progress(mined / p.target, mined .. " / " .. p.target .. " cobble")
end

local function eat_if_hungry()
  if food() < 14 then
    eat("bread")
    log("ate bread (food " .. food() .. ")")
  end
end

-- main loop
machine("cobble", "mining")
while mined < p.target do
  eat_if_hungry()
  if inventory_free() == 0 then
    state("depositing", "inventory full")
    deposit()
    state("mining", "chest ok")
  end
  mine_once()
  if mined % p.n == 0 then
    log("generator flow ok")
  end
end

deposit()
say("Done! " .. mined .. " cobblestone in the chest.")
`;

const cobbleV6 = cobbleV7
  .replace('local p = params({ n = 64, store_at = "home", target = 2000 })', 'local p = params({ n = 64, store_at = "home" })')
  .replace('  progress(mined / p.target, mined .. " / " .. p.target .. " cobble")\n', '')
  .replace('while mined < p.target do', 'while mined < 1000 do')
  .replace('  if food() < 14 then', '  if food() < 10 then');

const farmV4 = `-- farm_tend: harvest ripe wheat, replant, keep the water channel
local p = params({ rows = 9, crop = "wheat" })

local function tend_row(row)
  for i = 1, p.rows do
    local cell = farm_cell(row, i)
    if is_ripe(cell) then
      harvest(cell)
      plant(cell, p.crop .. " seeds")
    end
  end
end

machine("farm", "tending")
for row = 1, p.rows do
  tend_row(row)
  if row % 3 == 0 then
    check_water()
  end
end
report("farm tended")
`;

const sortV2 = `-- sort_chests: group items by kind across the storage room
local p = params({ room = "storage room", keep_empty = 1 })

local chests = chests_in(p.room)
local plan = {}

for _, chest in ipairs(chests) do
  for _, stack in ipairs(contents(chest)) do
    local kind = item_group(stack.name)
    plan[kind] = plan[kind] or {}
    table.insert(plan[kind], { from = chest, stack = stack })
  end
end

local target = 1
for kind, moves in pairs(plan) do
  local dest = chests[target]
  for _, m in ipairs(moves) do
    if m.from ~= dest then
      move(m.from, dest, m.stack.name, m.stack.count)
    end
  end
  log("grouped " .. kind .. " into chest " .. target)
  target = target + 1
  if target > #chests - p.keep_empty then target = 1 end
end

say("Storage room sorted.")
`;

const scoutV3 = `-- nether_scout: follow the cliff edge, look for fortress bricks
local p = params({ radius = 96, retreat_hp = 8 })

local start = position()
machine("scout", "walking")

local function safe()
  if hp() <= p.retreat_hp then
    state("retreating", "low hp")
    eat("bread")
    goto("portal")
    return false
  end
  return true
end

while distance(start) < p.radius do
  if not safe() then break end
  local edge = find_edge({ avoid = "lava" })
  walk_to(edge)
  local seen = look_for("nether_brick", 32)
  if seen then
    remember("fortress near " .. seen)
    say("Found fortress bricks!")
    break
  end
end
`;

export const SCRIPT_CODE: Record<string, Record<number, string>> = {
  cobble_loop: { 7: cobbleV7, 6: cobbleV6, 5: cobbleV6.replace('n = 64', 'n = 32'), 4: cobbleV6, 3: cobbleV6, 2: cobbleV6, 1: cobbleV6 },
  farm_tend: { 4: farmV4, 3: farmV4.replace('rows = 9', 'rows = 7'), 2: farmV4, 1: farmV4 },
  sort_chests: { 2: sortV2, 1: sortV2.replace('keep_empty = 1', 'keep_empty = 0') },
  nether_scout: { 3: scoutV3, 2: scoutV3.replace('retreat_hp = 8', 'retreat_hp = 6'), 1: scoutV3 },
  greet_player: { 1: '-- greet_player: wave and say hi when a friend joins\nlocal p = params({ who = "" })\nlook_at(p.who)\nsay("Hi " .. p.who .. "!")\n' },
  build_tower: { 2: "// build_tower (legacy JS)\nconst h = params({ height: 12 }).height;\nfor (let y = 0; y < h; y++) {\n  placeRing('cobblestone', y);\n}\n" },
};

const lintOk: ScriptLintInfo = { status: 'ok', errors: 0, warnings: 0, findings: [], note: null };
const lintWarn: ScriptLintInfo = {
  status: 'warnings',
  errors: 0,
  warnings: 1,
  findings: [{ severity: 'warn', line: 21, rule: 'loop-without-yield', message: 'loop may spin without a game action' }],
  note: null,
};
const lintErr: ScriptLintInfo = {
  status: 'errors',
  errors: 1,
  warnings: 0,
  findings: [{ severity: 'error', line: 4, rule: 'unknown-function', message: 'placeRing is not a game function' }],
  note: null,
};

const handlers: ScriptHandlerInfo[] = [
  {
    kind: 'danger',
    signature: 'danger|lava_near|at `mine(spot)`',
    js: 'step_back(2); wait(1)',
    then: 'resume',
    note: 'lava flowed onto the output once',
    author: 'Opus (maid_chan)',
    version: 6,
    learnedAt: isoAgo(60 * 30),
    lastUsed: isoAgo(60 * 2),
    successes: 5,
    failures: 1,
    demoted: false,
  },
  {
    kind: 'chat',
    signature: 'chat|friend_asks|any',
    js: 'pause_for_reply()',
    then: 'resume',
    note: null,
    author: 'Opus (maid_chan)',
    version: 7,
    learnedAt: isoAgo(60 * 4),
    lastUsed: isoAgo(6),
    successes: 9,
    failures: 0,
    demoted: false,
  },
];

interface Def {
  name: string;
  version: number;
  purpose: string;
  usedBy: string[];
  lint: ScriptLintInfo;
  runs: number;
  rate: number;
  avg: number;
  archived?: boolean;
  handlers?: ScriptHandlerInfo[];
  lang?: string;
  params: Array<{ name: string; default: string; tuned?: string }>;
}

const DEFS: Def[] = [
  { name: 'cobble_loop', version: 7, purpose: 'Mine the cobblestone generator to a target and store it', usedBy: ['standing order: keep stone stocked'], lint: lintOk, runs: 48, rate: 0.94, avg: 1260, handlers, params: [{ name: 'n', default: '64', tuned: '80' }, { name: 'store_at', default: 'home' }, { name: 'target', default: '2000' }] },
  { name: 'farm_tend', version: 4, purpose: 'Harvest and replant the wheat farm', usedBy: ['maid_chan_68 role farmer'], lint: lintOk, runs: 22, rate: 1, avg: 340, params: [{ name: 'rows', default: '9' }, { name: 'crop', default: 'wheat' }] },
  { name: 'sort_chests', version: 2, purpose: 'Group items by kind across the storage room', usedBy: [], lint: lintWarn, runs: 6, rate: 0.83, avg: 210, params: [{ name: 'room', default: 'storage room' }, { name: 'keep_empty', default: '1' }] },
  { name: 'nether_scout', version: 3, purpose: 'Scout the nether along cliff edges for a fortress', usedBy: [], lint: lintOk, runs: 4, rate: 0.5, avg: 900, params: [{ name: 'radius', default: '96' }, { name: 'retreat_hp', default: '8' }] },
  { name: 'greet_player', version: 1, purpose: 'Wave and say hi when a friend joins', usedBy: ['standing order: greet friends'], lint: lintOk, runs: 31, rate: 1, avg: 3, params: [{ name: 'who', default: '' }] },
  { name: 'build_tower', version: 2, purpose: 'Legacy cobblestone tower builder', usedBy: [], lint: lintErr, runs: 3, rate: 0.33, avg: 600, archived: true, lang: 'js', params: [{ name: 'height', default: '12' }] },
];

function entry(d: Def): ScriptLibraryEntry {
  return {
    name: d.name,
    version: d.version,
    versions: d.version,
    lang: d.lang ?? 'lua',
    author: 'maid_chan',
    authorKind: d.lang === 'js' ? 'legacy' : 'opus',
    purpose: d.purpose,
    params: d.params.map((p) => ({ name: p.name, default: p.default, tuned: p.tuned ?? null })),
    lastRun: isoAgo(d.name === 'cobble_loop' ? 21 : 60 * (DEFS.indexOf(d) + 1)),
    runs: d.runs,
    successRate: d.rate,
    avgSeconds: d.avg,
    timings: d.name === 'cobble_loop' ? 'mine 0.8 s · deposit 4 s' : null,
    usedBy: d.usedBy,
    lint: d.lint,
    migration: d.lang === 'js' ? 'JS → Lua migration pending' : null,
    archived: !!d.archived,
    promotedAs: d.name === 'cobble_loop' ? 'skill mine_cobble' : null,
    lastOutcome: d.rate < 1 ? 'stopped: inventory full and no chest reachable' : 'done',
    duplicateOf: null,
    updated: isoAgo(60 * (DEFS.indexOf(d) * 5 + 2)),
    hasLuaHistory: d.name === 'farm_tend',
    handlers: d.handlers ?? null,
  };
}

export function library(): ScriptLibraryEntry[] {
  return DEFS.map(entry);
}

function versionInfo(d: Def, v: number): ScriptVersionInfo {
  const code = SCRIPT_CODE[d.name]?.[v] ?? '';
  return {
    v,
    lang: d.lang ?? (d.name === 'farm_tend' && v < 2 ? 'js' : 'lua'),
    author: v === 1 && d.name === 'farm_tend' ? 'Lua migration' : 'maid_chan',
    authorKind: 'opus',
    savedAt: isoAgo(60 * 24 * (d.version - v) + 60),
    active: v === d.version,
    runs: Math.max(1, Math.round(d.runs / d.version)),
    successes: Math.round((d.runs / d.version) * d.rate),
    failures: Math.max(0, Math.round((d.runs / d.version) * (1 - d.rate))),
    avgSeconds: d.avg * (1 + (d.version - v) * 0.05),
    lastRun: isoAgo(60 * (d.version - v) + 21),
    topFailures: d.rate < 1 ? ['inventory full, no chest reachable'] : [],
    lines: code.split('\n').length,
  };
}

export function detail(name: string, v?: number): ScriptVersionDetail | null {
  const d = DEFS.find((x) => x.name === name);
  if (!d) return null;
  const ver = v && SCRIPT_CODE[name]?.[v] ? v : d.version;
  const runs: ScriptRunInfo[] = Array.from({ length: 8 }, (_, i) => ({
    at: isoAgo(60 * i + 21),
    ok: i !== 2,
    seconds: d.avg * (0.8 + (i % 3) * 0.15),
    source: 'saved',
    script: name,
    outcome: i === 2 ? 'failed: block changed before the swing' : 'done',
    actions: 600 + i * 13,
    failLine: i === 2 ? 14 : null,
    version: ver,
    params: '{"n":80,"store_at":"home"}',
    timings: 'mine 0.8 s · deposit 4 s',
    body: i % 2 ? 'maid_chan' : 'maid_chan_68',
    yield: i === 2 ? 120 : 640,
    status: i === 2 ? 'failed' : 'done',
  }));
  return {
    script: entry(d),
    version: versionInfo(d, ver),
    code: SCRIPT_CODE[name]?.[ver] ?? '',
    lint: d.lint,
    params: entry(d).params,
    history: Array.from({ length: d.version }, (_, i) => versionInfo(d, d.version - i)),
    runs,
    revertNote: ver === 6 && name === 'cobble_loop' ? 'v6 was reverted to after v7 failed twice (then v7 was fixed)' : null,
    tunedParams: d.params.some((p) => p.tuned) ? '{"n":80}' : null,
  };
}

type DiffLine = ReturnType<typeof lineDiff>[number];

/** The changed lines grouped into hunks with `context` lines around them (overlapping ones merged). */
function hunksOf(lines: DiffLine[], context: number): ScriptDiff['hunks'] {
  const hunks: ScriptDiff['hunks'] = [];
  const changed = lines.map((l, i) => (l.op !== ' ' ? i : -1)).filter((i) => i >= 0);
  const last = lines.length - 1;
  let i = 0;
  while (i < changed.length) {
    const start = Math.max(0, changed[i]! - context);
    let end = Math.min(last, changed[i]! + context);
    while (i + 1 < changed.length && changed[i + 1]! - context <= end + 1) {
      i++;
      end = Math.min(last, changed[i]! + context);
    }
    hunks.push(hunkOf(lines.slice(start, end + 1)));
    i++;
  }
  return hunks;
}

function hunkOf(slice: DiffLine[]): ScriptDiff['hunks'][number] {
  return {
    aStart: slice.find((l) => l.aLine)?.aLine ?? 0,
    aLines: slice.filter((l) => l.op !== '+').length,
    bStart: slice.find((l) => l.bLine)?.bLine ?? 0,
    bLines: slice.filter((l) => l.op !== '-').length,
    lines: slice,
  };
}

export function diff(name: string, a: number, b: number, context = 3): ScriptDiff {
  const A = (SCRIPT_CODE[name]?.[a] ?? '').split('\n');
  const B = (SCRIPT_CODE[name]?.[b] ?? '').split('\n');
  const lines = lineDiff(A, B);
  const hunks = hunksOf(lines, context);
  const unified = [`--- ${name} v${a}`, `+++ ${name} v${b}`, ...hunks.flatMap((h) => [`@@ -${h.aStart},${h.aLines} +${h.bStart},${h.bLines} @@`, ...h.lines.map((l) => `${l.op}${l.text}`)])].join('\n');
  return {
    name,
    a,
    b,
    added: lines.filter((l) => l.op === '+').length,
    removed: lines.filter((l) => l.op === '-').length,
    hunks,
    unified,
  };
}

export function lint(name: string, v?: number) {
  const d = DEFS.find((x) => x.name === name);
  if (!d) return null;
  return { name, version: v ?? d.version, lint: d.lint, params: entry(d).params, offline: true };
}
