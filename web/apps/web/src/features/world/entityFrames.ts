/**
 * The shared stream's `entities` frames, as a push for the world viewers: the dispatcher merges each frame into the
 * `world.entities` query cache and then calls `publishEntityFrame`; every world runtime subscribes through its
 * `WorldSource.onEntities` and reads the cache at once. Bodies then glide at the frames' pace (the bot's world socket sends
 * up to 10 a second) instead of the beat of the viewer's own poll, which is only a backstop.
 */
type Listener = () => void;
const listeners = new Set<Listener>();

export function publishEntityFrame(): void {
  for (const l of listeners) l();
}

export function subscribeEntityFrames(fn: Listener): () => void {
  listeners.add(fn);
  return () => {
    listeners.delete(fn);
  };
}
