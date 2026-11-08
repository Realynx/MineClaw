import { MutationObserver, onlineManager } from '@tanstack/react-query';
import { afterEach, expect, it, vi } from 'vitest';
import { api, errorText, setCsrfToken, setFetch } from '@/lib/api/client';
import { makeQueryClient } from '@/lib/api/query';

afterEach(() => { onlineManager.setOnline(true); vi.unstubAllGlobals(); setCsrfToken(null); setFetch((...args) => globalThis.fetch(...args)); });

it('rejects an offline command immediately and does not send it when connectivity returns', async () => {
  const fetch = vi.fn<typeof globalThis.fetch>(); setFetch(fetch); setCsrfToken('test');
  vi.stubGlobal('navigator', { onLine: false }); onlineManager.setOnline(false);
  const client = makeQueryClient(); client.mount();
  const mutation = new MutationObserver(client, { mutationFn: () => api('story.time', { body: { time: 6000 } }) });
  const stop = mutation.subscribe(() => undefined);
  try {
    await expect(mutation.mutate()).rejects.toMatchObject({ code: 'offline' });
    expect(mutation.getCurrentResult().isPaused).toBe(false);
    expect(mutation.getCurrentResult().status).toBe('error');
    expect(errorText(mutation.getCurrentResult().error)).toContain('send the command again');
    vi.stubGlobal('navigator', { onLine: true }); onlineManager.setOnline(true);
    await client.resumePausedMutations(); await Promise.resolve();
    expect(fetch).not.toHaveBeenCalled();
  } finally { stop(); client.unmount(); client.clear(); }
});

it('rejects direct writes before CSRF fetch while read requests retain their ordinary network behavior', async () => {
  const fetch = vi.fn<typeof globalThis.fetch>().mockResolvedValue(new Response(JSON.stringify({ csrfToken: 'read-token-0123456789' }), { status: 200 }));
  setFetch(fetch); vi.stubGlobal('navigator', { onLine: false });
  await expect(api('story.time', { body: { time: 6000 } })).rejects.toMatchObject({ code: 'offline' });
  expect(fetch).not.toHaveBeenCalled();
  await expect(api('auth.csrf')).resolves.toEqual({ csrfToken: 'read-token-0123456789' });
  expect(fetch).toHaveBeenCalledOnce();
});
