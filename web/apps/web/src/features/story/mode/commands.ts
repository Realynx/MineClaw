/**
 * Story Mode's memoised commands (they are props of the HUD and inputs of effects): run an action, focus a body, draft,
 * set the time, pause, set the speed, pick a chest on the map. `useStory` adds them to the screen state: the `Story` object
 * every action, the key map and the HUD take.
 */
import { tickClock, type StoryDirectiveSpec, type StorySpeed } from '@mineclaw/contracts';
import { useCallback } from 'react';
import { api, ApiError, errorText } from '@/lib/api/client';
import { newRequestId } from '@/lib/utils';
import { dispatch, type DirectorCall } from '../model';
import { catchUp } from '../useStory';
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

/** Runs one action: the director call for the colony and/or a directive for the AI bodies. */
function useRun({ canWrite, split, write, say }: StoryModeState) {
  return useCallback(
    async (
      director: DirectorCall | null,
      directive: StoryDirectiveSpec | null,
      verb: string,
      after?: (out: unknown) => string | null,
    ) => {
      if (!canWrite) {
        say('Your account can watch Story Mode but not give orders (needs the story role)', 'warn');
        return;
      }
      const plan = dispatch(split, director, directive, newRequestId(), verb);
      try {
        const [out] = await Promise.all([
          plan.director ? write(plan.director.route, plan.director.body as never) : Promise.resolve(null),
          plan.directive ? api('story.directives', { body: plan.directive }) : Promise.resolve(null),
        ]);
        const head = after?.(out);
        // A designation's or a build's answer may warn (e.g. targets in a preserved zone): said with it.
        const warnings = (out as { warnings?: string[] | null } | null)?.warnings ?? [];
        say([head, plan.summary, ...warnings].filter(Boolean).join(' · '), warnings.length ? 'warn' : undefined);
      } catch (e) {
        say(errorText(e), 'warn');
      }
    },
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
        (e) => say(errorText(e), 'warn'),
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
    togglePause: useTogglePause(s),
    setSpeed: useSetSpeed(s),
    pickChest: usePickChest(s),
  };
}

export type Story = ReturnType<typeof useStory>;
