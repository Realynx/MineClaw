import { expect, it } from 'vitest';
import type { StoryPawn } from '@mineclaw/contracts';
import { pawnBadges } from '@/features/story/pawnBadges';
import { COLORS, GLYPHS } from '@/features/story/visuals';

const pawn = (extra: Partial<StoryPawn> = {}): StoryPawn =>
  ({
    name: 'maid_chan_109',
    display: 'Made Up Nickname',
    online: true,
    job: { type: 'mine', label: 'Mining the quarry' },
    ...extra,
  }) as StoryPawn;

it('uses account numbers and the same work glyphs as the map, not nicknames or roles', () => {
  expect(pawnBadges([pawn()])[0]).toMatchObject({
    name: 'maid_chan_109',
    text: '109',
    activity: 'Mining the quarry',
    icon: GLYPHS.mine,
  });
  expect(pawnBadges([pawn({ name: 'maid_chat_1337', display: 'Nickname', role: 'builder' })])[0]?.text).toBe('1337');
  expect(pawnBadges([pawn({ name: 'maid_chan' })])[0]?.text).toBe('');
});

it('updates activity, retains danger and drafted cues, and omits offline or other-dimension pawns', () => {
  expect(pawnBadges([pawn({ job: null, drafted: true })])[0]).toMatchObject({
    activity: 'Drafted',
    icon: GLYPHS.drafted,
    color: '#ff9f43',
  });
  expect(pawnBadges([pawn({ job: { type: 'sleep', label: 'Sleeping' }, hp: 4 })])[0]).toMatchObject({
    activity: 'Sleeping',
    icon: GLYPHS.bed,
    color: COLORS.danger,
  });
  expect(pawnBadges([pawn({ online: false }), pawn({ dim: -1 })])).toEqual([]);
});

it('keeps task labels short and strips formatting while giving idle pawns an activity icon', () => {
  const badge = pawnBadges([
    pawn({ job: { type: 'haul', label: '§aMoving   a very long stack of items to the farthest storage chest' } }),
  ])[0]!;
  expect(badge.activity).toMatch(/^Moving a very long stack of .*…$/);
  expect(badge.activity.length).toBeLessThanOrEqual(34);
  const idle = pawnBadges([pawn({ job: null, state: 'idle' })])[0]!;
  expect(idle.activity).toBe('idle');
  expect(idle.icon).toBeTruthy();
});
