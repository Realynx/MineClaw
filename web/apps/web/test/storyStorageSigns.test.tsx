import { StoryColonyState, type StoryStorage } from '@mineclaw/contracts';
import { act, fireEvent, render, screen } from '@testing-library/react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import type { Track } from '@/features/story/optimistic';
import { StorageInspector } from '@/features/story/StoryInspector';
import type { StoryWrite } from '@/features/story/useStory';
import { storageEntriesBody } from '@/features/story/storage';
import { harnessDown, harnessUp, mountInspector, network, posted, settle } from './storyInspectorHarness';

vi.mock('@/features/world/ContainerPanel', () => ({ ContainerContents: () => null }));

const chest: [number, number, number] = [2, 64, 2];
const storage: StoryStorage = {
  chest,
  priority: 'critical',
  filter: { allow: ['food'], deny: [] },
  contents: [],
  signs: [{ at: [2, 64, 1], lines: ['food', '', '', ''], allow: ['food'], deny: [] }],
};
const stateOf = (storages: StoryStorage[]) => ({ storages, reservations: [] }) as unknown as StoryColonyState;

describe('chest sign filter inspector', () => {
  it('shows the effective sign filter while a viewer edit waits, then changes only when server state activates it', async () => {
    const write = vi.fn().mockResolvedValue({ ok: true }) as unknown as StoryWrite;
    const props = {
      chests: [chest],
      state: stateOf([storage]),
      canWrite: true,
      write,
      clip: null,
      onClip: vi.fn(),
      onClose: vi.fn(),
    };
    const view = render(<StorageInspector {...props} />);
    expect(screen.getByText(/Active filter from attached signs: Food/)).toBeInTheDocument();
    expect(screen.getByText(/Sign priority: highest/)).toBeInTheDocument();
    await act(async () => {
      await Promise.resolve();
      fireEvent.click(screen.getByRole('button', { name: 'Clear' }));
    });
    expect(write).toHaveBeenCalledWith('story.storage', {
      op: 'set',
      chests: [chest],
      filter: { allow: [], deny: [] },
    });
    view.rerender(
      <StorageInspector {...props} state={stateOf([{ ...storage, pendingFilter: { allow: [], deny: [] } }])} />,
    );
    expect(screen.getByText(/Active filter from attached signs: Food/)).toBeInTheDocument();
    expect(screen.getByText(/Waiting for a pawn to remove the signs/)).toHaveTextContent(
      'Requested filter (not active): Nothing',
    );
    view.rerender(
      <StorageInspector {...props} state={stateOf([{ ...storage, signs: [], filter: { allow: [], deny: [] } }])} />,
    );
    expect(screen.queryByTestId('storage-sign-status')).not.toBeInTheDocument();
    expect(screen.getByTestId('storage-inspector')).toHaveTextContent('Storage · Critical · Nothing');
  });

  it('applies Allow and Exclude entries to every selected chest, without deregistering them', async () => {
    const write = vi.fn().mockResolvedValue({ ok: true }) as unknown as StoryWrite;
    const other: [number, number, number] = [6, 64, 2];
    render(
      <StorageInspector
        chests={[chest, other]}
        state={stateOf([storage, { ...storage, chest: other, signs: [] }])}
        canWrite
        write={write}
        clip={null}
        onClip={vi.fn()}
        onClose={vi.fn()}
      />,
    );
    fireEvent.change(screen.getByLabelText('Storage filter entries'), { target: { value: 'food, 260:0, food' } });
    await act(async () => {
      await Promise.resolve();
      fireEvent.click(screen.getByRole('button', { name: 'Allow entries' }));
    });
    expect(write).toHaveBeenLastCalledWith('story.storage', {
      op: 'add',
      chests: [chest, other],
      filter: { allow: ['food', '260:0'], deny: [] },
    });
    await act(async () => {
      await Promise.resolve();
      fireEvent.click(screen.getByRole('button', { name: 'Exclude entries' }));
    });
    expect(write).toHaveBeenLastCalledWith('story.storage', {
      op: 'remove_entries',
      chests: [chest, other],
      filter: { allow: ['food', '260:0'], deny: [] },
    });
  });

  it('shows a requested priority without replacing the effective sign priority', async () => {
    const write = vi.fn().mockResolvedValue({ ok: true }) as unknown as StoryWrite;
    const props = {
      chests: [chest],
      state: stateOf([storage]),
      canWrite: true,
      write,
      clip: null,
      onClip: vi.fn(),
      onClose: vi.fn(),
    };
    const view = render(<StorageInspector {...props} />);
    await act(async () => {
      await Promise.resolve();
      fireEvent.change(screen.getByLabelText('Storage priority'), { target: { value: 'low' } });
    });
    expect(write).toHaveBeenCalledWith('story.storage', { op: 'set', chests: [chest], priority: 'low' });
    view.rerender(<StorageInspector {...props} state={stateOf([{ ...storage, pendingPriority: 'low' }])} />);
    expect(screen.getByLabelText('Storage priority')).toHaveValue('low');
    expect(screen.getByText(/Waiting for a pawn to remove the signs/)).toHaveTextContent(
      'Requested priority (not active): Low',
    );
    expect(screen.getByTestId('storage-inspector')).toHaveTextContent('Storage · Critical · Food');
  });

  it('preserves the effective filter and shows a rejected request instead of pretending it was applied', async () => {
    const write = vi.fn().mockRejectedValue(new Error('unrecognized filter entry')) as unknown as StoryWrite;
    render(
      <StorageInspector
        chests={[chest]}
        state={stateOf([storage])}
        canWrite
        write={write}
        clip={null}
        onClip={vi.fn()}
        onClose={vi.fn()}
      />,
    );
    await act(async () => {
      await Promise.resolve();
      fireEvent.click(screen.getByRole('button', { name: 'Clear' }));
    });
    expect(screen.getByRole('alert')).toHaveTextContent('unrecognized filter entry');
    expect(screen.getByText(/Active filter from attached signs: Food/)).toBeInTheDocument();
    expect(storageEntriesBody([chest], 'add', 'wood,\n food,wood').filter.allow).toEqual(['wood', 'food']);
  });
});

