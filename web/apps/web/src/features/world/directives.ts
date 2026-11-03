/**
 * In-world directives (the free-cam crosshair + chat bar): pure helpers the world pane uses, kept apart for unit tests.
 * A directive is ordinary chat: the bot reads the words, nothing here adds bot-side command semantics.
 */
import type { ChatAccepted } from '@mineclaw/contracts';
import { api } from '@/lib/api/client';
import { newRequestId } from '@/lib/utils';

/** True when a key event belongs to a text field (the chat bar, the go-to box…): world shortcuts must leave it alone. */
export function isTyping(target: EventTarget | null): boolean {
  if (!target || typeof (target as Element).closest !== 'function') return false;
  const el = target as HTMLElement;
  if (el.isContentEditable) return true;
  return !!el.closest('input, textarea, select, [contenteditable=""], [contenteditable="true"]');
}

export type DirectiveKind = 'come' | 'build' | 'mine' | 'stay';

export const DIRECTIVES: Array<{ kind: DirectiveKind; label: string }> = [
  { kind: 'come', label: 'Come here' },
  { kind: 'build', label: 'Build here' },
  { kind: 'mine', label: 'Mine here' },
  { kind: 'stay', label: 'Stay here' },
];

/** Natural phrasing plus the block's coordinates, e.g. "come here (12, 64, -3)". */
export function directiveText(kind: DirectiveKind, p: readonly [number, number, number]): string {
  const at = `(${p[0]}, ${p[1]}, ${p[2]})`;
  switch (kind) {
    case 'come':
      return `come here ${at}`;
    case 'build':
      return `build here ${at}`;
    case 'mine':
      return `mine here ${at}`;
    case 'stay':
      return `stay here ${at}`;
  }
}

/** A crosshair click on a bot: plain = select only it, Shift = add/remove it, Ctrl = clear the selection. */
export function nextSelection(
  current: readonly string[],
  name: string | null,
  mods: { shift: boolean; ctrl: boolean },
): string[] {
  if (mods.ctrl) return [];
  if (!name) return [...current];
  if (mods.shift) return current.includes(name) ? current.filter((n) => n !== name) : [...current, name];
  return [name];
}

/** Number key n (1–9): the n-th bot in view (nearest first); Shift adds it to the selection instead of replacing it. */
export function selectByNumber(
  current: readonly string[],
  inView: readonly string[],
  n: number,
  shift: boolean,
): string[] {
  const name = inView[n - 1];
  if (!name) return [...current];
  return nextSelection(current, name, { shift, ctrl: false });
}

export interface SendResult {
  bot: string;
  ok: boolean;
  error?: string;
}

type Post = (bot: string, body: { text: string; requestId: string }) => Promise<ChatAccepted>;

const postChat: Post = (bot, body) => api('bots.chat', { params: { bot }, body });

/**
 * Sends one line, as the signed-in user's linked player (the server adds who; the `chat` role is required), to every target
 * bot: one request per bot, each with its own request id, in parallel. Never throws.
 */
export async function sendDirective(
  bots: readonly string[],
  text: string,
  post: Post = postChat,
): Promise<SendResult[]> {
  const t = text.trim();
  if (!t || bots.length === 0) return [];
  return Promise.all(
    [...new Set(bots)].map(async (bot) => {
      try {
        const r = await post(bot, { text: t, requestId: newRequestId() });
        return r.accepted ? { bot, ok: true } : { bot, ok: false, error: r.note ?? 'not accepted' };
      } catch (e) {
        return { bot, ok: false, error: e instanceof Error ? e.message : String(e) };
      }
    }),
  );
}
