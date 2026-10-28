// module: flow | loops, retries and behaviour-tree helpers (deterministic; every helper runs at once and returns a result)
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
  /** quota(n, fn, {max, idle:3, item}) - fn(left, i) until n: fn returns what it added (number, a result's n, true = 1; item: that item's inventory gain); stops on false/{ok:false}, idle empty rounds or max (n+10). -> {ok, n, msg}. */
  def('quota', (n, f, o = {}) => {
    n = Math.max(0, Math.floor(n || 0));
    const max = Math.max(1, Math.floor(o.max ?? n + 10)), idle = Math.max(1, Math.floor(o.idle ?? 3));
    let total = 0, dry = 0, why = 'max rounds';
    for (let i = 1; total < n && i <= max; i++) {
      const before = o.item ? H.inv(o.item) : 0;
      const r = f(n - total, i);
      const g = o.item ? Math.max(0, H.inv(o.item) - before) : failed(r) ? 0 : gain(r);
      total += g;
      if (failed(r)) { why = isObj(r) && r.msg ? r.msg : 'stopped'; break; }
      if (g > 0) dry = 0; else if (++dry >= idle) { why = `nothing added in ${idle} rounds`; break; }
      if (n > 0) H.prog(Math.min(100, (100 * total) / n));
      H.idle(0.05);
    }
    return { ok: total >= n, n: total, msg: total >= n ? `${total}/${n}` : `${total}/${n}: ${why}` };
  });
  /** forEach(targets, fn, {order:'nearest', budget}) - fn(t, i) per target (find() results, positions, places), nearest to you next (order:'given' keeps it); at most budget calls; 'stop' ends. -> {ok, n: done, failed, msg}. */
  def('forEach', (targets, f, o = {}) => {
    const list = Array.isArray(targets) ? targets.slice() : targets == null ? [] : [targets];
    const budget = Math.max(0, Math.floor(o.budget ?? list.length));
    let n = 0, bad = 0, tried = 0, last = '';
    while (list.length > 0 && tried < budget) {
      let k = 0;
      if (o.order !== 'given') {
        const me = H.me('pos');
        let best = Infinity;
        list.forEach((t, j) => {
          const p = at(t);
          const d = p ? Math.hypot(p.x - me.x, p.y - me.y, p.z - me.z) : 1e9;
          if (d < best) { best = d; k = j; }
        });
      }
      const t = list.splice(k, 1)[0];
      const r = f(t, ++tried);
      if (r === 'stop') break;
      if (failed(r)) { bad++; if (isObj(r) && r.msg) last = r.msg; } else n++;
    }
    return { ok: n > 0 || tried === 0, n, failed: bad, msg: `${n}/${tried} done` + (bad ? `, ${bad} failed (last: ${last || 'false'})` : '') };
  });
  /** ensure(what) - ensure('pickaxe') / ensure(['pickaxe','food']): have one (any tier), else acquire it (chests, hive, craft, bootstrap) or fail; -> names held. */
  def('ensure', what => {
    const got = [];
    for (const w of Array.isArray(what) ? what : [what]) {
      const names = cat[w] ? known(cat[w]) : [w];
      const have = () => names.find(x => H.inv(x) > 0);
      let x = have();
      const acquire = fn('acquire');
      if (!x && acquire) {
        const r = acquire(w);
        x = have();
        if (!x) H.fail(`ensure: no ${w} (${r.msg})`);
      }
      const take = fn('take');
      if (!x && take) for (const y of names) { if (take(y, 1).ok && H.inv(y) > 0) { x = y; break; } }
      const craft = fn('craft');
      if (!x && craft) for (const y of craftable[w] ? known(craftable[w]) : [w]) { if (craft(y, 1).ok && H.inv(y) > 0) { x = y; break; } }
      if (!x) H.fail(`ensure: no ${w} (not in inventory or chests, can't craft)`);
      got.push(x);
    }
    return got.join(',');
  });
}
