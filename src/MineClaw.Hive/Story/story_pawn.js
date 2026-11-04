// purpose: Story Mode pawn: ask the colony director for the next job, do it with the usual primitives, report (no model, ever)
onInterrupt('*', () => 'resume')
const each = (list, f) => {
  for (const s of list || []) { const r = f(s); if (!ok(r)) return r }
  return { ok: true }
}
// Walk over first: a block out of view reads as air.
function reach(a, d) {
  if (!a || dist(pos(a)) <= d + 2) return { ok: true }
  return go(a, d)
}
// THAT tree (" only"); some logs down: planks (clutch/pillar), once more at the lowest log left; the rest is partial.
function chopTree(j) {
  const wood = j.wood || ['log']
  const held = () => wood.reduce((n, w) => n + inv(w), 0)
  const there = reach(j.at, 3)
  if (!ok(there)) return there
  const before = held()
  let r = chop(j.at + ' only')
  let got = held() - before
  if (ok(r) || got <= 0) return r
  if (j.planks && inv(j.planks) < 4) craft(j.planks, 4)
  const logs = j.wood_blocks || wood
  const rest = (j.logs || []).find(p => logs.includes(block(p)))
  if (rest) {
    const mark = held()
    const r2 = chop(rest + ' only')
    if (ok(r2)) return r2
    got += Math.max(0, held() - mark)
    r = r2
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
  const was = block(j.at), r = dig(j.at)
  if (!ok(r) || was == 'air' || block(j.at) != was) return r
  wait(1)
  return block(j.at) == was ? dig(j.at) : r
}
// Gear: armour off (to store it) or on, then the item to hold (a weapon for the draft, the old item back after it).
function gear(j) {
  let r = { ok: true }
  if (j.unwear) r = equip('all', 'unwear')
  if (ok(r)) r = each(j.wear, a => equip(a, 'wear'))
  if (ok(r) && j.hold) r = equip(j.hold, 'hold', null, j.hold_ench)
  return r
}
function work(j) {
  const a = j.at
  switch (j.kind) {
    case 'go': return go(a, j.dist || 1)
    case 'chop': return chopTree(j)
    case 'dig': {
      const there = reach(a, 3)
      return ok(there) ? digAt(j) : there
    }
    case 'harvest': {
      const there = reach(a, 3)
      if (!ok(there)) return there
      const r = dig(a)
      if (ok(r) && j.replant && inv(j.replant) > 0) place(j.replant, a)
      return r
    }
    case 'till': {
      const there = reach(a, 3)
      if (!ok(there)) return there
      skill('hold_item', j.hoe)
      return use(a)
    }
    case 'collect': {
      const there = go(a, 1)
      const r = collect(j.r || 4)
      return ok(r) || ok(there) ? r : there
    }
    case 'place':
      if (j.clear) dig(a)
      if (j.stand) go(j.stand, 0)
      return place(j.block, a)
    // The exact entity the order named (it is followed if it moves); a type name for old jobs.
    case 'attack': if (a) go(a, 3); return attack(j.target != null ? String(j.target) : j.mob)
    case 'equip': return gear(j)
    case 'eat': return eat()
    case 'craft': if (a) go(a, 2); return craft(j.item, j.n)
    case 'smelt': return smelt(j.item, j.n, j.fuel, false, a)
    case 'collect_furnace': return collect_furnace(a)
    case 'give': return handTo(j.to, j.item, j.n, { gather: false })
    case 'blueprint': return build(j.design)
    case 'sleep': return sleepAt(j)
    case 'idle': return story.idle(j)
    case 'hold':
      if (a) go(a, j.dist ?? 2)
      return wait(j.s || 5)
    default: return { ok: true }
  }
}
function pawn() {
  for (let i = 0; i < 300; i++) {
    const j = story.next(20)
    if (!j) continue
    if (j.kind === 'end') { story.report(j.id, 'done'); return 'released: ' + (j.label || 'colony') }
    // A tool or weapon is taken exactly (the enchanted one, or a plain one: f.ench); the rest by name.
    let r = each(j.fetch, f => f.ench ? equip(f.item, 'take', f.from, f.ench) : take(f.item, f.n, f.from))
    // Crafts first (a bench the colony places for a build: craft it, at the table when it needs one, then place it).
    if (ok(r)) r = each(j.craft, c => { if (c.at) { const g = go(c.at, 2); if (!ok(g)) return g } return craft(c.item, c.n) })
    // The tool the job's material takes in hand (the dig keeps it: never a lost drop, the Fortune pick for marked ores).
    if (ok(r) && j.tool) r = equip(j.tool, 'hold', null, j.tool_ench)
    if (ok(r)) r = work(j)
    if (ok(r)) r = each(j.stash, s => deposit(s.to, s.items))
    story.report(j.id, ok(r) ? 'done' : 'failed', r && r.msg)
  }
  return 'shift over'
}
function main() { return pawn() }
