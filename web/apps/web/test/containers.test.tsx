import type { ContainerSummary, ContainerView, StoryColonyState, StoryStorage } from '@mineclaw/contracts';
import { mockContainer } from '@mineclaw/contracts/testing';
import { QueryClientProvider, type QueryClient } from '@tanstack/react-query';
import { act, cleanup, render, screen, waitFor, within } from '@testing-library/react';
import type { ReactNode } from 'react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { setFetch } from '@/lib/api/client';
import { makeQueryClient, qk } from '@/lib/api/query';
import { ContainerGrid } from '@/features/live/inventory/ContainerGrid';
import {
  agoShort,
  containerLayout,
  freshnessText,
  isContainerBlock,
  containerTotals,
} from '@/features/live/inventory/containerLayout';
import { chestBadge } from '@/features/story/chestBadges';
import { ledgerKnown, ledgerRows, ledgerSlots, LedgerSummary } from '@/features/story/inspector/LedgerSummary';
import { StorageInspector } from '@/features/story/StoryInspector';
import { CONTAINER_KEEP_MS, ContainerContents } from '@/features/world/ContainerPanel';

afterEach(cleanup);

const NOW = Date.UTC(2026, 10, 3, 12, 0, 0);

const view = (over: Partial<ContainerView> = {}): ContainerView => ({
  ...mockContainer(0, 2, 74, -4, 0, NOW),
  ...over,
});

describe('container layouts', () => {
  it('a chest is three rows of nine, a double chest six, slots in place', () => {
    const single = containerLayout(view({ kind: 'chest', size: 27, slots: view().slots.filter((s) => s.slot < 27) }));
    expect(single.shape).toBe('rows');
    if (single.shape !== 'rows') return;
    expect(single.rows.map((r) => r.length)).toEqual([9, 9, 9]);
    expect(single.rows[0]![3]!.item?.name).toBe('planks');
    expect(single.rows[1]![0]!.item?.name).toBe('stick');
    const dbl = containerLayout(view());
    if (dbl.shape !== 'rows') throw new Error('rows');
    expect(dbl.rows).toHaveLength(6);
    expect(dbl.rows[5]![8]!.index).toBe(53);
    expect(dbl.rows[5]![8]!.item?.name).toBe('diamond');
    expect(dbl.rows[1]![4]!.item?.durability).toEqual({ max: 131, left: 100 });
  });

  it('a slot past the size is ignored; dispensers are 3×3 and a hopper one row of five', () => {
    const l = containerLayout(view({ kind: 'chest', size: 27 }));
    if (l.shape !== 'rows') throw new Error('rows');
    expect(l.rows.flat().some((c) => c.item?.name === 'diamond')).toBe(false); // slot 53
    const disp = containerLayout({ kind: 'dispenser', size: 9, slots: [] });
    if (disp.shape !== 'rows') throw new Error('rows');
    expect(disp.rows.map((r) => r.length)).toEqual([3, 3, 3]);
    const hop = containerLayout({ kind: 'hopper', size: 5, slots: [] });
    if (hop.shape !== 'rows') throw new Error('rows');
    expect(hop.rows.map((r) => r.length)).toEqual([5]);
  });

  it('a furnace is input, fuel and output; a brewing stand three bottles, the ingredient and fuel', () => {
    const f = containerLayout(mockContainer(0, 0, 74, -6));
    expect(f.shape).toBe('furnace');
    if (f.shape !== 'furnace') return;
    expect([f.input.item?.name, f.fuel.item?.name, f.output.item?.name]).toEqual(['iron_ore', 'coal', 'iron_ingot']);
    const b = containerLayout({
      kind: 'brewing_stand',
      size: 5,
      slots: [{ slot: 3, id: 372, meta: 0, count: 2, name: 'nether_wart', display: 'Nether Wart', enchanted: false }],
    });
    if (b.shape !== 'brewing') throw new Error('brewing');
    expect(b.ingredient.item?.name).toBe('nether_wart');
    expect(b.bottles.map((c) => c.index)).toEqual([0, 1, 2]);
    expect(b.fuel.index).toBe(4);
  });

  it('knows the container blocks of both versions', () => {
    for (const id of [54, 146, 61, 62, 23, 158, 154, 130, 117, 219, 234]) expect(isContainerBlock(id)).toBe(true);
    for (const id of [58, 1, 26, 145, 116, 218, 235]) expect(isContainerBlock(id)).toBe(false);
    expect(isContainerBlock(null)).toBe(false);
  });
});

