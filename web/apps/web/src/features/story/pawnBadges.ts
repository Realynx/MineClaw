import type { StoryPawn } from '@mineclaw/contracts';
import type { BotBadge } from '@mineclaw/world-viewer';
import { GLYPH_OF, inDanger } from './mapDraw';
import { COLORS, GLYPHS, pawnColor } from './visuals';

const IDLE =
  'M8 1.5a6.5 6.5 0 1 1 0 13 6.5 6.5 0 0 1 0-13Zm0 1.5a5 5 0 1 0 0 10A5 5 0 0 0 8 3ZM7.3 4h1.4v3.7l2.5 1.5-.7 1.2L7.3 8.5Z';
const EXTRA_GLYPHS: Record<string, string> = {
  sleep: 'bed',
  rest: 'bed',
  guard: 'drafted',
  flee: 'drafted',
  rescue: 'haul',
  sow: 'harvest',
  farm: 'harvest',
  plant: 'harvest',
  store: 'chest',
  deliver: 'haul',
  smelt: 'bench',
};

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
        icon: GLYPHS[GLYPH_OF[kind] ?? EXTRA_GLYPHS[kind] ?? ''] ?? IDLE,
        color: inDanger(p) ? COLORS.danger : p.drafted ? '#ff9f43' : pawnColor(p.name),
      };
    });
}
