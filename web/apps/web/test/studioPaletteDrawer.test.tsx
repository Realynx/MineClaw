import { act, fireEvent, render, screen, within } from '@testing-library/react';
import { afterEach, beforeEach, expect, it, vi } from 'vitest';
import { PaletteDrawer, type IconSheet } from '@/features/studio/BlockBar';
import { favouriteKeys, pushRecent, type PaletteEntry } from '@/features/studio/palette';

const palette: PaletteEntry[] = Array.from({ length: 1000 }, (_, index) => ({
  id: index + 1,
  meta: 0,
  name: index === 999 ? 'Endstone' : `Block ${index}`,
  facing: false,
  cat: index < 500 ? 'Building' : 'Colored',
  spec: `block_${index}`,
}));
const props = () => ({
  palette,
  version: 'test',
  current: palette[0]!,
  onChoose: vi.fn(),
  onClose: vi.fn(),
  sheet: null as IconSheet | null,
});
beforeEach(() => localStorage.clear());
afterEach(() => vi.unstubAllGlobals());

it('mounts only visible block rows and stores the large atlas URL once instead of once per icon', () => {
  const p = props();
  const payload = 'x'.repeat(20_000);
  p.sheet = {
    url: `data:image/png;base64,${payload}`,
    size: 32,
    cols: 16,
    rows: 63,
    index: new Map(palette.map((e, i) => [`${e.id}:0`, i])),
  };
  const { container } = render(<PaletteDrawer {...p} />);
  expect(screen.getByTestId('palette-count')).toHaveTextContent('1000');
  const list = screen.getByRole('list', { name: 'Blocks' });
  expect(within(list).getAllByRole('listitem').length).toBeLessThan(150);
  expect(container.innerHTML.split(payload)).toHaveLength(2);
  expect(screen.queryByRole('button', { name: 'Endstone' })).not.toBeInTheDocument();
  const scroll = screen.getByTestId('palette-scroll');
  fireEvent.scroll(scroll, { target: { scrollTop: 2400 } });
  expect(within(list).queryByRole('button', { name: 'Block 0' })).not.toBeInTheDocument();
  expect(within(list).getByRole('button', { name: 'Block 600' })).toBeInTheDocument();
  expect(within(list).getAllByRole('listitem').length).toBeLessThan(150);
});

it('searches all entries after scrolling and keeps category filtering and Enter selection', () => {
  const p = props();
  render(<PaletteDrawer {...p} />);
  const scroll = screen.getByTestId('palette-scroll');
  fireEvent.scroll(scroll, { target: { scrollTop: 2400 } });
  const input = screen.getByRole('textbox', { name: 'Search blocks' });
  fireEvent.change(input, { target: { value: 'Endstone' } });
  expect(scroll.scrollTop).toBe(0);
  expect(screen.getByTestId('palette-count')).toHaveTextContent('1');
  expect(screen.getByRole('button', { name: 'Endstone' })).toBeInTheDocument();
  fireEvent.keyDown(input, { key: 'Enter' });
  expect(p.onChoose).toHaveBeenCalledWith(palette[999]);
  expect(p.onClose).toHaveBeenCalledOnce();
  fireEvent.click(screen.getByRole('tab', { name: 'Colored 500' }));
  expect(input).toHaveValue('');
  expect(screen.getByTestId('palette-count')).toHaveTextContent('500');
  expect(screen.getByRole('button', { name: 'Block 500' })).toBeInTheDocument();
  expect(screen.queryByRole('button', { name: 'Block 0' })).not.toBeInTheDocument();
});

it('navigates to unmounted rows with the keyboard without letting Enter choose the first result', () => {
  const p = props();
  render(<PaletteDrawer {...p} />);
  const first = screen.getByRole('button', { name: 'Block 0' });
  first.focus();
  fireEvent.keyDown(first, { key: 'End' });
  const last = screen.getByRole('button', { name: 'Endstone' });
  expect(last).toHaveFocus();
  fireEvent.keyDown(last, { key: 'Enter' });
  expect(p.onChoose).not.toHaveBeenCalled(); // the button's native click chooses this entry, not the search's first hit
  fireEvent.click(last);
  expect(p.onChoose).toHaveBeenCalledExactlyOnceWith(palette[999]);
  fireEvent.keyDown(last, { key: 'Home' });
  expect(screen.getByRole('button', { name: 'Block 0' })).toHaveFocus();
});

it('keeps favourites and recent entries usable independently of the virtual block rows', () => {
  pushRecent('test', palette[999]!);
  const p = props();
  render(<PaletteDrawer {...p} />);
  fireEvent.click(
    within(screen.getByRole('list', { name: 'Recent blocks' })).getByRole('button', { name: 'Endstone' }),
  );
  expect(p.onChoose).toHaveBeenCalledWith(palette[999]);
  fireEvent.contextMenu(within(screen.getByRole('list', { name: 'Blocks' })).getByRole('button', { name: 'Block 0' }));
  expect(favouriteKeys('test')).toEqual(['1:0']);
  fireEvent.click(
    within(screen.getByRole('list', { name: 'Favourite blocks' })).getByRole('button', { name: 'Unstar Block 0' }),
  );
  expect(screen.queryByRole('list', { name: 'Favourite blocks' })).not.toBeInTheDocument();
});

it('reflows the visible rows when the palette becomes narrower', () => {
  let resize!: () => void;
  vi.stubGlobal(
    'ResizeObserver',
    class {
      constructor(cb: () => void) {
        resize = cb;
      }
      observe() {}
      disconnect() {}
    },
  );
  render(<PaletteDrawer {...props()} />);
  const scroll = screen.getByTestId('palette-scroll');
  Object.defineProperties(scroll, {
    clientWidth: { configurable: true, value: 256 },
    clientHeight: { configurable: true, value: 192 },
  });
  act(() => resize());
  const list = screen.getByRole('list', { name: 'Blocks' });
  expect(within(list).getAllByRole('listitem').length).toBeLessThanOrEqual(30);
  const first = screen.getByRole('button', { name: 'Block 0' });
  fireEvent.keyDown(first, { key: 'ArrowDown' });
  expect(screen.getByRole('button', { name: 'Block 5' })).toHaveFocus();
});
