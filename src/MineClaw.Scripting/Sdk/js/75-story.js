// module: story | colony pawns
(H, def) => {
  const id = j => (j && typeof j === 'object' ? j.id : j);
  // during(job, fn): fn while the job is still this pawn's; cut at once when the director takes it back -> {ok:false, cancelled:true}.
  const during = (j, f) => H.storyDuring(String(id(j)), f);
  // idle(job): an idle leg (a stroll or a pastime: j.act). Walk to the director's cell (this pawn's alone), then linger looking at
  // each of j.look in turn (another pawn, the stars, the sunset, a chair); cut the moment work comes up. Someone already standing
  // there (met on the way): move on, the director picks another cell.
  const idle = j => during(j, () => {
    const g = globalThis;
    if (j.at) {
      const r = g.go(j.at, j.dist ?? 2);
      if (j.wander && !g.ok(r)) return r;
      if (j.wander && g.find('player', { r: 1.5, limit: 2 }).length > 0) return { ok: true, msg: 'crowded' };
    }
    const look = j.look || [], s = j.s || 5;
    if (!look.length || typeof g.face !== 'function') return g.wait(s);
    for (const p of look) {
      g.face(p);
      g.wait(Math.max(1, s / look.length));
    }
    return { ok: true, msg: j.act || 'idle' };
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
      case 'equip': return gear(j);
      default: return { ok: true };
    }
  };
  // Gear (Story Mode's tools and drafting): the hidden equip(item, mode, from, ench); never a null argument (its schema wants strings).
  const equip = (item, mode, from, ench) => globalThis.equip(item, mode, from || '', ench || '');
  // gear(job): armour off (j.unwear) or on (j.wear), then what to hold (j.hold, j.hold_ench).
  const gear = j => {
    const g = globalThis;
    let r = j.unwear ? equip('all', 'unwear') : { ok: true };
    for (const a of j.wear || []) { if (!g.ok(r)) break; r = equip(a, 'wear'); }
    return g.ok(r) && j.hold ? equip(j.hold, 'hold', '', j.hold_ench) : r;
  };
  // fetch(job): the job's items out of their chests (a tool, weapon or armour piece exactly: f.ench, the enchanted one or a plain
  // one; the rest by name), then the tool its material takes in hand (j.tool, j.tool_ench: the dig keeps it, never a lost drop).
  const fetch = j => {
    const g = globalThis;
    for (const f of j.fetch || []) {
      const r = f.ench ? equip(f.item, 'take', f.from, f.ench) : g.take(f.item, f.n, f.from);
      if (!g.ok(r)) return r;
    }
    return j.tool ? equip(j.tool, 'hold', '', j.tool_ench) : { ok: true };
  };
  // till(job): hold the hoe (j.hoe) and use it on the dirt or grass at j.at, walking over first when it is far.
  const till = j => {
    const g = globalThis;
    if (g.dist(g.pos(j.at)) > 5) {
      const r = g.go(j.at, 3);
      if (!g.ok(r)) return r;
    }
    g.skill('hold_item', j.hoe);
    return g.use_block(j.at);
  };
  /** story.next(s)|report(job, done|failed|progress, note?)|during(job, fn)|idle(job)|work(job)|fetch(job) */
  def('story', Object.freeze({
    next: s => H.storyNext(s == null ? 0 : s),
    report: (j, status, note, pct) => H.storyReport(String(id(j)), status, note == null ? null : String(note), pct),
    during,
    idle,
    work,
    till,
    fetch,
  }));
}
