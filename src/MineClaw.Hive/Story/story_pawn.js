// purpose: Story Mode pawn: ask the colony director for the next job, do it with the usual primitives, report (no model, ever)
onInterrupt('*', () => 'resume')
const each = (list, f) => {
  for (const s of list || []) { const r = f(s); if (!ok(r)) return r }
  return { ok: true }
}
function work(j) {
  const a = j.at
  switch (j.kind) {
    case 'go': return go(a, j.dist || 1)
    case 'chop': return chop(a)
    case 'dig': return dig(a)
    case 'harvest': {
      const r = dig(a)
      if (ok(r) && j.replant && inv(j.replant) > 0) place(j.replant, a)
      return r
    }
    case 'collect': go(a, 1); return collect(j.r || 4)
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
    case 'sleep': case 'idle': case 'hold':
      if (a) go(a, j.dist || 2)
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
