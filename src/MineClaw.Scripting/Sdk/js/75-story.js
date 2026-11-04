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
  /** story.next(s)|report(job, done|failed|progress, note?)|during(job, fn)|idle(job) - the job loop */
  def('story', Object.freeze({
    next: s => H.storyNext(s == null ? 0 : s),
    report: (j, status, note, pct) => H.storyReport(String(id(j)), status, note == null ? null : String(note), pct),
    during,
    idle,
    till,
  }));
}
