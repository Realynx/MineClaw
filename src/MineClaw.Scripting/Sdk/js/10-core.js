// module: core | results, control, chat, logging, checkpoints, params (every call is a gated host call)
(H, def) => {
  const isObj = v => v !== null && typeof v === 'object';
  const failed = r => r === false || r === null || (isObj(r) && r.ok === false);

  /** ok(r) - true unless r failed: false, null or {ok:false}. undefined/numbers/strings count as success. */
  def('ok', r => !failed(r));
  /** expect(r, msg?) - fail the script unless r is truthy and not {ok:false} (msg, else r.msg). Returns r. */
  def('expect', (r, msg) => {
    if (!r || (isObj(r) && r.ok === false)) H.fail(msg != null ? String(msg) : isObj(r) && r.msg ? r.msg : 'expectation failed');
    return r;
  });
  /** fail(msg?) - end the script as failed (no msg = the last failed action's reason). Never for data: return it. */
  def('fail', msg => H.fail(msg == null ? null : String(msg)));
  /** log(...x) - debug line (script log + bot log; never chat). Also print(), console.log(). */
  def('log', (...xs) => H.log(...xs));
  def('print', (...xs) => H.log(...xs));
  def('console', Object.freeze({ log: (...x) => H.log(...x), info: (...x) => H.log(...x), warn: (...x) => H.log(...x), error: (...x) => H.log(...x) }));
  /** prog(pct, note?) - progress for the status line (0-100). */
  def('prog', (pct, note) => H.prog(pct, note));
  /** wait(s=1) - pause s seconds (interruptible; max 600). */
  def('wait', s => H.wait(s == null ? 1 : s));
  // waitFor(cond, s): poll a condition once a second (uncounted waits) until it holds or s seconds pass.
  const waitCond = (cond, s) => {
    const max = Math.max(0, s == null ? 60 : Number(s));
    for (let t = 0; ; t++) {
      if (cond()) return { ok: true, msg: 'met', n: 0 };
      if (t >= max) return { ok: false, msg: `timeout after ${max}s`, n: 0 };
      H.idle(Math.min(1, max - t));
    }
  };
  const waitFor = (kind, filter, s) => (typeof kind === 'function' ? waitCond(kind, filter) : H.waitFor(kind, filter, s));
  /** waitFor(kind, filter?, s=60) - wait until it is day: waitFor('day', null, 900) (or night); chat|danger|join (filter text or {from,text}), item ({item,n}), block ({x,y,z,name}), a signal, or a condition fn: waitFor(() => inv('log') >= 8, 120). -> {ok,msg}. */
  def('waitFor', waitFor);
  def('waitfor', waitFor);
  /** say(text) - one chat line to players (2 per run, 10 s apart; data goes to log). */
  def('say', t => H.say(t));
  /** emote(text) - a /me action line. */
  def('emote', t => H.emote(t));
  /** ask(player, question, s=60) - ask in chat and wait for that player's reply (string or null). ask('brain', q) pauses and wakes the brain; returns its answer. */
  def('ask', (p, q, s) => H.ask(p, q, s));
  /** wake(reason) - wake the sleeping brain with this script's state (rate-limited); the script keeps running. */
  def('wake', why => H.wake(why));
  /** cp(label, fn?) - checkpoint: carry is snapshotted and resume_from=label restarts here; with fn, a stage skipped when resuming past it. */
  def('cp', (label, fn) => H.cp(label, fn));
  /** on(topic, [filter,] fn) - handler for danger|lowhp|void|died|chat|'chat:<text>'|hungry|invFull|toolBroke|hurt|night|day|after(ms)|when(fn). fn(ev) runs between actions (the cut action re-runs after); return 'pause'|'wake'|'stop' to pass it on. */
  def('on', (topic, a, b) => (b === undefined ? H.on(topic, null, a) : H.on(topic, a, b)));
  /** onInterrupt(kind, fn) - this script's own inline fix for danger|lowhp|void|died|stuck|failed|needs_input|chat|'*', tried before any brain call. fn(ev, m) runs in scope under the patch budget; return 'resume' (default)|'restart_step'|'stop'|'escalate'. */
  def('onInterrupt', (kind, fn) => H.onInterrupt(kind, fn));
  /** skill(name, ...args) - any catalog skill (or action/alias) by name. */
  def('skill', (name, ...a) => H.skill(name, ...a));
  /** params(defaults) - this script's tunables: defaults < saved (script op=save params) < this run's params. Call once at the top. */
  def('params', d => H.params(d));
  /** carry - object kept across replace and checkpoints (JSON data only). args - this run's args/params (read-only). */
  def('carry', H.carry);
  def('args', H.args);
}
