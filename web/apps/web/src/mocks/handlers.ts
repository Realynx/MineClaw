/** MSW handlers: every route in ROUTES (registration order) plus the synthetic SSE stream. */
import { ROUTES, type RouteId } from '@mineclaw/contracts';
import { delay, http, HttpResponse, sse, type HttpHandler } from 'msw';
import { checkAccess, MockError, resolvers } from './api';
import { liveStatus, setEmitter, statusResponse, hiveTree, tick } from './data/live';

function errorResponse(e: unknown): Response {
  if (e instanceof MockError) {
    // A MemoryEditResult refusal passes through as 400 with that body.
    const body = 'ok' in e.extra ? e.extra : { error: e.message, ...(e.code ? { code: e.code } : {}), ...e.extra };
    return HttpResponse.json(body, { status: e.status, headers: e.headers });
  }
  console.error('[mock]', e);
  return HttpResponse.json({ error: 'Internal mock error', code: 'internal' }, { status: 500 });
}

async function readBody(request: Request, multipart: boolean): Promise<{ body: unknown; file: File | null }> {
  if (request.method === 'GET') return { body: null, file: null };
  if (multipart) {
    const form = await request.formData();
    const opts = form.get('options');
    return { body: typeof opts === 'string' ? (JSON.parse(opts) as unknown) : {}, file: (form.get('shard') as File | null) ?? null };
  }
  const text = await request.text();
  return { body: text ? (JSON.parse(text) as unknown) : {}, file: null };
}

function routeHandler(id: RouteId): HttpHandler | null {
  const route = ROUTES[id];
  const resolve = resolvers[id];
  if (!resolve || route.responseKind === 'sse') return null;
  const method = route.method === 'GET' ? http.get : http.post;
  return method(route.path, async ({ request, params }) => {
    await delay(route.method === 'GET' ? 60 + Math.random() * 140 : 250);
    try {
      checkAccess(id, request.headers.get('X-CSRF-Token'), request.method);
      const url = new URL(request.url);
      const rawQuery = Object.fromEntries(url.searchParams);
      const query = route.query ? (route.query.parse(rawQuery) as Record<string, unknown>) : {};
      const parsedParams = route.params ? (route.params.parse(params) as Record<string, unknown>) : {};
      const { body, file } = await readBody(request, route.bodyKind === 'multipart');
      const parsedBody = route.body ? (route.body.parse(body)) : body;
      const out = resolve({ params: parsedParams, query, body: parsedBody, file });
      if (out instanceof Response) return out;
      return HttpResponse.json(out as never, { status: route.status ?? 200 });
    } catch (e) {
      if (e && typeof e === 'object' && 'issues' in e) {
        return HttpResponse.json({ error: 'Invalid request', code: 'bad_request', details: (e as { issues: Array<{ path: unknown[]; message: string }> }).issues.map((i) => ({ path: i.path.join('.'), message: i.message })) }, { status: 400 });
      }
      return errorResponse(e);
    }
  });
}

type Client = { send: (m: { event: string; id: string; data: unknown }) => void };
const clients = new Set<Client>();
let frameId = 0;
let timer: ReturnType<typeof setInterval> | null = null;

function broadcast(event: string, data: unknown): void {
  frameId++;
  for (const c of clients) {
    try {
      c.send({ event, id: String(frameId), data });
    } catch {
      clients.delete(c);
    }
  }
}

setEmitter(broadcast);

const stream = sse(ROUTES['live.stream'].path, ({ client, request }) => {
  const c = client as unknown as Client;
  clients.add(c);
  request.signal.addEventListener('abort', () => clients.delete(c));
  const send = (event: string, data: unknown) => c.send({ event, id: String(++frameId), data });
  send('status', liveStatus());
  send('bots', statusResponse());
  send('hive', hiveTree());
  timer ??= setInterval(() => {
    for (const [event, data] of tick()) broadcast(event, data);
  }, 1000);
});

export const handlers: HttpHandler[] = [
  stream,
  ...(Object.keys(ROUTES) as RouteId[]).map(routeHandler).filter((h): h is HttpHandler => h !== null),
];
