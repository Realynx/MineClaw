/** A mine's shortcut sheet (the ? button and key) and the hint line that follows what is selected or in hand (U10). */
import { cleanup, fireEvent, render, screen } from '@testing-library/react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { setPlotMode } from '@/features/studio/canvas';
import { MineTerrain } from '@/features/studio/mine/mineTerrain';
import type { Studio } from '@/features/studio/studio';
import { studioKey, type InputState, type KeyActions } from '@/features/studio/studioInput';
import {
  MINE_SHORTCUTS,
  mineHint,
  mineHintState,
  ShortcutsButton,
  StudioShortcuts,
} from '@/features/studio/StudioShortcuts';

afterEach(() => {
  cleanup();
  setPlotMode({ kind: 'build' });
});

describe('the mine hint line', () => {
  it('follows a piece in hand first, then a selected component, then a box selection', () => {
    expect(mineHintState({ placing: true, component: true, selection: true })).toBe('placing');
    expect(mineHintState({ placing: false, component: true, selection: true })).toBe('component');
    expect(mineHintState({ placing: false, component: false, selection: true })).toBe('selection');
    expect(mineHintState({ placing: false, component: false, selection: false })).toBe('idle');
  });

  it('lists the keys of each state', () => {
    expect(mineHint('idle')).toMatch(/Middle drag: orbit.*PgUp \/ PgDn: working level.*\?: all keys/);
    expect(mineHint('component')).toMatch(
      /Arrows, PgUp \/ PgDn: move.*\[ \]: turn.*F: frame.*Delete: remove.*Esc: deselect/,
    );
    expect(mineHint('selection')).toMatch(/Delete: remove the plan inside.*Esc: deselect/);
    expect(mineHint('placing')).toMatch(/Click to place.*\[ \] or R: turn.*Right-click or Esc: cancel/);
  });
});

describe('the shortcut sheet', () => {
  it('lists every section and takes the focus', () => {
    render(<StudioShortcuts onClose={() => undefined} />);
    const sheet = screen.getByTestId('studio-shortcuts');
    for (const s of MINE_SHORTCUTS) expect(sheet).toHaveTextContent(s.title);
    expect(sheet).toHaveTextContent('Ctrl+Shift+G');
    expect(sheet).toHaveTextContent('Remove the plan inside the selection (never digs)');
    expect(document.activeElement).toBe(screen.getByRole('button', { name: 'Close' }));
  });

  it('closes on Esc, ?, the Close button and a click outside it, not on a click inside', () => {
    const onClose = vi.fn();
    render(<StudioShortcuts onClose={onClose} />);
    const sheet = screen.getByRole('dialog', { name: 'Mine shortcuts' });
    fireEvent.click(sheet);
    expect(onClose).not.toHaveBeenCalled();
    fireEvent.keyDown(sheet, { key: 'Escape' });
    fireEvent.keyDown(sheet, { key: '?' });
    fireEvent.keyDown(sheet, { key: 'a' });
    fireEvent.click(screen.getByRole('button', { name: 'Close' }));
    fireEvent.click(sheet.parentElement!);
    expect(onClose).toHaveBeenCalledTimes(4);
  });

  it('opens from the ? button', () => {
    const open = vi.fn();
    render(<ShortcutsButton onClick={open} />);
    fireEvent.click(screen.getByRole('button', { name: 'Keyboard shortcuts' }));
    expect(open).toHaveBeenCalledOnce();
  });
});

describe('the ? key', () => {
  const keys = (toggleShortcuts?: () => void): KeyActions =>
    ({
      studio: { selection: null } as unknown as Studio,
      state: () => ({ drag: null, pasting: false, under: null, tool: 'select' }) as unknown as InputState,
      inWorld: () => true,
      openChat: vi.fn(),
      toggleShortcuts,
    }) as unknown as KeyActions;
  const press = (a: KeyActions, init: KeyboardEventInit = { key: '?', code: 'Slash', shiftKey: true }) => {
    const e = new KeyboardEvent('keydown', { cancelable: true, ...init });
    studioKey(e, a);
    return e;
  };

  it('toggles the sheet in a mine, and never opens the chat', () => {
    setPlotMode({ kind: 'mine', terrain: MineTerrain.for('12345', '1.12.2') });
    const toggle = vi.fn();
    const a = keys(toggle);
    const e = press(a);
    expect(toggle).toHaveBeenCalledOnce();
    expect(e.defaultPrevented).toBe(true);
    expect(a.openChat).not.toHaveBeenCalled();
    // A plain / still opens the chat.
    press(a, { key: '/', code: 'Slash' });
    expect(a.openChat).toHaveBeenCalledOnce();
    expect(toggle).toHaveBeenCalledOnce();
  });

  it('does nothing in a build or without the sheet', () => {
    const toggle = vi.fn();
    expect(press(keys(toggle)).defaultPrevented).toBe(false);
    setPlotMode({ kind: 'mine', terrain: MineTerrain.for('12345', '1.12.2') });
    expect(press(keys()).defaultPrevented).toBe(false);
    expect(toggle).not.toHaveBeenCalled();
  });
});
