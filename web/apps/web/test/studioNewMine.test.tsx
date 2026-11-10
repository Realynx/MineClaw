/** "New mine" on the Designs page opens the studio on a fresh mine session (`kind` passed to designer.create). */
import type * as ReactQuery from '@tanstack/react-query';
import { render, screen, waitFor } from '@testing-library/react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import type * as Client from '@/lib/api/client';
import type * as Query from '@/lib/api/query';

const nav = vi.hoisted(() => ({ params: new URLSearchParams(), replace: vi.fn() }));
const client = vi.hoisted(() => ({ api: vi.fn(() => new Promise<never>(() => undefined)) }));

vi.mock('next/navigation', () => ({ useSearchParams: () => nav.params, useRouter: () => ({ replace: nav.replace }) }));
vi.mock('@/lib/api/client', async (original) => ({
  ...(await original<typeof Client>()),
  api: client.api,
}));
vi.mock('@/lib/api/query', async (original) => ({
  ...(await original<typeof Query>()),
  useApi: () => ({ data: undefined, isPending: true, error: null }),
}));
vi.mock('@/lib/auth', () => ({ useAuth: () => ({ me: { username: 'fox' }, can: () => true, canChat: false }) }));
vi.mock('@tanstack/react-query', async (original) => ({
  ...(await original<typeof ReactQuery>()),
  useQueryClient: () => ({ invalidateQueries: vi.fn() }),
}));
// The 3D viewer is not needed: the session request is made before it would be used.
vi.mock('@/features/studio/studio-viewer', () => ({}));

import DesignsPage from '@/app/(app)/designs/page';
import { StudioPage } from '@/features/studio/StudioPage';

afterEach(() => {
  client.api.mockClear();
  nav.params = new URLSearchParams();
});

describe('New mine', () => {
  it('sits next to New design on the Designs page and opens the studio as a mine', () => {
    render(<DesignsPage />);
    expect(screen.getByRole('link', { name: 'New mine' })).toHaveAttribute('href', '/designs/studio?kind=mine');
    expect(screen.getByRole('link', { name: 'New design' })).toHaveAttribute('href', '/designs/studio');
  });

  it.each([
    ['kind=mine', { kind: 'mine' }],
    ['', {}],
    ['id=5&kind=mine', { designId: 5 }],
  ])('the studio opened with "%s" asks designer.create for %j', async (query, body) => {
    nav.params = new URLSearchParams(query);
    render(<StudioPage />);
    await waitFor(() => expect(client.api).toHaveBeenCalledWith('designer.create', { body }));
  });
});
