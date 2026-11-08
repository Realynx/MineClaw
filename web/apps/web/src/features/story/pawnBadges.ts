import type { StoryPawn } from '@mineclaw/contracts';
import type { BotBadge } from '@mineclaw/world-viewer';
import { inDanger } from './mapDraw';
import { COLORS, pawnColor } from './visuals';
import { workAppearance } from './workAppearance';

/** Same work glyphs as the map, with only the account's numeric suffix as its visible name. */
export function pawnBadges(pawns: readonly StoryPawn[] | undefined): BotBadge[] {
  return (pawns ?? [])
    .filter((p) => p.online !== false && (p.dim ?? 0) === 0)
    .map((p) => {
      const kind = p.job?.type ?? (p.drafted ? 'guard' : (p.state ?? 'idle'));
      const activity = (p.job?.label || (p.drafted ? 'Drafted' : p.state || 'Idle'))
        .replace(/§./g, '')
        .replace(/\s+/g, ' ')
        .trim();
      return {
        name: p.name,
        text: p.name.match(/\d+$/)?.[0] ?? '',
        activity: activity.length > 34 ? `${activity.slice(0, 31).trimEnd()}…` : activity,
        icon: workAppearance(kind).path,
        color: inDanger(p) ? COLORS.danger : p.drafted ? '#ff9f43' : pawnColor(p.name),
      };
    });
}
