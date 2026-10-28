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
  /** near(kind, r=24) - nearest block/entity/remembered place of that name: {x,y,z,name,d} or null. */
  def('near', (kind, r) => H.near(kind, r));
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
