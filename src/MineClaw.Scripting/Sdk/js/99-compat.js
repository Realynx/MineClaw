// module: compat (deprecated) | the b1.8.1 skyblock strategy helpers, kept only so saved scripts that call them still run; not in the reference, lint warns on use, the author rewrites them with the primitives
(H, def) => {
  const isObj = v => v !== null && typeof v === 'object';
  const ok = r => !(r === false || r === null || (isObj(r) && r.ok === false));
  const call = (name, ...a) => (typeof globalThis[name] === 'function' ? globalThis[name](...a) : { ok: false, msg: 'no ' + name });
  const busy = m => /no free (gen )?cell|busy|claimed|held by/i.test(m || '');

  /** store(chest?, items?) - DEPRECATED: use stash(items, {to: chest}) (returns errors instead of failing). */
  const store = (chest, items) => {
    const r = stash(items || 'all-but-tools', { to: chest });
    if (r.ok || r.full) return r.ok ? { ok: true, msg: r.msg, n: r.n } : r;
    H.fail('store: ' + r.msg);
  };
  def('store', store);
  const whenFull = (r, onFull, job, minFree) => {
    if (!r || !r.full) return;
    if (typeof onFull === 'function') return onFull(r);
    if (H.free() < minFree) H.fail(job + ': ' + r.msg);
  };
  /** restock(item, n, chest?) - DEPRECATED: use gather(item, n, {from: 'chests', chest}). */
  def('restock', (item, n, chest) => {
    const have = H.inv(item);
    if (have >= n) return have;
    const r = call('take', item, n - have, chest ? { at: chest } : undefined);
    if (H.inv(item) < n) H.fail(`restock: ${item} ${H.inv(item)}/${n}` + (ok(r) ? '' : ` (${r.msg})`));
    return H.inv(item);
  });
  /** mineGen(opts) - DEPRECATED: use gather('cobblestone', n, {from: 'gen', at, per, stash: chest}) after ensure('pickaxe'). */
  def('mineGen', (o = {}) => {
    const at = o.at ?? 'auto', n = Math.max(1, o.n ?? 64), per = Math.max(1, o.per ?? 8), items = o.items ?? ['cobblestone'];
    ensure('pickaxe');
    room(2);
    let got = 0, idle = 0;
    for (let round = 0; got < n && round < Math.ceil(n / per) * 3 + 10; round++) {
      if (!['wooden_pickaxe', 'stone_pickaxe', 'iron_pickaxe', 'diamond_pickaxe'].some(p => H.item(p) && H.inv(p))) ensure('pickaxe');
      const before = H.inv('cobblestone');
      const r = call('gen', at, Math.min(per, n - got));
      const gained = Math.max(0, H.inv('cobblestone') - before);
      got += gained;
      if (!ok(r)) {
        if (busy(r.msg) || gained > 0) { H.wait(3); if (++idle > 20) H.fail('mineGen: ' + r.msg); continue; }
        H.fail('mineGen: ' + r.msg);
      }
      idle = 0;
      H.prog(Math.min(100, (100 * got) / n));
      if (H.free() < 2 && o.store !== false) whenFull(store(o.chest, items), o.onFull, 'mineGen', 2);
    }
    if (o.store !== false && H.inv('cobblestone') > 0) whenFull(store(o.chest, items), o.onFull, 'mineGen', 1);
    return got;
  });
  /** harvestFarm(opts) - DEPRECATED: use gather('wheat', n, {from: 'harvest', more: true, r, stash: chest}). */
  def('harvestFarm', (o = {}) => {
    const r = call('harvestWheat', { radius: o.radius, replant: o.replant !== false, part: o.part });
    if (!ok(r)) return r;
    if (o.store) store(o.chest, ['wheat']);
    return r;
  });
  /** chopTrees(opts) - DEPRECATED: use forEach(spots or find('log'), t => chop(t)) in quota(n, ...), or gather('log', n, {from: 'chop'}). */
  def('chopTrees', (o = {}) => {
    const n = o.n ?? (o.spots ? o.spots.length : 1);
    let chopped = 0;
    for (let round = 0; chopped < n && round < (o.rounds ?? 20); round++) {
      const spots = o.spots || [null];
      for (const s of spots) {
        if (chopped >= n) break;
        if (s && !/log/.test(H.block(s))) continue;
        const r = s ? call('chop', s) : call('chop');
        if (ok(r)) chopped++;
      }
      if (chopped < n) H.wait(o.waitS ?? 30);
    }
    if (o.store !== false && H.inv('log') > 0) store(o.chest, ['log']);
    return chopped;
  });
  /** deliver(player, item, n, opts) - DEPRECATED: use handTo(player, item, n). */
  def('deliver', (player, item, n, o = {}) => {
    room(3);
    if (H.inv(item) < n) call('take', item, n - H.inv(item), o.chest ? { at: o.chest } : undefined);
    for (let i = 0; H.inv(item) < n && o.mine && i < 50; i++) o.mine(n - H.inv(item));
    const k = Math.min(H.inv(item), n);
    if (k <= 0) H.fail(`deliver: no ${item}`);
    const r = call('give', player, item, k);
    if (!ok(r)) H.fail('deliver: ' + r.msg);
    return k;
  });
  /** waitForDay(maxS) - DEPRECATED: use waitFor('day', null, maxS) (or waitFor(() => !night(), maxS)). */
  def('waitForDay', (maxS = 900) => {
    for (let t = 0; H.night() && t < maxS; t += 60) H.waitFor('day', null, Math.min(60, maxS - t));
    return !H.night();
  });
  /** guardArea(opts) - DEPRECATED: use patrol({at, r}, fn, {until: () => !night()}) with use(defendIfAttacked()) and on('mob.near', fn). */
  def('guardArea', (o = {}) => {
    let fights = 0;
    if (o.at) call('go', o.at);
    for (let i = 0; i < (o.rounds ?? 120); i++) {
      if (o.until !== 'never' && !H.night()) break;
      if (H.me('hp') <= (o.retreatBelow ?? 6)) { const home = o.home || H.near('home', 64); if (home) call('go', home); return fights; }
      const m = H.hostile(o.radius ?? 8);
      if (m) { call('attack', m.name); fights++; } else H.wait(3);
      if (H.inv('rotten_flesh') > 0 && H.me('food') < 10) call('eat');
    }
    return fights;
  });
}
