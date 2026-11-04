// module: story | colony pawns
(H, def) => {
  const id = j => (j && typeof j === 'object' ? j.id : j);
  const report = (j, status, note, pct) => H.storyReport(String(id(j)), status, note == null ? null : String(note), pct);
  const call = (name, ...a) => globalThis[name](...a);
  const good = r => !(r === false || r === null || (typeof r === 'object' && r.ok === false));
  const why = r => String((r && r.msg) || 'failed');
  const eyes = c => {
    const [x, y, z] = String(c).split(',').map(Number), p = H.me('pos');
    return Math.hypot(x + 0.5 - p.x, y + 0.5 - p.y - 1.62, z + 0.5 - p.z);
  };
  const FAR = /from my eyes|couldn't get closer|not visible|in reach|can't reach/i, IN_WAY = /standing in it|in the way/i;
  // A job's fetch list: a busy chest is waited for (2, 4 s) and tried again; a build batch goes on with what it already carries.
  const fetch = j => {
    let got = false;
    for (const f of j.fetch || []) {
      let r;
      for (let i = 0; i < 3; i++) {
        r = call('take', f.item, f.n, f.from);
        if (good(r) || !/busy/i.test(why(r))) break;
        H.wait(2 + 2 * i);
      }
      if (good(r)) got = true;
      else if (!j.cells || !(H.inv(j.block) > 0)) return r;
    }
    if (got && j.cells) report(j, 'progress', 'fetched');
    return { ok: true };
  };
  // A build job's cells in order from its stand: a cell already built counts as done; out of reach (or standing in it) -> step
  // closer (or back to the stand) and try once more; each cell is reported; out of items, the rest go back to the queue.
  const place = j => {
    const cells = j.cells || [j.at], clear = Array.isArray(j.clear) ? j.clear : j.clear ? [j.at] : [];
    if (j.stand && eyes(j.stand) > 1.8) call('go', j.stand, 0);
    let n = 0;
    for (const c of cells) {
      const built = () => j.want && H.block(c) == j.want;
      if (!clear.includes(c) && built()) { if (j.cells) report(j, 'progress', 'cell ' + c + ' done'); n++; continue; }
      if (j.cells && !(H.inv(j.block) > 0)) break;
      if (clear.includes(c)) call('dig', c);
      if (eyes(c) > 4.4) call('go', c, 2);
      let r = call('place', j.block, c);
      if (!good(r) && built()) r = { ok: true };
      else if (!good(r) && (FAR.test(why(r)) || IN_WAY.test(why(r)))) {
        if (IN_WAY.test(why(r)) && j.stand) call('go', j.stand, 0); else call('go', c, 2);
        r = call('place', j.block, c);
      }
      if (!j.cells) return r;
      report(j, 'progress', 'cell ' + c + (good(r) ? ' done' : ' failed: ' + why(r)));
      if (good(r)) n++;
    }
    return { ok: true, msg: 'batch: ' + n + '/' + cells.length + ' placed' };
  };
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
  /** story.next(s)|report(job, done|failed|progress, note?)|during(job, fn)|idle/fetch/place(job) - job loop */
  def('story', Object.freeze({
    next: s => H.storyNext(s == null ? 0 : s),
    report,
    during,
    idle,
    till,
    fetch,
    place,
  }));
}
