/**
 * Mock mode (NEXT_PUBLIC_MOCK=1): the world viewer's own fixture world (`createFixtureSource` from
 * @mineclaw/world-viewer/testing: an island, a tunnel, a lit room, mobs, a drop, a growing build plan) with its bots renamed
 * to the mock hive's bodies, so thumbnails and the follow camera line up with the rest of the mock data. One instance per
 * page (Story Mode's mock director reads the same entities), and the director's block changes (its camp, felled trees,
 * built ghosts) ride along in the block deltas.
 */
import type { BlockDeltas, BotDto, EntityDelta, WorldSource } from '@mineclaw/world-viewer';
import { createFixtureSource, surface } from '@mineclaw/world-viewer/testing';
import { allOverrides, pendingBlocks, storySim } from '@/mocks/data/story';
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

/** Facing per pawn, from its last step (pawns turn the way they walk). */
const pawnYaw = new Map<string, { x: number; z: number; yaw: number }>();

/**
 * The mock colonies' online pawns as bodies of the world, as the real hive lists its in-process bodies (a pawn's player is a
 * bot, never an entity): the map draws them as player models, walking where the director moves them.
 */
function pawnBots(base: BotDto | undefined): BotDto[] {
  if (!base) return [];
  const out: BotDto[] = [];
  for (const c of storySim.colonies.values()) {
    if (c.released) continue;
    for (const sim of c.pawns.values()) {
      const pw = sim.p;
      if (pw.online === false || (pw.dim ?? 0) !== 0) continue;
      const x = sim.x,
        y = sim.y,
        z = sim.z;
      const last = pawnYaw.get(pw.name);
      let yaw = last?.yaw ?? 0;
      if (last && Math.hypot(x - last.x, z - last.z) > 0.05) yaw = (Math.atan2(-(x - last.x), z - last.z) * 180) / Math.PI;
      pawnYaw.set(pw.name, { x, z, yaw });
      out.push({
        ...base,
        name: pw.name,
        short: pw.name.replace(/^maid_/, ''),
        main: false,
        dim: 0,
        x,
        y,
        z,
        yaw,
        hp: pw.hp ?? 20,
        food: pw.food ?? 20,
        job: pw.job?.type ?? null,
        held: pw.held ?? null,
        equipment: null,
      });
    }
  }
  return out;
}

let shared: WorldSource | null = null;
let resendUntil = 0;
let playerOf: (() => string | null | undefined) | undefined;

export function createMockWorldSource(player?: () => string | null | undefined): WorldSource {
  if (player) playerOf = player;
  if (shared) return shared;
  const src = createFixtureSource({ radius: 8, ticksPerSecond: 60 });
  shared = {
    ...src,
    // The mock stream's feed (viewer lines, the bots' replies) bubbles over the renamed bodies like the real one.
    chat: (onSay) => subscribeChat(onSay, () => playerOf?.()),
    async entities(since?: number): Promise<EntityDelta> {
      const d = await src.entities(since);
      const bots = d.bots.map((b) => (RENAME[b.name] ? { ...b, ...RENAME[b.name] } : b));
      return { ...d, bots: [...bots, ...extras(bots), ...pawnBots(bots.find((b) => b.name === 'maid_chan'))] };
    },
    async chunks(dim, cols) {
      const out = await src.chunks(dim, cols);
      // Freshly loaded columns come from the fixture: re-send the director's changes for a moment so they land on them.
      resendUntil = Date.now() + 2500;
      return out;
    },
    async blocks(since?: number): Promise<BlockDeltas> {
      const d = await src.blocks!(since);
      // The first reply only sets the viewer's revision; changes ride on the later ones.
      const resend = Date.now() < resendUntil;
      if (!since || (pendingBlocks.length === 0 && !resend)) return d;
      const fresh = pendingBlocks.splice(0, pendingBlocks.length);
      const story = resend ? allOverrides() : fresh;
      const blocks = { ...(d.blocks ?? {}) } as Record<string, Array<[number, number, number, number, number]>>;
      blocks['0'] = [...(blocks['0'] ?? []), ...story];
      return { ...d, rev: Math.max(d.rev, since), blocks };
    },
  };
  return shared;
}
