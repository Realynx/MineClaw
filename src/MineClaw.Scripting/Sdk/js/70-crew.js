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
  // An everyBody run: the lanes running (live: body -> lane), what each body returned (by), failed tries and retry pauses per body.
  const crewRun = (role, a, o) => ({
    role, o, args: a == null ? H.runArgs() : a, // no args: every lane gets this run's params (its params() sees what the brain tuned)
    retries: o.retries ?? 3, patience: o.wait ?? 120, by: {}, live: {}, tries: {}, next: {}, n: 0, bad: 0, busy: H.clock(),
  });
  // Late logins (bodies arrive seconds apart) and bodies whose retry pause is over get their lane now.
  const startLanes = (E, here, now) => {
    for (const b of here) if (!E.live[b] && !(b in E.by) && (E.next[b] ?? 0) <= now) E.live[b] = H.lane(b, E.role, E.args);
  };
  const untilMet = E => {
    if (!(E.o.until && E.o.until())) return false;
    Object.values(E.live).forEach(id => H.laneCancel(id, 'until met'));
    return true;
  };
  // Every body in game is done: end, unless some are still logging in (or none came yet), then wait for them a while.
  const allDone = (E, here, now) => {
    if (Object.keys(E.live).length) { E.busy = now; return false; }
    if (!here.every(b => b in E.by)) return false;
    if (!H.pendingBodies() && (here.length || Object.keys(E.by).length)) return true;
    return now - E.busy >= E.patience;
  };
  // A lane that returned: done, or a retry on its body after a pause (a failed one counts unless it hit a limit) till retries.
  const laneEnded = (E, b, r) => {
    delete E.live[b];
    if (r.ok) { E.by[b] = r.msg; E.n++; return; }
    if (!/\blimit\b/.test(r.msg)) E.tries[b] = (E.tries[b] || 0) + 1;
    H.log(`lane ${b}: ${r.msg}`);
    if (!H.bodies().includes(b)) return; // logged out / released: dropped (a new lane if it comes back)
    if ((E.tries[b] || 0) > E.retries) { E.by[b] = 'failed: ' + r.msg; E.bad++; } else E.next[b] = H.clock() + (E.o.pause ?? 5) * (E.tries[b] || 1);
  };
  const reap = E => {
    for (const b of Object.keys(E.live)) {
      const r = H.laneEnd(E.live[b]);
      if (r) laneEnded(E, b, r);
    }
  };
  const everyResult = E => {
    const ran = Object.keys(E.by);
    return { ok: E.n > 0, n: E.n, failed: E.bad, by: E.by, msg: ran.length ? `${E.n}/${ran.length} bodies done (${ran.map(b => `${b} ${E.by[b]}`).join(', ')})` : 'no body came' };
  };
  /**
   * everyBody(role, args, {retries:3, until, wait:120}) - role(args = this run's params) on every body, and each that joins later; a failed lane
   * re-runs on its body (retries), a body that left is dropped, a danger pauses a lane till its body is safe. Ends when all
   * returned, until() or no body for wait s. -> {ok, n, failed, by:{body:result}, msg}.
   */
  def('everyBody', (role, a, o = {}) => {
    if (!H.hasCrew()) return { ok: false, n: 0, failed: 0, by: {}, msg: 'no crew here: this bot has one body' };
    const E = crewRun(role, a, o);
    for (;;) {
      const now = H.clock(), here = H.bodies();
      startLanes(E, here, now);
      if (untilMet(E) || allDone(E, here, now)) break;
      H.laneAny(Object.values(E.live), 2);
      reap(E);
    }
    return everyResult(E);
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
