// module: jobs | primitives every job is built from (any world/version): gather, stash, handTo, makeRoom, patrol; each -> {ok, n, msg}
(H, def) => {
  const isObj = v => v !== null && typeof v === 'object';
  const ok = r => !(r === false || r === null || (isObj(r) && r.ok === false));
  const call = (name, ...a) => (typeof globalThis[name] === 'function' ? globalThis[name](...a) : { ok: false, msg: 'no ' + name });
  const xyz = t => (isObj(t) && t.x != null ? `${t.x},${t.y},${t.z}` : t);
  const busy = m => /no free (gen )?cell|busy|claimed|held by/i.test(m || '');

  // "all 3 known chests full (...); 141 cobblestone in bag; ..." (the deposit tool's line when nothing fits anywhere).
  const FULL = /^(?:all \d+ known chests? full|no known chest)/;
  const bagged = (msg, items) => {
    const m = /;\s*([^;]*) in bag/.exec(msg || '');
    let n = 0;
    if (m) for (const d of m[1].matchAll(/(\d+) /g)) n += Number(d[1]);
    if (!n && items) for (const it of items) n += H.inv(it);
    return n;
  };

  /** stash(items | 'all-but-tools', {to}) - store items in chest to, else the known chests with room; 'all-but-tools' (default) keeps tools, armor, food. -> {ok, n, msg}; every chest full -> {ok:false, full:true, need, msg} (your call). */
  const stash = (items, o = {}) => {
    const list = Array.isArray(items) ? items : typeof items === 'string' && items !== 'all-but-tools' && items !== 'all' ? [items] : null;
    const a = {};
    if (o.to) a.at = xyz(o.to);
    if (list) a.items = list;
    const r = call('deposit', a);
    if (ok(r)) return { ok: true, n: r.n || 0, msg: r.msg };
    if (FULL.test(r.msg || '')) return { ok: false, full: true, need: bagged(r.msg, list), n: 0, msg: r.msg };
    return { ok: false, n: 0, msg: r.msg };
  };
  def('stash', stash);

  /** makeRoom(n=1, {to, drop}) - free n slots: stash('all-but-tools', {to}), then drop the junk in drop (['dirt']) while short. -> {ok, n: free, msg}. */
  const makeRoom = (n = 1, o = {}) => {
    n = Math.max(1, Math.floor(n));
    if (H.free() >= n) return { ok: true, n: H.free(), msg: `${H.free()} free` };
    const r = stash('all-but-tools', { to: o.to });
    for (const j of o.drop || []) if (H.free() < n && H.inv(j) > 0) call('drop', j, H.inv(j));
    const f = H.free();
    return { ok: f >= n, n: f, msg: f >= n ? `${f} free` : `need ${n} free slots, have ${f}` + (r.ok ? '' : ` (deposit: ${r.msg})`) };
  };
  def('makeRoom', makeRoom);

  // One way of getting it, repeated while it adds some: stops when done, after 2 tries that add nothing, at a full bag that can't
  // be stashed, or when a step answers {stop:true}.
  const repeat = (c, step, rounds) => {
    let r = null, dry = 0;
    for (let i = 0; c.left() > 0 && i < rounds; i++) {
      if (!c.room()) break;
      const before = c.left();
      r = step(c.left());
      if (c.left() < before) dry = 0;
      else if ((r && r.stop) || ++dry >= 2) break;
    }
    return r;
  };
  const of = (c, via) => (c.src || (c.src = H.sources(c.what))).filter(s => s.via === via);
  const WAYS = {
    chests(c) {
      if (c.more) return null;
      const spots = c.o.chest ? [xyz(c.o.chest)] : [undefined];
      for (const m of (H.where(c.what) || '').matchAll(/(-?\d+),\s*(-?\d+),\s*(-?\d+)/g)) spots.push(`${m[1]},${m[2]},${m[3]}`);
      let r = null;
      for (const at of [...new Set(spots)]) if (c.left() > 0 && c.room()) r = call('take', c.what, c.left(), at ? { at } : undefined);
      return r;
    },
    craft(c) {
      const i = H.item(c.what);
      if (c.more || (i && !i.craftable)) return null;
      return c.room() ? call('craft', c.what, c.left()) : null;
    },
    mine(c, s) {
      let r = null;
      for (const x of s ? [s] : of(c, 'mine')) if (c.left() > 0) r = repeat(c, k => call('mine', x.block, k, c.o.r), c.rounds);
      return r;
    },
    chop(c) {
      return of(c, 'chop').length ? repeat(c, () => call('chop'), c.rounds) : null;
    },
    harvest(c, s) {
      let r = null;
      for (const x of s ? [s] : of(c, 'harvest')) {
        const a = {};
        if (c.o.r != null) a.radius = c.o.r;
        if (x.skill === 'harvest_wheat') a.replant = c.o.replant !== false;
        if (c.left() > 0) r = repeat(c, () => H.skill(x.skill, a), c.rounds);
      }
      return r;
    },
    kill(c, s) {
      let r = null;
      for (const x of s ? [s] : of(c, 'kill'))
        if (c.left() > 0) r = repeat(c, () => {
          if (!H.find(x.mob, c.o.r ?? 24, 1).length) return { ok: false, stop: true, msg: `no ${x.mob} near` };
          const a = call('attack', x.mob);
          call('collect');
          return a;
        }, c.rounds);
      return r;
    },
    source(c) {
      let r = null;
      for (const s of of(c, 'harvest').concat(of(c, 'chop'), of(c, 'mine'), of(c, 'kill'))) if (c.left() > 0) r = WAYS[s.via](c, s) || r;
      return r || (c.src.length ? null : { ok: false, msg: `no known source of ${c.what} in this world` });
    },
    // A cobble generator cell (at, else 'auto'), per blocks a call; a busy cell is waited out (3 s, up to 20 times).
    gen(c) {
      let r = null, dry = 0, waits = 0;
      for (let i = 0; c.left() > 0 && i < c.rounds * 4; i++) {
        if (!c.room()) break;
        const before = c.left();
        r = call('gen', c.o.at ?? 'auto', Math.min(Math.max(1, c.o.per ?? 8), c.left()));
        if (c.left() < before) { dry = 0; continue; }
        if (!ok(r) && busy(r.msg) && ++waits <= 20) { H.wait(3); continue; }
        if (!ok(r) || ++dry >= 2) break;
      }
      return r;
    },
  };

  /**
   * gather(what, n=1, {from, more, stash, chest, near, tool, r, at, per}) - have n of an item by every known route, in order:
   * inventory, chests (chest, else every known one holding it), craft (if this version has a recipe), then its source per the
   * game data (harvest + replant, chop, mine the block that drops it, kill the mob). from = the
   * routes to try, in order: 'chests' 'craft' 'source' 'harvest' 'chop' 'mine' 'kill' 'gen' (generator cell at, per) or fn(left).
   * more:true = n more than held; stash: a chest (null|true = any with room): n more, stored as the bag fills. near: go first; tool: ensure.
   * -> {ok, n: gained, have, msg} (+ full, need if every chest is full); never fails the script: expect(r).
   */
  const gather = (what, n = 1, o = {}) => {
    if (typeof what !== 'string' || !what) H.fail('gather(what, n) needs an item name');
    n = Math.max(1, Math.floor(n));
    let stashed = 0, full = null;
    // stash: a chest, or true/null = the known chests with room (so stash: P.chest works with no chest set); false/absent = keep them.
    const st = o.stash === undefined || o.stash === false ? false : o.stash === null ? true : o.stash;
    const more = !!(o.more || st);
    const have = () => H.inv(what) + stashed, start = have(), goal = more ? start + n : n;
    const unload = () => {
      const before = H.inv(what);
      const r = stash([what], { to: st === true ? undefined : st });
      stashed += Math.max(0, before - H.inv(what));
      if (!r.ok) full = r;
      return r.ok;
    };
    const c = {
      what, o, more, src: null, rounds: Math.max(1, Math.floor(o.rounds ?? 64)),
      left: () => goal - have(),
      room: () => {
        if (full) return false;
        H.prog(Math.min(100, (100 * (have() - start)) / Math.max(1, goal - start)));
        return H.free() >= 2 || !st || unload();
      },
    };
    const notes = [];
    if (c.left() > 0 && o.near) call('go', xyz(o.near));
    if (c.left() > 0 && o.tool) ensure(o.tool);
    const routes = o.from == null ? (more ? ['source'] : ['chests', 'craft', 'source']) : [].concat(o.from);
    for (const via of routes) {
      if (c.left() <= 0 || full) break;
      const way = typeof via === 'function' ? cc => repeat(cc, via, cc.rounds) : WAYS[via];
      if (!way) H.fail(`gather: unknown route '${via}' (chests craft source harvest chop mine kill gen, or a fn)`);
      const r = way(c);
      if (r && !ok(r) && r.msg) notes.push(`${typeof via === 'function' ? 'fn' : via}: ${r.msg}`);
    }
    if (st && !full && H.inv(what) > 0) unload();
    const h = have(), got = Math.max(0, h - start), done = h >= goal;
    const msg = full ? `${what} ${got}/${goal - start}: ${full.msg}`
      : done ? `${more ? 'got' : 'have'} ${more ? got : h} ${what}` : `${what} ${more ? got + '/' + n : h + '/' + goal}` + (notes.length ? ` (${notes.join('; ')})` : '');
    const r = { ok: done && !full, n: got, have: H.inv(what), msg };
    if (full) { r.full = true; r.need = full.need; }
    return r;
  };
  def('gather', gather);

  /** handTo(player, item, n=1, {gather:true, ...}) - give a player n of an item, gather()ing what's missing first. -> {ok, n: given, msg}. */
  def('handTo', (player, item, n = 1, o = {}) => {
    if (o.gather !== false && H.inv(item) < n) gather(item, n, o);
    const k = Math.min(H.inv(item), n);
    if (k <= 0) return { ok: false, n: 0, msg: `no ${item} to give` };
    const r = call('give', player, item, k);
    return ok(r) ? { ok: k >= n, n: k, msg: `gave ${player} ${k} ${item}` + (k < n ? ` (${n - k} short)` : '') } : { ok: false, n: 0, msg: r.msg };
  });

  /** patrol(points | {at, r:8}, fn?, {rounds:1, until}) - walk the points (an area = its 4 corners), fn(p, i) at each, until until() or fn returns 'stop'. Guard = patrol + policies + on('mob.near'). -> {ok, n: reached, msg}. */
  def('patrol', (points, f, o = {}) => {
    let pts = Array.isArray(points) ? points : [points];
    if (!Array.isArray(points) && isObj(points) && points.at != null && points.x == null) {
      const a = typeof points.at === 'string' ? (([x, y, z]) => ({ x: +x, y: +y, z: +z }))(points.at.split(',')) : points.at;
      const r = Math.max(1, Math.floor(points.r ?? 8));
      pts = isObj(a) && Number.isFinite(a.x) ? [[-r, -r], [r, -r], [r, r], [-r, r]].map(([dx, dz]) => ({ x: a.x + dx, y: a.y, z: a.z + dz })) : [points.at];
    }
    pts = pts.filter(p => p != null);
    if (!pts.length) return { ok: false, n: 0, msg: 'patrol: no points' };
    const rounds = Math.max(1, Math.floor(o.rounds ?? 1));
    let n = 0, miss = '', stop = false;
    for (let round = 0; round < rounds && !stop; round++)
      for (const p of pts) {
        if (o.until && o.until()) { stop = true; break; }
        const g = call('go', xyz(p));
        if (!ok(g)) { miss = g.msg; H.idle(0.05); continue; }
        n++;
        if (f && f(p, n) === 'stop') { stop = true; break; }
      }
    return { ok: n > 0, n, msg: `reached ${n} point(s)` + (miss ? ` (last miss: ${miss})` : '') };
  });
}
