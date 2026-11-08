import { StoryTimeRangeRequest } from '@mineclaw/contracts';
import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { afterEach, expect, it, vi } from 'vitest';
import { TimeRangeEditor, timeRangeSpans } from '@/features/story/TimeRangeEditor';
import { TimeSlider } from '@/features/story/StoryHud';

afterEach(cleanup);

it('lets the owner select two endpoints and applies once, including an arc across dawn', async () => {
  const save = vi.fn().mockResolvedValue(true);
  render(<TimeRangeEditor disabled={false} onSave={save} />);
  expect(screen.queryByRole('checkbox')).not.toBeInTheDocument();
  fireEvent.click(screen.getByRole('button', { name: 'Time loop options' }));
  fireEvent.click(screen.getByRole('checkbox', { name: 'Loop time range' }));
  fireEvent.change(screen.getByRole('slider', { name: 'Start of time range' }), { target: { value: '18000' } });
  fireEvent.change(screen.getByRole('slider', { name: 'End of time range' }), { target: { value: '6000' } });
  expect(save).not.toHaveBeenCalled();
  expect(screen.getByText(/across dawn/)).toBeTruthy();
  fireEvent.click(screen.getByRole('button', { name: 'Apply time range' }));
  await waitFor(() => expect(save).toHaveBeenCalledWith({ start: 18000, end: 6000 }));
});

it('turns off a saved range and rejects equal endpoints', async () => {
  const save = vi.fn().mockResolvedValue(true);
  render(<TimeRangeEditor range={{ start: 0, end: 12000 }} disabled={false} onSave={save} />);
  fireEvent.click(screen.getByRole('button', { name: 'Time loop options' }));
  fireEvent.change(screen.getByRole('slider', { name: 'End of time range' }), { target: { value: '0' } });
  expect(screen.getByRole('button', { name: 'Apply time range' })).toBeDisabled();
  fireEvent.click(screen.getByRole('checkbox', { name: 'Loop time range' }));
  fireEvent.click(screen.getByRole('button', { name: 'Apply time range' }));
  await waitFor(() => expect(save).toHaveBeenCalledWith(null));
  expect(StoryTimeRangeRequest.safeParse({ range: { start: 0, end: 0 } }).success).toBe(false);
  expect(StoryTimeRangeRequest.safeParse({ range: { start: 0, end: 24000 } }).success).toBe(false);
});

it('disables the controls without write access', () => {
  render(<TimeRangeEditor range={{ start: 0, end: 12000 }} disabled onSave={vi.fn()} />);
  fireEvent.click(screen.getByRole('button', { name: 'Time loop options' }));
  expect(screen.getByRole('checkbox')).toBeDisabled();
  expect(screen.getByRole('slider', { name: 'Start of time range' })).toBeDisabled();
});

it('keeps saved wraparound intervals visible on the time bar while options are collapsed', () => {
  render(
    <TimeSlider
      control={{ available: true, range: { start: 18000, end: 6000 } }}
      canWrite
      onSet={vi.fn()}
      onSetRange={vi.fn()}
    />,
  );
  expect(screen.getByRole('button', { name: 'Time loop options' })).toHaveAttribute('aria-expanded', 'false');
  expect(screen.queryByRole('checkbox')).not.toBeInTheDocument();
  expect(screen.getAllByTestId('time-loop-highlight').map((el) => [el.style.left, el.style.width])).toEqual([
    ['75%', '25%'],
    ['0%', '25%'],
  ]);
  expect(timeRangeSpans({ start: 0, end: 12000 })).toEqual([{ left: 0, width: 50 }]);
  expect(timeRangeSpans(null)).toEqual([]);
});

it('retains pending endpoint edits when the options are collapsed and reopened', () => {
  render(<TimeRangeEditor range={{ start: 0, end: 12000 }} disabled={false} onSave={vi.fn()} />);
  const toggle = screen.getByRole('button', { name: 'Time loop options' });
  fireEvent.click(toggle);
  fireEvent.change(screen.getByRole('slider', { name: 'End of time range' }), { target: { value: '6000' } });
  fireEvent.click(toggle);
  expect(screen.queryByRole('slider')).not.toBeInTheDocument();
  expect(toggle).toHaveTextContent('unsaved');
  fireEvent.click(toggle);
  expect(screen.getByRole('slider', { name: 'End of time range' })).toHaveValue('6000');
});
