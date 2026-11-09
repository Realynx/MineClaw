import { render, screen } from '@testing-library/react';
import { afterEach, describe, expect, it } from 'vitest';
import { TakeControlButton } from '@/features/control/TakeControlButton';
import { controlReturnTo, takeControlHref } from '@/features/control/navigation';

afterEach(() => { window.history.replaceState(null, '', '/'); });

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
});

describe('control return navigation', () => {
  it.each([null, '', 'https://evil.test/story', '//evil.test/story', '/\\evil.test/story', '/admin', '/control', '/live/../../admin'])('rejects unsafe or unrelated return destinations: %s', (path) => {
    expect(controlReturnTo(path)).toBe('/story');
  });
  it('retains allowed query state and strips fragments', () => {
    expect(controlReturnTo('/live/maid_chan_109?pane=world#irrelevant')).toBe('/live/maid_chan_109?pane=world');
  });
});
