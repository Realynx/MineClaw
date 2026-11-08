import type { StoryColonyState, StoryStorage } from '@mineclaw/contracts';
import { act, fireEvent, render, screen } from '@testing-library/react';
import { describe, expect, it, vi } from 'vitest';
import { StorageInspector } from '@/features/story/StoryInspector';
import type { StoryWrite } from '@/features/story/useStory';
import { storageEntriesBody } from '@/features/story/storage';

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
