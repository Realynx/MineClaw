/**
 * A small, safe markdown renderer for text from outside the app (GitHub release notes). It builds React elements only (never
 * HTML strings): headings, paragraphs, lists, quotes, fenced code, `code`, **bold**, *italic*, [links](https://…) and bare
 * https URLs. Raw HTML in the source stays visible text; links are http(s) only and open in a new tab without a referrer.
 */
import { Fragment, type ReactNode } from 'react';
import { cn } from '@/lib/utils';

type Block =
  | { kind: 'heading'; level: number; text: string }
  | { kind: 'para'; text: string }
  | { kind: 'list'; ordered: boolean; items: string[] }
  | { kind: 'quote'; text: string }
  | { kind: 'code'; text: string }
  | { kind: 'rule' };

const LIST_ITEM = /^\s{0,3}([-*+]|\d{1,9}[.)])\s+(.*)$/;

/** Splits markdown into blocks (exported for tests). */
export function parseBlocks(source: string): Block[] {
  const lines = source.replace(/\r\n?/g, '\n').split('\n');
  const out: Block[] = [];
  let para: string[] = [];
  const flush = () => {
    if (para.length) out.push({ kind: 'para', text: para.join(' ') });
    para = [];
  };
  for (let i = 0; i < lines.length; i++) {
    const line = lines[i]!;
    const fence = /^\s{0,3}(```|~~~)/.exec(line);
    if (fence) {
      flush();
      const code: string[] = [];
      for (i++; i < lines.length && !lines[i]!.trimStart().startsWith(fence[1]!); i++) code.push(lines[i]!);
      out.push({ kind: 'code', text: code.join('\n') });
      continue;
    }
    if (!line.trim()) {
      flush();
      continue;
    }
    const heading = /^\s{0,3}(#{1,6})\s+(.*?)\s*#*\s*$/.exec(line);
    if (heading) {
      flush();
      out.push({ kind: 'heading', level: heading[1]!.length, text: heading[2]! });
      continue;
    }
    if (/^\s{0,3}([-*_])(\s*\1){2,}\s*$/.test(line)) {
      flush();
      out.push({ kind: 'rule' });
      continue;
    }
    if (/^\s{0,3}>/.test(line)) {
      flush();
      const quote: string[] = [];
      for (; i < lines.length && /^\s{0,3}>/.test(lines[i]!); i++) quote.push(lines[i]!.replace(/^\s{0,3}>\s?/, ''));
      i--;
      out.push({ kind: 'quote', text: quote.join(' ') });
      continue;
    }
    const item = LIST_ITEM.exec(line);
    if (item) {
      flush();
      const ordered = /\d/.test(item[1]!);
      const items: string[] = [];
      for (; i < lines.length; i++) {
        const l = lines[i]!;
        const m = LIST_ITEM.exec(l);
        if (m && /\d/.test(m[1]!) === ordered) items.push(m[2]!);
        else if (l.trim() && /^\s+/.test(l) && items.length) items[items.length - 1] += ` ${l.trim()}`;
        else break;
      }
      i--;
      out.push({ kind: 'list', ordered, items });
      continue;
    }
    para.push(line.trim());
  }
  flush();
  return out;
}

/** Only absolute http(s) links are followed. */
export function safeHref(url: string): string | null {
  try {
    const u = new URL(url);
    return u.protocol === 'https:' || u.protocol === 'http:' ? u.href : null;
  } catch {
    return null;
  }
}

// Underscore emphasis only at word edges, so snake_case identifiers stay plain text.
const INLINE = /(`[^`]+`)|(\*\*[^*]+\*\*|(?<![A-Za-z0-9])__[^_]+__(?![A-Za-z0-9]))|(\*[^*\s][^*]*\*|(?<![A-Za-z0-9])_[^_\s][^_]*_(?![A-Za-z0-9]))|(\[[^\]]+\]\([^)\s]+\))|(https?:\/\/[^\s<>()]+[^\s<>().,;:!?'"])/g;

function link(href: string | null, text: ReactNode, key: number): ReactNode {
  if (!href) return <Fragment key={key}>{text}</Fragment>;
  return (
    <a key={key} href={href} target="_blank" rel="noopener noreferrer" className="text-accent underline-offset-2 hover:underline">
      {text}
    </a>
  );
}

function inline(text: string): ReactNode[] {
  const out: ReactNode[] = [];
  let last = 0;
  let key = 0;
  for (const m of text.matchAll(INLINE)) {
    const at = m.index;
    if (at > last) out.push(text.slice(last, at));
    const [all, code, strong, em, md, bare] = m;
    if (code) out.push(<code key={key++} className="rounded bg-surface-2 px-1 py-px font-mono text-[0.9em]">{code.slice(1, -1)}</code>);
    else if (strong) out.push(<strong key={key++} className="font-semibold text-fg">{inline(strong.slice(2, -2))}</strong>);
    else if (em) out.push(<em key={key++}>{inline(em.slice(1, -1))}</em>);
    else if (md) {
      const split = md.lastIndexOf('](');
      out.push(link(safeHref(md.slice(split + 2, -1)), inline(md.slice(1, split)), key++));
    } else if (bare) out.push(link(safeHref(bare), bare, key++));
    else out.push(all);
    last = at + all.length;
  }
  if (last < text.length) out.push(text.slice(last));
  return out;
}

const headingClass = ['text-base font-semibold', 'text-sm font-semibold', 'text-sm font-semibold', 'text-[13px] font-semibold'];

export function Markdown({ source, className }: { source: string; className?: string }) {
  const blocks = parseBlocks(source);
  return (
    <div className={cn('space-y-2 text-sm break-words text-muted', className)}>
      {blocks.map((b, i) => {
        switch (b.kind) {
          case 'heading': {
            const Tag = `h${Math.min(6, b.level + 2)}` as 'h3';
            return (
              <Tag key={i} className={cn('pt-1 text-fg', headingClass[Math.min(3, b.level - 1)])}>
                {inline(b.text)}
              </Tag>
            );
          }
          case 'list': {
            const List = b.ordered ? 'ol' : 'ul';
            return (
              <List key={i} className={cn('space-y-0.5 pl-5', b.ordered ? 'list-decimal' : 'list-disc')}>
                {b.items.map((it, j) => (
                  <li key={j}>{inline(it)}</li>
                ))}
              </List>
            );
          }
          case 'quote':
            return (
              <blockquote key={i} className="border-l-2 border-border pl-3 italic">
                {inline(b.text)}
              </blockquote>
            );
          case 'code':
            return (
              <pre key={i} className="overflow-x-auto rounded-md bg-surface-2 p-2 font-mono text-xs text-fg">
                {b.text}
              </pre>
            );
          case 'rule':
            return <hr key={i} className="border-border" />;
          default:
            return <p key={i}>{inline(b.text)}</p>;
        }
      })}
    </div>
  );
}
