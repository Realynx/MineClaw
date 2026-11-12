import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { afterEach, beforeEach, expect, it, vi } from 'vitest';
import type { PawnSkins, SkinRef } from '@mineclaw/contracts';
import { setCsrfToken, setFetch } from '@/lib/api/client';
import { fakeImageBitmap, skinPng } from './skinPng';

const refresh = vi.fn();
vi.mock('@/features/world/integration', () => ({ loadWorldViewer: () => Promise.resolve({ refreshHumanSkins: refresh }) }));
vi.mock('@/features/story/skins/defaultSkins', () => ({ loadDefaultSkin: () => Promise.resolve(null) }));

const { SkinBrowser } = await import('@/features/story/skins/SkinBrowser');

const KNIGHT = 'a'.repeat(24);
let view: PawnSkins;
let calls: Array<{ url: string; body: unknown }>;

/** A tiny gateway: the list, uploads (id = 'b'×24), assignments. */
function gateway() {
  calls = [];
  setFetch((input, init) => {
    const url = typeof input === 'string' ? input : input instanceof URL ? input.href : input.url;
    const body = typeof init?.body === 'string' ? (JSON.parse(init.body) as Record<string, unknown>) : null;
    calls.push({ url, body });
    const json = (v: unknown) => Promise.resolve(new Response(JSON.stringify(v), { headers: { 'Content-Type': 'application/json' } }));
    if (url === '/api/skins' && init?.method === 'POST') {
      const skin = { id: 'b'.repeat(24), label: body!.label, slim: body!.slim, height: 64, png: body!.png, source: 'upload', createdAt: '2026-11-12T00:00:00Z' };
      view = { ...view, library: [...view.library, skin as PawnSkins['library'][number]] };
      return json({ skin, skins: view });
    }
    if (url === '/api/skins/assign') {
      const { pawns, skin } = body as { pawns: string[]; skin: SkinRef | null };
      const assignments = { ...view.assignments };
      for (const p of pawns) {
        if (skin) assignments[p.toLowerCase()] = skin;
        else delete assignments[p.toLowerCase()];
      }
      view = { ...view, assignments };
      return json(view);
    }
    if (url === '/api/skins') return json(view);
    return Promise.resolve(new Response('{}', { status: 404 }));
  });
  setCsrfToken('test-csrf-token-000000000');
}

beforeEach(() => {
  vi.stubGlobal('createImageBitmap', fakeImageBitmap);
  refresh.mockClear();
  view = {
    library: [{ id: KNIGHT, label: 'Knight', slim: false, height: 64, png: skinPng().toString('base64'), source: 'upload', createdAt: '2026-11-01T00:00:00Z' }],
    assignments: {},
    lookup: false,
  };
  gateway();
});
afterEach(() => {
  setCsrfToken(null);
  vi.unstubAllGlobals();
});

function show(canWrite = true) {
  const qc = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  render(
    <QueryClientProvider client={qc}>
      <SkinBrowser pawn="Maid_Chan" display="Maid" canWrite={canWrite} version="1.12.2" />
    </QueryClientProvider>,
  );
}

const posts = () => calls.filter((c) => c.body !== null);

it('previews a library skin before applying it to the pawn, then shows it as worn and refreshes the world views', async () => {
  show();
  expect(screen.getByText(/offline mode/)).toBeVisible();
  expect(screen.getByTestId('skin-wearing')).toHaveTextContent('Default appearance');
  fireEvent.click(await screen.findByRole('button', { name: 'Skin Knight' }));
  expect(within(screen.getByTestId('skin-preview')).getByText('Preview, not applied yet')).toBeVisible();
  expect(posts()).toEqual([]);
  fireEvent.click(screen.getByRole('button', { name: 'Apply to Maid' }));
  await waitFor(() => expect(screen.getByTestId('skin-wearing')).toHaveTextContent('Knight'));
  expect(posts()).toEqual([{ url: '/api/skins/assign', body: { pawns: ['Maid_Chan'], skin: { kind: 'library', id: KNIGHT } } }]);
  expect(screen.queryByTestId('skin-preview')).toBeNull();
  await waitFor(() => expect(refresh).toHaveBeenCalled());
  expect(screen.getByRole('status')).toHaveTextContent('Maid now wears Knight');

  fireEvent.click(screen.getByRole('button', { name: 'Use default appearance' }));
  await waitFor(() => expect(screen.getByTestId('skin-wearing')).toHaveTextContent('Default appearance'));
  expect(posts().at(-1)?.body).toEqual({ pawns: ['Maid_Chan'], skin: null });
});

it('selects a default skin (from the imported game assets) and says how to get previews', async () => {
  show();
  fireEvent.click(screen.getByRole('button', { name: 'Defaults' }));
  expect(await screen.findByText(/import your Minecraft jar in Settings/)).toBeVisible();
  fireEvent.click(screen.getByRole('button', { name: 'Skin Alex' }));
  fireEvent.click(screen.getByRole('button', { name: 'Apply to Maid' }));
  await waitFor(() => expect(screen.getByTestId('skin-wearing')).toHaveTextContent('Alex'));
  expect(posts()).toEqual([{ url: '/api/skins/assign', body: { pawns: ['Maid_Chan'], skin: { kind: 'default', id: 'alex' } } }]);
});

it('validates an upload in the browser, previews it, then adds it to the library and applies it', async () => {
  show();
  fireEvent.click(screen.getByRole('button', { name: 'Upload' }));
  const input = screen.getByLabelText('Skin file');
  fireEvent.change(input, { target: { files: [new File([new Uint8Array(skinPng({ width: 32, height: 32 }))], 'tiny.png', { type: 'image/png' })] } });
  expect(await screen.findByTestId('skin-upload-error')).toHaveTextContent('this image is 32×32');
  expect(posts()).toEqual([]);

  const png = skinPng({ rgba: [10, 200, 30, 255] });
  fireEvent.change(input, { target: { files: [new File([new Uint8Array(png)], 'Green Knight.png', { type: 'image/png' })] } });
  expect(await screen.findByRole('img', { name: 'Uploaded skin preview' })).toBeInTheDocument();
  expect(screen.getByLabelText('Skin name')).toHaveValue('Green Knight');
  fireEvent.click(screen.getByRole('checkbox', { name: /Slim arms/ }));
  fireEvent.click(screen.getByRole('button', { name: 'Upload and apply' }));
  await waitFor(() => expect(screen.getByTestId('skin-wearing')).toHaveTextContent('Green Knight'));
  expect(posts()).toEqual([
    { url: '/api/skins', body: { label: 'Green Knight', slim: true, png: png.toString('base64') } },
    { url: '/api/skins/assign', body: { pawns: ['Maid_Chan'], skin: { kind: 'library', id: 'b'.repeat(24) } } },
  ]);
  // Back on the library, where the new skin is listed for every pawn.
  expect(screen.getByRole('button', { name: 'Skin Green Knight' })).toBeVisible();
});

it('lets a viewer browse but not apply, and explains that lookups are off', async () => {
  show(false);
  fireEvent.click(await screen.findByRole('button', { name: 'Skin Knight' }));
  expect(screen.getByRole('button', { name: 'Apply to Maid' })).toBeDisabled();
  expect(screen.queryByRole('button', { name: 'Delete Knight' })).toBeNull();
  expect(screen.getByText(/needs the Story Mode role/)).toBeVisible();
  fireEvent.click(screen.getByRole('button', { name: 'Look up' }));
  expect(screen.getByTestId('skin-lookup-off')).toBeVisible();
});
