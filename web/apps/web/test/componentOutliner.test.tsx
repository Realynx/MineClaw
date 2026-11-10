import { fireEvent, render, screen, within } from '@testing-library/react';
import { describe, expect, it, vi } from 'vitest';
import {
  ComponentOutliner,
  OutlinerRowAction,
  type OutlinerItem,
} from '@/features/studio/components/ComponentOutliner';

const items: OutlinerItem[] = [
  { id: 'a', name: 'XOR gate', detail: '4, 0, 6', title: 'XOR gate at 4, 0, 6' },
  { id: 'b', name: 'Repeater', detail: '9, 0, 6', dim: true },
  { id: 'c', name: 'Clock', detail: '12, 0, 2' },
];
const labels = { list: 'Circuit parts', combine: 'Combine circuit parts', testId: 'parts' };

describe('component outliner', () => {
  it('lists each component with its name, place and count, and gives its row actions the row name', () => {
    const onRemove = vi.fn();
    render(
      <ComponentOutliner
        items={items}
        labels={labels}
        hint="Pick a part."
        rowActions={(item, label) => (
          <OutlinerRowAction
            label={`Delete ${label}`}
            title="Delete"
            danger
            onClick={() => {
              onRemove(item.id);
            }}
          >
            x
          </OutlinerRowAction>
        )}
      />,
    );
    const outliner = screen.getByTestId('parts');
    expect(within(outliner).getByRole('heading')).toHaveTextContent('Components 3');
    const list = screen.getByRole('listbox', { name: 'Circuit parts' });
    expect(
      within(list)
        .getAllByRole('option')
        .map((o) => o.getAttribute('aria-label')),
    ).toEqual(['XOR gate 1', 'Repeater 2', 'Clock 3']);
    expect(screen.getByRole('option', { name: 'XOR gate 1' })).toHaveAttribute(
      'title',
      'XOR gate at 4, 0, 6. Double-click to frame.',
    );
    expect(screen.getByRole('option', { name: 'Clock 3' })).toHaveAttribute('title', 'Clock. Double-click to frame.');
    expect(screen.getByRole('option', { name: 'Clock 3' })).toHaveTextContent('12, 0, 2');
    expect(screen.getByRole('option', { name: 'Repeater 2' })).toHaveClass('opacity-50');
    expect(screen.getByText('Pick a part.')).toBeInTheDocument();
    fireEvent.click(screen.getByRole('button', { name: 'Delete Clock 3' }));
    expect(onRemove).toHaveBeenCalledExactlyOnceWith('c');
  });

  it('shows the empty note instead of a list', () => {
    render(<ComponentOutliner items={[]} labels={labels} empty={<p>Place a gate from the library.</p>} />);
    expect(screen.queryByRole('listbox')).toBeNull();
    expect(screen.getByText('Place a gate from the library.')).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Combine components' })).toBeDisabled();
  });

  it('selects with a click, frames with a double-click, F or the frame button, and moves with the arrow keys', () => {
    const onSelect = vi.fn(),
      onFrame = vi.fn();
    const view = render(<ComponentOutliner items={items} labels={labels} onSelect={onSelect} onFrame={onFrame} />);
    expect(screen.getByRole('button', { name: 'Frame selected component' })).toBeDisabled();
    fireEvent.click(screen.getByRole('option', { name: 'Repeater 2' }));
    expect(onSelect).toHaveBeenLastCalledWith('b');
    view.rerender(
      <ComponentOutliner items={items} labels={labels} selectedId="b" onSelect={onSelect} onFrame={onFrame} />,
    );
    const repeater = screen.getByRole('option', { name: 'Repeater 2' });
    expect(repeater).toHaveAttribute('aria-selected', 'true');
    fireEvent.click(screen.getByRole('button', { name: 'Frame selected component' }));
    expect(onFrame).toHaveBeenLastCalledWith('b');
    fireEvent.doubleClick(screen.getByRole('option', { name: 'Clock 3' }));
    expect(onFrame).toHaveBeenLastCalledWith('c');
    repeater.focus();
    fireEvent.keyDown(repeater, { key: 'f', code: 'KeyF' });
    expect(onFrame).toHaveBeenLastCalledWith('b');
    fireEvent.keyDown(repeater, { key: 'ArrowDown' });
    expect(onSelect).toHaveBeenLastCalledWith('c');
    expect(screen.getByRole('option', { name: 'Clock 3' })).toHaveFocus();
    fireEvent.keyDown(document.activeElement!, { key: 'Home' });
    expect(onSelect).toHaveBeenLastCalledWith('a');
  });

  it('copies, pastes and duplicates from a focused row, and pastes or duplicates nothing when disabled', () => {
    const onCopy = vi.fn(),
      onPaste = vi.fn(),
      onDuplicate = vi.fn();
    const props = { items, labels, selectedId: 'a', canPaste: true, onCopy, onPaste, onDuplicate };
    const view = render(<ComponentOutliner {...props} />);
    const row = screen.getByRole('option', { name: 'XOR gate 1' });
    fireEvent.keyDown(row, { key: 'c', ctrlKey: true });
    fireEvent.keyDown(row, { key: 'v', metaKey: true });
    fireEvent.keyDown(row, { key: 'd', ctrlKey: true });
    expect(onCopy).toHaveBeenCalledExactlyOnceWith('a');
    expect(onPaste).toHaveBeenCalledOnce();
    expect(onDuplicate).toHaveBeenCalledExactlyOnceWith('a');
    view.rerender(<ComponentOutliner {...props} disabled />);
    fireEvent.keyDown(screen.getByRole('option', { name: 'XOR gate 1' }), { key: 'v', ctrlKey: true });
    fireEvent.keyDown(screen.getByRole('option', { name: 'XOR gate 1' }), { key: 'd', ctrlKey: true });
    expect(onPaste).toHaveBeenCalledOnce();
    expect(onDuplicate).toHaveBeenCalledOnce();
  });

  it('ticks rows with Ctrl- and Shift-click, with the selected one, and combines only the ticked rows', () => {
    const onSelect = vi.fn(),
      onCombine = vi.fn();
    render(
      <ComponentOutliner
        items={items}
        labels={labels}
        selectedId="a"
        onSelect={onSelect}
        onCombine={onCombine}
        combineHint="Pick parts to join."
      />,
    );
    expect(screen.queryByRole('region', { name: 'Combine circuit parts' })).toBeNull();
    fireEvent.click(screen.getByRole('option', { name: 'Clock 3' }), { ctrlKey: true });
    expect(onSelect).not.toHaveBeenCalled();
    const checklist = screen.getByRole('region', { name: 'Combine circuit parts' });
    expect(within(checklist).getByText('Pick parts to join.')).toBeInTheDocument();
    expect(screen.getByRole('checkbox', { name: 'Combine XOR gate 1' })).toBeChecked();
    expect(screen.getByRole('checkbox', { name: 'Combine Repeater 2' })).not.toBeChecked();
    expect(screen.getByRole('checkbox', { name: 'Combine Clock 3' })).toBeChecked();
    fireEvent.click(screen.getByRole('option', { name: 'Clock 3' }), { ctrlKey: true });
    expect(screen.getByRole('checkbox', { name: 'Combine Clock 3' })).not.toBeChecked();
    // Shift-click ticks the range from the row clicked last.
    fireEvent.click(screen.getByRole('option', { name: 'XOR gate 1' }), { shiftKey: true });
    expect(screen.getByRole('checkbox', { name: 'Combine Repeater 2' })).toBeChecked();
    fireEvent.click(screen.getByRole('button', { name: 'Combine 3 selected' }));
    expect(onCombine).toHaveBeenCalledExactlyOnceWith(['a', 'b', 'c']);
    expect(screen.queryByRole('region', { name: 'Combine circuit parts' })).toBeNull();
  });

  it('gives the ticks to the page when it holds them, and only selects on a modifier click when disabled', () => {
    const onCheck = vi.fn(),
      onSelect = vi.fn();
    const view = render(
      <ComponentOutliner
        items={items}
        labels={labels}
        selectedId="b"
        checked={null}
        onCheck={onCheck}
        onCombine={vi.fn()}
        onSelect={onSelect}
      />,
    );
    fireEvent.click(screen.getByRole('button', { name: 'Combine components' }));
    expect(onCheck).toHaveBeenLastCalledWith(['b']);
    view.rerender(
      <ComponentOutliner
        items={items}
        labels={labels}
        selectedId="b"
        checked={['b']}
        onCheck={onCheck}
        onCombine={vi.fn()}
        onSelect={onSelect}
      />,
    );
    fireEvent.click(screen.getByRole('checkbox', { name: 'Combine Clock 3' }));
    expect(onCheck).toHaveBeenLastCalledWith(['b', 'c']);
    fireEvent.click(screen.getByRole('button', { name: 'Cancel' }));
    expect(onCheck).toHaveBeenLastCalledWith(null);
    view.rerender(
      <ComponentOutliner
        items={items}
        labels={labels}
        checked={null}
        onCheck={onCheck}
        onCombine={vi.fn()}
        onSelect={onSelect}
        disabled
      />,
    );
    onCheck.mockClear();
    fireEvent.click(screen.getByRole('option', { name: 'Clock 3' }), { ctrlKey: true });
    expect(onSelect).toHaveBeenCalledWith('c');
    expect(onCheck).not.toHaveBeenCalled();
  });
});
