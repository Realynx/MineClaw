/**
 * Story Mode's memoised commands (they are props of the HUD and inputs of effects): run an action, focus a body, draft,
 * set the time, pause, set the speed, pick a chest on the map. `useStory` adds them to the screen state: the `Story` object
 * every action, the key map and the HUD take.
 */
import { tickClock, type StoryDirectiveSpec, type StorySpeed, type StoryTimeRange } from '@mineclaw/contracts';
import { useCallback } from 'react';
import { api, ApiError, errorText } from '@/lib/api/client';
import { newRequestId } from '@/lib/utils';
import { dispatch, type DirectorCall } from '../model';
import { storyWriteError } from '../optimistic';
import { catchUp } from '../useStory';
import { runText } from './release';
import { useStoryModeState, type StoryModeState, type V3 } from './state';

/** What a speed change says. */
const SPEED_SAY: Record<StorySpeed, string> = {
  normal: 'Normal speed',
  fast: 'Fast: pawns move about twice as fast',
  faster: 'Faster: pawns move about three times as fast',
};

/** Why a time change was refused. */
function timeError(e: unknown): string {
  const status = e instanceof ApiError ? e.status : 0;
  if (status === 409) return 'No op account — op the main bot to change time';
  if (status === 429) return 'One time change a second: try again';
  return errorText(e);
}

/** How the colony's part of an action ended: its answer, or the error it threw (`error` null: nothing was sent). A colony
 * write that was not needed (only AI bodies were told) counts as ok with no answer. */
export type RunResult = { ok: true; out: unknown } | { ok: false; error: unknown };

/** What running an action needs from the screen state. */
export type RunDeps = Pick<StoryModeState, 'canWrite' | 'split' | 'write' | 'say'>;

/** Runs one action: the director call for the colony and/or a directive for the AI bodies. Returns the colony's outcome
 * (map marks drawn at once settle by it, mode/release.ts); a failed directive is said but does not change it. */
export async function runAction(
  { canWrite, split, write, say }: RunDeps,
  director: DirectorCall | null,
  directive: StoryDirectiveSpec | null,
  verb: string,
  after?: (out: unknown) => string | null,
): Promise<RunResult> {
  if (!canWrite) {
    say('Your account can watch Story Mode but not give orders (needs the story role)', 'warn');
    return { ok: false, error: null };
  }
  const plan = dispatch(split, director, directive, newRequestId(), verb);
  let aiError: unknown = null;
  const [res] = await Promise.all([
    plan.director
      ? write(plan.director.route, plan.director.body as never).then(
          (out): RunResult => ({ ok: true, out }),
          (error: unknown): RunResult => ({ ok: false, error }),
        )
      : Promise.resolve<RunResult>({ ok: true, out: null }),
    plan.directive ? api('story.directives', { body: plan.directive }).catch((e: unknown) => (aiError = e)) : null,
  ]);
  try {
    if (!res.ok) say(storyWriteError(res.error).text, 'warn');
    else if (aiError) say(storyWriteError(aiError).text, 'warn');
    else say(...runText(plan, res.out, after));
  } catch (e) {
    say(storyWriteError(e).text, 'warn');
  }
  return res;
}

function useRun({ canWrite, split, write, say }: StoryModeState) {
  return useCallback(
    (
      director: DirectorCall | null,
      directive: StoryDirectiveSpec | null,
      verb: string,
      after?: (out: unknown) => string | null,
    ) => runAction({ canWrite, split, write, say }, director, directive, verb, after),
    [canWrite, split, write, say],
  );
}

/** Centres the map on a body. */
function useFocusBody({ bodies, map }: StoryModeState) {
  return useCallback(
    (name: string) => {
      const b = bodies.find((x) => x.name === name);
      if (b?.pos) map.current?.focus(Math.floor(b.pos[0]!), Math.floor(b.pos[2]!), Math.floor(b.pos[1]!));
    },
    [bodies, map],
  );
}

function useDraft({ canWrite, write, say }: StoryModeState) {
  return useCallback(
    async (names: string[], drafted: boolean) => {
      if (!names.length) return;
      if (!canWrite) return say('Needs the story role', 'warn');
      await write('story.draft', { pawns: names, drafted }).then(
        () => say(`${drafted ? 'Drafted' : 'Undrafted'} ${names.length} pawn${names.length === 1 ? '' : 's'}`),
        (e) => say(storyWriteError(e).text, 'warn'),
      );
    },
    [canWrite, write, say],
  );
}

/** The world's time of day (through the bot's op account); the colony clock catches up after. */
function useSetTime({ canWrite, say, id, qc }: StoryModeState) {
  return useCallback(
    async (tick: number) => {
      if (!canWrite) return say('Changing the time needs the story role', 'warn');
      try {
        const out = await api('story.time', { body: { time: tick } });
        say(`Time set to ${tickClock(out.time)}${out.by ? ` via ${out.by}` : ''}`);
        if (id) void catchUp(qc, id).catch(() => undefined);
      } catch (e) {
        say(timeError(e), 'warn');
      }
    },
    [canWrite, say, id, qc],
  );
}

function useTogglePause({ state, canWrite, write, say }: StoryModeState) {
  return useCallback(() => {
    if (!state || !canWrite) return;
    void write('story.pause', { paused: !state.paused }).then(() =>
      say(state.paused ? 'Colony resumed' : 'Colony paused'),
    );
  }, [state, canWrite, write, say]);
}

function useSetTimeRange({ canWrite, say, id, qc }: StoryModeState) {
  return useCallback(
    async (range: StoryTimeRange | null) => {
      if (!canWrite) return false;
      try {
        await api('story.timeRange', { body: { range } });
        say(
          range
            ? `Time loops from ${tickClock(range.start)} to ${tickClock(range.end)}`
            : 'Full day/night cycle restored',
        );
        if (id) await catchUp(qc, id);
        return true;
      } catch (e) {
        say(timeError(e), 'warn');
        return false;
      }
    },
    [canWrite, say, id, qc],
  );
}

/** Fast / Faster: a movement-speed boost for the colony's pawns (docs/web/story-mode.md "Speed"). */
function useSetSpeed({ state, canWrite, write, say }: StoryModeState) {
  return useCallback(
    (speed: StorySpeed) => {
      if (!state || !canWrite || state.options?.speed === speed) return;
      void write('story.options', { speed }).then(
        () => say(SPEED_SAY[speed]),
        (e) => say(errorText(e), 'warn'),
      );
    },
    [state, canWrite, write, say],
  );
}

/** Waits for a chest clicked on the map (null when cancelled). */
function usePickChest({ picking, setPickHint }: StoryModeState) {
  return useCallback(
    () =>
      new Promise<V3 | null>((resolve) => {
        picking.current = resolve;
        setPickHint(true);
      }),
    [picking, setPickHint],
  );
}

/** The screen state plus its memoised commands. */
export function useStory() {
  const s = useStoryModeState();
  return {
    ...s,
    run: useRun(s),
    focusBody: useFocusBody(s),
    draft: useDraft(s),
    setTime: useSetTime(s),
    setTimeRange: useSetTimeRange(s),
    togglePause: useTogglePause(s),
    setSpeed: useSetSpeed(s),
    pickChest: usePickChest(s),
  };
}

export type Story = ReturnType<typeof useStory>;
