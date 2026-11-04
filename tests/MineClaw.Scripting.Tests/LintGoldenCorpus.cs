namespace MineClaw.Scripting.Tests;

/// <summary>Script snippets for <see cref="LintGoldenTests"/>: the script-shaped string literals of the Scripting tests when the
/// golden was cut (extracted once and frozen here, so later test edits don't move the golden).</summary>
internal static class LintGoldenCorpus
{
    public static readonly string[] Snippets =
    [
        @"gen()
return 1",
        @"upto(4, () => harvest_generator('5,65,0', 8).ok)",
        @"return mine('cobblestone', 8).msg",
        @"return mine('dirt', 8).msg",
        @"ensure('pickaxe')
return gen().ok",
        @"expect(acquire('pickaxe'))
gen()
return 1",
        @"ensure('pickaxe')",
        @"return String(ensure('pickaxe'))",
        @"ensure('axe')
return 'x'",
        @"ensure: no axe (",
        @"for (let i = 1; i <= 400; i++) { wait(0.05); prog(i / 4) }",
        @"mine('cobblestone', 4)
const r = deposit()
return r.ok ? 'stored' : 'no chest'",
        @"ok: script dig_some done (no chest",
        @"local ok, m = mine{'cobblestone', 4}
if not ok then fail(m) end",
        @"local ok = deposit{}
return ok and 'y' or 'n'",
        @"local ok, m = mine{'cobblestone', 2}
return ok and 'dug' or m",
        @"ok: script old_dig done (dug",
        @"const a = deposit({at: '3,64,3'}), b = dig('1,64,1')
const busy = r => r.msg.includes('busy') ? 'B' : ''
return String(a.ok) + String(b.ok) + busy(a) + busy(b)",
        @"say('/op maid_chan')
emote('waves')
say('a'.repeat(95) + ' /kill @a')",
        @"say('checking the gen foxy')
for (let x = 4; x <= 6; x++) say(x + ',64,6 cobblestone')
say('all done')",
        @"say('4,64,6 cobblestone')",
        @"fail('4,6=cobblestone 5,6=water 6,6=water 4,7=lava')",
        @"fail() is for errors, not data",
        @"while (true) { wait(0.05) }",
        @"paused: script loop paused (danger:",
        @"L1 `while (true) { wait(0.05) }`",
        @"awake (script loop paused)",
        @"script paused: script loop paused (danger",
        @"wait(1.5)
return 'fin'",
        @"ok: script short done (fin",
        @"mine('cobblestone', 2)
return 'ok'",
        @"cobble_loop(",
        @"mine('cobblestone', 3)",
        @"{ const r = goto({""to"":""home""}); m = r.msg; if (!r.ok) fail() }",
        @"for (let i = 1; i <= 30; i++) { wait(60); prog(i * 100 / 30) }
return 'done'",
        @"asleep (script cobble_loop",
        @"for (let i = 1; i <= 30; i++) wait(60)",
        @"wait(60)
if (inv('sapling') === 0) wake('no saplings left')
for (let i = 1; i <= 30; i++) wait(60)",
        @"on('danger', () => { eat() })
for (let i = 1; i <= 30; i++) wait(60)",
        @"wait(60)
throw new Error('no cobble left at the gen')",
        @"wait(60)
let n = 0
for (let i = 1; i <= 100000000; i++) n = n + 1",
        @"local names = {""load"", ""loadstring"", ""dofile"", ""loadfile"", ""require"", ""package"", ""io"", ""debug"", ""collectgarbage"", ""rawset"",
  ""rawget"", ""pcall"", ""xpcall"", ""coroutine"", ""run_command"", ""tool"", ""llm"", ""ask_player"", ""goto_""}
local bad = {}
for _, k in ipairs(names) do if _G[k] ~= nil then bad[#bad + 1] = k end end
if string.dump ~= nil or os.execute ~= nil or os.getenv ~= nil then bad[#bad + 1] = ""string/os"" end
return ""bad:"" .. table.concat(bad, "","") .. "".""",
        @"go{1, 64, 2} go(3, 4, 5) go{me.pos} go{""home"", dist = 2} go""spawn""
mine{""cobblestone"", 16} mine{""dirt"", n = 3}
deposit{items = {""cobblestone""}, at = {x = 1, y = 2, z = 3}}
dig{""4,5,6""} chop{} chop()",
        @"local n, p = near""stone"", players()
local got = table.concat({inv""cobblestone"", tostring(inv.has(""cobblestone"", 5)), tostring(inv.has(""cobblestone"", 6)), inv.free(),
  me.hp, me.food, me.pos.y, me.dim, block(0, 64, 0), block{x = 0, y = 70, z = 0}, tostring(n ~= nil and n.d < 3),
  where""iron"", tostring(where""gold""), type(time()), tostring(night()), p[1].name .. ""+"" .. p[2].name, near""home"".x}, ""|"")
local want = ""5|true|false|35|20|20|65|0|stone|air|true|iron: chest 3,64,2 (x12)|nil|number|false|kiwi+robin|5""
if got == want then return ""same"" end
log(got)
return ""diff""",
        @"move{{item = ""log"", n = ""all"", to = ""wood""}}
harvest_cane{4} skill(""harvest_cane"", {radius = 6}) skill(""log"") log(""hi"")
return ""ok""",
        @"while inv""log"" < 32 do local ok, m = chop{} if not ok then say(m) break end end
move{{item = ""log"", n = ""all"", to = ""storage:wood""}}",
        @"for i = 1, 4 do mine{""cobblestone"", 16} prog(i * 25) end
deposit{}",
        @"take{""iron_ore"", 8} smelt{""iron_ore"", 8, ""coal""} wait(0.1)
craft{""iron_pickaxe""}",
        @"on(""danger"", function() eat{} end)
for i = 1, 3 do skill(""harvest_cane"", {}) wait(0.05) end",
        @"if inv""sapling"" == 0 then wake""no saplings left"" return ""waiting"" end
place{""sapling"", me.x + 2, me.y, me.z}",
        @"cp""start""
local ok, m = go{""cobble gen""}
if not ok then fail(m) end
for i = 1, 3 do mine{""cobblestone"", 8} end
cp""stored"" deposit{}",
        @"cp(""chop"", function() mine{""log"", 4} carry.logs = 4 end)
cp(""store"", function() if not go{""chest""} then fail() end deposit{} end)
return ""stored "" .. carry.logs",
        @"load(""go{'escaped'}"")()",
        @"loadstring(""go{'escaped'}"")()",
        @"dofile('evil.lua')",
        @"loadfile('evil.lua')()",
        @"require('os')",
        @"package.loadlib('x', 'y')",
        @"io.open('secrets.json')",
        @"os.execute('ls')",
        @"os.getenv('ANTHROPIC_API_KEY')",
        @"os.remove('x')",
        @"debug.sethook()",
        @"debug.getinfo(1)",
        @"collectgarbage('stop')",
        @"string.dump(print)",
        @"rawset(me, 'hp', 1)",
        @"rawget(inv, 'free')",
        @"setmetatable(me, {})",
        @"setmetatable(inv, {__call = function() return 99 end})",
        @"pcall(function() end)",
        @"xpcall(function() end, print)",
        @"coroutine.create(function() end)",
        @"string.rep('x', 1e9)",
        @"('x'):rep(1e9)",
        @"string.format('%999999999d', 1)",
        @"local t = {} for i = 1, 1e8 do t[i] = i end",
        @"
go{'escaped'}",
        @" .. table.concat(bad, ",
        @"return type(os.time()) .. type(os.clock()), getmetatable(me), getmetatable(inv)",
        @"for i = 1, 1e9 do if i % 50 == 0 then mine{'dirt', 1} end end",
        @"while true do wait(0.1) end",
        @"while true do mine{'dirt', 1} end",
        @"action limit (5)",
        @"local function f() return f() + 1 end
f()",
        @"failed: script err at line 2 (error: attempt to",
        @"mine{'dirt',1}",
        @"wait(0.2)
mine{'dirt', 1}",
        @"local ok, m = go{'home'}
if ok then return 'bad' end return 'handled ' .. m",
        @"mine{'cobblestone', 4}
if not go{'home'} then fail() end",
        @"failed: script trip at line 2 (goto: no route): 2 acts",
        @"chop_tree {}",
        @"mined 12 cobblestone (inv 44)",
        @"local ok, m, n = mine{'cobblestone', 12} return tostring(ok) .. '/' .. m .. '/' .. n",
        @"true/mined 12 cobblestone (inv 44)/12",
        @"5|true|false|35|20|20|65|0|stone|air|true|iron: chest 3,64,2 (x12)|nil|number|false|kiwi+robin|5",
        @"), string.Join(",
        @"(unknown item 'unobtainium')",
        @"skill:log {}",
        @"say'/op maid_chan' say'hi /kill @a' emote'/gamemode 1 waves' ask('kiwi', '/stop now?', 5)",
        @"local ok = 0 for i = 1, 8 do if say('hi ' .. i) then ok = ok + 1 end end
return ok .. ' ' .. ask('kiwi', 'which chest?', 10)",
        @"prog(50, 'half') mine{'cobblestone', 64}",
        @"local a = wake'no saplings left' local b = wake'again' return tostring(a) .. tostring(b)",
        @"local a = ask('brain', 'which chest?')
return 'got ' .. a",
        @"ok: script chop32 done (+32 oak wood",
        @"deposit{}
mine{'dirt', 1}
return 'done'",
        @"craft{'planks', 4}
mine{'dirt', 1}",
        @"cancelled: script t at line 1 (stop)",
        @"cp'walk'
n, who = 3, 'kiwi'
prog(40, 'walking')
go{'home'}
return 'arrived'",
        @"script fetch paused (danger: [DANGER] hp 4/20",
        @"`go{'home'}`",
        @"while true do wait(0.05) end",
        @"hits = 0
on('danger', function(e) hits = hits + 1 carry.last = e.text eat{} end)
go{'home'}
return 'h' .. hits .. ' ' .. carry.last",
        @"on('chat', {from = 'kiwi'}, function(e) carry.said = e.text end)
wait(0.4)
wait(0.4)
wait(0.2)
return carry.said",
        @"local ok, m = waitfor('chat', {from = 'kiwi', text = 'go'}, 5)
return tostring(ok) .. ':' .. m",
        @"local ok, m = waitfor('item', {item = 'cobblestone', n = 99}, 0.3) return m",
        @"while true do carry.n = (carry.n or 0) + 1 wait(0.05) end",
        @"failed: script job at line 2 (goto: no route): cp 2/2",
        @"mine{'dirt', 1}
cp'here'
go{'x'}
return 'ok'",
        @"no checkpoint('nope')",
        @"mine{'cobblestone', args.n} return args.n * 2",
        @"mine{'dirt', 1} mine{'dirt', 1} wait(0.1)",
        @"cp('start')
mine('cobblestone', 2)
wait(0.3)
cp('half')
mine('cobblestone', 2)
return 'mined 4'",
        @"eval('rm -rf /')
mine('cobblestone', 4)",
        @"67: script cobble_loop 0% (brain asleep, profile haiku)",
        @"mine('cobblestone', 1)",
        @"mine('cobblestone', 1)
return 'dug'",
        @", llm.Requests.Select(q => $",
        @", q.Messages.SelectMany(m => m.Content).OfType<LlmText>().Select(t => t.Text))}",
        @" + string.Join(",
        @"cp('a')
mine('cobblestone', 1)
cp('b')
wait(30)
return 'done'",
        @"character.look('kiwi')
const g = character.gesture('wave', 'kiwi')
const t = character.speak('come', 'coming!', 'kiwi')
const m = character.mood().mood
const r = character.relation('kiwi').affinity
const k = character.recall('gen', 2)[0].text
const h = character.heard('kiwi', 30)[0].intent
const n = character.remember('kiwi likes the gen')
return [g.ok, t, m, r, k, h, n.ok, character.intent('thanks!')].join('|')",
        @"const a = character.look('kiwi'), b = character.gesture('nod').ok, c = character.line('greet'), d = character.mood()
const e = character.speak('greet', 'hi!')
return [a, b, c, d, e, character.heard().length].join('|')",
        @"const hits = []
on('player.punchBlock', { player: 'PoofImaFox', held: 'pickaxe' }, e => { hits.push(e.block + '@' + e.pos.x); dig(e.pos) })
react('thanks', e => { hits.push('ty ' + e.player) })
wait(1.5)
return hits.join(',')",
        @"const got = []
on('player.gesture', { kind: 'nod', at: 'me', player: 'PoofImaFox' }, e => { got.push('yes:' + e.answer) })
react('player.gesture', { answer: 'no' }, e => { got.push('no:' + e.player) })
const end = machine({
  initial: 'idle',
  states: {
    idle: { tick: () => { wait(0.1) }, on: { 'player.gesture': e => e.kind === 'bob' ? 'come' : null } },
    come: { enter: () => { got.push('come') }, tick: () => 'done' },
  },
})
return got.join(',') + '|' + end",
        @"machine({ initial: 'idle', states: { idle: { on: { 'player.punchBlock': 'work', 'me.hurt': 'idle' }, tick() { wait(1) } }, work: { tick() { wait(1) } } } })",
        @"); return ActResult.Success(",
        @" + text); return ActResult.Success(",
        @"react(trigger",
        @"let n = 0
for (let i = 0; i < 5; i++) if (character.remember('x' + i).ok) n++
return 'notes ' + n",
        @"const near = find('log', { r: 8 })
const r = forEach(near, t => chopTree(t))
return [near.length, near.exhausted, near.beyond.length, near.beyond[0].x, r.exhausted, r.msg].join('|')",
        @", targets.Select(t => $",
        @"2 log(s) out of reach",
        @"return forEach(['1,65,1'], t => chopTree(t)).msg + ' | ' + forEach(['1,65,1', '4,65,4'], t => chopTree(t)).msg",
        @"return forEach(['1,65,1'], t => chopTree(t)).msg",
        @"return forEach(['1,65,1', '2,65,2'], t => chopTree(t)).msg",
        @"function digger() { expect(mine('cobblestone', 1)); crew.inc('n'); return me.body + ' dug' }
function main() { const r = everyBody('digger', null, { retries: 3, pause: 0.3 }); return `${r.n} ${r.failed} ${JSON.stringify(r.by)}` }",
        @"function digger() { expect(mine('cobblestone', 1)); return 'never' }
function main() { let i = 0; return everyBody('digger', null, { until: () => ++i > 2 }).msg }",
        @"chopped oak tree (4 logs)",
        @", StringComparison.Ordinal) && !chopped.ContainsKey(l.Key.Id[",
        @"2/2 bodies done (67 16 stone, 69 16 stone)",
        @"function digger() { expect(mine('cobblestone', 1)); return me.body + ' dug' }
",
        @"function main() { return everyBody('digger', null, { wait: 20 }).msg }",
        @"function r() {}
function main() { return everyBody('r').msg }",
        @"function main() { return everyBody('nope') }",
        @"function worker() { expect(go('5,65,5')); return 'there' }",
        @"expect(go('5,65,5'))
return 'x'",
        @"// purpose: keep the tool depot chest stocked: stone tools, plus cobblestone, sticks and tables for bodies to craft their own on site
const P = params({ chest: '10,64,12', shovel: 24, pickaxe: 4, axe: 0, cobblestone: 512, sticks: 256, tables: 4, batch: 16, rounds: 1, every: 300 })
const HEAD = { shovel: 1, pickaxe: 3, axe: 3 }, STICKS = { shovel: 2, pickaxe: 2, axe: 2 }
// what the known chests hold of an item: where() says ""name x12: chest@3,64,2 (...); x5: ... | total 17""
function inChests(item) {
  const w = where(item)
  if (!w) return 0
  const t = /\| total (\d+)/.exec(w)
  if (t) return Number(t[1])
  let n = 0
  for (const m of w.matchAll(/\bx(\d+):/g)) n += Number(m[1])
  return n
}
const held = item => inChests(item) + inv(item)
function tools(kind, target) {
  const item = 'stone_' + kind
  let made = 0, need = target - held(item)
  while (need > 0) {
    const n = Math.min(need, P.batch)
    gather('cobblestone', HEAD[kind] * n)                       // raw materials into the bag: chests first, then the quarry
    gather('log', Math.ceil(STICKS[kind] * n / 8) + 1)          // a log is 4 planks is 8 sticks (+1 spare for a table)
    const c = craft(item, n)
    const k = inv(item)
    if (!ok(c) || k === 0) { log(`depot: ${item}: ${c.msg}`); break }
    stash([item], { to: P.chest })
    made += k
    need -= k
  }
  return made
}
function kitItem(item, target) {
  const need = target - held(item)
  if (need <= 0) return 0
  if (item === 'stick') gather('log', Math.ceil(need / 8) + 1)
  if (item === 'crafting_table') gather('log', need + 1)
  if (item === 'cobblestone') return gather(item, need, { stash: P.chest }).n   // quarried as the bag fills, stored each time
  const before = inv(item), c = craft(item, need)                               // gather's craft route is for ""have n"", not ""n more""
  if (!ok(c)) { log(`depot: ${item}: ${c.msg}`); return 0 }
  const made = inv(item) - before
  stash([item], { to: P.chest })
  return made
}
function pass() {
  const made = []
  for (const kind of ['shovel', 'pickaxe', 'axe']) if (P[kind] > 0) made.push(`${tools(kind, P[kind])} ${kind}`)
  for (const [item, key] of [['cobblestone', 'cobblestone'], ['stick', 'sticks'], ['crafting_table', 'tables']])
    if (P[key] > 0) made.push(`${kitItem(item, P[key])} ${item}`)
  return made.join(', ')
}
function main() {
  let last = pass()
  for (let i = 1; i < P.rounds; i++) { wait(Math.min(P.every, 600)); last = pass() }
  return `depot stocked: made ${last}`
}",
        @"// purpose: get iron for a portal: mine ore, smelt it, craft the buckets and flint and steel, leave the spare ingots in the chest
const P = params({ ore: 16, chest: '10,64,12', r: 32, buckets: 2, flint: 1 })
function main() {
  ensure('pickaxe')                                              // stone or better mines iron
  const need = P.buckets * 3 + P.flint                           // a bucket is 3 ingots, flint and steel 1
  if (inv('iron_ingot') < need) {
    let got = gather('iron_ore', P.ore, { from: 'mine', r: P.r })
    if (got.n === 0) {                                           // none showing: strip-mine a corridor, then look again
      skill('strip_mine', { length: 32 })
      got = gather('iron_ore', P.ore, { from: 'mine', r: P.r })
    }
    if (inv('iron_ore') === 0) fail(`no iron ore found (${got.msg})`)
    skill('smelt_batch', { item: 'iron_ore', count: inv('iron_ore'), wait: true })
    skill('collect_furnace')
  }
  if (inv('iron_ingot') < need) fail(`only ${inv('iron_ingot')} iron ingots, need ${need}`)
  expect(craft('bucket', P.buckets))
  if (P.flint > 0) {
    gather('flint', P.flint, { from: 'source' })                 // 1 in 10 gravel drops flint
    expect(craft('flint_and_steel', P.flint))
  }
  const spare = inv('iron_ingot')
  if (spare > 0) stash(['iron_ingot'], { to: P.chest })
  return `iron: ${inv('bucket')} buckets, ${inv('flint_and_steel')} flint and steel, ${spare} ingots stored`
}",
        @"// purpose: dig sand at a far pit and carry it home in bag loads, on foot or through the portals; every body does n
const P = params({ n: 2500, pit: '0,64,0', home: '0,64,0', r: 40, load: 1800, portal: '', homePortal: '' })
const at = s => { const [x, y, z] = String(s).split(',').map(Number); return { x, y, z } }
// one go() is one 60 s walk; a trip of hundreds of blocks takes several
function travel(to, tries = 60) {
  let last = Infinity, stuck = 0
  for (let i = 0; i < tries; i++) {
    const d = dist(at(to))
    if (d <= 6) return { ok: true, msg: 'there' }
    stuck = d > last - 4 ? stuck + 1 : 0
    if (stuck >= 3) return { ok: false, msg: `stuck ${Math.round(d)} m short of ${to}` }
    last = d
    go(to, 4)
  }
  return { ok: false, msg: `${Math.round(dist(at(to)))} m short of ${to} after ${tries} legs` }
}
// portals: walk to the frame, step in (nether), step into the nether portal there (overworld again)
function viaPortal(frame) {
  const t = travel(frame)
  if (!ok(t)) return t
  const a = skill('enter_portal'), b = skill('enter_portal')
  return ok(a) && ok(b) && me.dim === 0 ? { ok: true, msg: 'through' } : { ok: false, msg: `portal: ${a.msg} / ${b.msg}` }
}
// what a body needs out there: a shovel, and the kit to make more (a table, 64 cobblestone, 64 sticks = 32 stone shovels)
function kit() {
  ensure('shovel')
  gather('crafting_table', 1)
  gather('cobblestone', 64)
  gather('stick', 64)
}
function hauler(a) {
  use(replaceToolWhenBroken('shovel'), keepFed({ below: 14, fetch: false }))   // a broken shovel is made again on site; food is carried
  kit()
  let done = 0, trips = 0
  while (done < a.n) {
    ensure('shovel')
    const r = gather('sand', Math.min(a.n - done, P.load), { from: 'mine', r: P.r })
    if (r.n <= 0) return `${me.body} ${done} sand, then none near ${P.pit} (${r.msg})`
    const home = P.portal ? viaPortal(P.portal) : { ok: true }
    if (ok(home)) { const t = travel(P.home); if (!ok(t)) return `${me.body} ${done} sand, then ${t.msg}` } else return `${me.body} ${done} sand, then ${home.msg}`
    const s = stash(['sand'], { to: P.home })
    if (!s.ok) return `${me.body} ${done} sand, then ${s.msg}`
    done += r.n
    trips++
    crew.inc('sand', r.n)
    if (done < a.n) { const back = P.homePortal ? viaPortal(P.homePortal) : travel(P.pit); if (!ok(back)) return `${me.body} ${done} sand, then ${back.msg}` }
  }
  return `${me.body} ${done} in ${trips} trips`
}
function main() {
  const r = everyBody('hauler', { n: P.n })
  if (!r.ok && /no crew/.test(r.msg)) return hauler({ n: P.n })
  return `far sand run: ${crew.get('sand')} sand (${r.msg})`
}",
        @"name x12: chest@3,64,2 (...); x5: ... | total 17",
        @" + a.GetProperty(",
        @"400 m at 40 m a go() is ten legs each way: ",
        @"const names = ['eval', 'require', 'importNamespace', 'System', 'process', 'setTimeout', 'fetch', '__h', 'H', 'run_command', 'ask_player']
const bad = names.filter(n => typeof globalThis[n] !== 'undefined' && n !== 'eval')
return 'bad:' + bad.join(',') + '.' + typeof me + typeof inv + typeof machine",
        @"go(1, 64, 2); go(me.pos); go({x: 3, y: 4, z: 5}); go('home', {dist: 2}); go('spawn')
mine('cobblestone', 16); mine({block: 'dirt', n: 3}); mine('dirt', undefined)
deposit({items: ['cobblestone'], at: {x: 1, y: 2, z: 3}}); deposit({}); deposit()
dig('4,5,6'); chop(); chopTree(); take('coal', 8, {at: '1,64,2'}); goto('x')",
        @"const n = near('stone'), p = players()
const got = [inv('cobblestone'), inv.has('cobblestone', 5), inv.has('cobblestone', 6), inv.free(), me.hp, me.food, me.pos.y, me.dim,
  block(0, 64, 0), block({x: 0, y: 70, z: 0}), n !== null && n.d < 3, where('iron'), where('gold'), typeof time(), night(),
  p[0].name + '+' + p[1].name, near('home').x, me.name, me.body].join('|')
const want = '5|true|false|35|20|20|65|0|stone|air|true|iron: chest 3,64,2 (x12)||number|false|kiwi+robin|5|maid_chan|main'
if (got === want) return 'same'
log(got)
return 'diff'",
        @"move({item: 'log', n: 'all', to: 'wood'})
harvest_cane(4); harvestCane({radius: 5}); skill('harvest_cane', {radius: 6}); skill('log'); log('hi')
const r = skill('toss_it')
return 'said: ' + r.msg",
        @"cp('chop', () => { mine('log', 4); carry.logs = 4 })
cp('store', () => { if (!go('chest').ok) fail(); deposit() })
return 'stored ' + carry.logs",
        @"eval(""go('escaped')"")",
        @"new Function(""go('escaped')"")()",
        @"Function(""return 1"")()",
        @"require('fs')",
        @"importNamespace('System.IO')",
        @"System.IO.File.ReadAllText('appsettings.json')",
        @"process.exit(1)",
        @"gen = () => 1",
        @"me.hp = 1; if (me.hp !== 20) {} else throw new Error('frozen')",
        @"inv.free = () => 99",
        @"'x'.repeat(1e9)",
        @"'x'.padStart(1e9)",
        @"let s = 'x'; for (let i = 0; i < 40; i++) s = s + s + 'y'",
        @"const a = []; for (let i = 0; i < 1e8; i++) a.push(i)",
        @"new Array(1e9).fill(0)",
        @"/(a+)+$/.test('a'.repeat(5000) + 'b')",
        @"setTimeout(() => go('escaped'), 1)",
        @"
go('escaped')",
        @"for (let i = 0; i < 10; i++) { try { mine('dirt', 1) } catch (e) { log('caught ' + e) } }
return 'escaped the limit'",
        @"action limit (2)",
        @"while (true) {}",
        @"while (inv('dirt') < 5) {}",
        @"for (let i = 0; i < 1e12; i++) { const x = me.hp }",
        @"for (let i = 0; i < 1e9; i++) { if (i % 50 === 0) mine('dirt', 1) }",
        @"while (true) wait(0.1)",
        @"while (true) mine('dirt', 1)",
        @"function f() { return f() + 1 }
f()",
        @"failed: script err at line 3 (error: ",
        @"mine('dirt', 1)
notAThing()",
        @"const r = await mine('dirt', 2)
async function twice() { await wait(0.05); return (await mine('dirt', 1)).ok }
return r.ok && await twice() ? 'awaited' : 'no'",
        @"await new Promise(() => {})
return 'never'",
        @"const r = go('home')
if (r.ok) return 'bad'
return 'handled ' + r.msg",
        @"mine('cobblestone', 4)
if (!go('home').ok) fail()",
        @"mine('cobblestone', 4)
go('home')",
        @"const r = mine('cobblestone', 12)
return `${r.ok}/${r.msg}/${r.n}`",
        @"return mine('cobblestone', 12)",
        @"say('/op maid_chan'); say('hi /kill @a'); emote('/gamemode 1 waves'); ask('kiwi', '/stop now?', 5)",
        @"let ok = 0
for (let i = 1; i <= 8; i++) if (say('hi ' + i).ok) ok++
return ok + ' ' + ask('kiwi', 'which chest?', 10)",
        @"const a = wake('no saplings left'), b = wake('again')
return `${a}${b}`",
        @"const a = ask('brain', 'which chest?')
return 'got ' + a",
        @"fail('4,6=cobblestone 5,6=water 6,6=air')",
        @"deposit()
mine('dirt', 1)
return 'done'",
        @"cp('walk')
carry.n = 3; carry.who = 'kiwi'
prog(40, 'walking')
go('home')
return 'arrived'",
        @"`go('home')`",
        @"let hits = 0
on('danger', e => { hits++; carry.last = e.text; eat() })
go('home')
return 'h' + hits + ' ' + carry.last",
        @"on('chat', {from: 'kiwi'}, e => { carry.said = e.text })
while (!carry.said) wait(0.05)
while (true) wait(0.05)",
        @"const r = waitFor('chat', {from: 'kiwi', text: 'go'}, 5)
return r.ok + ':' + r.msg",
        @"return waitFor('item', {item: 'cobblestone', n: 99}, 0.3).msg",
        @"while (true) { carry.n = (carry.n || 0) + 1; wait(0.05) }",
        @"mine('dirt', 1)
cp('here')
go('x')
return 'ok'",
        @"carry.logs = 7
cp('store')
return 'stored ' + carry.logs",
        @"cp('store')
return 'stored ' + carry.logs",
        @"mine('cobblestone', args.n)
return args.n * 2",
        @"mine('dirt', 1); mine('dirt', 1); wait(0.1)",
        @"mine('dirt', 1); mine('dirt', 1)",
        @"const P = params({ n: 64, bodies: ['67', '69'] })
function miner(a) {
  const got = gather('cobblestone', a.n, { from: 'gen', stash: true }).n
  crew.inc('cobble', got)
  return `${me.body} ${got}`
}
function main() {
  const r = parallel(P.bodies.map(b => [b, 'miner', { n: P.n }]))
  return `gen crew: ${crew.get('cobble')} cobble (${r.join(', ')})`
}",
        @"function digger() { expect(mine('cobblestone', 1)); return me.body + ' dug' }
function main(a) { return parallel([['67', 'digger'], ['69', 'digger']], { keepGoing: a.keep }).join(' | ') }",
        @"function pinger() {
  crew.send('69', { item: 'log', n: 4 })
  expect(crew.barrier('b1', 2, 10))
  return 'pinged ' + crew.lock('chest', 30)
}
function ponger() {
  const m = crew.recv(10)
  expect(crew.barrier('b1', 2, 10))
  return `got ${m.n} ${m.item} lock ${crew.lock('chest', 30)}`
}
function main() {
  const b = body('67')
  const hp = b.hp, free = b.free(), held = b.inv('cobblestone')
  const r = b.dig('1,64,1')
  const l = spawn('69', 'ponger')
  const out = parallel([['67', 'pinger']])
  return [hp, free > 0, held, r.ok, out[0], l.wait(10).msg].join(' ; ')
}",
        @"minee('dirt')",
        @"setTimeout(() => mine('dirt'), 10)",
        @"while (true) { const h = me.hp }",
        @"for (;;) { inv('dirt') }",
        @"take({count: 3})",
        @"machine({ initial: 'a', states: { a: { tick: () => 'b' , on: { danger: 'nope' } } } })",
        @"machine({ initial: 'x', states: { a: {} } })",
        @"function miner() {}
mine('dirt', 1)
parallel([['67', 'miner']])",
        @"function main() { parallel([['67', 'digger']]) }",
        @"function m() {}
function main() { parallel([['67', 'm'], ['67', 'm']]) }",
        @"for (let i = 0; i < 9; i++) { mine('dirt', 1) }",
        @"for (let i = 0; i < 3; i++) say('hi')",
        @"gen('auto', 8)",
        @"while (true) { mine('dirt', 1) }",
        @"mine('a',1); mine('a',1); mine('a',1); mine('a',1); mine('a',1); mine('a',1); mine('a',1)",
        @"machine({ initial: 'a', states: { a: { on: { danjer: 'a' } } } })",
        @"function miner() { say('hi') }
function main() { parallel([['67', 'miner']]) }",
        @"await new Promise(r => r(1))",
        @"const r = gen('auto', 8)
expect(r)",
        @"gen({ at: 'auto' })",
        @"const p = { to: 'home' }
go(p)",
        @"go(1, 64, 2)",
        @"for (let i = 0; i < 3; i++) { const r = mine('dirt', 1); if (!r.ok) break }",
        @"while (true) { if (!mine('dirt', 1).ok) break }",
        @"const { a, b: [c] } = { a: 1, b: [2] }
let d = a + c
label: for (const x of [1]) { continue label }
return d",
        @"class K { m() { return this.v } }
const k = new K()
return String(k.m()) + Math.max(1, 2) + JSON.stringify({ n: 1 })",
        @"const a = 1
minee('dirt')
gen()",
        @"lint errors (fix and resubmit): L2 unknown name 'minee' (did you mean mine",
        @"L3 gen() needs at=",
        @"// purpose: mine the gen
const P = params({ n: 64, at: 'auto' })
mineGen({ n: P.n, at: P.at })
return args.extra",
        @"ensure""pickaxe""
local ok,m=deposit{} expect(ok,m)",
        @"-- purpose: x
upto(20,function() gen{""auto"",1} end)",
        @"// purpose: x
const r = gen('auto', 1)
expect(r)",
        @"chopped oak tree (4 logs, 1 sapling), replanted",
        @"wood crew: 32 logs (67 16, 69 16)",
        @"gen crew: 32 cobble (67 16, 69 16)",
        @"return bodies().length + ' ' + crew.inc('x', 2)",
        @"function r() {}
function main() { return parallel([['67', 'r']]) }",
        @") && c.Contains(",
        @"const r = mine('cobblestone', 4)
log('got', r.n)
return r.ok ? 'mined ' + inv('dirt') : 'no'",
        @"mine {""block"":""cobblestone"",""count"":4}",
        @"const order = []
const bb = { n: 0 }
const end = machine({
  initial: 'a', bb,
  states: {
    a: { enter: () => { order.push('enter a') }, tick: b => (++b.n >= 3 ? 'b' : undefined), exit: () => { order.push('exit a') } },
    b: { enter: (b, m) => { order.push('enter b from ' + m.history[m.history.length - 1].from); return 'c' } },
    c: { final: true, enter: () => { order.push('enter c') } },
  },
})
return end + ' ' + bb.n + ' ' + order.join('|')",
        @"return machine({
  initial: 'walk',
  states: {
    walk: { tick: () => { go('far') }, on: { danger: 'flee' } },
    flee: { enter: () => { go('home'); return 'done' } },
  },
})",
        @"const bb = { brave: true, seen: 0 }
return machine({
  initial: 'idle', bb,
  guards: { scared: b => !b.brave },
  states: {
    idle: { on: {
      danger: { to: 'flee', guard: 'scared' },
      'chat:count': { to: 'idle', if: (b) => { b.seen++; return false } },
      'chat:quit': 'done',
    } },
    flee: { final: true },
  },
})",
        @"const bb = { ticks: 0 }
return machine({
  initial: 'wait', bb,
  states: {
    wait: { on: { [after(300)]: 'count' } },
    count: { on: { [every(2)]: (ev, b) => { b.ticks++ }, [when(() => bb.ticks >= 3)]: 'mine' } },
    mine: { tick: () => { mine('cobblestone', 1) }, on: { [when(() => inv('cobblestone') > 0)]: 'done' } },
  },
}) + ' ' + bb.ticks",
        @"return machine({
  initial: 'work', thresholds: { hungry: 10 },
  states: {
    work: { tick: () => { wait(0.05) }, on: { hungry: 'eat', night: 'sleep' } },
    eat: { enter: () => { eat(); return 'work' } },
    sleep: { final: true },
  },
})",
        @"const bb = { mined: 0 }
return machine({
  initial: 'mine', bb,
  states: {
    mine: { tick: b => { expect(mine('cobblestone', 1)); b.mined++; if (b.mined >= 2) return 'store' } },
    store: { tick: b => { expect(deposit()); return 'done' } },
  },
}) + ' after ' + bb.mined",
        @"return machine({
  initial: 'a',
  states: {
    a: { tick: () => 'b', exit: () => { deposit() }, on: { danger: 'c' } },
    b: { final: true }, c: { final: true },
  },
})",
        @"work -> eat (on hungry)",
        @"machine({ initial: 'a', states: { a: { tick: () => 'nowhere' } } })",
        @"const r = machine({ initial: 'work', bb: { enters: 0 }, states: {
  work: { enter: (bb) => { bb.enters++ }, tick: (bb) => { if (!bb.skip) mine('cobblestone', 1); else return 'done' } },
  done: { final: true } } })
return 'end'",
        @"mine('cobblestone', 2)
cp('store')
const r = deposit()
if (!r.ok) fail(r.msg)
return 'stored, fixes=' + carry.fixes",
        @"P.n = 9; carry.fixed = ev.kind + ':' + P.n; wait(0.05)",
        @"const P = params({ n: 4 })
mine('cobblestone', P.n)
mine('cobblestone', P.n)
return 'n=' + P.n + ' fixed=' + carry.fixed",
        @"params P: {""n"":4}",
        @"const P = params({ n: 1 })
P.n = 5
return P.n",
        @"work -> work (patch)",
        @"carry.fixes = (carry.fixes || 0) + 1",
        @"place('chest', '2,65,0')",
        @"const r = deposit()
if (!r.ok) fail(r.msg)
return 'went on'",
        @"const a = ask('brain', 'which chest?')
return 'answer ' + a",
        @"for (let i = 0; i < 20; i++) dig('1,64,1')",
        @"mine('cobblestone', 1)
return 'resumed by the brain'",
        @"let x = 0; for (let i = 0; i < 1e9; i++) { x++ }",
        @"const r = deposit()
if (!r.ok) fail(r.msg)
return 'no'",
        @"noSuchThing()",
        @"const a = 1
throw new Error('boom')",
        @"fail('nope')",
        @"fail(): nope",
        @"onInterrupt('danger', ev => { carry.handled = ev.kind; wait(0.05) })
mine('cobblestone', 1)
return 'h=' + carry.handled",
        @"log('patched')",
        @"onInterrupt('failed', ev => 'escalate')
const r = deposit()
if (!r.ok) fail(r.msg)
return 'went on'",
        @"go('5,65,5')",
        @"for (let i = 0; i < 3; i++) { const r = deposit(); if (!r.ok) fail(r.msg); wait(0.1); mine('dirt', 1) }
return 'ok'",
        @"const P = params({ ",
        @", Enumerable.Range(0, 15).Select(i => $",
        @"ask('brain', '",
        @"deposit: all 3 known chests full at 12,64,-5 (Fox)",
        @"expect(deposit('chest'))",
        @"deposit: all 4 known chests full at 1,70,9 (fox)",
        @"const logs = find('log')
const ripe = find('wheat', { filter: t => t.meta >= 7 })
const all = find('wheat')
const z = find('zombie'), h = find('hostile'), mobs = find('mob'), p = find('player'), d = find('kiwi')
const home = find('home'), none = find('diamond_ore', { r: 8 })
return [logs.map(t => `${t.x},${t.z}:${t.kind}`).join(' '), logs[0].d <= logs[1].d, ripe.length, ripe[0].x, all.length,
  z[0].kind + ':' + z[0].name, h.length, mobs.length, p[0].name, d.length, home[0].kind + '@' + home[0].x, none.length].join('|')",
        @"const p = item('wood_pickaxe'), st = item('stick'), c = item('cobblestone')
return [p.name, p.version, st.craftable, c.craftable, c.block, item('no_such_thing')].join(',')",
        @"const held = gather('cobblestone', 16)
const fromChest = gather('log', 8, { chest: '14,64,-5' })
const crafted = gather('stick', 4, { from: 'craft' })
const noRecipe = gather('cobblestone', 64, { from: 'craft' })
return [held.ok, held.n, held.have, fromChest.ok, fromChest.n, crafted.ok, crafted.n, noRecipe.ok].join(',')",
        @"const c = gather('cobblestone', 8, { from: 'source' })
const w = gather('wheat', 10, { from: 'harvest', more: true, r: 6 })
const l = gather('leather', 2, { from: 'kill' })
const d = gather('cobblestone', 4, { from: left => mine('cobblestone', left), more: true })
return [c.ok, c.n, w.ok, w.n, l.ok, l.have, d.ok, d.n].join(',') + ' | ' + w.msg",
        @"const a = quota(5, () => 2)
const b = quota(10, () => 0, { idle: 2 })
const c = quota(3, () => ({ ok: false, msg: 'nope' }))
const d = quota(16, () => mine('dirt', 8), { item: 'dirt' })
return [a.ok, a.n, b.ok, b.msg, c.msg, d.ok, d.n].join('|')",
        @"const seen = []
const all = forEach([{ x: 5, y: 65, z: 5 }, 'home', '1,65,1', { x: 3, y: 65, z: 0 }], t => { seen.push(typeof t === 'string' ? t : `${t.x},${t.z}`); return go(t) })
const two = forEach(['4,65,4', '1,65,1', '2,65,2'], t => { seen.push(t) }, { budget: 2 })
const stop = forEach(['1,65,1', '2,65,2'], () => 'stop')
const bad = forEach(['1,65,1'], () => ({ ok: false, msg: 'blocked' }))
return [seen.join(' '), all.n, two.n, stop.n, bad.ok, bad.failed, forEach([], () => 1).ok].join('|')",
        @"const a = patrol({ at: '0,65,0', r: 4 }, null, { rounds: 2 })
let k = 0
const b = patrol(['1,65,1', '2,65,2', '3,65,3'], () => (++k === 2 ? 'stop' : undefined), { rounds: 5 })
const c = patrol(['1,65,1'], null, { rounds: 9, until: () => true })
return [a.n, b.n, c.n, c.ok].join(',')",
        @"const a = stash(['log'], { to: '1,64,1' }), b = stash(), c = stash('dirt')
const g = handTo('kiwi', 'cobblestone', 5, { chest: '14,64,-5' })
return [a.ok, b.ok, c.ok, g.ok, g.n].join(',')",
        @"const f = stash(['dirt'])
const tight = makeRoom(2)
const r = makeRoom(2, { drop: ['dirt'] })
return [f.ok, f.full, tight.ok, tight.msg, r.ok, r.n].join('|')",
        @"let k = 0
const a = waitFor(() => ++k >= 2, 10)
const b = waitFor(() => false, 1)
return [a.ok, k, b.ok, b.msg].join(',')",
        @"const g = gather('rotten_flesh', 1, { from: 'source' })
return [item('stick').version, item('stone_pickaxe'), ensure('pickaxe'), g.ok, g.msg].join('|')",
        @"// purpose: keep mining the cobble gen patiently, deposit when full
const P = params({ at: '6,64,8', chest: '14,64,-5', rounds: 2, per: 8 })
use(replaceToolWhenBroken('pickaxe'))
return `mined ${mineGen({ at: P.at, n: P.rounds * P.per, per: P.per, chest: P.chest })} cobble`",
        @"// purpose: dump inventory then fight mobs at night
const P = params({ chest: '14,64,-5', home: '8,64,-8' })
store(P.chest)
return 'patrol done: ' + guardArea({ home: P.home, retreatBelow: 4, rounds: 20 })",
        @"// purpose: wait for west tree farm trees to grow, chop+replant them
const P = params({ spots: ['9,65,0', '9,65,3', '13,65,0', '13,65,3'], chest: '14,64,-5' })
return 'chopped ' + chopTrees({ spots: P.spots, n: 4, rounds: 20, waitS: 30, chest: P.chest })",
        @"return gather('cobblestone', 20, { from: 'gen', stash: '14,64,-5' }).msg",
        @"all 2 known chests full (14,64,-5, 5,64,4); 141 cobblestone in bag",
        @"const r = gather('cobblestone', 640, { from: 'gen', stash: true })
return [r.ok, r.full, r.need].join(',') + ' ' + r.msg",
        @"gather('log', 2, { from: 'teleport' })",
        @"false||false|need 2 free slots, have 0 (deposit: no path to 14,64,-5)|true|15",
        @"test-9.9||wooden_pickaxe|false|rotten_flesh 0/1 (source: no known source of rotten_flesh in this world)",
        @"return 'mined ' + mineGen({ n: 20, per: 8, chest: '14,64,-5' })",
        @"all 2 known chests full (14,64,-5, 5,64,4); 141 cobblestone in bag; craft+place a chest (have 300 logs) or keep carrying",
        @"- gather(what, n=1",
        @"- find(query",
        @"mineGen({ n: 8 })
waitForDay(5)",
        @"mineGen() is a deprecated compat helper: use gather('cobblestone'",
        @"waitFor('day'",
        @"store('1,2,3')
chopTrees({ n: 1 })",
        @"function store(x) { return x }
store(1)",
        @"// purpose: every body digs n sand near the shore and stashes it in the staging chest as its bag fills
const P = params({ n: 2500, chest: '10,64,10', r: 40 })
function digger(a) {
  const r = gather('sand', a.n, { from: 'mine', stash: P.chest, r: P.r })
  crew.inc('sand', r.n)
  return `${me.body} ${r.n}`
}
function main() {
  const r = everyBody('digger', { n: P.n })
  return `sand run: ${crew.get('sand')} sand (${r.msg})`
}",
        @" Bodies only see their loaded chunks; the area round the base was hunted out (",
        @"const r = scout('cow', { dir: 0 }); return r.ok + '|' + r.found[0].name + '|' + r.msg",
        @"return scout('cow', { dir: 90, max: 64 }).msg",
        @"no cow within 64 m that way (90 deg)",
        @"// purpose: walk two places
const a = 1
go(1, 64, 2)
const b = a + 1
function hop(p) {
  const far = p * 10
  go(far, 64, 0)
}
hop(b)
return 'done'",
        @"const p = params({n: 3, cell: 'A'})
return machine({
  initial: 'prep',
  bb: {mined: 0},
  states: {
    prep: { enter: (bb) => 'mine' },
    mine: {
      tick: (bb) => {
        mine('cobblestone', p.n)
        bb.mined += 1
        if (bb.mined > 5) return 'done'
      },
    },
  },
})",
        @"let acc = 0
for (let round = 0; round < 800; round++) {
  for (let i = 0; i < 1000; i++) { acc = (acc + i * 7) % 1000003; const t = acc ^ i; if (t < 0) acc++ }
  mine('dirt', 1)
}
return 'acc ' + acc",
        @" + RawStack(s)?.ReplaceLineEndings(",
        @", snap.Locals!.Select(l => $",
        @"go(1, 64, 2)
return 'x'",
        @"    at gen (sdk/50-jobs.js:41:7)",
        @"    at mineGen (script:7:3)",
        @"    at (anonymous) (script:9:1)",
        @"const r = retry(5, () => mine('dirt', 1))
const passes = upto(10, i => i < 4)
let k = 0
const met = until(() => k >= 3, () => { k++ })
const a = seq(() => true, () => ({ ok: false, msg: 'stop' }), () => fail('never'))
const b = sel(() => false, () => ({ ok: false }), () => 'third')
return [r.ok, passes, met, k, a.msg, b, ok(undefined), ok(null), ok({ ok: false })].join(',')",
        @"const r = timeout(0.4, () => { go('far'); return 'arrived' })
const q = timeout(5, () => 'quick')
return r.ok + ' ' + r.msg + ' / ' + q",
        @"use(depositWhenFull('14,64,-5', ['cobblestone']), replaceToolWhenBroken('pickaxe'), pauseAtNight({ shelter: 'home' }))
for (let i = 0; i < 8; i++) { expect(mine('cobblestone', 1)); wait(0.25) }
return 'mined ' + inv('stone_pickaxe')",
        @"store('1,64,1', ['dirt'])
const held = restock('cobblestone', 16, '14,64,-5')
const given = deliver('PoofimaFox', 'cobblestone', 16)
const chopped = chopTrees({ spots: ['3,65,3', '9,65,9'], n: 1, chest: '2,64,2' })
const farm = harvestFarm({ radius: 6, store: true })
return [held, given, chopped, farm.ok, waitForDay(5)].join(',')",
        @"const r = store('14,64,-5', ['cobblestone'])
return [r.ok, r.full, r.need].join(',') + ' | ' + r.msg",
        @"let seen = null
const got = mineGen({ n: 8, per: 8, chest: '14,64,-5', onFull: r => { seen = r.need } })
return 'got ' + got + ' need ' + seen",
        @"const t = light(0, 65, 0), f = light(pos('5,65,2'))
return [t.block, t.sky, f.block, f.now, f.night, canSpawn(5, 65, 2), canSpawn(pos('5,65,2'), true), canSpawn(1, 65, 1), block(0, 65, 0)].join(',')",
        @"const r = [excavate('1,60,1,4,62,4'), excavate('spiral stair', { to: 12 }), excavate({ site: 'spiral stair' }, { op: 'room', size: '4x4x3' }),
  seal('spiral stair'), lightUp([1, 60, 1, 4, 62, 4]),
  pattern.learn('stair', '0,40,0,1,60,1'), pattern.apply('column 3 step 0,-1,0 turn -90 pivot 0.5,0.5', 12, '5,60,5'), pattern.apply('spiral stair', 20)]
return r.every(ok)",
        @"room(2)
return ensure(['pickaxe'])",
        @"ensure('sword')",
        @"const P = params({ n: 64, at: 'auto', minFood: 14 })
return `${P.n} ${P.at} ${P.minFood}`",
        @"use(keepFed({ below: 14 }))
go('far')
return 'arrived'",
        @"use(defendIfAttacked(), fleeVoidEdges({ to: 'home' }))
go('far')
return 'done'",
        @"mineGen({ n: 8 })",
        @"skill:give_items {}",
        @"false,true,141 | all 2 known chests full (14,64,-5, 5,64,4); 141 cobblestone in bag",
        @"store('14,64,-5')",
        @"mineGen({ n: 64, per: 8, chest: '14,64,-5' })",
        @"return 'fights ' + guardArea({ radius: 8 })",
        @"machine({initial, states:",
        @"- light(x,y,z) or light(pos)",
        @"- canSpawn(x,y,z, now=false)",
        @"-- purpose: mine n cobble at the gen, store it, re-tool, stay fed
local n, at, chest = args.n or 64, args.at or ""auto"", args.chest
ensure""pickaxe"" room(2)
local got = 0
cp""mine""
upto(n * 3, function()
  if inv""stone_pickaxe"" + inv""wood_pickaxe"" + inv""iron_pickaxe"" == 0 then ensure""pickaxe"" end
  local b = inv""cobblestone""
  local ok, m = retry(3, function() return gen{at, math.min(8, n - got)} end)
  if not ok then if m:find(""no free cell"") then wait(3) return true end fail(m) end
  got = got + math.max(0, inv""cobblestone"" - b)
  if me.food < 14 then eat{} end
  if inv.free() < 2 then local d, dm = deposit{at = chest, items = {""cobblestone""}} expect(d, dm) end
  prog(100 * got / n)
  return got < n
end)
cp""store""
local ok, m = deposit{at = chest, items = {""cobblestone""}} expect(ok, m)
return ""mined "" .. got .. "" cobble""",
        @"// purpose: mine n cobble at the gen, store it, re-tool, stay fed
const P = params({ n: 64, at: 'auto', chest: null })
use(keepFed(), replaceToolWhenBroken('pickaxe'))
ensure('pickaxe')
return `mined ${gather('cobblestone', P.n, { from: 'gen', at: P.at, stash: P.chest }).n} cobble`",
        @"-- purpose: harvest ripe wheat, replant, store the wheat; n rounds, waiting for regrowth
local rounds, chest = args.rounds or 3, args.chest
ensure""hoe""
for r = 1, rounds do
  cp(""round"" .. r)
  local ok, m = harvest_wheat{radius = args.radius or 8, replant = true}
  if not ok and not m:find(""nothing ripe"") then fail(m) end
  if inv""wheat"" > 0 then local d, dm = deposit{at = chest, items = {""wheat"", ""seeds""}} expect(d, dm) end
  if r < rounds then wait(args.wait or 120) end
end
return ""harvested "" .. rounds .. "" rounds""",
        @"// purpose: harvest ripe wheat, replant, store the wheat; n rounds, waiting for regrowth
const P = params({ rounds: 3, radius: 8, chest: null, waitS: 120 })
ensure('hoe')
for (let r = 1; r <= P.rounds; r++) {
  gather('wheat', 64, { from: 'harvest', r: P.radius, stash: P.chest })
  if (r < P.rounds) wait(P.waitS)
}
return `harvested ${P.rounds} rounds`",
        @"-- purpose: store everything but tools and food in the chest, then come back
local chest, back = args.chest or ""14,64,-5"", me.pos
local ok, m = go{chest} expect(ok, m)
ok, m = deposit{at = chest} expect(ok, m)
ensure{""pickaxe"", ""food""}
ok, m = go{back} expect(ok, m)
return ""stored""",
        @"// purpose: store everything but tools and food in the chest, then come back
const P = params({ chest: '14,64,-5' })
const back = me.pos
expect(stash('all-but-tools', { to: P.chest }))
ensure(['pickaxe', 'food'])
expect(go(back))
return 'stored'",
        @"-- purpose: mine the gen by day, guard it at night, retreat when hurt; stop after n cobble
local at, home, n = args.at or ""auto"", args.home or ""home"", args.n or 128
ensure{""pickaxe"", ""sword""}
local got = 0
upto(400, function()
  if me.hp <= 6 then go{home} wait(30) return true end
  if night() then
    local mob = near(""zombie"", 8) or near(""skeleton"", 8) or near(""spider"", 8)
    if mob then local ok, m = attack{mob.name} if not ok then log(m) end else wait(3) end
  else
    local b = inv""cobblestone""
    local ok, m = gen{at, 8}
    if not ok and not m:find(""no free cell"") then fail(m) end
    got = got + math.max(0, inv""cobblestone"" - b)
    if inv.free() < 2 then local d, dm = deposit{} expect(d, dm) end
  end
  if me.food < 14 then eat{} end
  return got < n
end)
return ""mined "" .. got .. "", guarded the nights""",
        @"// purpose: mine the gen by day, guard it at night, retreat when hurt; stop after n cobble
const P = params({ n: 128, at: 'auto', home: 'home' })
const bb = { mined: 0 }
return machine({
  initial: 'mine', bb, policies: [keepFed()],
  on: { lowhp: 'retreat', [when(() => bb.mined >= P.n)]: 'done' },
  states: {
    mine: { tick: () => { bb.mined += gather('cobblestone', 16, { from: 'gen', at: P.at, stash: true }).n }, on: { night: 'guard' } },
    guard: { tick: () => { const m = hostile(8); if (!m || !ok(attack(m.name))) wait(3) }, on: { day: 'mine' } },
    retreat: { enter: () => { go(P.home) }, on: { [after(30000)]: 'mine' } },
  },
})",
        @"-- purpose: n cobble per body at the gen, auto cells, stored
function miner(a)
  ensure""pickaxe"" room(2)
  local got = 0
  cp""mine""
  upto(a.n, function()
    local ok, m, n = gen{""auto"", 8}
    if not ok then
      if m:find(""no free cell"") then wait(3) return true end
      fail(m)
    end
    got = got + (n or 0); crew.inc(""cobble"", n or 0)
    prog(crew.get(""cobble"") / a.total)
    if inv.free() < 2 then local ok2, m2 = deposit{} expect(ok2, m2) end
    return got < a.n
  end)
  local ok, m = deposit{} expect(ok, m)
  return me.name .. "" "" .. got
end
function main(a)
  local names, n, jobs = a.bodies or {""67"", ""69""}, a.n or 64, {}
  for _, b in ipairs(names) do jobs[#jobs + 1] = {body(b), ""miner"", {n = n, total = n * #names}} end
  local r = parallel(jobs)
  return ""gen crew: "" .. crew.get(""cobble"") .. "" cobble ("" .. table.concat(r, "", "") .. "")""
end",
        @"// purpose: n cobble per body at the gen (auto cells), stored; one result line
const P = params({ n: 64, bodies: ['67', '69'] })
function miner(a) {
  const got = gather('cobblestone', a.n, { from: 'gen', stash: true }).n
  crew.inc('cobble', got)
  return `${me.body} ${got}`
}
function main() {
  const r = parallel(P.bodies.map(b => [b, 'miner', { n: P.n }]))
  return `gen crew: ${crew.get('cobble')} cobble (${r.join(', ')})`
}",
        @" .. crew.get(",
        @" .. table.concat(r, ",
        @"| task | author calls | output tok | check ms (lint + dry run) |",
        @")), c => Assert.DoesNotContain(",
        @") || c.StartsWith(",
        @"no path to within 1 of (40, 65, 0) (no route)",
        @"return JSON.stringify(story.go('40,65,0', 1))",
        @"return JSON.stringify(story.escape())",
        @"at the top (a step from the surface): ",
        @"couldn't climb out … (needs a wooden pickaxe: digging stone … without it would lose the drop)",
        @"dug (drop lost)",
        @"can't get near 1,64,1: no dry spot with a face beside it in view (no path)",
        @"j1 progress cell 1,64,1 failed: can't get near 1,64,1: no dry spot with a face beside it in view (no path)",
        @"const a = story.during('mine', () => 'ran'); const b = story.during('gone', () => 'ran'); return a + ' ' + b.cancelled",
        @"can't place at 4,64,1: its nearest support face is 5.2 blocks from my eyes at 0.5,64,3.5 (reach 4.5) and I couldn't get closer",
        @"return story.next()",
        @"toss{'PoofimaFox', 'log', 5}
give{'PoofimaFox', 'planks', 2}
local ok = skill('toss', {player='PoofimaFox', item='log'})
return 'gave'",
        @"local ok, m = toss{'PoofimaFox', 'log', 5}
return tostring(ok) .. m",
        @"(give_items: player too far)",
        @"return toss{'PoofimaFox', 'log', 5}",
        @"toss{'PoofimaFox', 'log', 5}",
        @"local ok, m = toss{'PoofimaFox', 'log', 5}
if not ok then drop{'log', 5} end",
        @"local ok, m = toss{'PoofimaFox', 'log', 5}
return 'dropped them instead'",
        @"local ok = toss{'PoofimaFox', 'log', 5}
return true",
        @"ok: script t done (give_items ok",
        @"local ok, m = skill('harvst_cane', {})
return 'r:' .. m",
        @"-- purpose: keep mining the cobble gen patiently, deposit when full
ensure""pickaxe""
for round=1,20 do
  cp""mine""
  local ok,m=harvest_generator{at=""6,64,8"",count=20}
  if inv.free()<3 or inv""cobblestone"">=128 then local d,dm=deposit{at=""14,64,-5"",items={""cobblestone""}} expect(d,dm) end
  wait(3)
  ensure""pickaxe""
end
return ""done 20 rounds""",
        @"// purpose: keep mining the cobble gen patiently, deposit when full
const P = params({ at: '6,64,8', chest: '14,64,-5', rounds: 20, per: 20 })
use(replaceToolWhenBroken('pickaxe'))
ensure('pickaxe')
return `mined ${gather('cobblestone', P.rounds * P.per, { from: 'gen', at: P.at, per: P.per, stash: P.chest }).n} cobble`",
        @"-- dump inventory then fight mobs at night
local ok,m=deposit{""14,64,-5""} log(m)
on(""danger"",function() if me.hp<=4 then go{""8,64,-8""} end end)
for i=1,20 do
 if me.hp<=4 then go{""8,64,-8""} return ""retreated low hp"" end
 local ok,m=skill(""fight"",{}) log(m)
 if inv""rotten_flesh"">0 then eat{} end
 wait(3)
end
return ""patrol done""",
        @"// purpose: dump inventory then fight mobs at night
const P = params({ chest: '14,64,-5', home: '8,64,-8' })
stash('all-but-tools', { to: P.chest })
let fights = 0
patrol([me.pos], () => {
  if (me.hp <= 4) { expect(go(P.home)); return 'stop' }
  const m = hostile(8)
  if (m && ok(skill('fight', {}))) fights++; else wait(3)
}, { rounds: 20, until: () => !night() })
return 'patrol done: ' + fights",
        @"-- wait for west tree farm trees to grow, chop+replant them
local spots={""9,65,0"",""9,65,3"",""13,65,0"",""13,65,3""}
local n=0
upto(20,function()
  for _,s in ipairs(spots) do
    local x,y,z=s:match(""(-?%d+),(-?%d+),(-?%d+)"")
    local b=block(tonumber(x),tonumber(y),tonumber(z))
    if b and b:find(""log"") then local ok,m=chop{s} if ok then n=n+1 end end
  end
  if n>=4 then return false end
  wait(30) return true
end)
if inv""log"">0 then deposit{""14,64,-5"",{""log""}} end
return ""chopped ""..n",
        @"// purpose: wait for west tree farm trees to grow, chop+replant them
const P = params({ spots: ['9,65,0', '9,65,3', '13,65,0', '13,65,3'], chest: '14,64,-5' })
const r = quota(4, left => {
  const done = forEach(P.spots.filter(s => /log/.test(block(s))), s => chop(s), { budget: left })
  if (done.n < left) wait(30)
  return done.n
}, { max: 20, idle: 20 })
stash(['log'], { to: P.chest })
return 'chopped ' + r.n",
        @"(-?%d+),(-?%d+),(-?%d+)",
        @"// purpose: broken
minee('dirt')",
        @", b.Library.Get(",
        @"// purpose: say the params
const P = params({ n: 64, at: 'auto' })
return `n=${P.n} at=${P.at}`",
        @"tuned {""n"":32,""extra"":1}",
        @"not declared by its params(): extra",
        @"// purpose: checkpoint
carry.logs = 9
cp('store')
return 'ok ' + carry.logs",
        @"mine('cobblestone', 2)
say('got the cobble, heading back')",
        @"say('west cell is cobble, east is water')
return 'scanned'",
        @"const a = deposit('all'), b = deposit('stone_axe')
return String(a.ok) + String(b.ok) + ' ' + a.msg + ' | ' + b.msg",
        @"deposit{at?, items?}",
        @"// mine and count patches
for (let i = 1; i <= 40; i++) { wait(0.05); prog(i * 2.5) }
return 'k=' + carry.k",
        @"carry.k = (carry.k || 0) + 1; wait(0.05)",
        @"carry.k = (carry.k || 0) + 1",
        @"throw new Error('stale handler')",
        @", hf.Runtime.Interrupts.Recent().Select(InterruptJsonlWriter.Serialize))}",
        @"name x12: chest@3,64,2 (...) | total 17",
        @"iron: chest 3,64,2 (x12)",
        @" + name, out var f) ? f(args, ct) : Task.FromResult(ActResult.Success($",
        @"// purpose: mine cobble at the gen, store it
ensure('pickaxe'); room(2); const b = inv('cobblestone')
cp('mine'); upto(args.n ?? 16, () => { expect(retry(3, () => gen())); return inv.free() > 0 })
expect(inv('cobblestone') > b, 'mined nothing')
cp('store'); expect(deposit()); return 'stored'",
        @"-- purpose: mine cobble at the gen, store it
ensure""pickaxe"" room(2) local b=inv""cobblestone""
cp""mine"" upto(args.n or 16,function() expect(retry(3,function() return gen{} end)) return inv.free()>0 end)
expect(inv""cobblestone"">b,""mined nothing"")
cp""store"" local ok,m=deposit{} expect(ok,m) return ""stored""",
        @"script at line 3 (bad input: missing 'at'): cp 1/2, 5 acts, 3s",
        @"gen() needs at=",
        @"place('torch')",
        @"place() needs at=",
        @"place({at: '1,64,2'})",
        @"place() needs block=",
        @"dig() needs at=",
        @"const r = place('torch')",
        @"place('torch', null)",
        @"go() needs to=",
        @"take({n: 8})",
        @"take() needs item=",
        @"mine() needs block=",
        @"gen('4,64,6')",
        @"gen(args.at, 1)",
        @"gen({at: '4,64,6', n: 8})",
        @"gen(4, 64, 6)",
        @"place('torch', '1,64,2')",
        @"place('torch', 1, 64, 2)",
        @"place('torch', me.pos)",
        @"place({block: 'torch', at: {x: 1, y: 64, z: 2}})",
        @"dig({x: 1, y: 64, z: 2})",
        @"dig(1, 64, 2)",
        @"const t = '1,64,2'
dig(t)",
        @"const spots = [1, 64, 2]
dig(...spots)",
        @"go('4,64,6')
go('chest')
go({to: args.to})",
        @"chop()
deposit()
deposit('14,64,-5', ['log'])
take('coal', 8)
mine('dirt')",
        @"upto(3, () => gen('4,64,6').ok)",
        @"deposit({})
log('x')",
        @"place{'torch'}",
        @"place{at='1,64,2'}",
        @"gen{'4,64,6'} dig{1,64,2} go{to=args.to} local function gen() return true end gen{}",
        @"lint errors (fix and resubmit): L3 gen() needs at=",
        @"return dig('1,64,1').msg",
        @"// purpose: first draft
const m = require('evil')
return 'x'",
        @"// purpose: mine n cobble at the gen and store it
const P = params({ n: 64, at: 'auto', chest: null })
return `mined ${mineGen({ at: P.at, n: P.n, chest: P.chest })} cobble`",
        @")], StopReasons.EndTurn, new LlmUsage(10, 10, 0, 0), request.Model ?? ",
        @"calls deprecated helpers: mineGen → use gather('cobblestone'",
        @"deprecated helpers (compose the primitives instead",
        @"return 'day ' + waitForDay(1)",
        @"waitForDay() is a deprecated compat helper: use waitFor('day'",
        @"expect(mine('dirt', 2))
return 'dug'",
        @"mine('dirt', 1)",
        @"// purpose: say hi to the log
log('hi')
return 'done'",
        @"local ok, m = retry(3, function() return mine{'dirt', 1} end) return tostring(ok) .. ' ' .. m",
        @"local ok, m = retry(2, function() return mine{'dirt', 1} end) expect(ok, m)",
        @"local n = upto(10, function(i) mine{'dirt', 1} return i < 3 end) return 'passes ' .. n",
        @"return 'passes ' .. upto(4, function() end)",
        @"expect(inv'cobblestone' > 0, 'no cobble to place')
return 'x'",
        @"return expect(1 + 1 == 2, 'math') and 'fine'",
        @"room(3) return 'ok'",
        @"room(2) return 'ok'",
        @"room(1) return 'ok'",
        @"room: need 1 free slots, have 0 (deposit: no chest with room)",
        @"local ok, got = ensure{'pickaxe', 'food'} return got",
        @"ensure: no axe (not in inventory or chests, can't craft)",
        @"while 1 do log('x') end",
        @"pcall(function() mine{'dirt',1} end)",
        @"local f = loadstring('x')",
        @"os.execute('rm')",
        @"coroutine.yield()",
        @"for i = 1, 3 do mine{'dirt', 1} end",
        @"upto(5, function() dig{'1,64,1'} end)",
        @"for i = 1, 3 do say'hi' wait(1) end",
        @"mine{'a',1} mine{'b',1} mine{'c',1} dig{'1,1,1'} dig{'1,1,2'} place{'torch','1,1,1'} go{'1,1,1'}",
        @"while true do local ok, m = mine{'dirt', 1} expect(ok, m) end",
        @"local function step() wait(1) end
while true do step() end",
        @"while inv'log' < 32 do local ok, m = chop{} if not ok then fail(m) end end",
        @"for i = 1, 4 do local ok = mine{'dirt', 1} if not ok then break end end",
        @"while (true) { const x = inv('log') }",
        @"lint errors (fix and resubmit): L1 unbounded while loop",
        @"for (let i = 1; i <= 2; i++) mine('dirt', 1)
return 'x'",
        @"lint: L1 mine() result ignored",
        @"// purpose: flaky job
if (args.boom) fail('boom at 3,64,2')
expect(mine('dirt', 1))
return 'ok'",
        @"expect(mine('dirt', 1))
return 'ok'",
        @"flaky v1 (boom) 67%/3",
        @"// purpose: cobble
expect(mine('cobblestone', 2))
return 'v1'",
        @"// purpose: cobble
expect(mine('cobblestone', args.n ?? 3))
return 'v2'",
        @"ok: saved cobble_loop v2 args(n); v1 stays callable as cobble_loop@v1",
        @"ok: cobble_loop unchanged (v2)",
        @"cobble_loop v2 (n)",
        @"// purpose: mine cobble at the gen
upto(16, () => { expect(mine('cobblestone', 1)) })
expect(deposit())",
        @"// purpose: mine cobble at the gen
upto(32, () => { expect(mine('cobblestone', 2)) })
expect(deposit())",
        @"// purpose: mine cobble at the gen
upto(8, () => { expect(mine('cobblestone', 1)) })
expect(deposit())",
        @"expect(craft('torch', 4))",
        @"expect(mine('dirt', 1))
return 'good'",
        @"expect(mine('dirt', 1))
fail('v2 is broken')
return 'bad'",
        @"auto-reverted job v2 (0%/3) -> v1 (100%/3)",
        @"ok: job v1 active again (was v2; v2 kept)",
        @"expect(mine('dirt', 1))
return 'same'",
        @"flagged for archive (unused 14d): a_job",
        @"ok: script hello done (hi from js",
        @"// purpose: wait a while
wait(30)
return 'done'",
        // Hand-written edge cases (Lua long brackets, repeat/until false, os.*, keyed tables, varargs, negative numbers).
        @"--[[ purpose: a long comment
spanning lines ]] local s = [==[ a ]] long
string ]==] repeat local x = inv""dirt"" until false
os.execute(""x"") os.time() local t = {[""at""] = ""1,2,3"", n = 4} place(t)
dig{x=1,y=64,z=2} dig{1,64,2} place(""stone"", 1, 64, -2) place(""stone"", nil) goto(...)
craft(f(x)) craft{item=nil} withdraw{n=3} mine'stone' mine{""cobblestone"",16} gen() chop_tree() pcall(x) debug.traceback()
while 1 do say""hi"" mine""dirt"" end for i=1,3 do local ok = dig{'1,2,3'} dig'1,2,3' end",
        @"-- job: dig a shaft
local function f(a, ...) return a end
function g() return dig{} end
local h = function() return 1 end
upto(5, function() mine""dirt"" place{""dirt"", '1,2,3'} return true end)
repeat wait(1) until inv""dirt"" > 3
while true do if inv.free() == 0 then break end end
do local x = (1 + 2) end
if x then craft""plank"" elseif y then craft(""plank"", -1) else craft{""a"";""b""} end",
        @"const t = { at: '1,2,3', n: 4 }; place(t); place('stone', 1, 64, -2); place('stone', null); goto(...args)
dig({ x: 1, y: 64, z: 2 }); dig({}); dig({ ...t }); dig(`1,2,${3}`); dig(cond ? a : b); withdraw({ n: 3 }); craftItem('x')
harvestGenerator({ count: 3 }); harvest_generator(undefined); while (1) { mine('dirt') } do { wait(1) } while ('x')
for (;;) { if (inv.free()) break } for (const k in t) mine('stone'); for (const k of []) { say('x') }
class A extends B { static x = 1; [k] = 2 } label: for (;;) { continue label } debugger; import('x'); new Promise(r => r())
gen = 3; const { a, b: [c, ...d] = [], [k]: e = 2 } = t; try { x() } catch ({ message }) { log(message) }",
    ];
}
