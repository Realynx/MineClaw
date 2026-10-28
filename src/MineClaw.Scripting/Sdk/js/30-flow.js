// module: flow | loops, retries and behaviour-tree helpers (deterministic; every helper runs at once and returns a result)
(H, def) => {
  const isObj = v => v !== null && typeof v === 'object';
  const failed = r => r === false || r === null || (isObj(r) && r.ok === false);
  const cat = {
    pickaxe: ['diamond_pickaxe', 'iron_pickaxe', 'stone_pickaxe', 'wood_pickaxe'],
    axe: ['diamond_axe', 'iron_axe', 'stone_axe', 'wood_axe'],
    shovel: ['diamond_shovel', 'iron_shovel', 'stone_shovel', 'wood_shovel'],
    sword: ['diamond_sword', 'iron_sword', 'stone_sword', 'wood_sword'],
    hoe: ['diamond_hoe', 'iron_hoe', 'stone_hoe', 'wood_hoe'],
    food: ['cooked_porkchop', 'cooked_beef', 'cooked_chicken', 'bread', 'cooked_fish', 'mushroom_stew', 'apple', 'golden_apple', 'cookie', 'melon', 'raw_porkchop', 'beef', 'raw_fish'],
  };
  const craftable = { pickaxe: ['stone_pickaxe', 'wood_pickaxe'], axe: ['stone_axe', 'wood_axe'], shovel: ['stone_shovel', 'wood_shovel'], sword: ['stone_sword', 'wood_sword'], hoe: ['stone_hoe', 'wood_hoe'], food: ['bread'] };
  const fn = g => (typeof globalThis[g] === 'function' ? globalThis[g] : null);

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
  /** room(n=1) - make sure n slots are free (deposit() if not), else fail with the reason. */
  def('room', n => {
    n = Math.max(1, Math.floor(n || 1));
    if (H.free() >= n) return true;
    const d = fn('deposit');
    const r = d ? d({}) : { ok: false, msg: 'no deposit' };
    if (H.free() < n) H.fail(`room: need ${n} free slots, have ${H.free()}` + (r.ok ? '' : ` (deposit: ${r.msg})`));
    return true;
  });
  /** ensure(what) - ensure('pickaxe') / ensure(['pickaxe','food']): have one (any tier), else acquire it (chests, hive, craft, bootstrap) or fail; -> names held. */
  def('ensure', what => {
    const got = [];
    for (const w of Array.isArray(what) ? what : [what]) {
      const names = cat[w] || [w];
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
      if (!x && craft) for (const y of craftable[w] || [w]) { if (craft(y, 1).ok && H.inv(y) > 0) { x = y; break; } }
      if (!x) H.fail(`ensure: no ${w} (not in inventory or chests, can't craft)`);
      got.push(x);
    }
    return got.join(',');
  });
}
