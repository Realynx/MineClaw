/**
 * Mock mode (NEXT_PUBLIC_MOCK=1): the world viewer's own fixture world (`createFixtureSource` from
 * @mineclaw/world-viewer/testing: an island, a tunnel, a lit room, mobs, a drop, a growing build plan) with its bots renamed
 * to the mock hive's bodies, so thumbnails and the follow camera line up with the rest of the mock data.
 */
import type { BotDto, EntityDelta, WorldSource } from '@mineclaw/world-viewer';
import { createFixtureSource, surface } from '@mineclaw/world-viewer/testing';
import { subscribeChat } from './chatBubbles';

const RENAME: Record<string, { name: string; short: string }> = {
  Alice: { name: 'maid_chan', short: 'main' },
  Bob: { name: 'maid_chan_67', short: '67' },
};

/** Extra mock bodies: standing still on the island's surface west of the house (the fixture's own bots walk about). */
function extras(bots: BotDto[]): BotDto[] {
  const a = bots.find((b) => b.name === 'maid_chan');
  if (!a) return [];
  const at = (name: string, short: string, x: number, z: number, yaw: number, job: string, held: string): BotDto => ({
    ...a,
    name,
    short,
    main: false,
    x: x + 0.5,
    y: surface(x, z) + 1,
    z: z + 0.5,
    yaw,
    job,
    held,
  });
  return [
    at('maid_chan_68', '68', -6, 18, 120, 'tending the wheat farm', 'iron hoe'),
    at('maid_chan_70', '70', 4, -7, 240, 'sorting chests', 'chest'),
    at('maid_chan_109', '109', -2, 22, 60, 'running the crew', 'iron axe'),
    // maid_chan_109's lumberjack crew (the mock hive's subhive bodies)
    ...['102', '114', '124', '126', '131', '135'].map((short, i) =>
      at(`maid_chan_${short}`, short, -12 + (i % 3) * 3, 24 + Math.floor(i / 3) * 3, (i * 70) % 360, 'chopping birch', 'iron axe'),
    ),
  ];
}

export function createMockWorldSource(player?: () => string | null | undefined): WorldSource {
  const src = createFixtureSource({ radius: 8, ticksPerSecond: 60 });
  return {
    ...src,
    // The mock stream's feed (viewer lines, the bots' replies) bubbles over the renamed bodies like the real one.
    chat: (onSay) => subscribeChat(onSay, player),
    async entities(since?: number): Promise<EntityDelta> {
      const d = await src.entities(since);
      const bots = d.bots.map((b) => (RENAME[b.name] ? { ...b, ...RENAME[b.name] } : b));
      return { ...d, bots: [...bots, ...extras(bots)] };
    },
  };
}
