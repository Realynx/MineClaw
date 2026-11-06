// purpose: Story Mode pawn: ask the colony director for the next job, do it with the usual primitives, report (no model, ever)
onInterrupt('*', () => 'resume')
const each = (list, f) => {
  for (const s of list || []) { const r = f(s); if (!ok(r)) return r }
  return { ok: true }
}
// Walk over first: a block out of view reads as air.
function reach(a, d) {
  if (!a || dist(pos(a)) <= d + 2) return { ok: true }
  return story.go(a, d)
}
// THAT tree (" only"); some down: planks, once more at the lowest log; else partial.
function chopTree(j) {
  const wood = j.wood || ['log']
  const held = () => wood.reduce((n, w) => n + inv(w), 0)
  const there = reach(j.at, 3)
  if (!ok(there)) return there
  story.ready(j)
  const before = held()
  const r = chop(j.at + ' only'), got = held() - before
  if (ok(r) || got <= 0) return r
  if (j.planks && inv(j.planks) < 4) craft(j.planks, 4)
  return chopRest(j, wood, held, r, got)
}
function chopRest(j, wood, held, r, got) {
  const logs = j.wood_blocks || wood
  const rest = (j.logs || []).find(p => logs.includes(block(p)))
  if (rest) {
    const mark = held()
    r = chop(rest + ' only')
    if (ok(r)) return r
    got += Math.max(0, held() - mark)
  }
  return { ok: true, partial: true, msg: 'partial: ' + got + ' log(s) taken; ' + ((r && r.msg) || 'the rest is out of reach') }
}
// Bed at night (refused by day), else rest by it; no bed: its own spot.
function sleepAt(j) {
  const there = j.bed ? reach(j.bed, 2) : j.at ? go(j.at, j.dist ?? 2) : { ok: true }
  if (j.bed && ok(there) && night()) use_block(j.bed)
  return wait(j.s || 5)
}
// j.stand: it stands on its target, step off first. A block still there after an ok dig gets one more go.
function digAt(j) {
  if (j.stand) go(j.stand, 0)
  const was = block(j.at), r = story.dig(j.at, j)
  if (!ok(r) || was == 'air' || block(j.at) != was) return r
  wait(1)
  return block(j.at) == was ? dig(j.at) : r
}
function harvestAt(j, a) {
  const there = reach(a, 3)
  if (!ok(there)) return there
  const r = story.dig(a, j)
  if (ok(r) && j.replant && inv(j.replant) > 0) place(j.replant, a)
  return r
}
// Each job kind's action (a: j.at); any other kind: story.work.
const acts = new Map(Object.entries({
  go: (j, a) => story.go(a, j.dist || 1),
  chop: chopTree,
  dig: (j, a) => { const t = reach(a, 3); return ok(t) ? digAt(j) : t },
  harvest: harvestAt,
  till: j => story.till(j),
  collect: (j, a) => { const t = go(a, 1), r = collect(j.r || 4); return ok(r) || ok(t) ? r : t },
  place: j => story.place(j),
  attack: (j, a) => { if (a) go(a, 3); return attack('' + (j.target ?? j.mob)) },
  eat: () => eat(),
  craft: (j, a) => { if (a) go(a, 2); return craft(j.item, j.n) },
  smelt: (j, a) => smelt(j.item, j.n, j.fuel, false, a),
  collect_furnace: (j, a) => collect_furnace(a),
  give: j => handTo(j.to, j.item, j.n, { gather: false }),
  blueprint: j => build(j.design),
  fish: (j, a) => { const t = go(a, 0); return ok(t) ? fish(j.water, j.s, j.max) : t },
  sleep: sleepAt,
  idle: j => story.idle(j),
  hold: (j, a) => { if (a) go(a, j.dist ?? 2); return wait(j.s || 5) },
}))
function work(j) {
  const f = acts.get(j.kind)
  return f ? f(j, j.at) : story.work(j)
}
// Fetch, crafts first (a bench a build needs; at the table if need be), the work, then stash.
function runJob(j) {
  let r = story.fetch(j)
  if (ok(r)) r = each(j.craft, c => { if (c.at) { const g = go(c.at, 2); if (!ok(g)) return g } return craft(c.item, c.n) })
  if (ok(r)) r = work(j)
  if (ok(r) && j.stash) r = each(j.stash, s => deposit(s.to, s.items))
  return r
}
function pawn() {
  for (let i = 0; i < 300; i++) {
    const j = story.next(20)
    if (!j) continue
    if (j.kind === 'end') { story.report(j.id, 'done'); return 'released: ' + (j.label || 'colony') }
    const r = runJob(j)
    story.sweep()
    story.report(j.id, ok(r) ? 'done' : 'failed', r && r.msg)
  }
  return 'shift over'
}
function main() { return pawn() }
