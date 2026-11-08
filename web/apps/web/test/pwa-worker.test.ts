// @vitest-environment node
import { readFileSync } from 'node:fs';
import { runInNewContext } from 'node:vm';
import { describe, expect, it, vi } from 'vitest';

describe('installable app assets', () => {
  it('declares standalone installation and real 192/512 pixel icons', () => {
    const manifest = JSON.parse(readFileSync('public/manifest.webmanifest', 'utf8')) as { icons: Array<{ src: string; sizes: string }> };
    expect(manifest).toMatchObject({
      id: '/',
      scope: '/',
      display: 'standalone',
      start_url: '/story',
      prefer_related_applications: false,
    });
    for (const size of [192, 512]) {
      const icon = manifest.icons.find((x: { sizes: string }) => x.sizes === `${size}x${size}`);
      const png = readFileSync('public' + icon!.src);
      expect(png.subarray(1, 4).toString()).toBe('PNG');
      expect([png.readUInt32BE(16), png.readUInt32BE(20)]).toEqual([size, size]);
    }
  });
  it('never intercepts API, authenticated data, commands, textures, or cross-origin requests', () => {
    const callbacks: Record<string, (event: unknown) => void> = {};
    runInNewContext(readFileSync('public/sw.js', 'utf8'), {
      self: {
        location: { origin: 'https://colony.example' },
        addEventListener: (kind: string, fn: (e: unknown) => void) => {
          callbacks[kind] = fn;
        },
      },
      URL,
    });
    for (const request of [
      { url: 'https://colony.example/api/auth/me', method: 'GET', mode: 'cors' },
      { url: 'https://colony.example/api/admin/settings', method: 'GET', mode: 'navigate' },
      { url: 'https://colony.example/api/story/orders', method: 'POST', mode: 'navigate' },
      { url: 'https://colony.example/texture.png', method: 'GET', mode: 'no-cors' },
      { url: 'https://other.example/page', method: 'GET', mode: 'navigate' },
    ]) {
      const respondWith = vi.fn();
      callbacks.fetch!({ request, respondWith });
      expect(respondWith).not.toHaveBeenCalled();
    }
  });
  it('uses a generic offline fallback without caching live pages and removes only its own old cache', async () => {
    const callbacks: Record<string, (event: unknown) => void> = {};
    const offline = new Response('generic offline page');
    const caches = {
      match: vi.fn().mockResolvedValue(offline),
      keys: () => Promise.resolve(['mineclaw-pwa-v0', 'mineclaw-pwa-v1', 'local-textures']),
      delete: vi.fn().mockResolvedValue(true),
    };
    const fetch = vi.fn().mockRejectedValue(new TypeError('offline'));
    runInNewContext(readFileSync('public/sw.js', 'utf8'), {
      self: {
        location: { origin: 'https://colony.example' },
        addEventListener: (kind: string, fn: (e: unknown) => void) => {
          callbacks[kind] = fn;
        },
      },
      URL,
      Response,
      fetch,
      caches,
    });
    let response: Promise<Response> | undefined;
    callbacks.fetch!({
      request: { method: 'GET', mode: 'navigate', url: 'https://colony.example/administration' },
      respondWith: (p: Promise<Response>) => {
        response = p;
      },
    });
    expect(await (await response)!.text()).toBe('generic offline page');
    expect(caches.match).toHaveBeenCalledWith('/offline.html');
    let activated: Promise<unknown> | undefined;
    callbacks.activate!({
      waitUntil: (p: Promise<unknown>) => {
        activated = p;
      },
    });
    await activated;
    expect(caches.delete.mock.calls).toEqual([['mineclaw-pwa-v0']]);
  });
});
