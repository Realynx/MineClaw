// module: jobs | the jobs the saved scripts kept rewriting, each one call (mined from the script library: see ARCHITECTURE.md)
(H, def) => {
  const isObj = v => v !== null && typeof v === 'object';
  const ok = r => !(r === false || r === null || (isObj(r) && r.ok === false));
  const call = (name, ...a) => (typeof globalThis[name] === 'function' ? globalThis[name](...a) : { ok: false, msg: 'no ' + name });
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
  /**
   * store(chest?, items?) - deposit (all, or only items) at chest, else the next known chests with room; -> result ("stored 64
   * cobblestone: 30 → 14,64,-5 (now full), 34 → 5,64,4"). Every chest full -> {ok:false, full:true, need, msg} (you decide: place
   * a chest, keep carrying); other errors fail.
   */
  const store = (chest, items) => {
    const r = call('deposit', chest ? { at: chest, items } : items ? { items } : {});
    if (ok(r)) return r;
    if (FULL.test(r.msg || '')) return { ok: false, full: true, need: bagged(r.msg, items), msg: r.msg, n: 0 };
    H.fail('store: ' + r.msg);
  };
  def('store', store);
  // A full store inside a job: onFull(r) decides, else carry on while the bag has room, else fail with the actionable line.
  const whenFull = (r, onFull, job, minFree) => {
    if (!r || !r.full) return;
    if (typeof onFull === 'function') return onFull(r);
    if (H.free() < minFree) H.fail(job + ': ' + r.msg);
  };
  /** restock(item, n, chest?) - top the inventory up to n of item from chests; fails when short. -> count held. */
  def('restock', (item, n, chest) => {
    const have = H.inv(item);
    if (have >= n) return have;
    const r = call('take', item, n - have, chest ? { at: chest } : undefined);
    if (H.inv(item) < n) H.fail(`restock: ${item} ${H.inv(item)}/${n}` + (ok(r) ? '' : ` (${r.msg})`));
    return H.inv(item);
  });
  /**
   * mineGen({at:'auto', n:64, per:8, chest, items:['cobblestone'], store:true, onFull}) - pickaxe ensured (re-tooled when it breaks),
   * mine n cobble at the generator cell (or 'auto': a free claimed cell), store whenever nearly full and at the end; every chest
   * full -> onFull(r) if given, else fail once the bag is full. -> mined.
   */
  def('mineGen', (o = {}) => {
    const at = o.at ?? 'auto', n = Math.max(1, o.n ?? 64), per = Math.max(1, o.per ?? 8), items = o.items ?? ['cobblestone'];
    ensure('pickaxe');
    room(2);
    let got = 0, idle = 0;
    for (let round = 0; got < n && round < Math.ceil(n / per) * 3 + 10; round++) {
      if (!H.inv('wood_pickaxe') && !H.inv('stone_pickaxe') && !H.inv('iron_pickaxe') && !H.inv('diamond_pickaxe')) ensure('pickaxe');
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
  /** harvestFarm({radius, replant:true, store:false, chest}) - harvest ripe wheat (hoe + replant); optionally store the wheat. -> result. */
  def('harvestFarm', (o = {}) => {
    const r = call('harvestWheat', { radius: o.radius, replant: o.replant !== false, part: o.part });
    if (!ok(r)) return r;
    if (o.store) store(o.chest, ['wheat']);
    return r;
  });
  /** chopTrees({spots, n, rounds:20, waitS:30, store:true, chest}) - chop grown trees (at spots, else the nearest), replanting; wait for regrowth until n logs. -> chopped. */
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
  /** deliver(player, item, n, {chest, mine}) - bring n item to a player: take from chests, mine the rest (mine: fn(missing)), then give. -> given. */
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
  /** waitForDay(maxS=900) - wait until it is day (true) or the time runs out (false). */
  def('waitForDay', (maxS = 900) => {
    for (let t = 0; H.night() && t < maxS; t += 60) H.waitFor('day', null, Math.min(60, maxS - t));
    return !H.night();
  });
  /** guardArea({at, radius:8, until:'day', home, retreatBelow:6}) - fight hostiles near at until day (or a count of rounds); retreat home at low hp. -> kills tried. */
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
