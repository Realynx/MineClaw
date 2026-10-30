// module: world | queries (zero tokens, no action used)
(H, def) => {
  /** me.hp me.food me.pos {x,y,z} me.x/.y/.z me.dim me.night me.name me.body - this body, live. */
  def('me', Object.freeze({
    get hp() { return H.me('hp'); }, get food() { return H.me('food'); }, get pos() { return H.me('pos'); },
    get x() { return H.me('x'); }, get y() { return H.me('y'); }, get z() { return H.me('z'); }, get dim() { return H.me('dim'); },
    get night() { return H.me('night'); }, get name() { return H.me('name'); }, get body() { return H.me('body'); },
  }));
  /** inv(item) - how many in the inventory; inv.free() - free slots; inv.has(item, n=1). */
  const inv = item => H.inv(item);
  inv.free = () => H.free();
  inv.has = (item, n) => H.inv(item) >= (n == null ? 1 : n);
  def('inv', Object.freeze(inv));
  /** block(x,y,z) or block(pos) - block name there ('air', 'cobblestone'...). */
  def('block', (...a) => H.block(...a));
  /** light(x,y,z) or light(pos) - {sky, block, now, night}: raw sky/block light 0-15 and the level now / after dark (mobs spawn at <= 7). */
  def('light', (...a) => H.light(...a));
  /** canSpawn(x,y,z, now=false) or canSpawn(pos, now) - true if a hostile mob can spawn with its feet there after dark (now=true: right now). */
  def('canSpawn', (...a) => H.canSpawn(...a));
  /** near(kind, r=24) - nearest block/entity/remembered place of that name {x,y,z,name,d} or null. */
  def('near', (kind, r) => H.near(kind, r));
  /** find(query, {r:24, filter, limit:16}) - matches nearest first: blocks ('log'), mobs ('cow', 'hostile'), players ('player' or a name), drops ('item'), else that remembered place; filter(t) keeps some (ripe: t => t.meta >= 7). -> [{x,y,z,name,d,kind,meta?}]; none within r: the list has .exhausted and .beyond (nearest within 64). */
  def('find', (query, o = {}) => {
    const q = String(query), r = o.r ?? 24;
    let list = H.find(q, o.r, o.filter ? 64 : o.limit);
    if (o.filter) list = list.filter(o.filter).slice(0, Math.max(1, o.limit ?? 16));
    if (list.length === 0) {
      // The work frontier: nothing left here, and where the nearest ones are, so expanding the area is one call.
      let more = r < 64 ? H.find(q, 64, o.filter ? 64 : 8).filter(t => t.d > r) : [];
      if (o.filter) more = more.filter(o.filter);
      list.exhausted = true;
      list.r = r;
      list.beyond = more.slice(0, 3);
    }
    return list;
  });
  // Standable ground in column x,z near y (feet cell), from this body's view; null when unknown (not loaded).
  const ground = (x, z, y) => {
    const open = n => n === 'air' || /grass$|flower|fern|snow_layer|torch|sapling|tallgrass|double_plant/.test(n || 'air');
    for (let yy = y + 12; yy >= y - 16; yy--) {
      const b = H.block(x, yy - 1, z);
      if (b && b !== 'air' && !/water|lava|leaves/.test(b) && open(H.block(x, yy, z)) && open(H.block(x, yy + 1, z))) return yy;
    }
    return null;
  };
  const glob = n => (typeof globalThis[n] === 'function' ? globalThis[n] : null);
  /** scout(query, {dir, max:240}) - walk out (dir: degrees; default: one per body) till find(query) sees one -> {ok, found, msg}. */
  def('scout', (query, o = {}) => {
    // Fox 10-30: "go in a random direction until they encounter animals. That's how that works." Bodies only see their
    // loaded chunks; walking out into fresh ones finds what the hunted-out area round the base no longer has.
    const start = H.me('pos'), step = o.step ?? 16, max = o.max ?? 240, r = o.r ?? 24;
    let h = 7; for (const ch of String(H.me('body') || H.me('name') || '')) h = (h * 31 + ch.charCodeAt(0)) >>> 0;
    let deg = o.dir === 'random' ? Math.random() * 360 : o.dir != null ? Number(o.dir) : h % 360;
    let d = 0, fails = 0;
    for (;;) {
      const hit = find(query, { r, limit: o.limit ?? 8 });
      if (hit.length) return { ok: true, found: hit, d: Math.round(d), msg: `found ${hit[0].name} ${Math.round(hit[0].d)} m off, ${Math.round(d)} m out` };
      if (d >= max) return { ok: false, found: [], d: Math.round(d), msg: `no ${query} within ${max} m that way (${Math.round(deg) % 360} deg)` };
      d += step;
      const a = deg * Math.PI / 180, me = H.me('pos');
      const x = Math.floor(start.x - Math.sin(a) * d), z = Math.floor(start.z + Math.cos(a) * d); // yaw 0 = south (+z), 90 = west
      const y = ground(x, z, Math.floor(me.y)) ?? Math.floor(me.y);
      const g = glob('go') ? glob('go')({ x, y, z }, 4) : { ok: false, msg: 'no go' };
      if (g && g.ok === false) { if (++fails >= 4) return { ok: false, found: [], d: Math.round(d), msg: `stuck ${Math.round(d)} m out: ${g.msg}` }; deg += 35; d -= step; }
      else fails = 0;
    }
  });
  /** item(name) - this version's facts for an item/block {name, id, block, stack, craftable, version} or null. */
  def('item', name => H.item(String(name)));
  /** hostile(r=8) - nearest hostile mob {name,x,y,z,d} or null. */
  def('hostile', r => H.hostile(r));
  /** where(item) - known chests/places holding it (text) or null. */
  def('where', item => H.where(item));
  /** night() - true at night; time() - world time of day (0-24000). */
  def('night', () => H.night());
  def('time', () => H.time());
  /** players() - online players [{name, d}] (not you). */
  def('players', () => H.players());
  /** pos(x,y,z) / pos("x,y,z") - a position {x,y,z}; dist(a, b=me.pos) - blocks between. */
  def('pos', (x, y, z) => (typeof x === 'string' ? (([a, b, c]) => ({ x: +a, y: +b, z: +c }))(x.split(',')) : { x, y, z }));
  def('dist', (a, b) => { b = b || H.me('pos'); return Math.hypot(a.x - b.x, a.y - b.y, a.z - b.z); });
}
