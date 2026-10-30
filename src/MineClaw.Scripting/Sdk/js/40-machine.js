// module: machine | reactive state machines: states, events, timers, conditions, a blackboard; resumes in the same state after a restart
(H, def) => {
  const isObj = v => v !== null && typeof v === 'object';
  /**
   * machine({initial, states:{name:{enter, tick, exit, on:{event: 'target' | (ev,bb,m)=>target | {to, guard}}, final}}, on, guards, bb, policies, thresholds, id})
   * - runs until a final state (or 'done'), returns its name. enter/tick/exit(bb, m) may return a target; tick repeats in the state. Events: danger lowhp void died chat 'chat:<text>' hungry invFull toolBroke[:kind] hurt night day,
   * timers after(ms) every(ticks), conditions when(()=>cond); an event cuts the running action. guards: {name:(bb,ev)=>bool}.
   * m.state m.history m.bb m.go(state). State and bb are checkpointed on every transition.
   */
  def('machine', spec => {
    if (!isObj(spec) || !isObj(spec.states)) H.fail('machine({initial, states:{...}}) needs states');
    const initial = spec.initial || Object.keys(spec.states)[0];
    for (const p of spec.policies || []) use(p);
    const bb = isObj(spec.bb) ? spec.bb : isObj(spec.data) ? spec.data : {};
    return H.machine(spec.id || 'm', initial, spec, bb);
  });
  /** after(ms) - event key: once, ms after entering the state ({[after(5000)]: 'next'}). */
  def('after', ms => 'after:' + Math.max(10, Math.round(ms)));
  /** every(ticks) - event key: every n game ticks (20/s) while in the state. */
  def('every', ticks => 'every:' + Math.max(1, Math.round(ticks)));
  /** when(fn) - event key: fires when fn() turns true ({[when(() => inv.free() < 2)]: 'store'}). */
  def('when', f => H.when(f));
  /** signal(topic, opts?) - listen for a built-in: hungry {below:14}, invFull {free:1}, toolBroke[:kind], hurt, night, day. */
  def('signal', (topic, opts) => H.signal(topic, opts));
}
