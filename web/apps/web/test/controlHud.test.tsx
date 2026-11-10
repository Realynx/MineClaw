import { act, cleanup, render, screen } from '@testing-library/react';
import { afterEach, expect, it, vi } from 'vitest';
import { bubbles, ControlHud, HELD_NAME_MS, HURT_MS, type ControlHudProps } from '@/features/control/ControlHud';

afterEach(() => {
  cleanup();
  vi.useRealTimers();
});

const hud = (props: Partial<ControlHudProps> = {}) => <ControlHud hp={20} food={20} slot={0} held={null} {...props} />;
const fills = (name: string) =>
  Array.from(screen.getByRole('img', { name }).children, (icon) => (icon as HTMLElement).dataset.fill);

it('draws hearts and hunger as ten icons of two points each, half icons for odd points', () => {
  render(hud({ hp: 7, food: 20 }));
  expect(fills('Health 7 / 20')).toEqual([
    'full',
    'full',
    'full',
    'half',
    'empty',
    'empty',
    'empty',
    'empty',
    'empty',
    'empty',
  ]);
  expect(fills('Food 20 / 20')).toEqual(Array(10).fill('full'));
});

it('shows armour only while some is worn, and air only while it runs down', () => {
  const view = render(hud());
  // An older server sends neither.
  expect(screen.queryByRole('img', { name: /Armour/ })).not.toBeInTheDocument();
  expect(screen.queryByRole('img', { name: /Air/ })).not.toBeInTheDocument();
  view.rerender(hud({ armor: 0, air: 300 }));
  expect(screen.queryByRole('img', { name: /Armour/ })).not.toBeInTheDocument();
  expect(screen.queryByRole('img', { name: /Air/ })).not.toBeInTheDocument();
  view.rerender(hud({ armor: 15, air: 299 }));
  expect(fills('Armour 15 / 20')).toEqual([...Array<string>(7).fill('full'), 'half', 'empty', 'empty']);
  expect(fills('Air 10 / 10')).toEqual(Array(10).fill('full'));
  view.rerender(hud({ armor: 15, air: -10 }));
  expect(fills('Air 0 / 10')).toEqual(Array(10).fill('empty')); // drowning
});

it('counts a bubble down for every 30 ticks of air', () => {
  expect([300, 299, 272, 271, 150, 32, 31, 2, 0, -20].map(bubbles)).toEqual([10, 10, 9, 9, 5, 1, 1, 0, 0, 0]);
});

it('names the held item for a moment when the hand changes, and not when nothing is held', () => {
  vi.useFakeTimers();
  const view = render(hud({ slot: 0, held: 'stone' }));
  expect(screen.queryByTestId('held-name')).not.toBeInTheDocument(); // not on opening
  view.rerender(hud({ slot: 1, held: 'iron pickaxe' }));
  expect(screen.getByTestId('held-name')).toHaveTextContent('iron pickaxe');
  act(() => {
    vi.advanceTimersByTime(HELD_NAME_MS - 1);
  });
  expect(screen.getByTestId('held-name')).toBeInTheDocument();
  act(() => {
    vi.advanceTimersByTime(1);
  });
  expect(screen.queryByTestId('held-name')).not.toBeInTheDocument();
  // The item in the same slot changed (the last block placed, a new one picked up).
  view.rerender(hud({ slot: 1, held: 'dirt' }));
  expect(screen.getByTestId('held-name')).toHaveTextContent('dirt');
  view.rerender(hud({ slot: 2, held: null }));
  expect(screen.queryByTestId('held-name')).not.toBeInTheDocument();
});

it('flashes the screen edge red when health drops, and not when it rises', () => {
  vi.useFakeTimers();
  const view = render(hud({ hp: 20 }));
  expect(screen.queryByTestId('damage-vignette')).not.toBeInTheDocument();
  view.rerender(hud({ hp: 16 }));
  expect(screen.getByTestId('damage-vignette')).toBeInTheDocument();
  act(() => {
    vi.advanceTimersByTime(HURT_MS);
  });
  expect(screen.queryByTestId('damage-vignette')).not.toBeInTheDocument();
  view.rerender(hud({ hp: 18 }));
  expect(screen.queryByTestId('damage-vignette')).not.toBeInTheDocument();
  // A second hit while the first still shows starts the flash over.
  view.rerender(hud({ hp: 15 }));
  act(() => {
    vi.advanceTimersByTime(HURT_MS - 100);
  });
  view.rerender(hud({ hp: 12 }));
  act(() => {
    vi.advanceTimersByTime(HURT_MS - 100);
  });
  expect(screen.getByTestId('damage-vignette')).toBeInTheDocument();
  act(() => {
    vi.advanceTimersByTime(100);
  });
  expect(screen.queryByTestId('damage-vignette')).not.toBeInTheDocument();
});
