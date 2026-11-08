import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import { afterEach, expect, it, vi } from 'vitest';
import { DeleteDesign } from '@/features/studio/DeleteDesign';
import { StaticAuthProvider } from '@/lib/auth';
import { setCsrfToken, setFetch } from '@/lib/api/client';
import type { Role } from '@mineclaw/contracts';

afterEach(() => setCsrfToken(null));

function setup(roles: Role[] = ['design']) {
  const qc = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  const invalidate = vi.spyOn(qc, 'invalidateQueries');
  const onDeleted = vi.fn();
  render(<QueryClientProvider client={qc}>
    <StaticAuthProvider me={{ username: 'builder', player: null, roles, totpEnrolled: false, sessionCreatedAt: '', idleExpiresAt: '', absoluteExpiresAt: '' }}>
      <DeleteDesign id={9} name="Storehouse" onDeleted={onDeleted} />
    </StaticAuthProvider>
  </QueryClientProvider>);
  return { invalidate, onDeleted };
}

it('requires confirmation of the named saved design and invalidates library queries after deletion', async () => {
  const send = vi.fn(() => Promise.resolve(new Response(JSON.stringify({ id: 9, text: 'Removed from the library.' }), { headers: { 'Content-Type': 'application/json' } })));
  setFetch(send);
  setCsrfToken('test-csrf-token-000000000');
  const { invalidate, onDeleted } = setup();
  fireEvent.click(screen.getByRole('button', { name: 'Delete Storehouse from library' }));
  expect(screen.getByText('Remove “Storehouse” (#9) from your design library.')).toBeVisible();
  expect(screen.getByText(/Existing buildings and their build and repair tasks stay unchanged/)).toBeVisible();
  expect(send).not.toHaveBeenCalled();
  fireEvent.click(screen.getByRole('button', { name: 'Cancel' }));
  expect(send).not.toHaveBeenCalled();
  fireEvent.click(screen.getByRole('button', { name: 'Delete Storehouse from library' }));
  fireEvent.click(screen.getByRole('button', { name: 'Delete from library' }));
  await waitFor(() => expect(onDeleted).toHaveBeenCalledOnce());
  expect(send).toHaveBeenCalledWith('/api/designs/9/delete', expect.objectContaining({ method: 'POST', body: JSON.stringify({ name: 'Storehouse' }) }));
  expect(invalidate).toHaveBeenCalledWith({ queryKey: ['designs.list'] });
});

it('does not offer deletion to a viewer without design permission', () => {
  setup(['viewer']);
  expect(screen.queryByRole('button', { name: /Delete/ })).toBeNull();
});
