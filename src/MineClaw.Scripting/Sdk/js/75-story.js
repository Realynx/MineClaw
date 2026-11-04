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
      if (eyes(c) > 4) call('go', c, 2);
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
  // pour(job): a liquid build's cells (j.liquid water|lava), each a source once done. A cell already holding one is done; with no
  // full bucket the pawn fills its empty ones at j.source first (fill_bucket), then pours from the stand aimed at the cell
  // (empty_bucket / move_lava: the face that lands it in that cell) and checks the world shows a source there. Each cell is reported;
  // no way to fill a bucket ends the batch (the rest go back to the queue).
  const NEAR = /can't get near|no block face|from my eyes|couldn't get closer|can't reach/i;
  const pour = j => {
    const lava = j.liquid === 'lava', liq = lava ? 'lava' : 'water', full = liq + '_bucket', cells = j.cells || [j.at];
    const src = c => H.storyLiquid(c) === liq;
    const fill = () => {
      let r = { ok: false, msg: 'no ' + liq + ' source to fill a bucket from' };
      for (let k = 0; j.source && k < (j.buckets || 1) && H.inv('bucket') > 0; k++) {
        r = H.skill('fill_bucket', { liquid: liq, pos: j.source });
        if (!good(r)) break;
      }
      return H.inv(full) > 0 ? { ok: true } : r;
    };
    const tip = c => (lava ? H.skill('move_lava', { to: c }) : H.skill('empty_bucket', { pos: c }));
    let n = 0;
    for (const c of cells) {
      if (src(c)) { report(j, 'progress', 'cell ' + c + ' done'); n++; continue; }
      if (!(H.inv(full) > 0)) {
        const f = fill();
        if (!good(f)) { if (!n) return f; break; }
      }
      if (j.stand && eyes(j.stand) > 1.8) call('go', j.stand, 0);
      if (eyes(c) > 4) call('go', c, 2);
      let r = tip(c);
      if (!good(r) && NEAR.test(why(r))) { call('go', c, 2); r = tip(c); }
      if (good(r) && !src(c)) {
        H.wait(0.5);
        if (!src(c)) r = { ok: false, msg: c + ' shows ' + (H.storyLiquid(c) || H.block(c)) + ', not a ' + liq + ' source' };
      }
      report(j, 'progress', 'cell ' + c + (good(r) ? ' done' : ' failed: ' + why(r)));
      if (good(r)) n++;
    }
    return { ok: true, msg: 'batch: ' + n + '/' + cells.length + ' poured' };
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
      case 'pour': return pour(j);
      default: return run(j); // escape, dump (below)
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
  // A busy chest is waited for (2, 4 s) and tried again; a build batch goes on with what it already carries and says when its items
  // are out of the chests ("fetched").
  const fetch = j => {
    const g = globalThis;
    let got = false;
    for (const f of j.fetch || []) {
      let r;
      for (let i = 0; i < 3; i++) {
        r = f.ench ? equip(f.item, 'take', f.from, f.ench) : g.take(f.item, f.n, f.from);
        if (g.ok(r) || !/busy/i.test(why(r))) break;
        H.wait(2 + 2 * i);
      }
      if (g.ok(r)) got = true;
      else if (!j.cells || !(H.inv(j.block) > 0)) return r;
    }
    if (got && j.cells) report(j, 'progress', 'fetched');
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
  /** story.next(s)|report(job, status, note?)|during(job, fn)|idle|work|fetch|go(at, d)|escape - the job loop */
  def('story', Object.freeze({
    next: s => H.storyNext(s == null ? 0 : s),
    report,
    during,
    idle,
    go,
    escape,
    work,
    till,
    fetch,
    place,
    pour,
  }));
}
