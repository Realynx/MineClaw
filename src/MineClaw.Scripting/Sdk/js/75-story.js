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
      default: return run(j); // escape, dump (below)
    }
  };
  // Gear (Story Mode's tools and drafting): the hidden equip(item, mode, from, ench); never a null argument (its schema wants strings).
  const equip = (item, mode, from, ench, keep) => mode === 'store'
    ? globalThis.equip(item, mode, from || '', ench || '', keep || 0) : globalThis.equip(item, mode, from || '', ench || '');
  // gear(job): armour off (j.unwear) or on (j.wear), then what to hold (j.hold, j.hold_ench).
  const gear = j => {
    const g = globalThis;
    let r = j.unwear ? equip('all', 'unwear') : { ok: true };
    for (const a of j.wear || []) { if (!g.ok(r)) break; r = equip(a, 'wear'); }
    return g.ok(r) && j.hold ? equip(j.hold, 'hold', '', j.hold_ench) : r;
  };
  // fetch(job): the job's items out of their chests (a tool, weapon or armour piece exactly: f.ench, the enchanted one or a plain
  // one; the rest by name), its extra gear into a chest (j.store: {to, item, keep}: the `keep` freshest stay; a pawn carries what its
  // policy needs, the rest is colony stock), then the tool its material takes in hand (j.tool, j.tool_ench: never a lost drop).
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
    for (const s of j.store || []) {
      const r = equip(s.item, 'store', s.to, '', s.keep);
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
  // escape(toward?): out of a pit, a cave or water by itself (H.storyPit says where it is and the next move): walk to the exit when
  // there is one, else dig a staircase (never down, never beside liquid or under sand/gravel), else nerd-pole with blocks it carries,
  // else dig the walls for blocks and pole. Nothing works: the colony is asked for help (a pawn brings blocks, or a teleport home).
  // -> {ok, out: it moved out, stuck: still stuck, msg}
  // climb (the colony found no way home from here: a ravine, a canyon): whatever its shape, walk to the wall nearest home
  // (s.approach) and cut a staircase up it toward home until level with home; the caller asks for help when it can't.
  const escape = (toward, climb) => {
    const g = globalThis, tried = {}, ty = toward == null ? null : +String(toward).split(',')[1];
    let s = null, why = '', moved = false;
    for (let i = 0; i < (climb ? 64 : 40); i++) {
      s = H.storyPit(toward == null ? null : String(toward), !!climb);
      if (climb && ty != null && s.feet_y >= ty - 1) return { ok: true, out: true, msg: 'climbed up to ' + s.feet };
      if (!s.pit) return { ok: true, out: moved, msg: moved ? 'got out' : 'not stuck' };
      if (s.approach && !tried['a' + s.approach]) {
        tried['a' + s.approach] = 1;
        const r = g.go(s.approach, 0);
        if (g.ok(r)) { moved = true; continue; }
        why = r.msg;
      }
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
    const where = (s.water ? 'in water' : climb ? 'below home' : s.cave ? 'in a cave' : 'in a pit') + ' at ' + s.feet;
    if (climb) return { ok: false, stuck: true, msg: "couldn't climb out " + where + (why ? ' (' + why + ')' : '') };
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
    if (e.out) return walk();
    // A target high above (a crown log, a ledge, a ceiling): stand below it; story.dig pillars up from there.
    const t = typeof at === 'string' ? at.split(',').map(Number) : null;
    if (t && t.length === 3 && t[1] > H.me('pos').y + 2) {
      const s = H.storyReach(at);
      if (s.within) return { ok: true, msg: 'in reach' };
      if (s.stand) { const b = g.go(s.stand, 0); if (g.ok(b)) return { ok: true, msg: 'below it at ' + s.stand }; }
    }
    return r;
  };
  const REACH = /can't reach|out of reach|from my eyes|no walkable spot/i;
  const feet = () => { const p = H.me('pos'); return Math.floor(p.x) + ',' + Math.floor(p.y + 1e-6) + ',' + Math.floor(p.z); };
  // reachUp(at, fn, job?): a block above reach. Walk to the column H.storyReach picks (under it or beside it), nerd-pole up (one
  // place at its own feet per step) until it is in reach, do fn, then dig the pillar back down from the top (every block it placed,
  // collected again). Short of blocks: the job's pillar stack from storage (j.pillar), else dirt dug around the column.
  const reachUp = (at, fn, j) => {
    const g = globalThis, scan = () => H.storyReach(String(at));
    let s = scan();
    if (s.within) return fn();
    if (!s.stand) return { ok: false, msg: 'out of reach even from a pillar: ' + s.why };
    if (s.blocks < s.steps && j && j.pillar) g.take(j.pillar.item, j.pillar.n, j.pillar.from);
    s = scan();
    for (const c of s.gather || []) {
      if (s.blocks >= s.steps) break;
      g.dig(c);
      s = scan();
    }
    if (!s.stand) return { ok: false, msg: 'out of reach even from a pillar: ' + s.why };
    if (s.blocks < s.steps) return { ok: false, msg: 'out of reach: ' + s.steps + ' block(s) to pillar up, ' + s.blocks + ' on me' };
    const w = g.go(s.stand, 0);
    if (!g.ok(w)) return { ok: false, msg: 'out of reach: ' + w.msg };
    const placed = [];
    for (let i = 0; i < s.steps + 2; i++) {
      const now = scan();
      if (now.within || !now.block) break;
      const f = feet(), p = g.place(now.block, f);
      if (!g.ok(p)) break;
      placed.push(f);
    }
    const r = fn();
    for (let i = placed.length - 1; i >= 0; i--) if (!g.ok(g.dig(placed[i]))) break;
    return r;
  };
  // dig(at, job?): the usual dig; a block out of reach is reached from a pillar (reachUp), never just given up on.
  const dig = (at, j) => {
    const g = globalThis, r = g.dig(at);
    if (g.ok(r) || !REACH.test(r.msg || '')) return r;
    return reachUp(at, () => g.dig(at), j);
  };
  // ready(job): a job up high (a tall tree) starts with blocks to pillar with: the job's pillar stack from storage when short.
  const ready = j => {
    const g = globalThis;
    if (j && j.pillar && g.inv(j.pillar.item) < 4) g.take(j.pillar.item, j.pillar.n, j.pillar.from);
    return { ok: true };
  };
  // run(job): the colony's own kinds (story.work hands them over). escape: the colony saw it stuck (a pit, a cave, a run of
  // unreachable targets): out by itself, else to j.at (home); help is asked for when nothing works. dump: no storage takes these:
  // carry them to the drop spot and put them down.
  const run = j => {
    const g = globalThis;
    switch (j.kind) {
      case 'escape': {
        // Done only once home is reached by walking (the colony then knows it is out); a pit, a cave or water first.
        let e = escape(j.at);
        if (!g.ok(e)) { g.wait(j.s || 8); return e; }
        if (!j.at) return e;
        let r = g.go(j.at, j.dist ?? 2);
        if (g.ok(r) || r.cancelled) return r;
        // No walkable way home (a ravine, a canyon, a deep hole): climb toward home, then walk again.
        report(j, 'progress', 'no way home (' + r.msg + '): trying a staircase toward home');
        e = escape(j.at, true);
        if (g.ok(e)) {
          r = g.go(j.at, j.dist ?? 2);
          if (g.ok(r) || r.cancelled) return r;
        }
        const help = H.storyStuck('no way home: ' + (g.ok(e) ? r.msg : e.msg));
        g.wait(j.s || 8);
        return { ok: false, stuck: true, msg: 'no way home (' + (g.ok(e) ? r.msg : e.msg) + '): ' + help };
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
  /** story.next(s)|report(job, status, note?)|during|idle|work|fetch|go|dig|escape - the job loop */
  def('story', Object.freeze({
    next: s => H.storyNext(s == null ? 0 : s),
    report,
    during,
    idle,
    go,
    escape,
    dig,
    ready,
    work,
    till,
    fetch,
    place,
  }));
}
