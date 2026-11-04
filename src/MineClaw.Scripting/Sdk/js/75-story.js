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
  // run(job): the job kinds story_pawn hands over (its own switch is full). escape: the colony saw it stuck (a pit, a cave, a run of
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
  /** story.next(s)|report(job, status, note?)|during(job, fn)|idle|go(at, d)|escape() - the job loop */
  def('story', Object.freeze({
    next: s => H.storyNext(s == null ? 0 : s),
    report: (j, status, note, pct) => H.storyReport(String(id(j)), status, note == null ? null : String(note), pct),
    during,
    idle,
    go,
    escape,
    run,
  }));
}
