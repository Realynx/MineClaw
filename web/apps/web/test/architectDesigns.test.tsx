import { fireEvent, render, screen, cleanup, waitFor } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { ArchitectPanel } from '@/features/story/ArchitectPanel';
import { DESIGN_DRAG_TYPE } from '@/features/story/DesignLibrary';
import { PictureControls, pictureDistance, positionPictureCamera } from '@/features/studio/PictureControls';
import { qk } from '@/lib/api/query';
import type { WorldViewerEngine } from '@mineclaw/world-viewer';
import type { StoryArchitectEntry } from '@mineclaw/contracts';
import { StaticAuthProvider } from '@/lib/auth';
import { setCsrfToken, setFetch } from '@/lib/api/client';

afterEach(() => { cleanup(); setCsrfToken(null); });

describe('Architect design library', () => {
  it('shows design pictures inside the main panel and carries only the selected id for drag placement', () => {
    const qc = new QueryClient({ defaultOptions: { queries: { retry: false, staleTime: Infinity } } });
    qc.setQueryData(qk('designs.list', {}, { q: undefined, limit: 24, offset: 0 }), {
      total: 1,
      revisionsTotal: 2,
      limit: 24,
      offset: 0,
      items: [{ id: 9, name: 'Storehouse', status: 'accepted', rev: 2, revisions: 2, hasPicture: true, updated: 100 }],
    });
    const onPick = vi.fn();
    const { container } = render(
      <QueryClientProvider client={qc}>
        <StaticAuthProvider me={{ username: 'builder', player: null, roles: ['design'], totpEnrolled: false, sessionCreatedAt: '', idleExpiresAt: '', absoluteExpiresAt: '' }}>
        <ArchitectPanel tool={{ kind: 'select' }} onTool={vi.fn()} onDesignPick={onPick} />
        </StaticAuthProvider>
      </QueryClientProvider>,
    );
    fireEvent.click(screen.getByRole('button', { name: 'Designs' }));
    const picture = screen.getByRole('img', { name: 'Storehouse' });
    expect(picture.getAttribute('src')).toBe('/api/designs/9/picture.png?v=100');
    expect(container.querySelector('[data-testid="architect"]')?.contains(picture)).toBe(true);
    const card = screen.getByTitle('Place Storehouse (#9)');
    fireEvent.click(card);
    expect(onPick).toHaveBeenCalledWith(9);
    const data = { setData: vi.fn(), effectAllowed: '' };
    fireEvent.dragStart(card, { dataTransfer: data });
    expect(data.setData).toHaveBeenCalledExactlyOnceWith(DESIGN_DRAG_TYPE, '9');
    expect(screen.getByRole('link', { name: /Change picture/ }).getAttribute('href')).toContain('picture=1');
  });

  it('renders Misc placeables in the content area with no detached empty window', () => {
    const entry = {
      id: 'test',
      tab: 'misc',
      label: 'Test block',
      unlocked: true,
      item: [4, 0],
      tier: 'gathered',
      have: 1,
      craftable: 0,
      from: [],
      short: [],
      chain: [],
    } as unknown as StoryArchitectEntry;
    const { container } = render(
      <ArchitectPanel
        tool={{ kind: 'select' }}
        onTool={vi.fn()}
        palette={{ entries: [entry] } as never}
        onPick={vi.fn()}
      />,
    );
    fireEvent.click(screen.getByRole('button', { name: 'Misc' }));
    const architect = container.querySelector('[data-testid="architect"]');
    expect(architect?.contains(screen.getByRole('button', { name: /Test block/ }))).toBe(true);
    expect(container.children).toHaveLength(1);
  });

  it.each([25, 1])('returns to existing designs after deleting the last page and clears only a matching placement (%s)', async (selectedId) => {
    let items = Array.from({ length: 25 }, (_, i) => ({
      id: i + 1, name: `Design ${i + 1}`, status: 'accepted', rev: 1, revisions: 1, hasPicture: false,
    }));
    setCsrfToken('test-csrf-token-000000000');
    setFetch((input, init) => {
      const url = new URL(typeof input === 'string' ? input : input instanceof URL ? input.href : input.url, 'http://localhost');
      if (init?.method === 'POST') {
        expect(url.pathname).toBe('/api/designs/25/delete');
        items = items.filter((d) => d.id !== 25);
        return Promise.resolve(new Response(JSON.stringify({ id: 25, text: 'Removed' }), { headers: { 'Content-Type': 'application/json' } }));
      }
      const offset = Number(url.searchParams.get('offset') ?? 0);
      return Promise.resolve(new Response(JSON.stringify({ total: items.length, revisionsTotal: items.length, limit: 24, offset, items: items.slice(offset, offset + 24) }), { headers: { 'Content-Type': 'application/json' } }));
    });
    const qc = new QueryClient({ defaultOptions: { queries: { retry: false, staleTime: Infinity } } });
    const onTool = vi.fn();
    render(<QueryClientProvider client={qc}>
      <StaticAuthProvider me={{ username: 'builder', player: null, roles: ['design'], totpEnrolled: false, sessionCreatedAt: '', idleExpiresAt: '', absoluteExpiresAt: '' }}>
        <ArchitectPanel tool={{ kind: 'design', id: selectedId, name: `Design ${selectedId}`, blocks: [] }} onTool={onTool} onDesignPick={vi.fn()} />
      </StaticAuthProvider>
    </QueryClientProvider>);
    fireEvent.click(await screen.findByRole('button', { name: 'Next' }));
    fireEvent.click(await screen.findByRole('button', { name: 'Delete Design 25 from library' }));
    fireEvent.click(screen.getByRole('button', { name: 'Delete from library' }));
    await screen.findByTitle('Place Design 1 (#1)');
    await waitFor(() => expect(screen.queryByText('No saved designs found.')).toBeNull());
    expect(screen.queryByTitle('Place Design 25 (#25)')).toBeNull();
    if (selectedId === 25) expect(onTool).toHaveBeenCalledExactlyOnceWith({ kind: 'select' });
    else expect(onTool).not.toHaveBeenCalled();
  });
});

