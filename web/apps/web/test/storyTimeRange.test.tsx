import { StoryTimeRangeRequest } from '@mineclaw/contracts';
import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { afterEach, expect, it, vi } from 'vitest';
import { TimeRangeEditor } from '@/features/story/TimeRangeEditor';

afterEach(cleanup);

it('lets the owner select two endpoints and applies once, including an arc across dawn', async () => {
  const save = vi.fn().mockResolvedValue(true);
  render(<TimeRangeEditor disabled={false} onSave={save} />);
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
  expect(screen.getByRole('checkbox')).toBeDisabled();
  expect(screen.getByRole('slider', { name: 'Start of time range' })).toBeDisabled();
});
