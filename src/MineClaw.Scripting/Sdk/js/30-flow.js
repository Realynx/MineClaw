// module: flow | loops, retries and behaviour-tree helpers (deterministic)
(H, def) => {
  const isObj = v => v !== null && typeof v === 'object';
  const failed = r => r === false || r === null || (isObj(r) && r.ok === false);
  const cat = {
    pickaxe: ['diamond_pickaxe', 'iron_pickaxe', 'stone_pickaxe', 'wooden_pickaxe', 'golden_pickaxe'],
    axe: ['diamond_axe', 'iron_axe', 'stone_axe', 'wooden_axe', 'golden_axe'],
    shovel: ['diamond_shovel', 'iron_shovel', 'stone_shovel', 'wooden_shovel', 'golden_shovel'],
    sword: ['diamond_sword', 'iron_sword', 'stone_sword', 'wooden_sword', 'golden_sword'],
    hoe: ['diamond_hoe', 'iron_hoe', 'stone_hoe', 'wooden_hoe', 'golden_hoe'],
    food: ['cooked_porkchop', 'cooked_beef', 'cooked_chicken', 'bread', 'cooked_fish', 'baked_potato', 'mushroom_stew', 'apple', 'golden_apple', 'cookie', 'melon', 'carrot', 'raw_porkchop', 'porkchop', 'beef', 'raw_fish', 'fish', 'chicken'],
  };
  const craftable = { pickaxe: ['stone_pickaxe', 'wooden_pickaxe'], axe: ['stone_axe', 'wooden_axe'], shovel: ['stone_shovel', 'wooden_shovel'], sword: ['stone_sword', 'wooden_sword'], hoe: ['stone_hoe', 'wooden_hoe'], food: ['bread'] };
  const fn = g => (typeof globalThis[g] === 'function' ? globalThis[g] : null);
  // Only the names this game version knows (item() resolves through its game data), so a list never names another version's item.
  const known = names => names.filter(x => H.item(x));
  // A step's yield: a number, a result's n (1 when it worked without one), true = 1.
  const gain = r => (typeof r === 'number' ? Math.max(0, r) : r === true ? 1 : isObj(r) ? (r.n == null ? 1 : Math.max(0, Number(r.n) || 0)) : 0);
  const at = t => (isObj(t) && t.x != null ? t : typeof t === 'string' && /^\s*-?\d+\s*,\s*-?\d+\s*,\s*-?\d+\s*$/.test(t)
    ? (([x, y, z]) => ({ x: +x, y: +y, z: +z }))(t.split(',')) : null);

  /** retry(n, fn) - fn(i) until it succeeds (not false/null/{ok:false}), at most n times, waiting 1,2,4.. s (max 20) between; returns the last result. */
  def('retry', (n, f) => {
    n = Math.max(1, Math.floor(n || 3));
    let r;
    for (let i = 1; i <= n; i++) {
      r = f(i);
      if (!failed(r)) return r;
      if (i < n) H.wait(Math.min(2 ** (i - 1), 20));
    }
    return r;
  });
  /** upto(n, fn) - fn(i) for i=1..n, stopping early when it returns false or {ok:false}; returns passes made. */
  def('upto', (n, f) => {
    n = Math.max(0, Math.floor(n || 1));
    for (let i = 1; i <= n; i++) {
      if (failed(f(i))) return i;
      H.idle(0.05);
    }
    return n;
  });
  /** until(cond, fn, max=100) - fn(i) until cond() is true (checked first); true when cond was met. */
  def('until', (cond, f, max) => {
    max = Math.max(1, Math.floor(max || 100));
    for (let i = 1; i <= max; i++) {
      if (cond()) return true;
      f(i);
      H.idle(0.05);
    }
    return !!cond();
  });
  /** seq(...fns) - run in order; stops at the first failure (false/null/{ok:false}) and returns it; else true. */
  def('seq', (...fs) => {
    for (const f of fs) { const r = f(); if (failed(r)) return r === null ? false : r; }
    return true;
  });
  /** sel(...fns) - first one that succeeds wins (its result); all failed -> the last failure. */
  def('sel', (...fs) => {
    let r = false;
    for (const f of fs) { r = f(); if (!failed(r)) return r === undefined ? true : r; }
    return r;
  });
  /** timeout(s, fn) - fn() but at most s seconds (the running action is cut); -> fn's result or {ok:false,msg:'timeout'}. */
  def('timeout', (s, f) => H.timeout(s, f));
  /** room(n=1) - makeRoom(n) or fail the script with the reason. */
  def('room', n => {
    n = Math.max(1, Math.floor(n || 1));
    if (H.free() >= n) return true;
    const m = fn('makeRoom');
    const r = m ? m(n) : { ok: false, msg: `need ${n} free slots, have ${H.free()}` };
    if (!r.ok) H.fail('room: ' + r.msg);
    return true;
  });
  // One quota round: fn's result and what it added (o.item: that item's inventory gain).
  const quotaRound = (f, left, i, o) => {
    const before = o.item ? H.inv(o.item) : 0;
    const r = f(left, i);
    return { r, g: o.item ? Math.max(0, H.inv(o.item) - before) : failed(r) ? 0 : gain(r) };
  };
  // Why a round ends the quota (null: go on): fn failed, or `idle` rounds in a row added nothing.
  const quotaStop = (q, r, g, idle) => {
    if (failed(r)) return isObj(r) && r.msg ? r.msg : 'stopped';
    if (g > 0) q.dry = 0; else if (++q.dry >= idle) return `nothing added in ${idle} rounds`;
    return null;
  };
  /** quota(n, fn, {max, idle:3, item}) - fn(left, i) until n: fn returns what it added (number, a result's n, true = 1; item: that item's inventory gain); stops on false/{ok:false}, idle empty rounds or max (n+10). -> {ok, n, msg}. */
  def('quota', (n, f, o = {}) => {
    n = Math.max(0, Math.floor(n || 0));
    const max = Math.max(1, Math.floor(o.max ?? n + 10)), idle = Math.max(1, Math.floor(o.idle ?? 3)), q = { dry: 0 };
    let total = 0, why = 'max rounds';
    for (let i = 1; total < n && i <= max; i++) {
      const { r, g } = quotaRound(f, n - total, i, o);
      total += g;
      const stop = quotaStop(q, r, g, idle);
      if (stop !== null) { why = stop; break; }
      if (n > 0) H.prog(Math.min(100, (100 * total) / n));
      H.idle(0.05);
    }
    return { ok: total >= n, n: total, msg: total >= n ? `${total}/${n}` : `${total}/${n}: ${why}` };
  });
  // This body's own failed targets (key -> clock s until): its next forEach rounds skip them too.
  const failedUntil = new Map();
  // The target list's entry nearest this body (one that isn't a position counts as far).
  const nearest = list => {
    const me = H.me('pos');
    let best = Infinity, k = 0;
    list.forEach((t, j) => {
      const p = at(t);
      const d = p ? Math.hypot(p.x - me.x, p.y - me.y, p.z - me.z) : 1e9;
      if (d < best) { best = d; k = j; }
    });
    return k;
  };
  const leaseKey = (t, o) => {
    const p = o.claim === false ? null : at(t);
    return p ? `${Math.floor(p.x)},${Math.floor(p.y)},${Math.floor(p.z)}` : null;
  };
  // One body per target: a sibling running the same job already works this one (or it failed here a short while ago), so take
  // the next (never follow it).
  const leasedElsewhere = (key, o) => key && ((failedUntil.get(key) ?? 0) > H.clock() || !H.claimTarget(key, o.lease ?? 300));
  // Done: it stays marked for a while so a sibling with an older list skips it too (outliving this run). Failed: marked for a
  // short while too, for every body: a target this body couldn't work (logs out of reach) was otherwise taken at once by the next
  // body, which failed the same way, then by this one again (live 10-30: 119 and 1337 took turns on one spruce at 144,66,267).
  // -> the lease is kept.
  const markWorked = (key, r, o) => {
    if (!failed(r) && r !== 'stop') return H.claimTarget(key, o.doneLease ?? 600, true);
    if (!failed(r)) return false;
    const s = o.failLease ?? 180;
    failedUntil.set(key, H.clock() + s);
    return H.claimTarget(key, s, true);
  };
  // fn on one target under its lease. A run that ends mid-target (stopped, logged out) gives its lease back at its end.
  const workTarget = (st, t, key, f, o) => {
    let r, kept = false;
    try {
      r = f(t, ++st.tried);
      if (key) kept = markWorked(key, r, o);
    } finally { if (key && !kept) H.releaseTarget(key); }
    return r;
  };
  const tally = (st, r) => {
    if (!failed(r)) { st.n++; return; }
    st.bad++;
    if (isObj(r) && r.msg) st.last = r.msg;
  };
  const beyondText = b => `${b.name || ''} ${b.x},${b.y},${b.z}${b.d != null ? ` (${b.d} m)` : ''}`.trim();
  // The work frontier, typed: nothing here was left for this body; where the nearest remaining ones are (find's beyond).
  const exhausted = (res, targets, taken) => {
    const beyond = Array.isArray(targets) && Array.isArray(targets.beyond) ? targets.beyond : [];
    res.exhausted = true;
    res.beyond = beyond;
    res.msg = 'exhausted: ' + (taken ? `all ${taken} targets taken by other bodies` : 'no targets')
      + (beyond.length ? '; nearest beyond: ' + beyond.map(beyondText).join(', ') : '');
    return res;
  };
  const forEachResult = (st, targets) => {
    const { n, bad, tried, taken, last } = st;
    const res = { ok: n > 0 || (tried === 0 && taken === 0), n, failed: bad, taken,
      msg: `${n}/${tried} done` + (bad ? `, ${bad} failed (last: ${last || 'false'})` : '') + (taken ? `, ${taken} taken by other bodies` : '') };
    return tried === 0 ? exhausted(res, targets, taken) : res;
  };
  /** forEach(targets, fn, {order:'nearest', budget, claim:true}) - fn(t, i) per target (find() results, positions), nearest to you next (order:'given' keeps it); targets are leased while fn runs (another body's are skipped); at most budget calls; 'stop' ends. -> {ok, n: done, failed, taken, msg}; nothing left -> exhausted:true, beyond (from find). */
  def('forEach', (targets, f, o = {}) => {
    const list = Array.isArray(targets) ? targets.slice() : targets == null ? [] : [targets];
    const budget = Math.max(0, Math.floor(o.budget ?? list.length)), st = { n: 0, bad: 0, tried: 0, taken: 0, last: '' };
    while (list.length > 0 && st.tried < budget) {
      const t = list.splice(o.order !== 'given' ? nearest(list) : 0, 1)[0];
      const key = leaseKey(t, o);
      if (leasedElsewhere(key, o)) { st.taken++; continue; }
      const r = workTarget(st, t, key, f, o);
      if (r === 'stop') break;
      tally(st, r);
    }
    return forEachResult(st, targets);
  });
  // The first of names fn (take | craft) gets one of that this body then holds; else x.
  const firstGot = (f, names, x) => {
    for (const y of names) if (f(y, 1).ok && H.inv(y) > 0) return y;
    return x;
  };
  const acquired = (acquire, w, have) => {
    const r = acquire(w);
    const x = have();
    if (!x) H.fail(`ensure: no ${w} (${r.msg})`);
    return x;
  };
  // One kind (any tier held, else acquire, a chest, a craft) -> the name held.
  const ensureOne = w => {
    const names = cat[w] ? known(cat[w]) : [w];
    const have = () => names.find(x => H.inv(x) > 0);
    let x = have();
    const acquire = fn('acquire');
    if (!x && acquire) x = acquired(acquire, w, have);
    const take = fn('take');
    if (!x && take) x = firstGot(take, names, x);
    const craft = fn('craft');
    if (!x && craft) x = firstGot(craft, craftable[w] ? known(craftable[w]) : [w], x);
    if (!x) H.fail(`ensure: no ${w} (not in inventory or chests, can't craft)`);
    return x;
  };
  /** ensure(what) - ensure('pickaxe') / ensure(['pickaxe','food']): have one (any tier), else acquire it (chests, hive, craft, bootstrap) or fail; -> names held. */
  def('ensure', what => {
    const got = [];
    for (const w of Array.isArray(what) ? what : [what]) got.push(ensureOne(w));
    return got.join(',');
  });
}
