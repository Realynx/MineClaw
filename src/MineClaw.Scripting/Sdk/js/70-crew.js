// module: crew | lanes run a role of this script on another body
(H, def) => {
  const lane = (id, b) => Object.freeze({
    id, body: b,
    // lane.wait(s?) -> {ok,msg}
    wait: s => H.laneWait(id, s),
    cancel: why => H.laneCancel(id, why),
    get status() { return H.laneStatus(id); },
    then(res, rej) { const r = H.laneWait(id); return r.ok ? res && res(r.msg) : rej ? rej(r.msg) : r; },
  });
  const nameOf = b => (typeof b === 'string' ? b : b && b.name);
  /**
   * body(name) - a handle on another body: .name .hp .food .pos .busy .inv(item) .free(), every action as a method
   * (on that idle body; in-process only), b.run(role, args) -> lane.
   */
  def('body', name => {
    const base = {
      name: String(name),
      get hp() { return H.bodyInfo(name).hp; }, get food() { return H.bodyInfo(name).food; },
      get pos() { return H.bodyInfo(name).pos; }, get busy() { return H.bodyInfo(name).busy; },
      inv: item => H.bodyInv(name, item), free: () => H.bodyInfo(name).free,
      run: (role, args) => spawnLane(name, role, args),
    };
    return new Proxy(base, { get: (t, k) => (k in t ? t[k] : typeof k === 'string' ? (...a) => H.bodyAct(name, k, a) : undefined) });
  });
  /** bodies() - this crew's bodies now (["67","69"]). */
  def('bodies', () => H.bodies());
  const spawnLane = (b, role, args) => {
    const n = nameOf(b);
    return lane(H.lane(n, role, args), n);
  };
  /** spawn(body, role, args) - start role(args) of this script on that body; -> lane {wait(s), cancel(), status, then}. */
  def('spawn', spawnLane);
  /**
   * parallel([[body, role, args], ...], {keepGoing}) - run roles on several bodies at once, wait for all; -> results in order.
   * The first failure cancels the others (unless keepGoing). Roles are top-level functions.
   */
  def('parallel', (jobs, o) => {
    const ids = jobs.map(j => (Array.isArray(j) ? spawnLane(j[0], j[1], j[2]) : spawnLane(j.body, j.role, j.args)).id);
    return H.laneJoin(ids, !!(o && o.keepGoing));
  });
  /**
   * everyBody(role, args, {retries:3, until, wait:120}) - role(args = this run's params) on every body, and each that joins later; a failed lane
   * re-runs on its body (retries), a body that left is dropped, a danger pauses a lane till its body is safe. Ends when all
   * returned, until() or no body for wait s. -> {ok, n, failed, by:{body:result}, msg}.
   */
  def('everyBody', (role, a, o = {}) => {
    if (!H.hasCrew()) return { ok: false, n: 0, failed: 0, by: {}, msg: 'no crew here: this bot has one body' };
    const args = a == null ? H.runArgs() : a; // no args: every lane gets this run's params (its params() sees what the brain tuned)
    const retries = o.retries ?? 3, patience = o.wait ?? 120, by = {}, live = {}, tries = {}, next = {};
    let n = 0, bad = 0, busy = H.clock();
    for (;;) {
      const now = H.clock(), here = H.bodies();
      // Late logins (bodies arrive seconds apart) and bodies whose retry pause is over get their lane now.
      for (const b of here) if (!live[b] && !(b in by) && (next[b] ?? 0) <= now) live[b] = H.lane(b, role, args);
      if (o.until && o.until()) { Object.values(live).forEach(id => H.laneCancel(id, 'until met')); break; }
      if (Object.keys(live).length) busy = now;
      else if (here.every(b => b in by)) {
        // Every body in game is done: end, unless some are still logging in (or none came yet), then wait for them a while.
        if (!H.pendingBodies() && (here.length || Object.keys(by).length)) break;
        if (now - busy >= patience) break;
      }
      H.laneAny(Object.values(live), 2);
      for (const b of Object.keys(live)) {
        const r = H.laneEnd(live[b]);
        if (!r) continue;
        delete live[b];
        if (r.ok) { by[b] = r.msg; n++; continue; }
        if (!/\blimit\b/.test(r.msg)) tries[b] = (tries[b] || 0) + 1;
        H.log(`lane ${b}: ${r.msg}`);
        if (!H.bodies().includes(b)) continue; // logged out / released: dropped (a new lane if it comes back)
        if ((tries[b] || 0) > retries) { by[b] = 'failed: ' + r.msg; bad++; } else next[b] = H.clock() + (o.pause ?? 5) * (tries[b] || 1);
      }
    }
    const ran = Object.keys(by);
    return { ok: n > 0, n, failed: bad, by, msg: ran.length ? `${n}/${ran.length} bodies done (${ran.map(b => `${b} ${by[b]}`).join(', ')})` : 'no body came' };
  });
  /**
   * crew.get(k) crew.set(k,v) crew.inc(k,by=1) - shared board (JSON, 4 KB); crew.send(body,msg) crew.recv(s=60) - per-body inbox;
   * crew.barrier(name, n, s=300) - wait until n lanes reached it; crew.lock(key, ttlS=120) / crew.unlock(key) - leases.
   */
  def('crew', Object.freeze({
    get: k => H.crewGet(k), set: (k, v) => H.crewSet(k, v), inc: (k, by) => H.crewInc(k, by == null ? 1 : by),
    send: (to, msg) => H.crewSend(nameOf(to), msg), recv: s => H.crewRecv(s),
    barrier: (name, n, s) => H.crewBarrier(name, n, s), lock: (k, ttl) => H.crewLock(k, ttl), unlock: k => H.crewUnlock(k),
    get id() { return H.crewId(); },
  }));
}
