/**
 * The Design Studio's save chrome (StudioChrome.tsx): the "Unsaved changes" chip and the primary Save while dirty, "edited
 * since" on the saved bar, the sync chip with its Retry, and the notice line's tones, action and dismiss.
 */
import { MOCK_DESIGNER_PALETTE, type DesignerSession } from '@mineclaw/contracts';
import { fireEvent, render, screen } from '@testing-library/react';
import { describe, expect, it, vi } from 'vitest';
import { NoticeLines, SavedBar, StudioHeader, SyncChip, syncText } from '@/features/studio/StudioChrome';
import type { StudioSnapshot } from '@/features/studio/studio';
import { NO_NOTICES, withNotice } from '@/features/studio/studioSave';

const session: DesignerSession = {
  id: 7,
  owner: 'poofimafox',
  model: 'main',
  designId: 5,
  name: 'hut',
  rev: 3,
  blocks: [],
  anchor: null,
  messages: [],
  costUsd: 0,
  calls: 0,
  version: 'b1.8.1',
  palette: MOCK_DESIGNER_PALETTE,
  plot: { size: 64, height: 48, depth: 16 },
  revisions: [],
};

const snap = (over: Partial<StudioSnapshot> = {}): StudioSnapshot => ({
  rev: 3,
  blocks: 1,
  dug: 0,
  canUndo: false,
  canRedo: false,
  undoLabel: null,
  redoLabel: null,
  pending: 0,
  sync: 'idle',
  syncError: null,
  busy: false,
  messages: [],
  costUsd: 0,
  selection: null,
  anchor: null,
  designId: 5,
  name: 'hut',
  error: null,
  dirty: false,
  ...over,
});

function header(s: StudioSnapshot, retrySync = vi.fn()) {
  render(
    <StudioHeader
      snap={s}
      session={session}
      version="b1.8.1"
      locked={false}
      changeVersion={() => undefined}
      changeKind={() => undefined}
      saveName="hut"
      setSaveName={() => undefined}
      saveSummary=""
      setSaveSummary={() => undefined}
      canSave
      saving={false}
      doSave={() => undefined}
      revisions={[]}
      retrySync={retrySync}
    />,
  );
  return { retrySync, save: screen.getByRole('button', { name: 'Save as new revision' }) };
}

describe('save chrome', () => {
  it('shows unsaved changes beside a primary Save while dirty, and neither once saved', () => {
    const dirty = header(snap({ dirty: true }));
    expect(screen.getByTestId('unsaved-chip')).toHaveTextContent('Unsaved changes');
    expect(dirty.save.className).toContain('bg-accent');
  });

  it('a clean page has no chip and a plain Save', () => {
    const clean = header(snap());
    expect(screen.queryByTestId('unsaved-chip')).toBeNull();
    expect(clean.save.className).not.toContain('bg-accent');
    expect(screen.getByTestId('sync-chip')).toHaveTextContent('Synced');
  });

  it('the saved bar says "edited since" while dirty, and the save reply when not', () => {
    const saved = { designId: 5, rev: 2, text: 'saved #5 r2' };
    const props = { saved, mineRange: null, canChat: false, mainBot: null, askBot: () => undefined };
    const { rerender } = render(<SavedBar {...props} />);
    expect(screen.getByTestId('saved-bar')).toHaveTextContent('saved #5 r2');
    rerender(<SavedBar {...props} dirty />);
    expect(screen.getByTestId('saved-bar')).toHaveTextContent('Design #5 r2 · edited since');
    expect(screen.getByTestId('saved-bar')).not.toHaveTextContent('saved #5 r2');
  });
});

describe('sync chip', () => {
  it('says where the edits stand', () => {
    expect(syncText({ pending: 0, sync: 'idle', syncError: null })).toBe('Synced');
    expect(syncText({ pending: 1, sync: 'sending', syncError: null })).toBe('Sending 1 cell');
    expect(syncText({ pending: 120, sync: 'sending', syncError: null })).toBe('Sending 120 cells');
    expect(syncText({ pending: 0, sync: 'sending', syncError: null })).toBe('Sending');
    expect(syncText({ pending: 40, sync: 'retrying', syncError: 'the designer is restarting' })).toBe(
      'Retrying: the designer is restarting',
    );
    expect(syncText({ pending: 40, sync: 'blocked', syncError: 'no such session' })).toBe(
      'Not syncing: no such session',
    );
  });

  it('offers Retry only while failing, and Retry asks the studio to send again', () => {
    const retry = vi.fn();
    const { rerender } = render(<SyncChip snap={{ pending: 3, sync: 'sending', syncError: null }} retry={retry} />);
    expect(screen.queryByRole('button', { name: 'Retry' })).toBeNull();
    rerender(<SyncChip snap={{ pending: 3, sync: 'blocked', syncError: 'no such session' }} retry={retry} />);
    expect(screen.getByRole('alert')).toHaveTextContent('Not syncing: no such session');
    fireEvent.click(screen.getByRole('button', { name: 'Retry' }));
    expect(retry).toHaveBeenCalledTimes(1);
    rerender(<SyncChip snap={{ pending: 3, sync: 'retrying', syncError: 'offline' }} retry={retry} />);
    fireEvent.click(screen.getByRole('button', { name: 'Retry' }));
    expect(retry).toHaveBeenCalledTimes(2);
  });

  it('the header shows the chip and wires its Retry', () => {
    const { retrySync } = header(snap({ pending: 9, sync: 'blocked', syncError: 'payload too large', dirty: true }));
    expect(screen.getByTestId('sync-chip')).toHaveAttribute('data-sync', 'blocked');
    fireEvent.click(screen.getByRole('button', { name: 'Retry' }));
    expect(retrySync).toHaveBeenCalledTimes(1);
  });
});

describe('notice line', () => {
  it('shows a failure in red until dismissed, with its action, above a passing notice', () => {
    const run = vi.fn(),
      dismiss = vi.fn();
    let n = withNotice(NO_NOTICES, 'Tunnel: 30 cells');
    n = withNotice(n, 'Saved r4, but 3 cells on this page are not in it.', {
      tone: 'error',
      action: { label: 'Resync and save again', run },
    });
    render(<NoticeLines notices={n} dismiss={dismiss} />);
    const alert = screen.getByTestId('studio-alert');
    expect(alert).toHaveAttribute('role', 'alert');
    expect(alert).toHaveAttribute('data-tone', 'error');
    expect(alert.className).toContain('text-danger');
    expect(screen.getByTestId('studio-notice')).toHaveTextContent('Tunnel: 30 cells');
    expect(screen.getByTestId('studio-notice')).toHaveAttribute('data-tone', 'info');
    fireEvent.click(screen.getByRole('button', { name: 'Resync and save again' }));
    expect(run).toHaveBeenCalledTimes(1);
    fireEvent.click(screen.getByRole('button', { name: 'Dismiss' }));
    expect(dismiss).toHaveBeenCalledTimes(1);
  });

  it('a passing notice has no dismiss, and nothing renders without notices', () => {
    const { container, rerender } = render(<NoticeLines notices={NO_NOTICES} dismiss={() => undefined} />);
    expect(container).toBeEmptyDOMElement();
    rerender(
      <NoticeLines
        notices={withNotice(NO_NOTICES, 'Saved. The picture could not be taken.', { tone: 'warn' })}
        dismiss={() => undefined}
      />,
    );
    expect(screen.getByTestId('studio-notice').className).toContain('text-warn');
    expect(screen.queryByRole('button', { name: 'Dismiss' })).toBeNull();
  });
});
