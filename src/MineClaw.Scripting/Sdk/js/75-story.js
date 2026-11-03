// module: story | colony pawns
(H, def) => {
  const id = j => (j && typeof j === 'object' ? j.id : j);
  /** story.next(s=0) - this pawn's job or null; story.report(job, 'done'|'failed'|'progress', note?) */
  def('story', Object.freeze({
    next: s => H.storyNext(s == null ? 0 : s),
    report: (j, status, note, pct) => H.storyReport(String(id(j)), status, note == null ? null : String(note), pct),
  }));
}
