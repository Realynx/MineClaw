// module: story | colony pawns
(H, def) => {
  const id = j => (j && typeof j === 'object' ? j.id : j);
  // during(job, fn): fn while the job is still this pawn's; cut at once when the director takes it back -> {ok:false, cancelled:true}.
  const during = (j, f) => H.storyDuring(String(id(j)), f);
  // idle(job): an idle leg. Stroll to the director's cell (this pawn's alone) and linger; cut the moment work comes up. Someone
  // already standing there (met on the way): move on, the director picks another cell.
  const idle = j => during(j, () => {
    const g = globalThis;
    if (j.at) {
      const r = g.go(j.at, j.dist ?? 2);
      if (j.wander && !g.ok(r)) return r;
      if (j.wander && g.find('player', { r: 1.5, limit: 2 }).length > 0) return { ok: true, msg: 'crowded' };
    }
    return g.wait(j.s || 5);
  });
  // work(job): a station bill the pawn script hands over (1.12.2: brewing stand, enchanting table, anvil). A brew fills its glass
  // bottles first (j.fill: a water source or cauldron); every other kind is nothing to do.
  const work = j => {
    const g = globalThis;
    switch (j.kind) {
      case 'brew': {
        if (j.fill && j.fill.n > 0) {
          const f = H.skill('fill_bottles', { pos: j.fill.at, count: j.fill.n });
          if (!g.ok(f)) return f;
        }
        return H.skill('brew', { potion: j.potion, form: j.form, count: j.n, pos: j.at });
      }
      case 'enchant':
        return H.skill('enchant_item', { item: j.item, option: j.option || 3, prefer: j.prefer || '', prefer_only: !!j.prefer_only, pos: j.at });
      case 'anvil': return H.skill('use_anvil', { item: j.item, mode: j.mode || 'repair', pos: j.at });
      default: return { ok: true };
    }
  };
  /** story.next(s)|report(job, done|failed|progress, note?)|during(job, fn)|idle(job)|work(job) - the job loop */
  def('story', Object.freeze({
    next: s => H.storyNext(s == null ? 0 : s),
    report: (j, status, note, pct) => H.storyReport(String(id(j)), status, note == null ? null : String(note), pct),
    during,
    idle,
    work,
  }));
}
