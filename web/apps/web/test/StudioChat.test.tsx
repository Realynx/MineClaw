import { MOCK_DESIGNER_MODELS } from '@mineclaw/contracts';
import { fireEvent, render, screen } from '@testing-library/react';
import { describe, expect, it, vi } from 'vitest';
import { ChatPanel } from '@/features/studio/ChatPanel';

const base = {
  messages: [],
  models: MOCK_DESIGNER_MODELS,
  model: 'main',
  costUsd: 0.1234,
  busy: false,
  canWrite: true,
  hasSelection: true,
  withSelection: true,
  onWithSelection: () => undefined,
  error: null,
};

describe('studio chat panel', () => {
  it('lists the configured models and picks one', () => {
    const onModel = vi.fn();
    render(<ChatPanel {...base} onModel={onModel} onSend={() => undefined} onClose={() => undefined} />);
    const picker = screen.getByLabelText('Model') as HTMLSelectElement;
    expect([...picker.options].map((o) => o.value)).toEqual(['main', 'worker', 'cheap', 'profile:deepseek']);
    expect(picker.options[3]!.textContent).toContain('deepseek');
    fireEvent.change(picker, { target: { value: 'profile:deepseek' } });
    expect(onModel).toHaveBeenCalledWith('profile:deepseek');
    expect(screen.getByTestId('session-cost').textContent).toBe('$0.12');
  });

  it('Enter sends, Enter on an empty line closes (back to the world)', () => {
    const onSend = vi.fn();
    const onClose = vi.fn();
    render(<ChatPanel {...base} onModel={() => undefined} onSend={onSend} onClose={onClose} />);
    const box = screen.getByLabelText('Message the designer');
    fireEvent.change(box, { target: { value: 'add a door' } });
    fireEvent.keyDown(box, { key: 'Enter' });
    expect(onSend).toHaveBeenCalledWith('add a door');
    fireEvent.keyDown(box, { key: 'Enter' });
    expect(onClose).toHaveBeenCalledTimes(1);
  });

  it('shows each reply with its ops and cost', () => {
    render(
      <ChatPanel
        {...base}
        messages={[
          { seq: 1, role: 'user', text: 'build a hut', at: 1 },
          { seq: 2, role: 'assistant', text: 'Built it.', at: 2, model: 'claude-opus-5-5', costUsd: 0.0123, ops: [{ kind: 'fill', label: 'filled a floor', cells: 49 }] },
        ]}
        onModel={() => undefined}
        onSend={() => undefined}
        onClose={() => undefined}
      />,
    );
    expect(screen.getByText('filled a floor')).toBeTruthy();
    expect(screen.getByTestId('message-cost').textContent).toContain('$0.01');
  });
});