describe('freshness', () => {
  it('says who looked and when, or that nobody did', () => {
    expect(freshnessText(view(), NOW)).toBe('Seen 3 min ago by maid_chan_102');
    expect(freshnessText(mockContainer(0, 17, 70, -32, 0, NOW), NOW)).toBe('Open right now by maid_chan_70');
    expect(freshnessText(mockContainer(0, 20, 70, -32, 0, NOW), NOW)).toBe('Not opened yet by any bot');
    expect(freshnessText(mockContainer(0, 6, 74, -4, 0, NOW), NOW)).toBe('Story storage ledger · 40 s ago');
    expect(freshnessText(view({ partial: true, seenAt: NOW - 2 * 3600_000 }), NOW)).toBe(
      'Seen 2 h ago by maid_chan_102 · one half only',
    );
    expect(agoShort(NOW - 2000, NOW)).toBe('just now');
    expect(agoShort(NOW - 3 * 86400_000, NOW)).toBe('3 d ago');
    expect(containerTotals(view())).toEqual({
      used: 12,
      items: 64 + 64 + 37 + 48 + 32 + 12 + 1 + 64 + 64 + 21 + 9 + 2,
    });
  });
});

describe('the container grid', () => {
  it('renders every slot of a large chest with the items in place', () => {
    render(<ContainerGrid view={view()} />);
    const grid = screen.getByTestId('container-grid');
    expect(grid.dataset.kind).toBe('double_chest');
    expect(grid.querySelectorAll('[data-slot]')).toHaveLength(54);
    expect(screen.getByTestId('ctr-slot-0').dataset.item).toBe('log');
    expect(screen.getByTestId('ctr-slot-53').dataset.count).toBe('2');
    expect(screen.getByTestId('ctr-slot-52').dataset.item).toBeUndefined();
  });

  it('renders a furnace as its three slots', () => {
    render(<ContainerGrid view={mockContainer(0, 0, 74, -6)} />);
    expect(screen.getByRole('group', { name: 'Furnace' })).toBeTruthy();
    expect(screen.getByTestId('container-grid').querySelectorAll('[data-slot]')).toHaveLength(3);
    expect(screen.getByLabelText(/^Fuel: Coal ×3/)).toBeTruthy();
  });
});

describe('Story chest badges', () => {
  const storage = (over: Partial<StoryStorage> = {}): StoryStorage => ({
    chest: [2, 74, -4],
    doubleWith: [3, 74, -4],
    label: null,
    priority: 'preferred',
    filter: { allow: ['wood'], deny: [] },
    contents: [],
    freeSlots: 27,
    knownAt: new Date(NOW - 60_000).toISOString(),
    ...over,
  });
  const summary = (over: Partial<ContainerSummary> = {}): ContainerSummary => ({
    pos: [2, 74, -4],
    dim: 0,
    kind: 'double_chest',
    size: 54,
    used: 45,
    total: 900,
    top: [],
    seenAt: NOW,
    seenBy: 'maid_chan_102',
    source: 'memory',
    cells: [
      [2, 74, -4],
      [3, 74, -4],
    ],
    ...over,
  });

  it('shows the category, and the fill from the fresher of the hive and the ledger', () => {
    const fresh = chestBadge(storage(), summary(), null)!;
    expect(fresh.text).toBe('Wood');
    expect(fresh.fill).toBeCloseTo(45 / 54);
    const stale = chestBadge(storage(), summary({ seenAt: NOW - 3600_000 }), null)!;
    expect(stale.fill).toBeCloseTo(27 / 54); // the ledger: 27 of 54 free
    expect(chestBadge(storage({ label: 'Logs' }), undefined, null)!.text).toBe('Logs');
  });

  it('a chest that is not storage shows its fill alone, or nothing when nobody looked', () => {
    expect(chestBadge(undefined, summary(), null)).toEqual({ text: null, fill: 45 / 54 });
    expect(chestBadge(undefined, summary({ source: 'unknown' }), null)).toBeNull();
    expect(chestBadge(undefined, undefined, null)).toBeNull();
  });
});

