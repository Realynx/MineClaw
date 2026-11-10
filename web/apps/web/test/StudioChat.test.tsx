import { MOCK_DESIGNER_MODELS } from '@mineclaw/contracts';
import { act, fireEvent, render, screen } from '@testing-library/react';
import { createRef } from 'react';
import { describe, expect, it, vi } from 'vitest';
import { ChatPanel, type ChatPanelHandle } from '@/features/studio/ChatPanel';

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
  it('starts mine chat collapsed, opens with the world shortcut, and retains its draft when collapsed', () => {
    const ref = createRef<ChatPanelHandle>();
    render(<ChatPanel {...base} kind="mine" ref={ref} onModel={() => undefined} onSend={() => undefined} onClose={() => undefined} />);
    expect(screen.getByRole('button', { name: 'Expand designer chat' }).getAttribute('aria-expanded')).toBe('false');
    expect(screen.queryByRole('textbox', { name: 'Message the designer' })).toBeNull();
    act(() => ref.current?.focus());
    const input = screen.getByRole('textbox', { name: 'Message the designer' });
    expect(document.activeElement).toBe(input);
    fireEvent.change(input, { target: { value: 'Connect the two tunnels' } });
    fireEvent.click(screen.getByRole('button', { name: 'Collapse designer chat' }));
    fireEvent.click(screen.getByRole('button', { name: 'Expand designer chat' }));
    expect(screen.getByRole<HTMLTextAreaElement>('textbox', { name: 'Message the designer' }).value).toBe('Connect the two tunnels');
  });

  it('starts redstone chat collapsed like mine chat, and opens with the world shortcut', () => {
    const ref = createRef<ChatPanelHandle>();
    render(
      <ChatPanel
        {...base}
        kind="redstone"
        ref={ref}
        onModel={() => undefined}
        onSend={() => undefined}
        onClose={() => undefined}
      />,
    );
    expect(screen.getByRole('button', { name: 'Expand designer chat' }).getAttribute('aria-expanded')).toBe('false');
    expect(screen.queryByRole('textbox', { name: 'Message the designer' })).toBeNull();
    act(() => ref.current?.focus());
    expect(document.activeElement).toBe(screen.getByRole('textbox', { name: 'Message the designer' }));
  });

  it('starts build chat open and can collapse it', () => {
    render(<ChatPanel {...base} kind="build" onModel={() => undefined} onSend={() => undefined} onClose={() => undefined} />);
    fireEvent.click(screen.getByRole('button', { name: 'Collapse designer chat' }));
    expect(screen.queryByRole('textbox', { name: 'Message the designer' })).toBeNull();
  });
  it('lists the configured models and picks one', () => {
    const onModel = vi.fn();
    render(<ChatPanel {...base} onModel={onModel} onSend={() => undefined} onClose={() => undefined} />);
    const picker = screen.getByLabelText<HTMLSelectElement>('Model');
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
          {
            seq: 2,
            role: 'assistant',
            text: 'Built it.',
            at: 2,
            model: 'claude-opus-5-5',
            costUsd: 0.0123,
            ops: [{ kind: 'fill', label: 'filled a floor', cells: 49 }],
          },
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