describe('chest inspector on the shown colony state', () => {
  beforeEach(harnessUp);
  afterEach(harnessDown);

  const plain: StoryStorage = { chest, priority: 'normal', filter: { allow: ['food'], deny: [] }, contents: [] };
  const colony = (storages: StoryStorage[], rev = 10) =>
    StoryColonyState.parse({ rev, at: rev, since: null, full: true, storages });
  const inspector =
    (chests: [number, number, number][], onError = vi.fn()) =>
    ({ state, track }: { state: StoryColonyState | undefined; track: Track }) => (
      <StorageInspector
        chests={chests}
        state={state}
        canWrite
        write={vi.fn()}
        track={track}
        onError={onError}
        clip={null}
        onClip={vi.fn()}
        onClose={vi.fn()}
      />
    );
  const checked = (name: string) => screen.getByRole('checkbox', { name }).getAttribute('aria-checked');

  it('an unsigned chest shows the chosen priority before the colony answers, and the select stays usable', async () => {
    const calls = network();
    const page = mountInspector('inspect-priority', colony([plain]), inspector([chest]));
    fireEvent.change(screen.getByLabelText('Storage priority'), { target: { value: 'important' } });
    expect(screen.getByLabelText('Storage priority')).toHaveValue('important');
    expect(screen.getByLabelText('Storage priority')).not.toBeDisabled();
    expect(screen.queryByRole('status')).toBeNull();
    await settle();
    expect(posted(calls).map((c) => c.body)).toEqual([{ op: 'set', chests: [chest], priority: 'important' }]);
    posted(calls)[0]!.reply({ chests: [chest], rev: 11 });
    await settle();
    expect(screen.getByLabelText('Storage priority')).toHaveValue('important');
    await page.server(colony([{ ...plain, priority: 'important' }], 11));
    expect(screen.getByTestId('storage-inspector')).toHaveTextContent('Storage · Important · Food');
  });

  it('two quick filter clicks both register, the second built on the first', async () => {
    const calls = network();
    mountInspector('inspect-filter', colony([plain]), inspector([chest]));
    fireEvent.click(screen.getByRole('button', { name: 'Clear' }));
    expect(checked('Food')).toBe('false');
    fireEvent.click(screen.getByRole('checkbox', { name: 'Wood' }));
    expect(checked('Wood')).toBe('true');
    expect(checked('Food')).toBe('false');
    await settle();
    // One request at a time for these chests: the second waits for the first.
    expect(posted(calls).map((c) => c.body)).toEqual([{ op: 'set', chests: [chest], filter: { allow: [], deny: [] } }]);
    posted(calls)[0]!.reply({ chests: [chest], rev: 11 });
    await settle();
    expect(posted(calls).map((c) => c.body)).toEqual([
      { op: 'set', chests: [chest], filter: { allow: [], deny: [] } },
      { op: 'set', chests: [chest], filter: { allow: ['wood'], deny: [] } },
    ]);
  });

  it('a signed chest shows the requested priority at once and keeps the effective line', async () => {
    const calls = network();
    mountInspector('inspect-signed', colony([storage]), inspector([chest]));
    fireEvent.change(screen.getByLabelText('Storage priority'), { target: { value: 'low' } });
    expect(screen.getByLabelText('Storage priority')).toHaveValue('low');
    expect(screen.getByText(/Waiting for a pawn to remove the signs/)).toHaveTextContent(
      'Requested priority (not active): Low',
    );
    expect(screen.getByTestId('storage-inspector')).toHaveTextContent('Storage · Critical · Food');
    await settle();
    expect(posted(calls).map((c) => c.body)).toEqual([{ op: 'set', chests: [chest], priority: 'low' }]);
  });

  it.each([
    { name: '400', status: 400, body: { error: 'unrecognized filter entry' }, text: 'unrecognized filter entry' },
    {
      name: '503 busy',
      status: 503,
      body: { error: 'busy; retry shortly', code: 'busy', retryAfterMs: 1000 },
      text: 'The colony is busy; the change was not saved.',
    },
  ])('a refused change ($name) puts the tree back and says why', async ({ status, body, text }) => {
    const calls = network();
    mountInspector('inspect-refused', colony([plain]), inspector([chest]));
    fireEvent.click(screen.getByRole('button', { name: 'Clear' }));
    expect(checked('Food')).toBe('false');
    await settle();
    posted(calls)[0]!.reply(body, status);
    await settle();
    expect(checked('Food')).toBe('true');
    expect(screen.getByRole('alert')).toHaveTextContent(text);
    expect(posted(calls)).toHaveLength(1);
  });

  it('another chest shows its own label and none of the last chest’s error', async () => {
    const calls = network();
    const other: [number, number, number] = [6, 64, 2];
    const state = colony([
      { ...plain, label: 'Ores' },
      { ...plain, chest: other, label: 'Kitchen' },
    ]);
    const page = mountInspector('inspect-switch', state, inspector([chest]));
    expect(screen.getByLabelText('Chest label')).toHaveValue('Ores');
    fireEvent.click(screen.getByRole('button', { name: 'Clear' }));
    await settle();
    posted(calls)[0]!.reply({ error: 'unrecognized filter entry' }, 400);
    await settle();
    expect(screen.getByRole('alert')).toBeInTheDocument();
    page.show(inspector([other]));
    expect(screen.getByLabelText('Chest label')).toHaveValue('Kitchen');
    expect(screen.queryByRole('alert')).toBeNull();
    // Back on the first chest, its error is still its own.
    page.show(inspector([chest]));
    expect(screen.getByRole('alert')).toHaveTextContent('unrecognized filter entry');
  });
});