// ------------------------------------------------------------------ contents at once

interface Call {
  url: string;
  reply(body: unknown, status?: number): void;
}

/** The page's fetch, answered by hand: a request waits until the test replies to it. */
function network(): Call[] {
  const calls: Call[] = [];
  setFetch(
    (input) =>
      new Promise<Response>((resolve) => {
        const url = typeof input === 'string' ? input : input instanceof URL ? input.href : input.url;
        calls.push({ url, reply: (body, status = 200) => resolve(new Response(JSON.stringify(body), { status })) });
      }),
  );
  return calls;
}

const clients: QueryClient[] = [];
function client(): QueryClient {
  const qc = makeQueryClient();
  clients.push(qc);
  return qc;
}
const ui = (qc: QueryClient, node: ReactNode) => <QueryClientProvider client={qc}>{node}</QueryClientProvider>;

afterEach(() => {
  cleanup();
  vi.useRealTimers();
  for (const qc of clients.splice(0)) qc.clear();
  setFetch((...args) => globalThis.fetch(...args));
});

const DOUBLE = { dim: 0, x: 2, y: 74, z: -4 };
const DOUBLE_URL = '/api/world/container?dim=0&x=2&y=74&z=-4';
const SKELETON = '.skeleton';
/** How long a wait for what a reply sets off may poll (it does not measure time). */
const WAIT = { timeout: 4000 };

/** The colony's own ledger of the island's double chest: stacks of logs in two entries, cobblestone, an emptied entry. */
const ledger = (over: Partial<StoryStorage> = {}): StoryStorage => ({
  chest: [2, 74, -4],
  doubleWith: [3, 74, -4],
  label: null,
  priority: 'normal',
  filter: { allow: [], deny: [] },
  contents: [
    [4, 0, 64],
    [17, 0, 100],
    [17, 0, 28],
    [280, 0, 0],
  ],
  freeSlots: 51,
  knownAt: new Date(NOW - 40_000).toISOString(),
  ...over,
});

/** The summary's rows as "id:meta label count" (the icon's own letters left out). */
const ledgerLines = (root: HTMLElement = document.body) =>
  within(within(root).getByRole('list', { name: 'Ledger contents' }))
    .getAllByRole('listitem')
    .map((row) => `${row.dataset.item} ${row.children[1]!.textContent} ${row.dataset.count}`);

