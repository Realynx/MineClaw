import type { BotStatusView, HiveTreeNode } from '@mineclaw/contracts';
import { hashOf } from '@/features/story/visuals';
import { workAppearance } from '@/features/story/workAppearance';

/** Main's gold is reserved; other minds have a stable blue/green/violet identity. */
export const MAIN_MIND_COLOR = '#f5c542';
const MIND_COLORS = ['#a78bfa', '#5ec8e5', '#58d6ae', '#ee87c6', '#8aa9ff', '#bf9be8'];
export function mindColor(node: Pick<HiveTreeNode, 'name' | 'kind'>): string {
  return node.kind === 'main' ? MAIN_MIND_COLOR : MIND_COLORS[hashOf(node.name) % MIND_COLORS.length]!;
}

const WORK_TEXT: Array<[string, RegExp]> = [
  ['flee', /\b(flee|fleeing|retreat|escaping)\b/i],
  ['deconstruct', /\b(deconstruct\w*|demolish\w*|dismantl\w*)\b/i],
  ['attack', /\b(attack\w*|fight\w*|combat)\b/i],
  ['guard', /\b(guard\w*|patrol\w*|drafted)\b/i],
  ['chop', /\b(chop\w*|lumber\w*|fell\w*|stripping)\b/i],
  ['mine', /\b(mining|mine|dig\w*|quarry|excavat\w*)\b/i],
  ['build', /\b(build\w*|plac\w*|construct\w*|repair\w*)\b/i],
  ['smelt', /\b(smelt\w*|furnace\w*|fuel\w*)\b/i],
  ['cook', /\b(cook\w*)\b/i],
  ['craft', /\b(craft\w*)\b/i],
  ['fish', /\b(fish\w*)\b/i],
  ['harvest', /\b(harvest\w*|farm\w*|plant\w*|sow\w*|replant\w*|tending)\b/i],
  ['haul', /\b(haul\w*|fetch\w*|deliver\w*|deposit\w*|sorting|organiz\w*|storage|rescu\w*)\b/i],
  ['sleep', /\b(sleep\w*|rest\w*|bed)\b/i],
  ['move', /\b(walk\w*|mov\w*|heading|travel\w*|scout\w*|path\w*)\b/i],
];

/** Live telemetry is newer than the hierarchy's standing assignment (e.g. "story colony"). */
export function nodeActivity(node: HiveTreeNode, live?: BotStatusView) {
  const label = (live ? live.task : node.task)?.replace(/§./g, '').trim() || 'Idle';
  const kind = WORK_TEXT.find(([, pattern]) => pattern.test(label))?.[0] ?? 'idle';
  return { label, kind, ...workAppearance(kind) };
}
