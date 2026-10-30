// module: character | the persona: gaze, gestures, lines, mood, relations, memory, recent chat, instant event reactions (zero tokens)
(H, def) => {
  const isObj = v => v !== null && typeof v === 'object';
  const call = (name, ...a) => (typeof globalThis[name] === 'function' ? globalThis[name](...a) : { ok: false, msg: 'no ' + name });
  let following = false;

  /** character.look(target) - turn the head to a player (name), a position ({x,y,z}, [x,y,z], 'x,y,z') or x,y,z: head only, no action; false if not in view. */
  const look = (...a) => H.chLook(...a);
  /** character.face(player) - look at a player's eyes and keep facing them for s seconds (default 2, max 10) between actions. */
  const face = (player, s) => {
    if (!H.chLook(player)) return false;
    const n = Math.min(10, Math.max(0, s == null ? 2 : s)) / 0.5;
    for (let i = 0; i < n; i++) { H.idle(0.5); H.chLook(player); }
    return true;
  };
  /** character.gesture(name, target?) - brainstem body language: nod|shake|wave|greet|bow|bob (crouch-bob)|hop|excite|ack|look; target = a player to look at. Refused during a reflex, a fight, near a hostile or an edge -> {ok:false,msg}. */
  const gesture = (name, target) => H.chGesture(name, target);
  /** character.follow(player, {dist=3, s=60}) - walk after a player for up to s seconds until stopFollow(), the player leaves, or s runs out -> {ok,msg}. */
  const follow = (player, o = {}) => {
    following = true;
    const total = Math.min(1800, Math.max(1, o.s == null ? 60 : o.s));
    let left = total, r = { ok: true, msg: 'followed ' + player };
    while (following && left > 0) {
      if (!H.players().some(p => p.name.toLowerCase() === String(player).toLowerCase())) { r = { ok: false, msg: player + ' is gone' }; break; }
      const slice = Math.min(5, left);
      const step = H.timeout(slice, () => call('follow', player, o.dist == null ? 3 : o.dist));
      if (isObj(step) && step.ok === false && !String(step.msg).startsWith('timeout')) { r = step; break; }
      left -= slice;
    }
    following = false;
    return r;
  };
  /** character.stopFollow() - end a character.follow() (call it from a handler, e.g. react('player.say', {intent:'stop'}, ...)). */
  const stopFollow = () => { const was = following; following = false; return was; };
  /** character.line(kind, player?) - a fresh prefetched in-character line for a moment (greet|thanks|status|hurt|come|follow|stop|bye|sorry|praise|tease|cheer|idle) that wasn't said recently, or null. */
  const line = (kind, player) => H.chLine(kind, player);
  /** character.typing(x) - a human-like pause before speaking: x = the text (about 30 ms per char, 0.2-1.4 s) or milliseconds. */
  const typing = x => {
    const ms = typeof x === 'number' ? x : 180 + 28 * String(x || '').length;
    return H.idle(Math.min(1.4, Math.max(0.05, ms * (0.85 + Math.random() * 0.3) / 1000)));
  };
  /** character.speak(kind, fallback?, player?) - pick line(kind) (else fallback), type it like a human, say it; -> the text said or null. */
  const speak = (kind, fallback, player) => {
    const t = H.chLine(kind, player) || fallback;
    if (!t) return null;
    typing(t);
    const r = H.say(t);
    if (r && r.ok !== false) { H.chSaid(t); return t; }
    return null;
  };
  /** character.mood() - {mood, energy, note} from the self-profile (read-only), or null. */
  const mood = () => H.chMood();
  /** character.relation(player) - {player, affinity, trust, playful, nick, trusted, regular} (read-only), or null for a stranger. */
  const relation = p => H.chRelation(p);
  /** character.recall(query, k=3) - memory retrieval [{id, text}] (local, zero tokens; 20 per run). */
  const recall = (q, k) => H.chRecall(q, k);
  /** character.remember(text) - a short note into memory, groomed like every memory write (3 per run) -> {ok,msg}. */
  const remember = t => H.chRemember(t);
  /** character.heard(player?, sinceSec=60) - recent chat [{player, text, intent, ago}] (that player's, else everyone's), newest last. */
  const heard = (p, s) => H.chHeard(p, s);
  /** character.intent(text) - the intent class of a line: come|follow|stop|give|status|thanks|praise|insult|greeting|farewell|help|question|command|banter. */
  const intent = t => H.chIntent(t);

  /** character - the persona object: look face gesture follow stopFollow line typing speak mood relation recall remember heard intent. */
  def('character', Object.freeze({ look, face, gesture, follow, stopFollow, line, typing, speak, mood, relation, recall, remember, heard, intent }));

  /** react(trigger, filter?, fn) - instant, no model: trigger = a game event (player.punchBlock{player,pos,block,held} player.swing{player,held} player.hurt{player,by,cause} player.died{player,cause} player.pickup{player,item,count} player.toss{player,item,count,pos} player.equip{player,slot,item} player.sneak player.unsneak{player} player.near player.leave{player,d} player.join player.quit{player} player.say{player,text,intent} player.lookAt{player,target,pos} player.gesture{player,kind:nod|shake|bob|wave|jump|spin|stare|point,at:'me'|bot,answer:yes|no,target,block} block.changed{pos,from,to,by} item.dropped{pos,item,count} mob.near{mob,d,pos} me.hurt{by,hp} me.lowHp{hp} time.dusk time.dawn{time} weather{rain}; 'player' = all player.*) or a chat intent ('come','thanks',...). filter: player, block, item, mob, kind, held (pickaxe|axe|shovel|sword|hoe|tool|empty|any|item), intent, text, target, at, by, cause, pos, near {x,y,z,r}, region {min,max}, dist, minCount, cooldown (s), once, cut. fn(e) runs between actions. */
  def('react', (trigger, filter, fn) => {
    if (typeof filter === 'function') { fn = filter; filter = {}; }
    const t = String(trigger || '');
    if (t.includes('.') || ['player', 'block', 'item', 'mob', 'me', 'time', 'weather'].includes(t)) return H.on(t, filter || {}, fn);
    return H.on('player.say', Object.assign({ intent: t }, filter || {}), fn);
  });
}
