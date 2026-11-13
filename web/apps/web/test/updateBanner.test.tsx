import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import type { Role, SystemUpdateStatus } from '@mineclaw/contracts';
import { fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { Markdown, parseBlocks, safeHref } from '@/components/ui/Markdown';
import { UpdateBanner } from '@/features/system/UpdateBanner';
import { setCsrfToken, setFetch } from '@/lib/api/client';
import { StaticAuthProvider } from '@/lib/auth';

const NOTES = '## Fixes\n- **faster** paths\n- see [the docs](https://example.com/docs) and [bad](javascript:alert(1))\n\n<script>alert(1)</script>';

function status(over: Partial<SystemUpdateStatus> = {}, update: Partial<SystemUpdateStatus['update']> = {}): SystemUpdateStatus {
  return {
    enabled: true,
    current: '1.1.0',
    latest: {
      version: '1.2.0',
      name: 'MineClaw 1.2.0',
      notes: NOTES,
      notesTruncated: false,
      url: 'https://github.com/Realynx/MineClaw/releases/tag/v1.2.0',
      publishedAt: '2026-11-10T12:00:00Z',
      prerelease: false,
    },
    outdated: true,
    checkedAt: '2026-11-12T10:00:00Z',
    lastError: null,
    installKind: 'systemd',
    update: { mode: 'trigger', status: null, request: null, ...update },
    ...over,
  };
}

const json = (body: unknown, code = 200) =>
  Promise.resolve(new Response(JSON.stringify(body), { status: code, headers: { 'Content-Type': 'application/json' } }));

function setup(roles: Role[], first: SystemUpdateStatus) {
  let current = first;
  const send = vi.fn((url: string, init?: RequestInit) => {
    if (url === '/api/system/update' && init?.method === 'POST') {
      current = status({}, { request: { version: '1.2.0', requestedBy: 'fox', requestedAt: '2026-11-12T10:01:00Z' } });
      return json(current);
    }
    if (url === '/api/system/update') return json(current);
    return json({ error: 'not found', code: 'not_found' }, 404);
  });
  setFetch(send as unknown as typeof fetch);
  setCsrfToken('test-csrf-token-000000000');
  const qc = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  const me = { username: 'fox', player: null, roles, totpEnrolled: false, sessionCreatedAt: '', idleExpiresAt: '', absoluteExpiresAt: '' };
  const view = render(
    <QueryClientProvider client={qc}>
      <StaticAuthProvider me={me}>
        <UpdateBanner />
      </StaticAuthProvider>
    </QueryClientProvider>,
  );
  return { send, view };
}

beforeEach(() => window.localStorage.clear());
afterEach(() => setCsrfToken(null));

describe('update banner', () => {
  it('tells every signed-in user, renders the notes safely and hides per version', async () => {
    const { view } = setup(['viewer'], status());
    const banner = await screen.findByRole('region', { name: 'MineClaw update' });
    expect(within(banner).getByText('MineClaw v1.2.0 is available')).toBeVisible();
    expect(within(banner).getByText('(you run v1.1.0)')).toBeVisible();
    expect(within(banner).queryByRole('button', { name: 'Update now' })).toBeNull();

    fireEvent.click(within(banner).getByRole('button', { name: /Release notes/ }));
    expect(within(banner).getByText('faster').tagName).toBe('STRONG');
    expect(within(banner).getByRole('link', { name: 'the docs' })).toHaveAttribute('href', 'https://example.com/docs');
    expect(within(banner).queryByRole('link', { name: 'bad' })).toBeNull();
    expect(within(banner).getByText('<script>alert(1)</script>')).toBeVisible();
    expect(banner.querySelector('script')).toBeNull();

    fireEvent.click(within(banner).getByRole('button', { name: 'Hide until the release after v1.2.0' }));
    expect(screen.queryByRole('region', { name: 'MineClaw update' })).toBeNull();
    expect(JSON.parse(window.localStorage.getItem('mineclaw.updates.dismissed')!)).toEqual({ version: '1.2.0' });
    view.unmount();
    const again = setup(['viewer'], status());
    await waitFor(() => expect(again.send).toHaveBeenCalled());
    await new Promise((r) => setTimeout(r, 30));
    expect(screen.queryByRole('region', { name: 'MineClaw update' })).toBeNull();
  });

  it('shows nothing when up to date or disabled', async () => {
    const { send } = setup(['admin'], status({ outdated: false }));
    await waitFor(() => expect(send).toHaveBeenCalled());
    expect(screen.queryByRole('region', { name: 'MineClaw update' })).toBeNull();
  });

  it('lets an admin start a systemd update after confirming, then shows it waiting', async () => {
    const { send } = setup(['admin'], status());
    fireEvent.click(await screen.findByRole('button', { name: 'Update now' }));
    expect(send).not.toHaveBeenCalledWith('/api/system/update', expect.objectContaining({ method: 'POST' }));
    fireEvent.click(screen.getByRole('button', { name: 'Update to v1.2.0' }));
    await screen.findByText(/Update to v1.2.0 requested; waiting for the updater/);
    expect(send).toHaveBeenCalledWith(
      '/api/system/update',
      expect.objectContaining({ method: 'POST', body: JSON.stringify({ version: '1.2.0' }) }),
    );
    expect(screen.getByRole('button', { name: 'Update now' })).toBeDisabled();
  });

  it('shows a finished run and the copyable command for container installs', async () => {
    const finished = new Date(Date.now() - 60_000).toISOString();
    const { view } = setup(['admin'], status({ current: '1.2.0', outdated: false }, { status: { state: 'rolled-back', version: '1.2.0', from: '1.1.0', finishedAt: finished, message: 'health check failed' } }));
    expect(await screen.findByText(/failed and was rolled back/)).toBeVisible();
    expect(screen.getByText(/health check failed/)).toBeVisible();
    view.unmount();

    setup(['admin'], status({ installKind: 'docker' }, { mode: 'command', command: 'cd /opt/mineclaw-docker && docker compose pull' }));
    fireEvent.click(await screen.findByRole('button', { name: 'How to update' }));
    expect(screen.getByText('cd /opt/mineclaw-docker && docker compose pull')).toBeVisible();
    expect(screen.queryByRole('button', { name: 'Update now' })).toBeNull();
  });
});

describe('markdown', () => {
  it('parses blocks and only allows http(s) links', () => {
    expect(parseBlocks('# Title\n\ntext\nmore\n\n1. one\n2. two\n\n```\n<b>x</b>\n```').map((b) => b.kind)).toEqual(['heading', 'para', 'list', 'code']);
    expect(safeHref('javascript:alert(1)')).toBeNull();
    expect(safeHref('https://github.com/x')).toBe('https://github.com/x');
    render(<Markdown source="keep snake_case_names and _em_ https://github.com/Realynx/MineClaw." />);
    expect(screen.getByText('em').tagName).toBe('EM');
    expect(screen.getByRole('link')).toHaveAttribute('href', 'https://github.com/Realynx/MineClaw');
    expect(screen.getByText(/keep snake_case_names/)).toBeVisible();
  });
});