describe('Design picture controls', () => {
  it('fits tall designs and narrow viewers without cropping their bounding sphere', () => {
    const bounds = { min: [0, 0, 0], max: [5, 30, 9] } as const;
    expect(pictureDistance(bounds as never, 0.5)).toBeGreaterThan(pictureDistance(bounds as never, 2));
    const r = Math.hypot(6, 31, 10) / 2;
    expect(pictureDistance(bounds as never, 2)).toBeGreaterThan(r / Math.sin((35 * Math.PI) / 180));
  });

  it('aims picture directions at the world-height design, including its studio ground offset', () => {
    const focus = vi.fn();
    const e = { camera: { aspect: 1.6, fov: 70 }, rig: { flyYaw: 0, beginTransition: vi.fn() }, focus };
    positionPictureCamera(e as unknown as WorldViewerEngine, { min: [30, 0, 30], max: [34, 4, 34] }, 64, 90);
    expect(e.rig.flyYaw).toBe(90);
    expect(focus).toHaveBeenCalledWith(32, 66, 32, expect.objectContaining({ pitch: 25 }));
    expect(e.rig.beginTransition).toHaveBeenCalledWith(e.camera, expect.any(Number), 0);
  });

  it('offers directions for old designs and prevents pictures from unsaved edits replacing the saved icon', () => {
    render(
      <PictureControls
        viewer={{ current: null }}
        bounds={{ min: [0, 0, 0], max: [2, 4, 2] }}
        baseY={64}
        defaultOpen
        canWrite
        busy={false}
        dirty
        designId={9}
        onCapture={vi.fn()}
      />,
    );
    expect(screen.getByRole('button', { name: 'North' })).toBeEnabled();
    expect(screen.getByRole('button', { name: 'Update picture' })).toBeDisabled();
  });
});
