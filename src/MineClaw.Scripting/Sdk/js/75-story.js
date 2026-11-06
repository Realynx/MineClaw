// module: story | colony pawns
(H, def) => {
  const id = j => (j && typeof j === 'object' ? j.id : j);
  const report = (j, status, note, pct) => H.storyReport(String(id(j)), status, note == null ? null : String(note), pct);
  const call = (name, ...a) => globalThis[name](...a);
  const good = r => !(r === false || r === null || (typeof r === 'object' && r.ok === false));
  const why = r => String((r && r.msg) || 'failed');
  const eyes = c => {
    const [x, y, z] = String(c).split(',').map(Number), p = H.me('pos');
    return Math.hypot(x + 0.5 - p.x, y + 0.5 - p.y - 1.62, z + 0.5 - p.z);
  };
  const FAR = /from my eyes|couldn't get closer|not visible|in reach|can't reach/i, IN_WAY = /standing in it|in the way/i;
  // A build job's cells in order from its stand: a cell already built counts as done; out of reach (or standing in it) -> step
  // closer (or back to the stand) and try once more; each cell is reported; out of items, the rest go back to the queue. j.metas
  // (a design's turned blocks): each cell placed with its meta's facing, axis or half (place_turned); a wrong one is never "built".
  const TURN = /wrong rotation/i;
  // A batch cell's report ("cell c done" | "cell c failed: why") -> 1 when it is done.
  const noteCell = (j, c, r) => {
    report(j, 'progress', 'cell ' + c + (good(r) ? ' done' : ' failed: ' + why(r)));
    return good(r) ? 1 : 0;
  };
  const isBuilt = (j, c) => j.want && H.block(c) == j.want;
  const BUILT = Object.freeze({ ok: true });
  const ON_IT = /I'm standing on/i;
  // A dig of a build's own cell: clear_dig (the owner's order: soft protection on natural ground yields; a chest, a never-break mark
  // or a player's block still refuses), plain dig on an older runtime.
  const clearDig = c => (typeof globalThis.clear_dig === 'function' ? call('clear_dig', c) : call('dig', c));
  // A cell to clear (j.clear: another block in it, or its own block turned wrong) -> null once it is out, else why not ("can't clear
  // the cell …: protected …"): never "occupied by" for a dig that was refused. Standing on it, the pawn steps to the stand first.
  const clearOut = (j, c) => {
    let r = clearDig(c);
    if (!good(r) && ON_IT.test(why(r)) && j.stand) { call('go', j.stand, 0); r = clearDig(c); }
    return good(r) ? null : { ok: false, msg: "can't clear the cell " + c + ': ' + why(r) };
  };
  // A block put down for itself (a pillar step, a scaffold) or taken back: the colony keeps every one until the world shows it gone,
  // and one left behind (a failed or cut job, a pawn gone) becomes a cleanup job. Fox 11-05: "Bots should delete whatever they put
  // down unless it was a directive to build it."
  const temp = (c, placed, kind) => H.storyTemp(String(c), !!placed, kind);
  // A scaffold block: cobblestone or dirt it carries (cheap, plainly temporary), else one of the batch's blocks.
  const CHEAP = ['cobblestone', 'dirt'];
  const scaffoldBlock = j => CHEAP.find(b => H.inv(b) > 0) || j.block;
  // A floating cell's scaffold (j.scaffold {cell: spot}: nothing to click against there yet): a block at the spot, dug back once the
  // cell is in (or the placing failed) -> the spot, null (none), or the failure.
  const scaffoldUp = (j, c) => {
    const s = j.scaffold && j.scaffold[c];
    if (!s) return null;
    const r = call('place', scaffoldBlock(j), s);
    if (!good(r)) return { ok: false, msg: 'no scaffold at ' + s + ' for ' + c + ': ' + why(r) };
    temp(s, true, 'build scaffold');
    return s;
  };
  const scaffoldDown = s => {
    const r = clearDig(s);
    if (good(r) || H.block(s) == 'air') temp(s, false, 'build scaffold');
  };
  // The placing, tried once more after stepping closer (or back to the stand).
  const putCell = (j, c, put) => {
    const r = put(), w = why(r);
    if (good(r)) return r;
    if (!TURN.test(w) && isBuilt(j, c)) return { ok: true };
    if (!FAR.test(w) && !IN_WAY.test(w)) return r;
    if (IN_WAY.test(w) && j.stand) call('go', j.stand, 0); else call('go', c, 2);
    return put();
  };
  // One placing: dug first when it is to be cleared, walked to when far, on a scaffold when it floats.
  const placeCell = (j, c, m, clear) => {
    const put = () => (m == null ? call('place', j.block, c) : call('place_turned', j.block, c, m));
    const no = clear.includes(c) ? clearOut(j, c) : null;
    if (no) return no;
    if (eyes(c) > 4) call('go', c, 2);
    const s = scaffoldUp(j, c);
    if (s && s.ok === false) return s;
    try { return putCell(j, c, put); } finally { if (s) scaffoldDown(s); }
  };
  // A cell of place(): already built -> BUILT; a batch out of items -> null (the rest go back); else the placing's result.
  const placeOne = (j, c, m, clear) => {
    if (!clear.includes(c) && isBuilt(j, c)) return BUILT;
    if (j.cells && !(H.inv(j.block) > 0)) return null;
    return placeCell(j, c, m, clear);
  };
  const clearOf = j => (Array.isArray(j.clear) ? j.clear : j.clear ? [j.at] : []);
  const place = j => {
    const cells = j.cells || [j.at], clear = clearOf(j), metas = j.metas || [];
    if (j.stand && eyes(j.stand) > 1.8) call('go', j.stand, 0);
    let n = 0;
    for (let i = 0; i < cells.length; i++) {
      const r = placeOne(j, cells[i], metas[i], clear);
      if (r === null) break;
      if (!j.cells && r !== BUILT) return r;
      n += j.cells ? noteCell(j, cells[i], r) : 1;
    }
    return { ok: true, msg: 'batch: ' + n + '/' + cells.length + ' placed' };
  };
  // pour(job): a liquid build's cells (j.liquid water|lava), each a source once done. A cell already holding one is done; with no
  // full bucket the pawn fills its empty ones at j.source (fill_bucket: from dry land beside it). Then empty_bucket / move_lava pours
  // from a dry spot with a face beside the cell in view (flowing water there is fine: it becomes the source); the world must show
  // a source. Two tries a cell, each reported. A fill that fails ends the batch (`fill failed at …`: the director picks another
  // source; the rest go back, no failure). In water, the pawn gets out first.
  const AGAIN = /can't get near|no block face|from my eyes|couldn't get closer|can't reach|didn't appear/i;
  // A pour job's liquid: lava (move_lava) or water (empty_bucket), the source it leaves and the bucket that holds it.
  const pourKit = j => {
    const lava = j.liquid === 'lava', liq = lava ? 'lava' : 'water';
    return { j, lava, liq, full: liq + '_bucket' };
  };
  const isSource = (p, c) => H.storyLiquid(c) === p.liq;
  const inWater = () => { const p = H.me('pos'); return H.storyLiquid([p.x, p.y + 0.2, p.z].map(Math.floor).join(',')) !== ''; };
  const dryOff = p => { if (inWater()) escape(p.j.stand); };
  const fillBuckets = p => {
    const j = p.j;
    let r = { ok: false, msg: 'no ' + p.liq + ' source to fill a bucket from' };
    for (let k = 0; j.source && k < (j.buckets || 1) && H.inv('bucket') > 0; k++) {
      r = H.skill('fill_bucket', { liquid: p.liq, pos: j.source });
      if (!good(r)) break;
    }
    return H.inv(p.full) > 0 ? { ok: true } : r;
  };
  // Out of the water, with a full bucket (filled at j.source when it has none): null, else the fill that failed.
  const readyBucket = p => {
    dryOff(p);
    if (H.inv(p.full) > 0) return null;
    const f = fillBuckets(p);
    if (!good(f)) return f;
    dryOff(p);
    return null;
  };
  const tipAt = (p, c) => (p.lava ? H.skill('move_lava', { to: c }) : H.skill('empty_bucket', { pos: c }));
  // A pour the world doesn't show as a source after half a second is a failure.
  const settled = (p, c, r) => {
    H.wait(0.5);
    return isSource(p, c) ? r : { ok: false, msg: c + ' shows ' + (H.storyLiquid(c) || H.block(c)) + ', not a ' + p.liq + ' source' };
  };
  // One cell: from the stand when far, a second try when it couldn't get at it; the world's source decides.
  const pourCell = (p, c) => {
    if (p.j.stand && eyes(c) > 4.5) call('go', p.j.stand, 0);
    let r = tipAt(p, c);
    if (!good(r) && AGAIN.test(why(r)) && !isSource(p, c)) r = tipAt(p, c);
    if (good(r) && !isSource(p, c)) r = settled(p, c, r);
    return !good(r) && isSource(p, c) ? { ok: true } : r;
  };
  const pour = j => {
    const p = pourKit(j), cells = j.cells || [j.at];
    let n = 0;
    for (const c of cells) {
      if (isSource(p, c)) { n += noteCell(j, c, BUILT); continue; }
      const f = readyBucket(p);
      if (f) {
        report(j, 'progress', 'fill failed at ' + j.source + ': ' + why(f));
        return { ok: true, msg: 'batch: ' + n + '/' + cells.length + ' poured; no bucket filled: ' + why(f) };
      }
      n += noteCell(j, c, pourCell(p, c));
    }
    return { ok: true, msg: 'batch: ' + n + '/' + cells.length + ' poured' };
  };
  // during(job, fn): fn while the job is still this pawn's; cut at once when the director takes it back -> {ok:false, cancelled:true}.
  const during = (j, f) => H.storyDuring(String(id(j)), f);
  // idle(job): an idle leg (a stroll or a pastime: j.act). Walk to the director's cell (this pawn's alone), then linger looking at
  // each of j.look in turn (another pawn, the stars, the sunset, a chair); cut the moment work comes up. Someone already standing
  // there (met on the way): move on, the director picks another cell.
  // The walk to the idle cell: a wanderer that can't get there, or finds someone already standing there, stops (-> that result);
  // undefined = linger.
  const idleWalk = j => {
    const g = globalThis, r = g.go(j.at, j.dist ?? 2);
    if (!j.wander) return undefined;
    if (!g.ok(r)) return r;
    return g.find('player', { r: 1.5, limit: 2 }).length > 0 ? { ok: true, msg: 'crowded' } : undefined;
  };
  const linger = j => {
    const g = globalThis, look = j.look || [], s = j.s || 5;
    if (!look.length || typeof g.face !== 'function') return g.wait(s);
    for (const p of look) {
      g.face(p);
      g.wait(Math.max(1, s / look.length));
    }
    return { ok: true, msg: j.act || 'idle' };
  };
  const idle = j => during(j, () => {
    const stop = j.at ? idleWalk(j) : undefined;
    return stop !== undefined ? stop : linger(j);
  });
  // work(job): a station bill the pawn script hands over (1.12.2: brewing stand, enchanting table, anvil). A brew fills its glass
  // bottles first (j.fill: a water source or cauldron); every other kind is nothing to do.
  const brew = j => {
    if (j.fill && j.fill.n > 0) {
      const f = H.skill('fill_bottles', { pos: j.fill.at, count: j.fill.n });
      if (!globalThis.ok(f)) return f;
    }
    return H.skill('brew', { potion: j.potion, form: j.form, count: j.n, pos: j.at });
  };
  const WORK = new Map([
    ['brew', brew],
    ['enchant', j => H.skill('enchant_item', { item: j.item, option: j.option || 3, prefer: j.prefer || '', prefer_only: !!j.prefer_only, pos: j.at })],
    ['anvil', j => H.skill('use_anvil', { item: j.item, mode: j.mode || 'repair', pos: j.at })],
    ['look', j => H.skill('peek_container', { pos: j.at })], // the colony's record of that chest from what it sees
    ['equip', j => gear(j)],
    ['pour', j => pour(j)],
  ]);
  const work = j => (WORK.get(j.kind) || run)(j); // any other kind: run (escape, dump: below)
  // Gear (Story Mode's tools and drafting): the hidden equip(item, mode, from, ench); never a null argument (its schema wants strings).
  const equip = (item, mode, from, ench, keep) => mode === 'store'
    ? globalThis.equip(item, mode, from || '', ench || '', keep || 0) : globalThis.equip(item, mode, from || '', ench || '');
  // gear(job): armour off (j.unwear) or on (j.wear), then what to hold (j.hold, j.hold_ench).
  const gear = j => {
    const g = globalThis;
    let r = j.unwear ? equip('all', 'unwear') : { ok: true };
    for (const a of j.wear || []) { if (!g.ok(r)) break; r = equip(a, 'wear'); }
    return g.ok(r) && j.hold ? equip(j.hold, 'hold', '', j.hold_ench) : r;
  };
  // fetch(job): the job's items out of their chests (a tool, weapon or armour piece exactly: f.ench, the enchanted one or a plain
  // one; the rest by name), its extra gear into a chest (j.store: {to, item, keep}: the `keep` freshest stay; a pawn carries what its
  // policy needs, the rest is colony stock), then the tool its material takes in hand (j.tool, j.tool_ench: never a lost drop).
  // A busy chest is waited for (2, 4 s) and tried again; a build batch goes on with what it already carries and says when its items
  // are out of the chests ("fetched").
  const takeOne = f => {
    let r;
    for (let i = 0; i < 3; i++) {
      r = f.ench ? equip(f.item, 'take', f.from, f.ench) : globalThis.take(f.item, f.n, f.from);
      if (globalThis.ok(r) || !/busy/i.test(why(r))) break;
      H.wait(2 + 2 * i);
    }
    return r;
  };
  // Every j.fetch item: -> {got: any came out} or {stop: the take that ends the job (a build batch goes on with what it carries)}.
  const fetchItems = j => {
    let got = false;
    for (const f of j.fetch || []) {
      const r = takeOne(f);
      if (globalThis.ok(r)) got = true;
      else if (!j.cells || !(H.inv(j.block) > 0)) return { stop: r };
    }
    return { got };
  };
  const storeExtras = j => {
    for (const s of j.store || []) {
      const r = equip(s.item, 'store', s.to, '', s.keep);
      if (!globalThis.ok(r)) return { stop: r };
    }
    return null;
  };
  const fetch = j => {
    const t = fetchItems(j);
    if ('stop' in t) return t.stop;
    if (t.got && j.cells) report(j, 'progress', 'fetched');
    const s = storeExtras(j);
    if (s) return s.stop;
    return j.tool ? equip(j.tool, 'hold', '', j.tool_ench) : { ok: true };
  };
  // till(job): hold the hoe (j.hoe) and use it on the dirt or grass at j.at, walking over first when it is far. j.clear_at: tall
  // grass or a flower on it, cut first (a hoe does nothing under a plant).
  const till = j => {
    const g = globalThis;
    if (g.dist(g.pos(j.at)) > 5) {
      const r = g.go(j.at, 3);
      if (!g.ok(r)) return r;
    }
    if (j.clear_at && g.block(j.clear_at) !== 'air') {
      const c = g.dig(j.clear_at);
      if (!g.ok(c) && g.block(j.clear_at) !== 'air') return c;
    }
    g.skill('hold_item', j.hoe);
    return g.use_block(j.at);
  };
  // escape(toward?): out of a pit, a cave or water by itself (H.storyPit says where it is and the next move): walk to the exit when
  // there is one, else dig a staircase (never down, never beside liquid or under sand/gravel), else nerd-pole with blocks it carries,
  // else dig the walls for blocks and pole. Nothing works: the colony is asked for help (a pawn brings blocks, or a teleport home).
  // -> {ok, out: it moved out, stuck: still stuck, msg}
  // escDig(at): a dig to get out. The no-tool rule is for work targets: a pawn refused for want of a tool ("would lose the drop")
  // digs with whatever it holds (escape_dig, logged by the client); every other refusal stands.
  const LOSSY = /would lose the drop/i;
  const escDig = at => {
    const g = globalThis, r = g.dig(at);
    if (g.ok(r) || !LOSSY.test(r.msg || '') || typeof g.escape_dig !== 'function') return r;
    return g.escape_dig(at);
  };
  // climb (the colony found no way home from here: a ravine, a canyon): whatever its shape, walk to the wall nearest home
  // (s.approach) and cut a staircase up it toward home until level with home; the caller asks for help when it can't.
  // A stair out: dig its cells (escDig), then walk up it.
  const escStair = st => {
    let r = { ok: true };
    for (const c of st.dig) { r = escDig(c); if (!globalThis.ok(r)) break; }
    return globalThis.ok(r) ? globalThis.go(st.to, 0) : r;
  };
  const canPole = s => s.pillar && s.blocks > 0;
  // A pillar step out: the cell joins e.poles (taken back once it is out, from where it stands; the rest is the colony's cleanup).
  const pole = (e, s) => {
    const r = globalThis.place(s.block, s.feet);
    if (globalThis.ok(r)) { e.poles.push(s.feet); temp(s.feet, true, 'escape pillar'); }
    return r;
  };
  // Out: its escape pillar dug back, top first, as far as it reaches from where it stands; never the column under its own feet (it
  // would fall back in): what is left is the colony's cleanup.
  const column = c => String(c).split(',').filter((_, k) => k != 1).join(',');
  const unpoleEscape = e => {
    const mine = column(feet());
    for (let i = e.poles.length - 1; i >= 0; i--) {
      const c = e.poles[i];
      if (column(c) != mine && (globalThis.ok(escDig(c)) || H.block(c) == 'air')) temp(c, false, 'escape pillar');
    }
    e.poles = [];
  };
  // A step's moves in order, [applies, key, move]: the approach to the wall nearest home, the exit, in a pit a pillar step first
  // when it carries blocks (no digging at all), the stair (a ravine's wall is climbed by its stair), then a pillar step.
  const escMoves = (e, s, climb) => [
    [!!s.approach, 'a' + s.approach, () => globalThis.go(s.approach, 0)],
    [!!s.exit, 'e' + s.exit, () => globalThis.go(s.exit, 0)],
    [!climb && canPole(s), 'p' + s.feet, () => pole(e, s)],
    [!!s.stair, s.stair && s.stair.to, () => escStair(s.stair)],
    [canPole(s), 'p' + s.feet, () => pole(e, s)],
  ];
  // A move not tried here yet: -> true when it worked (the body moved).
  const escTry = (e, key, move) => {
    if (e.tried[key]) return false;
    e.tried[key] = 1;
    const r = move();
    if (globalThis.ok(r)) { e.moved = true; return true; }
    e.why = r.msg;
    return false;
  };
  // No blocks to pole with: dig one of the walls for them (-> true: dug or tried, look again).
  const escGather = (e, s) => {
    const c = s.pillar && s.blocks <= 0 && (s.gather || []).find(x => !e.tried['g' + x]);
    if (!c) return false;
    e.tried['g' + c] = 1;
    const r = globalThis.dig(c);
    if (!globalThis.ok(r)) e.why = r.msg;
    return true;
  };
  // One step out -> false when there is nothing left to try.
  const escStep = (e, s, climb) => {
    for (const [on, key, move] of escMoves(e, s, climb)) if (on && escTry(e, key, move)) return true;
    return escGather(e, s);
  };
  // Out (or level with home when climbing) -> the result; null = still in.
  const escOut = (s, climb, ty, moved) => {
    if (climb && ty != null && s.feet_y >= ty - 1) return { ok: true, out: true, msg: 'climbed up to ' + s.feet };
    if (!s.pit) return { ok: true, out: moved, msg: moved ? 'got out' : 'not stuck' };
    return null;
  };
  const escStuck = (s, climb, cause) => {
    const where = (s.water ? 'in water' : climb ? 'below home' : s.cave ? 'in a cave' : 'in a pit') + ' at ' + s.feet;
    const tail = where + (cause ? ' (' + cause + ')' : '');
    if (climb) return { ok: false, stuck: true, msg: "couldn't climb out " + tail };
    const help = H.storyStuck('stuck ' + tail);
    return { ok: false, stuck: true, msg: 'stuck ' + tail + ': ' + help };
  };
  const str = v => (v == null ? null : String(v));
  const escape = (toward, climb) => {
    // e: the moves tried (each once per spot), the last refusal, whether it moved.
    const e = { tried: {}, why: '', moved: false, poles: [] }, ty = toward == null ? null : +String(toward).split(',')[1];
    let s = null;
    for (let i = 0; i < (climb ? 64 : 40); i++) {
      s = H.storyPit(str(toward), !!climb);
      const out = escOut(s, climb, ty, e.moved);
      if (out) { unpoleEscape(e); return out; }
      if (!escStep(e, s, climb)) break;
    }
    return escStuck(s, climb, e.why);
  };
  const NO_WAY = /no path|no route|unreachable|partway|stuck/i;
  // go(at, d, opts?): the usual go; when it finds no way and the pawn is in a pit, cave or water, it gets out first and goes again.
  const go = (at, d, o) => {
    const g = globalThis, walk = () => (o ? g.go(at, d, o) : g.go(at, d));
    const r = walk();
    if (g.ok(r) || r.cancelled || !NO_WAY.test(r.msg || '')) return r;
    const e = escape(at);
    if (!g.ok(e)) return { ok: false, stuck: true, msg: r.msg + '; ' + e.msg };
    if (e.out) return walk();
    return standBelow(at) || r;
  };
  // A target high above (a crown log, a ledge, a ceiling): stand below it; story.dig pillars up from there. -> null: no such spot.
  const standBelow = at => {
    const g = globalThis, t = typeof at === 'string' ? at.split(',').map(Number) : null;
    if (!t || t.length !== 3 || !(t[1] > H.me('pos').y + 2)) return null;
    const s = H.storyReach(at);
    if (s.within) return { ok: true, msg: 'in reach' };
    if (!s.stand) return null;
    return g.ok(g.go(s.stand, 0)) ? { ok: true, msg: 'below it at ' + s.stand } : null;
  };
  const REACH = /can't reach|out of reach|from my eyes|no walkable spot/i;
  const feet = () => { const p = H.me('pos'); return Math.floor(p.x) + ',' + Math.floor(p.y + 1e-6) + ',' + Math.floor(p.z); };
  // reachUp(at, fn, job?): a block above reach. Walk to the column H.storyReach picks (under it or beside it), nerd-pole up (one
  // place at its own feet per step) until it is in reach, do fn, then dig the pillar back down from the top (every block it placed,
  // collected again). Short of blocks: the job's pillar stack from storage (j.pillar), else dirt dug around the column.
  const reachScan = at => H.storyReach(String(at));
  const NO_PILLAR = s => ({ ok: false, msg: 'out of reach even from a pillar: ' + s.why });
  // Blocks to pole with: the job's pillar stack from storage when short, then dirt dug around the column. -> the scan after.
  const stockUp = (at, s, j) => {
    if (s.blocks < s.steps && j && j.pillar) globalThis.take(j.pillar.item, j.pillar.n, j.pillar.from);
    s = reachScan(at);
    for (const c of s.gather || []) {
      if (s.blocks >= s.steps) break;
      globalThis.dig(c);
      s = reachScan(at);
    }
    return s;
  };
  const pillarShort = s => (!s.stand ? NO_PILLAR(s)
    : s.blocks < s.steps ? { ok: false, msg: 'out of reach: ' + s.steps + ' block(s) to pillar up, ' + s.blocks + ' on me' } : null);
  // Nerd-pole up (one place at its own feet per step) until it is in reach -> the cells placed, bottom first.
  const poleUp = (at, steps) => {
    const placed = [];
    for (let i = 0; i < steps + 2; i++) {
      const now = reachScan(at);
      if (now.within || !now.block) break;
      const f = feet(), p = globalThis.place(now.block, f);
      if (!globalThis.ok(p)) break;
      placed.push(f);
      temp(f, true, 'reach pillar');
    }
    return placed;
  };
  // Dig the pillar back down from the top, standing on each block it placed -> the cells it couldn't take back.
  const poleDown = placed => {
    const left = [];
    for (let i = placed.length - 1; i >= 0; i--) {
      const c = placed[i];
      if (globalThis.ok(globalThis.dig(c)) || H.block(c) == 'air') temp(c, false, 'reach pillar'); else left.push(c);
    }
    return left;
  };
  // Still up on what is left of its pillar (a block under it it couldn't take back): no way down without it, so the colony takes it
  // home (the rescue teleport) and the rest of the pillar is a cleanup job; never left standing on purpose.
  const unpole = (placed, stand) => {
    const left = poleDown(placed);
    if (!left.length || !(H.me('pos').y > +String(stand).split(',')[1] + 1.5)) return;
    try { H.storyStuck('stranded on its pillar at ' + feet() + ': ' + left.length + ' block(s) under it it could not take back'); }
    catch (e) { /* not a colony pawn: nobody to ask */ }
  };
  const reachUp = (at, fn, j) => {
    let s = reachScan(at);
    if (s.within) return fn();
    if (!s.stand) return NO_PILLAR(s);
    s = stockUp(at, s, j);
    const short = pillarShort(s);
    if (short) return short;
    const w = globalThis.go(s.stand, 0);
    if (!globalThis.ok(w)) return { ok: false, msg: 'out of reach: ' + w.msg };
    const placed = poleUp(at, s.steps);
    try { return fn(); } finally { unpole(placed, s.stand); }
  };
  // dig(at, job?): the usual dig; a block out of reach is reached from a pillar (reachUp), never just given up on. j.ordered (a
  // preserved zone's repair): the owner's clear (clear_dig), which the preserved-block filter lets through.
  const dig = (at, j) => {
    const g = globalThis, one = () => (j && j.ordered ? clearDig(at) : g.dig(at)), r = one();
    if (g.ok(r) || !REACH.test(r.msg || '')) return r;
    return reachUp(at, one, j);
  };
  // ready(job): a job up high (a tall tree) starts with blocks to pillar with: the job's pillar stack from storage when short.
  const ready = j => {
    const g = globalThis;
    if (j && j.pillar && g.inv(j.pillar.item) < 4) g.take(j.pillar.item, j.pillar.n, j.pillar.from);
    return { ok: true };
  };
  // run(job): the colony's own kinds (story.work hands them over). escape: the colony saw it stuck (a pit, a cave, a run of
  // unreachable targets): out by itself, else to j.at (home); help is asked for when nothing works. dump: no storage takes these:
  // carry them to the drop spot and put them down.
  const walkHome = j => globalThis.go(j.at, j.dist ?? 2);
  // escape: done only once home is reached by walking (the colony then knows it is out); a pit, a cave or water first.
  const runEscape = j => {
    const g = globalThis, e = escape(j.at);
    if (!g.ok(e)) { g.wait(j.s || 8); return e; }
    if (!j.at) return e;
    const r = walkHome(j);
    return g.ok(r) || r.cancelled ? r : climbHome(j, r);
  };
  // No walkable way home (a ravine, a canyon, a deep hole): climb toward home, then walk again; else the colony is asked for help.
  const climbHome = (j, r) => {
    const g = globalThis;
    report(j, 'progress', 'no way home (' + r.msg + '): trying a staircase toward home');
    const e = escape(j.at, true);
    if (g.ok(e)) {
      r = walkHome(j);
      if (g.ok(r) || r.cancelled) return r;
    }
    const cause = g.ok(e) ? r.msg : e.msg;
    const help = H.storyStuck('no way home: ' + cause);
    g.wait(j.s || 8);
    return { ok: false, stuck: true, msg: 'no way home (' + cause + '): ' + help };
  };
  // dump: carry them to the drop spot and put them down.
  const runDump = j => {
    const g = globalThis, r = go(j.at, j.dist ?? 1);
    if (!g.ok(r)) return r;
    for (const d of j.drop || []) {
      const x = d.n ? g.drop(d.item, d.n) : g.drop(d.item);
      if (!g.ok(x)) return x;
    }
    return { ok: true, msg: 'dropped ' + (j.drop || []).map(d => d.item).join(', ') };
  };
  const run = j => (j.kind === 'escape' ? runEscape(j) : j.kind === 'dump' ? runDump(j) : { ok: true });
  // sweep(): after every job, whatever its outcome, the pawn digs back its own scaffolds, pillars and boxes in reach (the hidden
  // clear_temporary); what it can't reach is the colony's cleanup.
  const sweep = () => {
    const g = globalThis;
    return typeof g.clear_temporary === 'function' ? g.clear_temporary() : { ok: true, msg: 'temporary blocks: none' };
  };
  /** story.next(s)|report(job, status, note?)|during|idle|work|fetch|go|dig|escape|sweep - the job loop */
  def('story', Object.freeze({
    next: s => H.storyNext(s == null ? 0 : s),
    report,
    during,
    idle,
    go,
    escape,
    dig,
    ready,
    work,
    till,
    fetch,
    place,
    pour,
    sweep,
  }));
}
