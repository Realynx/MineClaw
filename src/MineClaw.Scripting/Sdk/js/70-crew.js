// module: crew | several bodies: lanes run a role of this script on another body (in-process lanes or hive clones)
(H, def) => {
  const lane = (id, b) => Object.freeze({
    id, body: b,
    /** lane.wait(s?) -> {ok,msg} */
    wait: s => H.laneWait(id, s),
    cancel: why => H.laneCancel(id, why),
    get status() { return H.laneStatus(id); },
    then(res, rej) { const r = H.laneWait(id); return r.ok ? res && res(r.msg) : rej ? rej(r.msg) : r; },
  });
  const nameOf = b => (typeof b === 'string' ? b : b && b.name);
  /**
   * body(name) - a handle on another body: .name .hp .food .pos .busy .inv(item) .free(), every action as a method
   * (b.gen('auto', 8): runs on that idle body; in-process only), b.run(role, args) -> lane.
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
  /** bodies() - the bodies a crew can use (["67","69",...]). */
  def('bodies', () => H.bodies());
  const spawnLane = (b, role, args) => {
    const n = nameOf(b);
    return lane(H.lane(n, role, args), n);
  };
  /** spawn(body, role, args) - start role(args) of this script on that body; -> lane {wait(s), cancel(), status, then}. */
  def('spawn', spawnLane);
  /**
   * parallel([[body, role, args], ...], {keepGoing}) - run roles on several bodies at once and wait for all; -> their results in order.
   * The first failure cancels the others (unless keepGoing). Roles are top-level functions; the top level holds only functions/constants.
   */
  def('parallel', (jobs, o) => {
    const ids = jobs.map(j => (Array.isArray(j) ? spawnLane(j[0], j[1], j[2]) : spawnLane(j.body, j.role, j.args)).id);
    return H.laneJoin(ids, !!(o && o.keepGoing));
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
