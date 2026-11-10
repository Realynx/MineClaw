/**
 * A mine design queued in parts: one `story.builds` add per mine item, in order (the dig top-down, then the blocks put in), all
 * with the design's group tag, so no request passes the supervisor's 64 KiB Story body cap. The bot turns each mine item into
 * its own build and does not make mine items wait for one another, so the queue ends up as one request would have left it.
 * AI bodies in the selection get one directive, with the first part: each directive is a chat line that wakes the body's
 * brain, and a newer build directive replaces the one before, so a directive per part would cost a turn each for nothing.
 */
import type { StoryBuildItem } from '@mineclaw/contracts';
import { api, errorText } from '@/lib/api/client';
import { newRequestId } from '@/lib/utils';
import { dispatch } from '../model';
import type { Story } from './commands';

type MineQueue = Pick<Story, 'canWrite' | 'split' | 'write' | 'say'>;

type Sent =
  | { ok: true; summary: string; warnings: string[]; ai: boolean; aiError: string | null }
  | { ok: false; error: string; aiError: string | null };

/**
 * Sends one part to the director and, with `toAi`, to the AI bodies. Only the director's answer decides whether the part is
 * queued: a refused directive is said separately and never stops the colony's parts.
 */
async function sendPart(c: MineQueue, item: StoryBuildItem, toAi: boolean): Promise<Sent> {
  const items = [item];
  const plan = dispatch(
    c.split,
    { route: 'story.builds', body: { op: 'add', items } },
    toAi ? { kind: 'build', build: items } : null,
    newRequestId(),
    'queued',
  );
  const [colony, ai] = await Promise.allSettled([
    plan.director ? c.write('story.builds', { op: 'add', items }) : Promise.resolve(null),
    plan.directive ? api('story.directives', { body: plan.directive }) : Promise.resolve(null),
  ]);
  const aiError =
    ai.status === 'rejected' && plan.directive
      ? `${plan.directive.bots.join(', ')}: the AI directive failed · ${errorText(ai.reason)}`
      : null;
  if (colony.status === 'rejected') return { ok: false, error: errorText(colony.reason), aiError };
  const ok = { ok: true as const, summary: plan.summary, warnings: colony.value?.warnings ?? [] };
  return { ...ok, ai: plan.directive !== null, aiError };
}

/**
 * Queues `items` one request at a time and stops at the first failure ("queued N of M parts"). One line says how it went.
 * Resolves to the number of parts queued.
 */
export async function releaseMine(c: MineQueue, items: readonly StoryBuildItem[], name: string, blocks: number) {
  if (!c.canWrite) {
    c.say('Your account can watch Story Mode but not give orders (needs the story role)', 'warn');
    return 0;
  }
  const total = items.length;
  const warnings: string[] = [];
  let summary = '';
  let aiError: string | null = null;
  let toAi = false;
  for (let i = 0; i < total; i++) {
    const sent = await sendPart(c, items[i]!, i === 0);
    aiError ??= sent.aiError;
    if (!sent.ok) {
      const stopped = `${name}: queued ${i} of ${total} parts, then stopped · ${sent.error}`;
      c.say([stopped, aiError].filter(Boolean).join(' · '), 'warn');
      return i;
    }
    if (i === 0) {
      summary = sent.summary;
      toAi = sent.ai;
    }
    warnings.push(...sent.warnings);
  }
  const head = `Queued ${name}: ${blocks} blocks${total > 1 ? ` in ${total} parts` : ''}`;
  const aiPart = total > 1 && toAi && !aiError ? `the AI got part 1 of ${total}` : '';
  // Parts often answer with the same warning: said once.
  const lines = [head, summary, aiPart, ...new Set(warnings), aiError].filter(Boolean);
  c.say(lines.join(' · '), warnings.length || aiError ? 'warn' : undefined);
  return total;
}