describe("the colony's ledger of a storage chest", () => {
  it('has something to show once the chest was counted, or holds stacks', () => {
    expect(ledgerKnown(ledger())).toBe(true);
    expect(ledgerKnown(ledger({ contents: [] }))).toBe(true);
    expect(ledgerKnown(ledger({ knownAt: null }))).toBe(true);
    expect(ledgerKnown(ledger({ contents: [], knownAt: null }))).toBe(false);
    expect(ledgerKnown(undefined)).toBe(false);
  });

  it('is one row per item, most first, without emptied entries', () => {
    expect(ledgerRows(ledger())).toEqual([
      { id: 17, meta: 0, label: 'Log', count: 128 },
      { id: 4, meta: 0, label: 'Cobblestone', count: 64 },
    ]);
  });

  it('knows the slots in use of 27 or 54 only once the chest was counted', () => {
    expect(ledgerSlots(ledger())).toEqual({ used: 3, slots: 54 });
    expect(ledgerSlots(ledger({ doubleWith: null, freeSlots: 20 }))).toEqual({ used: 7, slots: 27 });
    expect(ledgerSlots(ledger({ freeSlots: 99 }))).toEqual({ used: 0, slots: 54 });
    // Before a count `freeSlots` is the bot's default for a single chest, not knowledge.
    expect(ledgerSlots(ledger({ knownAt: null, freeSlots: 27 }))).toBeNull();
    expect(ledgerSlots(ledger({ freeSlots: null }))).toBeNull();
  });

  it('reads like the ledger answer it stands in for: its age, counts only, the slots in use, the totals', () => {
    render(<LedgerSummary storage={ledger()} now={NOW} />);
    expect(screen.getByTestId('ledger-freshness')).toHaveTextContent('Story storage ledger · 40 s ago · counts only');
    expect(screen.getByTestId('ledger-slots')).toHaveTextContent('3 / 54 slots');
    expect(ledgerLines()).toEqual(['17:0 Log 128', '4:0 Cobblestone 64']);
  });

  it('says so when the chest was empty at the last count, and leaves the slots out before any count', () => {
    const empty = render(<LedgerSummary storage={ledger({ contents: [], freeSlots: 54 })} now={NOW} />);
    expect(screen.getByTestId('ledger-summary')).toHaveTextContent('Empty when last counted.');
    expect(screen.getByTestId('ledger-slots')).toHaveTextContent('0 / 54 slots');
    expect(screen.queryByRole('list')).toBeNull();
    empty.unmount();

    render(<LedgerSummary storage={ledger({ knownAt: null })} now={NOW} />);
    expect(screen.getByTestId('ledger-freshness')).toHaveTextContent(
      'Story storage ledger · not counted yet · counts only',
    );
    expect(screen.queryByTestId('ledger-slots')).toBeNull();
    expect(ledgerLines()).toHaveLength(2);
  });
});

describe('chest contents at once', () => {
  it('shows the placeholder instead of the skeleton until the first answer, then the grid replaces it', async () => {
    const qc = client();
    const calls = network();
    const { container } = render(
      ui(
        qc,
        <ContainerContents target={DOUBLE} heading placeholder={<LedgerSummary storage={ledger()} now={NOW} />} />,
      ),
    );
    expect(calls.map((c) => c.url)).toEqual([DOUBLE_URL]);
    expect(screen.getByTestId('ledger-summary')).toHaveTextContent('Story storage ledger · 40 s ago · counts only');
    expect(container.querySelector(SKELETON)).toBeNull();
    expect(screen.queryByTestId('container-grid')).toBeNull();

    calls[0]!.reply(view());
    await waitFor(() => expect(screen.getByTestId('container-grid')).toBeInTheDocument(), WAIT);
    expect(screen.queryByTestId('ledger-summary')).toBeNull();
    expect(screen.getByTestId('container-freshness')).toHaveTextContent('by maid_chan_102');
    expect(screen.getByTestId('container-contents')).toHaveTextContent('12 / 54 slots');
  });

  it('without a placeholder the skeleton is unchanged', () => {
    const qc = client();
    network();
    const { container } = render(ui(qc, <ContainerContents target={DOUBLE} />));
    const skeleton = container.querySelector(SKELETON)!;
    expect(skeleton.className).toBe('skeleton rounded-md h-[7.5rem] w-[20rem] max-w-full rounded-lg');
    expect(screen.queryByTestId('ledger-summary')).toBeNull();
    expect(screen.queryByTestId('container-grid')).toBeNull();
  });

  it('a container that is gone says so, placeholder or not', async () => {
    const qc = client();
    const calls = network();
    render(ui(qc, <ContainerContents target={DOUBLE} placeholder={<LedgerSummary storage={ledger()} now={NOW} />} />));
    calls[0]!.reply({ error: 'no container there', code: 'not_found' }, 404);
    await waitFor(() => expect(screen.getByTestId('container-missing')).toBeInTheDocument(), WAIT);
    expect(screen.queryByTestId('ledger-summary')).toBeNull();
  });

  it('keeps a closed container for half an hour: opened again its grid shows at once, and it is read again', async () => {
    vi.useFakeTimers();
    const qc = client();
    const calls = network();
    const first = render(ui(qc, <ContainerContents target={DOUBLE} />));
    expect(calls).toHaveLength(1);
    await act(async () => {
      calls[0]!.reply(view());
      await vi.advanceTimersByTimeAsync(20);
    });
    expect(screen.getByTestId('container-grid')).toBeInTheDocument();
    first.unmount();

    // Twice as long as an answer nobody shows is otherwise kept (5 minutes).
    act(() => void vi.advanceTimersByTime(10 * 60_000));
    const { container } = render(ui(qc, <ContainerContents target={DOUBLE} />));
    expect(screen.getByTestId('container-grid')).toBeInTheDocument();
    expect(container.querySelector(SKELETON)).toBeNull();
    expect(calls).toHaveLength(2);
    expect(CONTAINER_KEEP_MS).toBe(30 * 60_000);
  });

  it('a kept answer that said "open right now" reads as the look it was until the new answer arrives', async () => {
    const qc = client();
    const calls = network();
    const target = { dim: 0, x: 17, y: 70, z: -32 };
    const then = Date.now() - 20 * 60_000;
    qc.setQueryData(qk('world.container', {}, target), mockContainer(0, 17, 70, -32, 0, then), { updatedAt: then });

    render(ui(qc, <ContainerContents target={target} />));
    expect(screen.getByTestId('container-grid')).toBeInTheDocument();
    const line = screen.getByTestId('container-freshness');
    expect(line).toHaveTextContent('Seen 20 min ago by maid_chan_70');
    expect(line.className).not.toContain('text-ok');
    expect(calls).toHaveLength(1);

    calls[0]!.reply(mockContainer(0, 17, 70, -32, 1, Date.now()));
    await waitFor(() => expect(line).toHaveTextContent('Open right now by maid_chan_70'), WAIT);
    expect(line.className).toContain('text-ok');
  });
});

