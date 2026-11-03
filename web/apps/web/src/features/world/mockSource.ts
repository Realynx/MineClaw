/**
 * Mock mode (NEXT_PUBLIC_MOCK=1): the world viewer's own fixture world (`createFixtureSource` from
 * @mineclaw/world-viewer/testing: an island, a tunnel, a lit room, mobs, a drop, a growing build plan) with its bots renamed
 * to the mock hive's bodies, so thumbnails and the follow camera line up with the rest of the mock data.
 */
import type { BotDto, EntityDelta, WorldSource } from '@mineclaw/world-viewer';
import { createFixtureSource } from '@mineclaw/world-viewer/testing';

const RENAME: Record<string, { name: string; short: string }> = {
  Alice: { name: 'maid_chan', short: 'main' },
  Bob: { name: 'maid_chan_67', short: '67' },
};

/** Extra mock bodies placed near the fixture's bots. */
function extras(bots: BotDto[]): BotDto[] {
  const a = bots.find((b) => b.name === 'maid_chan');
  if (!a) return [];
  return [
    { ...a, name: 'maid_chan_68', short: '68', main: false, x: a.x - 6, z: a.z + 5, yaw: ((a.yaw ?? 0) + 120) % 360, job: 'tending the wheat farm', held: 'iron hoe' },
    { ...a, name: 'maid_chan_70', short: '70', main: false, x: a.x + 4, z: a.z - 7, yaw: ((a.yaw ?? 0) + 240) % 360, job: 'sorting chests', held: 'chest' },
  ];
}

export function createMockWorldSource(): WorldSource {
  const src = createFixtureSource({ radius: 8, ticksPerSecond: 60 });
  return {
    ...src,
    async entities(since?: number): Promise<EntityDelta> {
      const d = await src.entities(since);
      const bots = d.bots.map((b) => (RENAME[b.name] ? { ...b, ...RENAME[b.name] } : b));
      return { ...d, bots: [...bots, ...extras(bots)] };
    },
  };
}
