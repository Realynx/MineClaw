import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { InstallApp, PwaProvider } from '@/features/pwa/PwaProvider';

afterEach(() => vi.unstubAllGlobals());
const setup = (secure: boolean) => {
  vi.stubGlobal('isSecureContext', secure);
  return render(
    <PwaProvider>
      <InstallApp />
    </PwaProvider>,
  );
};
describe('app installation', () => {
  it('explains trusted HTTPS instead of pretending to install from plain HTTP', async () => {
    setup(false);
    fireEvent.click(screen.getByRole('button', { name: 'Install MineClaw' }));
    expect(await screen.findByText(/certificate your browser trusts/)).toBeInTheDocument();
  });
  it('invokes the browser prompt only from the install button and hides it after acceptance', async () => {
    setup(true);
    const prompt = vi.fn().mockResolvedValue(undefined);
    const event = Object.assign(new Event('beforeinstallprompt', { cancelable: true }), {
      prompt,
      userChoice: Promise.resolve({ outcome: 'accepted' }),
    });
    fireEvent(window, event);
    expect(event.defaultPrevented).toBe(true);
    expect(prompt).not.toHaveBeenCalled();
    fireEvent.click(screen.getByRole('button', { name: 'Install MineClaw' }));
    await waitFor(() => expect(prompt).toHaveBeenCalledOnce());
    await waitFor(() => expect(screen.queryByRole('button', { name: 'Install MineClaw' })).toBeNull());
  });
});
