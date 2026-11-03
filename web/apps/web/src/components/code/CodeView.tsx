'use client';

/**
 * Read-only script source (CodeMirror 6): syntax highlighting (Lua / JS), lint marks, and the running line highlighted and
 * followed as it moves. Line changes are dispatched as state effects, so the editor is never rebuilt while a script runs.
 */
import { javascript } from '@codemirror/lang-javascript';
import { HighlightStyle, StreamLanguage, syntaxHighlighting } from '@codemirror/language';
import { lua } from '@codemirror/legacy-modes/mode/lua';
import { EditorState, RangeSetBuilder, StateEffect, StateField, type Extension } from '@codemirror/state';
import { Decoration, EditorView, GutterMarker, gutterLineClass, lineNumbers, type DecorationSet } from '@codemirror/view';
import { tags as t } from '@lezer/highlight';
import { useEffect, useRef } from 'react';
import { cn } from '@/lib/utils';

export interface LintMark {
  line: number;
  severity: string;
  message: string;
}

const highlight = HighlightStyle.define([
  { tag: [t.keyword, t.controlKeyword, t.operatorKeyword, t.definitionKeyword], color: 'var(--code-kw)', fontWeight: '500' },
  { tag: [t.string, t.special(t.string)], color: 'var(--code-str)' },
  { tag: [t.number, t.bool, t.null, t.atom], color: 'var(--code-num)' },
  { tag: [t.comment, t.lineComment, t.blockComment], color: 'var(--code-comment)', fontStyle: 'italic' },
  { tag: [t.function(t.variableName), t.function(t.propertyName)], color: 'var(--code-fn)' },
  { tag: [t.variableName, t.propertyName], color: 'var(--text)' },
  { tag: [t.operator, t.punctuation, t.bracket], color: 'var(--text-muted)' },
  { tag: [t.standard(t.variableName)], color: 'var(--code-fn)' },
]);

const setRunning = StateEffect.define<number | null>();
const setLint = StateEffect.define<LintMark[]>();

const runningLine = Decoration.line({ class: 'cm-running-line' });

const runningField = StateField.define<{ line: number | null; deco: DecorationSet }>({
  create: () => ({ line: null, deco: Decoration.none }),
  update(value, tr) {
    let line = value.line;
    for (const e of tr.effects) if (e.is(setRunning)) line = e.value;
    if (line === value.line && !tr.docChanged) return value;
    if (line === null || line < 1 || line > tr.state.doc.lines) return { line, deco: Decoration.none };
    const pos = tr.state.doc.line(line).from;
    return { line, deco: Decoration.set([runningLine.range(pos)]) };
  },
  provide: (f) => EditorView.decorations.from(f, (v) => v.deco),
});

class ClassMarker extends GutterMarker {
  override elementClass: string;
  constructor(readonly cls: string) {
    super();
    this.elementClass = cls;
  }
  override eq(other: GutterMarker): boolean {
    return other instanceof ClassMarker && other.cls === this.cls;
  }
}
const runningGutter = new ClassMarker('cm-running-gutter');

const lintField = StateField.define<DecorationSet>({
  create: () => Decoration.none,
  update(value, tr) {
    for (const e of tr.effects) {
      if (!e.is(setLint)) continue;
      const builder = new RangeSetBuilder<Decoration>();
      const marks = [...e.value].filter((m) => m.line >= 1 && m.line <= tr.state.doc.lines).sort((a, b) => a.line - b.line);
      for (const m of marks) {
        const l = tr.state.doc.line(m.line);
        if (l.length === 0) continue;
        builder.add(
          l.from + (l.text.length - l.text.trimStart().length),
          l.to,
          Decoration.mark({ class: m.severity === 'error' ? 'cm-lint-error' : 'cm-lint-warn', attributes: { title: m.message } }),
        );
      }
      return builder.finish();
    }
    return value.map(tr.changes);
  },
  provide: (f) => EditorView.decorations.from(f),
});

const runningGutterExt = gutterLineClass.compute([runningField], (state) => {
  const { line } = state.field(runningField);
  const b = new RangeSetBuilder<GutterMarker>();
  if (line !== null && line >= 1 && line <= state.doc.lines) b.add(state.doc.line(line).from, state.doc.line(line).from, runningGutter);
  return b.finish();
});

const baseTheme = EditorView.theme({
  '&': { backgroundColor: 'var(--code-bg)' },
  '.cm-content': { padding: '8px 0', caretColor: 'transparent' },
  '.cm-line': { padding: '0 12px' },
  '.cm-gutterElement': { padding: '0 8px 0 10px' },
});

export function CodeView({
  code,
  lang = 'lua',
  runningLine: line = null,
  follow = true,
  lint = [],
  className,
  label = 'Script source',
}: {
  code: string;
  lang?: string;
  runningLine?: number | null;
  follow?: boolean;
  lint?: LintMark[];
  className?: string;
  label?: string;
}) {
  const host = useRef<HTMLDivElement>(null);
  const view = useRef<EditorView | null>(null);

  useEffect(() => {
    if (!host.current) return;
    const langExt: Extension = lang === 'js' || lang === 'javascript' ? javascript() : StreamLanguage.define(lua);
    const v = new EditorView({
      parent: host.current,
      state: EditorState.create({
        doc: code,
        extensions: [
          lineNumbers(),
          runningGutterExt,
          langExt,
          syntaxHighlighting(highlight),
          runningField,
          lintField,
          baseTheme,
          EditorState.readOnly.of(true),
          EditorView.editable.of(false),
          EditorView.contentAttributes.of({ 'aria-label': label, tabindex: '0' }),
        ],
      }),
    });
    view.current = v;
    return () => {
      v.destroy();
      view.current = null;
    };
  }, [code, lang, label]);

  useEffect(() => {
    view.current?.dispatch({ effects: setLint.of(lint) });
  }, [lint, code]);

  useEffect(() => {
    const v = view.current;
    if (!v) return;
    const effects: StateEffect<unknown>[] = [setRunning.of(line)];
    if (follow && line !== null && line >= 1 && line <= v.state.doc.lines) {
      effects.push(EditorView.scrollIntoView(v.state.doc.line(line).from, { y: 'center' }));
    }
    v.dispatch({ effects });
  }, [line, follow, code]);

  return <div ref={host} className={cn('min-h-0 overflow-hidden', className)} />;
}