describe("Story's chest inspector", () => {
  const stateOf = (storages: StoryStorage[]) => ({ storages, reservations: [] }) as unknown as StoryColonyState;
  const inspect = (qc: QueryClient, storages: StoryStorage[]) =>
    render(
      ui(
        qc,
        <StorageInspector
          chests={[[2, 74, -4]]}
          state={stateOf(storages)}
          canWrite
          write={vi.fn()}
          clip={null}
          onClip={vi.fn()}
          onClose={vi.fn()}
        />,
      ),
    );

  it("shows the colony's ledger of a clicked storage chest at once; the exact grid replaces it", async () => {
    const qc = client();
    const calls = network();
    inspect(qc, [ledger({ knownAt: new Date(Date.now() - 60_000).toISOString() })]);
    const contents = screen.getByTestId('container-contents');
    const summary = within(contents).getByTestId('ledger-summary');
    expect(summary).toHaveTextContent('Story storage ledger');
    expect(summary).toHaveTextContent('counts only');
    expect(summary).toHaveTextContent('3 / 54 slots');
    expect(ledgerLines(summary)).toEqual(['17:0 Log 128', '4:0 Cobblestone 64']);
    expect(contents.querySelector(SKELETON)).toBeNull();
    expect(calls.map((c) => c.url)).toEqual([DOUBLE_URL]);

    calls[0]!.reply(view());
    await waitFor(() => expect(within(contents).getByTestId('container-grid')).toBeInTheDocument(), WAIT);
    expect(within(contents).queryByTestId('ledger-summary')).toBeNull();
    expect(within(contents).getByTestId('ctr-slot-26').dataset.item).toBe('cobblestone');
  });

  it('keeps the skeleton for a chest that is not storage, or that the colony has not counted yet', () => {
    const qc = client();
    network();
    const plain = inspect(qc, []);
    expect(screen.getByTestId('container-contents').querySelector(SKELETON)).not.toBeNull();
    expect(screen.queryByTestId('ledger-summary')).toBeNull();
    plain.unmount();

    inspect(qc, [ledger({ contents: [], knownAt: null, freeSlots: 27 })]);
    expect(screen.getByTestId('container-contents').querySelector(SKELETON)).not.toBeNull();
    expect(screen.queryByTestId('ledger-summary')).toBeNull();
  });
});
