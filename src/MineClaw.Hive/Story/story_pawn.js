// purpose: Story Mode pawn: ask the colony director for the next job, do it with the usual primitives, report (no model, ever)
onInterrupt('*', () => 'resume')
const each = (list, f) => {
  for (const s of list || []) { const r = f(s); if (!ok(r)) return r }
  return { ok: true }
}
// Walk over first when the target is far: a block out of this body's view reads as air, and a tool acting on it then does nothing.
function reach(a, d) {
  if (!a || dist(pos(a)) <= d + 2) return { ok: true }
  return go(a, d)
}
// A tree from an empty inventory: chop_tree fells what it can reach of THAT tree (" only": never another one near it; the director
// spread the pawns over the trees). When some logs came down but not all, make planks of them (2x2 grid: clutch and pillar blocks for
// work up high) and go once more at the lowest log still standing. Whatever is left out of reach after that is a partial job: done,
// saying so (the director tells the colony).
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
// Sleep at the bed (only at night: the server refuses a bed by day), else rest by it; no bed: on its own spot.
function sleepAt(j) {
  const there = j.bed ? reach(j.bed, 2) : j.at ? go(j.at, j.dist ?? 2) : { ok: true }
  if (j.bed && ok(there) && night()) use_block(j.bed)
  return wait(j.s || 5)
}
function work(j) {
  const a = j.at
  switch (j.kind) {
    case 'go': return go(a, j.dist || 1)
    case 'chop': return chopTree(j)
    case 'dig': {
      const there = reach(a, 3)
      return ok(there) ? dig(a) : there
    }
    case 'harvest': {
      const there = reach(a, 3)
      if (!ok(there)) return there
      const r = dig(a)
      if (ok(r) && j.replant && inv(j.replant) > 0) place(j.replant, a)
      return r
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
    case 'attack': if (a) go(a, 3); return attack(j.mob)
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
    let r = each(j.fetch, f => take(f.item, f.n, f.from))
    if (ok(r)) r = work(j)
    if (ok(r)) r = each(j.stash, s => deposit(s.to, s.items))
    story.report(j.id, ok(r) ? 'done' : 'failed', r && r.msg)
  }
  return 'shift over'
}
function main() { return pawn() }
