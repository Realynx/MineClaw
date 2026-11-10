import type { WorldViewerHandle } from '@mineclaw/world-viewer';
import { useEffect, useState } from 'react';

/** How often the map asks the viewer whether it is catching up (a few times a second, never per frame). */
const UPDATING_POLL_MS = 250;
/** Polls in a row that must say "catching up" before the chip shows: a section re-meshed during live play is not news. */
const UPDATING_AFTER_POLLS = 2;

/** "Updating the map…" while the viewer catches up (a tab that came back, a reconnect): the picture may be stale. It
 * blocks nothing. Kept out of StoryMap.tsx: a second component there brings StoryMap's older render-time ref patterns
 * under the React lint (16 errors). */
export function MapUpdatingChip({
  viewer,
  hidden,
}: {
  viewer: { current: WorldViewerHandle | null };
  hidden: boolean;
}) {
  const [shown, setShown] = useState(false);
  useEffect(() => {
    let streak = 0;
    const id = setInterval(() => {
      // A viewer without the status (an older engine) never shows the chip.
      const status = viewer.current?.engine()?.runtime?.syncStatus?.();
      streak = status?.updating ? streak + 1 : 0;
      setShown(streak >= UPDATING_AFTER_POLLS);
    }, UPDATING_POLL_MS);
    return () => clearInterval(id);
  }, [viewer]);
  if (hidden || !shown) return null;
  return (
    <p
      role="status"
      className="pointer-events-none absolute top-2 left-2 rounded-full border border-border bg-surface/90 px-2.5 py-0.5 text-[11px] text-muted shadow-sm"
      data-testid="story-map-updating"
    >
      Updating the map…
    </p>
  );
}
