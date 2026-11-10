/**
 * A formation (mode/actions.ts `formation`): one right-drag is one `story.orders` request carrying every pawn's move, so the
 * gesture costs one write, one catch-up and one toast; a refusal is one message. A bot from before `moves` refuses them with
 * "pawns required", and the formation then goes one order per pawn as it used to.
 */
import { StoryOrderRequest } from '@mineclaw/contracts';
import { describe, expect, it, vi } from 'vitest';
import { ApiError } from '@/lib/api/client';
import { formation } from '@/features/story/mode/actions';
import type { Story } from '@/features/story/mode/commands';
import type { FormationMove } from '@/features/story/StoryMap';

const MOVES: FormationMove[] = Array.from({ length: 12 }, (_, i) => ({ pawn: `p${i}`, target: [i * 2, 64, -3] }));

function story(write: (route: string, body: unknown) => Promise<unknown>) {
  const say = vi.fn();
  const w = vi.fn(write);
  const c = { canWrite: true, say, write: w } as unknown as Story;
  return { c, say, write: w };
}

describe('formation', () => {
  it('sends N moves in one request and says one toast', async () => {
    const { c, say, write } = story(() => Promise.resolve({ ids: MOVES.map((m) => m.pawn), rev: 41 }));
    await formation(c, MOVES, false, 0);
    expect(write).toHaveBeenCalledTimes(1);
    const [route, body] = write.mock.calls[0]!;
    expect(route).toBe('story.orders');
    expect(StoryOrderRequest.parse(body)).toEqual(body);
    expect(body).toEqual({
      moves: MOVES.map((m) => ({ pawn: m.pawn, target: { x: m.target[0], y: m.target[1], z: m.target[2] } })),
    });
    expect(say).toHaveBeenCalledTimes(1);
    expect(say).toHaveBeenCalledWith('Formation: 12 pawns along the line');
  });

  it('a queued formation carries queue once, beside the moves', async () => {
    const { c, say, write } = story(() => Promise.resolve({ ids: ['p0', 'p1'], rev: 3 }));
    await formation(c, MOVES.slice(0, 2), true, 1);
    expect(write).toHaveBeenCalledTimes(1);
    expect(write.mock.calls[0]![1]).toMatchObject({ queue: true, moves: [{ pawn: 'p0' }, { pawn: 'p1' }] });
    expect(say).toHaveBeenCalledWith(
      'Queued a formation: 2 pawns along the line · 1 undrafted pawn not in it (draft with R)',
    );
  });

  it('a refusal is one warning, with the busy wording for a busy colony', async () => {
    const busy = new ApiError(503, 'busy: the colony did not take the change', { code: 'busy', retryAfterMs: 1000 });
    const { c, say, write } = story(() => Promise.reject(busy));
    await formation(c, MOVES, false, 0);
    expect(write).toHaveBeenCalledTimes(1);
    expect(say).toHaveBeenCalledTimes(1);
    expect(say).toHaveBeenCalledWith(
      'The formation was not given: The colony is busy; the change was not saved.',
      'warn',
    );

    const unknownPawn = story(() => Promise.reject(new ApiError(400, "no pawn 'p3'", {})));
    await formation(unknownPawn.c, MOVES, false, 0);
    expect(unknownPawn.write).toHaveBeenCalledTimes(1);
    expect(unknownPawn.say).toHaveBeenCalledWith("The formation was not given: no pawn 'p3'", 'warn');
  });

  it('an unknown outcome is not called a refusal: the colony may still have given it', async () => {
    for (const e of [
      new ApiError(503, 'The bot is not reachable right now.', { code: 'upstream_unavailable' }),
      new ApiError(502, 'upstream error', { code: 'upstream_error' }),
      new TypeError('fetch failed'),
    ]) {
      const { c, say, write } = story(() => Promise.reject(e));
      await formation(c, MOVES, false, 0);
      expect(write).toHaveBeenCalledTimes(1);
      expect(say).toHaveBeenCalledTimes(1);
      const [text, tone] = say.mock.calls[0]! as [string, string];
      expect(tone).toBe('warn');
      expect(text).toMatch(/^Could not confirm the formation \(.+\); the pawns may still take it\.$/);
      expect(text).not.toContain('was not given');
    }
  });

  it('a bot from before `moves` gets one order per pawn', async () => {
    const { c, say, write } = story((_route, body) =>
      (body as { moves?: unknown }).moves
        ? Promise.reject(new ApiError(400, 'pawns required', {}))
        : Promise.resolve({ ids: ['x'] }),
    );
    await formation(c, MOVES.slice(0, 3), false, 0);
    expect(write).toHaveBeenCalledTimes(4);
    expect(write.mock.calls.slice(1).map((call) => call[1])).toEqual(
      MOVES.slice(0, 3).map((m) => ({
        pawns: [m.pawn],
        order: { kind: 'move', target: { x: m.target[0], y: m.target[1], z: m.target[2] } },
      })),
    );
    expect(say).toHaveBeenCalledTimes(1);
    expect(say).toHaveBeenCalledWith('Formation: 3 pawns along the line');
  });

  it('needs the story role and sends nothing without it', async () => {
    const { c, say, write } = story(() => Promise.resolve({}));
    (c as { canWrite: boolean }).canWrite = false;
    await formation(c, MOVES, false, 0);
    expect(write).not.toHaveBeenCalled();
    expect(say).toHaveBeenCalledWith('Needs the story role', 'warn');
  });
});
