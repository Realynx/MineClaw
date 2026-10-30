// module: dig | underground digs, one C# job each
(H, def) => {
  // A volume: 'x0,y0,z0,x1,y1,z1' or [6 numbers] (a box), a remembered dig's name, or {box|site|rule, at, to, repeats}.
  const vol = v => (typeof v === 'string' ? (/^-?\d+\s*,/.test(v) ? { box: v } : { site: v })
    : Array.isArray(v) ? { box: v.join(',') } : Object.assign({}, v || {}));
  const run = (op, v, o) => {
    const a = Object.assign(op ? { op } : {}, vol(v), o || {});
    for (const k of Object.keys(a)) if (a[k] === undefined) delete a[k]; // an unset at/to is no arg, not null
    return H.skill('excavate', a);
  };
  const isRule = s => /^\s*(column|unit)\b/.test(String(s));
  /** excavate(vol, opts) - dig box 'x0,y0,z0,x1,y1,z1' or saved dig {site, to:y}; opts.op room|strip; seals caves, torches. */
  def('excavate', (v, o) => run(null, v, o));
  /** seal(vol) - fill a dig's cave openings, not its doorways. lightUp(vol) - torch it. */
  def('seal', (v, o) => run('seal', v, o));
  def('lightUp', (v, o) => run('light', v, o));
  /** pattern.learn(name, box|rule), pattern.apply(name|rule, toY, at?) - rule e.g. 'column 3 step 0,-1,0 turn -90 pivot 0.5,0.5'. */
  def('pattern', Object.freeze({
    learn: (name, src, at) => run('learn', isRule(src) ? { rule: src, at } : vol(src), { name }),
    apply: (p, to, at) => run(null, isRule(p) ? { rule: p, at } : { site: p, at }, { to }),
  }));
}
