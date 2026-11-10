/**
 * A mine design queued in parts: one `story.builds` add per mine item, in order (the dig top-down, then the blocks put in), all
 * with the design's group tag, so no request passes the supervisor's 64 KiB Story body cap. The bot turns each mine item into
 * its own build and does not make mine items wait for one another, so the queue ends up as one request would have left it.
 * AI bodies in the selection get one directive, with the first part: each directive is a chat line that wakes the body's
 * brain, and a newer build directive replaces the one before, so a directive per part would cost a turn each for nothing.
 * Like every other command (release.ts `runShown`), the line is said at once from the plan and again from the answers, and the
 * outcome is returned so the caller settles the mine's pending holograms by it.
 */
import type { StoryBuildItem } from '@mineclaw/contracts';
import { api, errorText } from '@/lib/api/client';
import { newRequestId } from '@/lib/utils';
import { dispatch } from '../model';
import { storyWriteError } from '../optimistic';
import type { RunResult, Story } from './commands';

type MineQueue = Pick<Story, 'canWrite' | 'split' | 'write' | 'say'>;

type Sent =
  | { ok: true; out: unknown; summary: string; warnings: string[]; ai: boolean; aiError: string | null }
  | { ok: false; error: unknown; aiError: string | null };

/** One part's plan: to the colony, and with `toAi` to the AI bodies too. */
const partPlan = (c: MineQueue, items: StoryBuildItem[], toAi: boolean, requestId: string) =>
  dispatch(
    c.split,
    { route: 'story.builds', body: { op: 'add', items } },
    toAi ? { kind: 'build', build: items } : null,
    requestId,
    'queued',
  );

/**
 * Sends one part to the director and, with `toAi`, to the AI bodies. Only the director's answer decides whether the part is
 * queued: a refused directive is said separately and never stops the colony's parts.
 */
async function sendPart(c: MineQueue, item: StoryBuildItem, toAi: boolean): Promise<Sent> {
  const items = [item];
  const plan = partPlan(c, items, toAi, newRequestId());
  const [colony, ai] = await Promise.allSettled([
    plan.director ? c.write('story.builds', { op: 'add', items }) : Promise.resolve(null),
    plan.directive ? api('story.directives', { body: plan.directive }) : Promise.resolve(null),
  ]);
  const aiError =
    ai.status === 'rejected' && plan.directive
      ? `${plan.directive.bots.join(', ')}: the AI directive failed · ${errorText(ai.reason)}`
      : null;
  if (colony.status === 'rejected') return { ok: false, error: colony.reason, aiError };
  const ok = { ok: true as const, out: colony.value, summary: plan.summary, warnings: colony.value?.warnings ?? [] };
  return { ...ok, ai: plan.directive !== null, aiError };
}

/**
 * Queues `items` one request at a time and stops at the first failure ("queued N of M parts"). A line says it at once from the
 * plan, and one more how it went. Resolves to the colony's outcome: the last part's answer (its `rev` carries every part
 * before it), or the first failed part's error (`error` null: nothing was sent).
 */
export async function releaseMine(
  c: MineQueue,
  items: readonly StoryBuildItem[],
  name: string,
  blocks: number,
): Promise<RunResult> {
  if (!c.canWrite) {
    c.say('Your account can watch Story Mode but not give orders (needs the story role)', 'warn');
    return { ok: false, error: null };
  }
  const total = items.length;
  const head = `Queued ${name}: ${blocks} blocks${total > 1 ? ` in ${total} parts` : ''}`;
  if (c.split && total) c.say([head, partPlan(c, [items[0]!], true, '').summary].join(' · '));
  let out: unknown = null;
  const warnings: string[] = [];
  let summary = '';
  let aiError: string | null = null;
  let toAi = false;
  for (let i = 0; i < total; i++) {
    const sent = await sendPart(c, items[i]!, i === 0);
    aiError ??= sent.aiError;
    if (!sent.ok) {
      const stopped = `${name}: queued ${i} of ${total} parts, then stopped · ${storyWriteError(sent.error).text}`;
      c.say([stopped, aiError].filter(Boolean).join(' · '), 'warn');
      return { ok: false, error: sent.error };
    }
    out = sent.out;
    if (i === 0) {
      summary = sent.summary;
      toAi = sent.ai;
    }
    warnings.push(...sent.warnings);
  }
  const aiPart = total > 1 && toAi && !aiError ? `the AI got part 1 of ${total}` : '';
  // Parts often answer with the same warning: said once.
  const lines = [head, summary, aiPart, ...new Set(warnings), aiError].filter(Boolean);
  c.say(lines.join(' · '), warnings.length || aiError ? 'warn' : undefined);
  return { ok: true, out };
}
