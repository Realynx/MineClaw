import { fireEvent, render, screen } from '@testing-library/react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { TakeControlButton } from '@/features/control/TakeControlButton';
import { controlReturnTo, takeControlHref } from '@/features/control/navigation';
const f = vi.hoisted(() => ({ push: vi.fn(), begin: vi.fn() }));
vi.mock('next/navigation', () => ({ useRouter: () => ({ push: f.push }) }));
vi.mock('@/features/control/captureHandoff', () => ({ beginTakeoverCapture: f.begin }));

afterEach(() => { window.history.replaceState(null, '', '/'); vi.clearAllMocks(); });

describe('pawn takeover entry', () => {
  it.each(['/story?view=fly', '/live/maid_chan_109?pane=world'])('keeps the current view as the return destination: %s', (path) => {
    window.history.replaceState(null, '', path);
    render(<TakeControlButton pawn="maid_chan_109" canControl online />);
    const link = screen.getByRole('link', { name: 'Take control of pawn 109' });
    expect(link).toHaveAttribute('href', takeControlHref('maid_chan_109', path));
  });

  it.each([{ canControl: false, online: true }, { canControl: true, online: false }])('prevents entry without permission or a connected pawn: %s', (props) => {
    render(<TakeControlButton pawn="maid_chan_109" {...props} />);
    expect(screen.getByRole('button', { name: 'Take control of pawn 109' })).toBeDisabled();
    expect(screen.queryByRole('link')).not.toBeInTheDocument();
  });

  it('keeps a safe fallback and escapes pawn identifiers in the destination', () => {
    render(<TakeControlButton pawn="pawn & friend" canControl online returnTo="/live/pawn?pane=world" />);
    expect(screen.getByRole('link')).toHaveAttribute('href', '/control?pawn=pawn%20%26%20friend&returnTo=%2Flive%2Fpawn%3Fpane%3Dworld');
  });

  it('starts capture during the click and navigates only after the handoff is ready', () => {
    render(<TakeControlButton pawn="maid_chan_109" canControl online />);
    fireEvent.click(screen.getByRole('link'));
    expect(f.begin).toHaveBeenCalledWith('maid_chan_109', expect.any(Function));
    expect(f.push).not.toHaveBeenCalled();
    const ready = f.begin.mock.calls[0] as unknown as [string, () => void];
    ready[1]();
    expect(f.push).toHaveBeenCalledWith(takeControlHref('maid_chan_109', '/story'));
  });

  it('keeps modifier clicks as ordinary links without stealing fullscreen or mouse input', () => {
    render(<TakeControlButton pawn="maid_chan_109" canControl online />);
    fireEvent.click(screen.getByRole('link'), { ctrlKey: true });
    expect(f.begin).not.toHaveBeenCalled();
    expect(f.push).not.toHaveBeenCalled();
  });
});

describe('control return navigation', () => {
  it.each([null, '', 'https://evil.test/story', '//evil.test/story', '/\\evil.test/story', '/admin', '/control', '/live/../../admin'])('rejects unsafe or unrelated return destinations: %s', (path) => {
    expect(controlReturnTo(path)).toBe('/story');
  });
  it('retains allowed query state and strips fragments', () => {
    expect(controlReturnTo('/live/maid_chan_109?pane=world#irrelevant')).toBe('/live/maid_chan_109?pane=world');
  });
});
