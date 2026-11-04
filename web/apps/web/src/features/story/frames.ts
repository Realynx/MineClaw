/**
 * When the shared stream last delivered a `story` frame per colony. The page's `since=rev` catch-up only covers a quiet or
 * reconnecting stream: while frames flow (the bridge polls the colony at 2 Hz) the catch-up would fetch the same delta again
 * (live 11-04: a story state request every ~2.6 s per tab on top of the frames).
 */
const lastFrame = new Map<string, number>();

export function noteStoryFrame(id: string, now: number = Date.now()): void {
  lastFrame.set(id, now);
}

/** True when a frame for `id` arrived within `ms`. */
export function storyFramesFlowing(id: string, ms = 5_000, now: number = Date.now()): boolean {
  const at = lastFrame.get(id);
  return at !== undefined && now - at < ms;
}
