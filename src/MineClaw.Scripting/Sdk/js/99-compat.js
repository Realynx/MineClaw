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
  const hasPick = () => ['wooden_pickaxe', 'stone_pickaxe', 'iron_pickaxe', 'diamond_pickaxe'].some(p => H.item(p) && H.inv(p));
  // A failed gen call: a busy cell (or one that still gave some) is waited out (3 s, up to 20 times) -> true = try again.
  const genRetry = (g, r, gained) => {
    if (!busy(r.msg) && !(gained > 0)) { H.fail('mineGen: ' + r.msg); return false; }
    H.wait(3);
    if (++g.idle > 20) H.fail('mineGen: ' + r.msg);
    return true;
  };
  const genRound = (g, at, per, n) => {
    if (!hasPick()) ensure('pickaxe');
    const before = H.inv('cobblestone');
    const r = call('gen', at, Math.min(per, n - g.got));
    const gained = Math.max(0, H.inv('cobblestone') - before);
    g.got += gained;
    if (!ok(r) && genRetry(g, r, gained)) return;
    g.idle = 0;
    H.prog(Math.min(100, (100 * g.got) / n));
    if (H.free() < 2 && g.o.store !== false) whenFull(store(g.o.chest, g.items), g.o.onFull, 'mineGen', 2);
  };
  /** mineGen(opts) - DEPRECATED: use gather('cobblestone', n, {from: 'gen', at, per, stash: chest}) after ensure('pickaxe'). */
  def('mineGen', (o = {}) => {
    const at = o.at ?? 'auto', n = Math.max(1, o.n ?? 64), per = Math.max(1, o.per ?? 8), g = { o, items: o.items ?? ['cobblestone'], got: 0, idle: 0 };
    ensure('pickaxe');
    room(2);
    for (let round = 0; g.got < n && round < Math.ceil(n / per) * 3 + 10; round++) genRound(g, at, per, n);
    if (o.store !== false && H.inv('cobblestone') > 0) whenFull(store(o.chest, g.items), o.onFull, 'mineGen', 1);
    return g.got;
  });
  /** harvestFarm(opts) - DEPRECATED: use gather('wheat', n, {from: 'harvest', more: true, r, stash: chest}). */
  def('harvestFarm', (o = {}) => {
    const r = call('harvestWheat', { radius: o.radius, replant: o.replant !== false, part: o.part });
    if (!ok(r)) return r;
    if (o.store) store(o.chest, ['wheat']);
    return r;
  });
  const chopGoal = o => o.n ?? (o.spots ? o.spots.length : 1);
  // One round over the spots (a spot with no log left is skipped; none: chop the nearest) -> the count chopped so far.
  const chopRound = (spots, n, chopped) => {
    for (const s of spots) {
      if (chopped >= n) break;
      if (s && !/log/.test(H.block(s))) continue;
      if (ok(s ? call('chop', s) : call('chop'))) chopped++;
    }
    return chopped;
  };
  const storeLogs = o => { if (o.store !== false && H.inv('log') > 0) store(o.chest, ['log']); };
  /** chopTrees(opts) - DEPRECATED: use forEach(spots or find('log'), t => chop(t)) in quota(n, ...), or gather('log', n, {from: 'chop'}). */
  def('chopTrees', (o = {}) => {
    const n = chopGoal(o);
    let chopped = 0;
    for (let round = 0; chopped < n && round < (o.rounds ?? 20); round++) {
      chopped = chopRound(o.spots || [null], n, chopped);
      if (chopped < n) H.wait(o.waitS ?? 30);
    }
    storeLogs(o);
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
  const retreat = o => {
    const home = o.home || H.near('home', 64);
    if (home) call('go', home);
  };
  // One guard round: fight the nearest hostile, else wait; eat rotten flesh when hungry -> 1 when it fought.
  const guardRound = o => {
    const m = H.hostile(o.radius ?? 8);
    let fought = 0;
    if (m) { call('attack', m.name); fought = 1; } else H.wait(3);
    if (H.inv('rotten_flesh') > 0 && H.me('food') < 10) call('eat');
    return fought;
  };
  /** guardArea(opts) - DEPRECATED: use patrol({at, r}, fn, {until: () => !night()}) with use(defendIfAttacked()) and on('mob.near', fn). */
  def('guardArea', (o = {}) => {
    let fights = 0;
    if (o.at) call('go', o.at);
    for (let i = 0; i < (o.rounds ?? 120); i++) {
      if (o.until !== 'never' && !H.night()) break;
      if (H.me('hp') <= (o.retreatBelow ?? 6)) { retreat(o); return fights; }
      fights += guardRound(o);
    }
    return fights;
  });
}
