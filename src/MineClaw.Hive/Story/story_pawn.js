// purpose: Story Mode pawn: next job from the colony director, do it, report (no model, ever)
onInterrupt('*', () => 'resume')
const each = (list, f) => { for (const s of list || []) { const r = f(s); if (!ok(r)) return r } return { ok: true } }
// Walk there first (out of view reads as air).
const reach = (a, d) => !a || dist(pos(a)) <= d + 2 ? { ok: true } : go(a, d)
// THAT tree; some logs down: planks, once more at the lowest log left; the rest is partial.
function chopTree(j) {
  const wood = j.wood || ['log'], held = () => wood.reduce((n, w) => n + inv(w), 0)
  let r = reach(j.at, 3)
  if (!ok(r)) return r
  let got = held()
  r = chop(j.at + ' only')
  got = held() - got
  if (ok(r) || got <= 0) return r
  if (j.planks && inv(j.planks) < 4) craft(j.planks, 4)
  const logs = j.wood_blocks || wood, rest = (j.logs || []).find(p => logs.includes(block(p)))
  if (rest) { const m = held(); r = chop(rest + ' only'); if (ok(r)) return r; got += Math.max(0, held() - m) }
  return { ok: true, partial: true, msg: 'partial: ' + got + ' log(s) taken; ' + (r?.msg || 'the rest is out of reach') }
}
// Bed at night, else rest by it.
function sleepAt(j) {
  const there = j.bed ? reach(j.bed, 2) : j.at ? go(j.at, j.dist ?? 2) : { ok: true }
  if (j.bed && ok(there) && night()) use_block(j.bed)
  return wait(j.s || 5)
}
// Step off first (j.stand); still there after an ok dig: once more.
function digAt(j) {
  if (j.stand) go(j.stand, 0)
  const was = block(j.at), r = dig(j.at)
  if (!ok(r) || was == 'air' || block(j.at) != was) return r
  wait(1)
  return block(j.at) == was ? dig(j.at) : r
}
// Armour off or on, then what to hold.
function gear(j) {
  let r = j.unwear ? equip('all', 'unwear') : { ok: true }
  if (ok(r)) r = each(j.wear, a => equip(a, 'wear'))
  return ok(r) && j.hold ? equip(j.hold, 'hold', '', j.hold_ench || '') : r
}
function work(j) {
  const a = j.at
  let t = /^(dig|harvest|till)$/.test(j.kind) ? reach(a, 3) : 0
  if (t && !ok(t)) return t
  switch (j.kind) {
    case 'go': return go(a, j.dist || 1)
    case 'chop': return chopTree(j)
    case 'dig': return digAt(j)
    case 'harvest':
      t = dig(a)
      if (ok(t) && j.replant && inv(j.replant) > 0) place(j.replant, a)
      return t
    case 'till': skill('hold_item', j.hoe); return use(a)
    case 'collect': t = go(a, 1); { const r = collect(j.r || 4); return ok(r) || ok(t) ? r : t }
    case 'place': if (j.clear) dig(a); if (j.stand) go(j.stand, 0); return place(j.block, a)
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
    case 'hold': if (a) go(a, j.dist ?? 2); return wait(j.s || 5)
    default: return { ok: true }
  }
}
function pawn() {
  for (let i = 0; i < 300; i++) {
    const j = story.next(20)
    if (!j) continue
    if (j.kind === 'end') { story.report(j.id, 'done'); return 'released: ' + (j.label || 'colony') }
    let r = each(j.fetch, f => f.ench ? equip(f.item, 'take', f.from, f.ench) : take(f.item, f.n, f.from))
    if (ok(r)) r = each(j.craft, c => { if (c.at) { const g = go(c.at, 2); if (!ok(g)) return g } return craft(c.item, c.n) })
    // the material's tool in hand: never a lost drop
    if (ok(r) && j.tool) r = equip(j.tool, 'hold', '', j.tool_ench || '')
    if (ok(r)) r = work(j)
    if (ok(r)) r = each(j.stash, s => deposit(s.to, s.items))
    story.report(j.id, ok(r) ? 'done' : 'failed', r?.msg)
  }
  return 'shift over'
}
function main() { return pawn() }
