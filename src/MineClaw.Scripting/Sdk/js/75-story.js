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
      default: return run(j); // escape, dump (below)
    }
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
  // escape(toward?): out of a pit, a cave or water by itself (H.storyPit says where it is and the next move): walk to the exit when
  // there is one, else dig a staircase (never down, never beside liquid or under sand/gravel), else nerd-pole with blocks it carries,
  // else dig the walls for blocks and pole. Nothing works: the colony is asked for help (a pawn brings blocks, or a teleport home).
  // -> {ok, out: it moved out, stuck: still stuck, msg}
  const escape = toward => {
    const g = globalThis, tried = {};
    let s = null, why = '', moved = false;
    for (let i = 0; i < 40; i++) {
      s = H.storyPit(toward == null ? null : String(toward));
      if (!s.pit) return { ok: true, out: moved, msg: moved ? 'got out' : 'not stuck' };
      if (s.exit && !tried['e' + s.exit]) {
        tried['e' + s.exit] = 1;
        const r = g.go(s.exit, 0);
        if (g.ok(r)) { moved = true; continue; }
        why = r.msg;
      }
      const st = s.stair;
      if (st && !tried[st.to]) {
        tried[st.to] = 1;
        let r = { ok: true };
        for (const c of st.dig) { r = g.dig(c); if (!g.ok(r)) break; }
        if (g.ok(r)) r = g.go(st.to, 0);
        if (g.ok(r)) { moved = true; continue; }
        why = r.msg;
      }
      if (s.pillar && s.blocks > 0 && !tried['p' + s.feet]) {
        tried['p' + s.feet] = 1;
        const r = g.place(s.block, s.feet);
        if (g.ok(r)) { moved = true; continue; }
        why = r.msg;
      }
      const c = s.pillar && s.blocks <= 0 && (s.gather || []).find(x => !tried['g' + x]);
      if (c) {
        tried['g' + c] = 1;
        const r = g.dig(c);
        if (!g.ok(r)) why = r.msg;
        continue;
      }
      break;
    }
    const where = (s.water ? 'in water' : s.cave ? 'in a cave' : 'in a pit') + ' at ' + s.feet;
    const help = H.storyStuck('stuck ' + where + (why ? ' (' + why + ')' : ''));
    return { ok: false, stuck: true, msg: 'stuck ' + where + (why ? ' (' + why + ')' : '') + ': ' + help };
  };
  const NO_WAY = /no path|no route|unreachable|partway|stuck/i;
  // go(at, d, opts?): the usual go; when it finds no way and the pawn is in a pit, cave or water, it gets out first and goes again.
  const go = (at, d, o) => {
    const g = globalThis, walk = () => (o ? g.go(at, d, o) : g.go(at, d));
    const r = walk();
    if (g.ok(r) || r.cancelled || !NO_WAY.test(r.msg || '')) return r;
    const e = escape(at);
    if (!g.ok(e)) return { ok: false, stuck: true, msg: r.msg + '; ' + e.msg };
    return e.out ? walk() : r;
  };
  // run(job): the colony's own kinds (story.work hands them over). escape: the colony saw it stuck (a pit, a cave, a run of
  // unreachable targets): out by itself, else to j.at (home); help is asked for when nothing works. dump: no storage takes these:
  // carry them to the drop spot and put them down.
  const run = j => {
    const g = globalThis;
    switch (j.kind) {
      case 'escape': {
        const e = escape(j.at);
        if (!g.ok(e)) { g.wait(j.s || 8); return e; }
        if (e.out || !j.at) return e;
        const r = g.go(j.at, j.dist ?? 2);
        if (g.ok(r) || r.cancelled) return r;
        return { ok: false, stuck: true, msg: r.msg + ': ' + H.storyStuck('no way home: ' + r.msg) };
      }
      case 'dump': {
        const r = go(j.at, j.dist ?? 1);
        if (!g.ok(r)) return r;
        for (const d of j.drop || []) {
          const x = d.n ? g.drop(d.item, d.n) : g.drop(d.item);
          if (!g.ok(x)) return x;
        }
        return { ok: true, msg: 'dropped ' + (j.drop || []).map(d => d.item).join(', ') };
      }
      default: return { ok: true };
    }
  };
  /** story.next(s)|report(job, status, note?)|during(job, fn)|idle|work|go(at, d)|escape - the job loop */
  def('story', Object.freeze({
    next: s => H.storyNext(s == null ? 0 : s),
    report: (j, status, note, pct) => H.storyReport(String(id(j)), status, note == null ? null : String(note), pct),
    during,
    idle,
    go,
    escape,
    work,
    till,
  }));
}
