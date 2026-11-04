/**
 * The world pane's "go to" (WorldPane.tsx): x,y,z flies the camera there; a bot's name (or short name) flies to it in free cam
 * or follows it otherwise; anything else is looked up as a place (/api/locate).
 */
import type { CameraMode, WorldViewerHandle } from '@mineclaw/world-viewer';
import type { RefObject } from 'react';
import { api, errorText } from '@/lib/api/client';

export function parseCoords(q: string): [number, number, number] | null {
  const m = /^\s*(-?\d+(?:\.\d+)?)\s*[, ]\s*(-?\d+(?:\.\d+)?)\s*[, ]\s*(-?\d+(?:\.\d+)?)\s*$/.exec(q);
  return m ? [Number(m[1]), Number(m[2]), Number(m[3])] : null;
}

type ViewerBot = ReturnType<WorldViewerHandle['bots']>[number];

function goToBot(v: WorldViewerHandle | null, bot: ViewerBot, mode: CameraMode, setMode: (m: CameraMode) => void) {
  if (mode === 'fly') {
    // Free cam stays free: fly to the bot with the crosshair on its body.
    v?.focus(bot.x - 0.5, bot.y + 0.6, bot.z - 0.5, { distance: 4, pitch: 8 });
    return;
  }
  v?.followBot(bot.name);
  setMode('follow');
}

/** Goes to `q` (trimmed, not empty); resolves to the error to show, or null. */
export async function goTo(
  q: string,
  viewer: RefObject<WorldViewerHandle | null>,
  mode: CameraMode,
  setMode: (m: CameraMode) => void,
): Promise<string | null> {
  const c = parseCoords(q);
  if (c) {
    viewer.current?.focus(c[0], c[1], c[2]);
    return null;
  }
  const bot = viewer.current?.bots().find((b) => b.name.toLowerCase() === q.toLowerCase() || b.dto.short === q);
  if (bot) {
    goToBot(viewer.current, bot, mode, setMode);
    return null;
  }
  try {
    const r = await api('locate', { query: { q } });
    viewer.current?.focus(r.x, r.y, r.z);
    return null;
  } catch (err) {
    return errorText(err).replace('Not found.', `No place called “${q}”.`);
  }
}
