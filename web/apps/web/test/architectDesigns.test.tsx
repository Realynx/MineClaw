import { fireEvent, render, screen, cleanup } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { ArchitectPanel } from '@/features/story/ArchitectPanel';
import { DESIGN_DRAG_TYPE } from '@/features/story/DesignLibrary';
import { PictureControls, pictureDistance, positionPictureCamera } from '@/features/studio/PictureControls';
import { qk } from '@/lib/api/query';
import type { WorldViewerEngine } from '@mineclaw/world-viewer';
import type { StoryArchitectEntry } from '@mineclaw/contracts';

afterEach(cleanup);

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
        <ArchitectPanel tool={{ kind: 'select' }} onTool={vi.fn()} onDesignPick={onPick} />
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
