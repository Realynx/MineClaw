// module: policies | one-line reactive policies for any script or machine: handle the event, then the cut action carries on
(H, def) => {
  const isObj = v => v !== null && typeof v === 'object';
  const ok = r => !(r === false || r === null || (isObj(r) && r.ok === false));
  const call = (name, ...a) => (typeof globalThis[name] === 'function' ? globalThis[name](...a) : { ok: false, msg: 'no ' + name });

  /** policy(name, topic, opts, fn) - a custom policy: signal(topic, opts) + on(topic, fn); attach with use() or machine({policies}). */
  const policy = (name, topic, opts, fn) => Object.freeze({
    name, topic,
    attach() { if (opts !== undefined) H.signal(topic, opts); H.on(topic, null, ev => fn(ev)); },
  });
  def('policy', policy);
  /** use(...policies) - attach policies to this script (or, with a position, use_block: use("x,y,z")). */
  def('use', (...xs) => {
    if (xs.length && !(isObj(xs[0]) && typeof xs[0].attach === 'function')) return call('useBlock', ...xs);
    for (const p of xs) p.attach();
    return true;
  });
  /** keepFed({below:14, fetch:true}) - eat when food drops below; fetch food (ensure) if none is carried. */
  def('keepFed', (o = {}) => policy('keepFed', 'hungry', { below: o.below ?? 14 }, () => {
    let r = call('eat');
    if (!ok(r) && o.fetch !== false) { ensure('food'); r = call('eat'); }
    if (!ok(r)) H.log('keepFed: ' + r.msg);
  }));
  /** fleeVoidEdges({to}) - on a void/edge danger walk to safety (to, else the 'home' place), then carry on; nowhere to go -> pause. */
  def('fleeVoidEdges', (o = {}) => policy('fleeVoidEdges', 'void', undefined, () => {
    const to = o.to || H.near('home', 64);
    if (!to) return 'pause';
    const r = call('go', to);
    if (!ok(r)) return 'pause';
  }));
  /** defendIfAttacked({retreatBelow:6, home}) - when hurt: fight the nearest hostile, or retreat home at low hp; then carry on. */
  def('defendIfAttacked', (o = {}) => Object.freeze({
    name: 'defendIfAttacked', topic: 'hurt',
    attach() {
      H.signal('hurt');
      const react = () => {
        const low = o.retreatBelow ?? 6;
        if (H.me('hp') <= low) { const home = o.home || H.near('home', 64); if (home) call('go', home); return; }
        const m = H.hostile(o.radius ?? 6);
        if (m) call('attack', m.name);
      };
      H.on('hurt', null, react);
      H.on('lowhp', null, react);
    },
  }));
  /** depositWhenFull(chest?, items?, onFull?) - when the inventory fills, store() (chest, else the next with room) and carry on; every chest full -> onFull(r), else fail with why. */
  def('depositWhenFull', (chest, items, onFull) => policy('depositWhenFull', 'invFull', { free: 1 }, () => {
    const r = store(chest, items);
    if (r && r.full) {
      if (typeof onFull === 'function') return onFull(r);
      H.fail('depositWhenFull: ' + r.msg);
    }
  }));
  /** replaceToolWhenBroken(kind='pickaxe') - when the last tool of that kind breaks, ensure() a new one and carry on. */
  def('replaceToolWhenBroken', (kind = 'pickaxe') => policy('replaceToolWhenBroken', 'toolBroke:' + kind, null, () => { ensure(kind); }));
  /** pauseAtNight({shelter}) - at night go to shelter (optional) and wait for day, then carry on. */
  def('pauseAtNight', (o = {}) => policy('pauseAtNight', 'night', null, () => {
    if (o.shelter) call('go', o.shelter);
    for (let i = 0; i < 3 && H.night(); i++) H.waitFor('day', null, 600);
  }));
}
