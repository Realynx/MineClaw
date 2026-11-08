/** Interleave nearby work with the oldest waiting work, so a busy nearby area cannot starve the rest of the view. */
export class FairQueue {
  private readonly waiting = new Set<string>();
  private nearestTurn = true;

  pick(nearest: string[], limit: number): string[] {
    const wanted = new Set(nearest);
    for (const key of this.waiting) if (!wanted.has(key)) this.waiting.delete(key);
    for (const key of nearest) this.waiting.add(key);
    const oldest = this.waiting.values();
    const picked = new Set<string>();
    let near = 0;
    while (picked.size < limit && picked.size < wanted.size) {
      let key: string | undefined;
      if (this.nearestTurn) {
        while (near < nearest.length && picked.has(nearest[near])) near++;
        key = nearest[near++];
      } else {
        do {
          key = oldest.next().value;
        } while (key !== undefined && picked.has(key));
      }
      if (key === undefined) break;
      picked.add(key);
      this.nearestTurn = !this.nearestTurn;
    }
    for (const key of picked) this.waiting.delete(key);
    return [...picked];
  }

  clear(): void {
    this.waiting.clear();
    this.nearestTurn = true;
  }
}
