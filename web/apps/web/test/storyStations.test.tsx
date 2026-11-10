import {
  STORY_DEFAULT_RECIPES,
  StoryBillsRequest,
  StoryColonyState,
  StoryWorkbenchesRequest,
  type StoryBill,
} from '@mineclaw/contracts';
import { cleanup, fireEvent, render, screen } from '@testing-library/react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { api, ApiError } from '@/lib/api/client';
import { BUSY_TEXT, storyWriteError, type Track } from '@/features/story/optimistic';
import { BedInspector, BillDialog, BillsInspector, FuelPicker, ReservationList } from '@/features/story/StoryInspector';
import { BillRow } from '@/features/story/inspector/BillRow';
import { EMPTY_SELECTION } from '@/features/story/model';
import type { Story } from '@/features/story/mode/commands';
import { selectionInspector } from '@/features/story/mode/SelectionInspector';
import type { StoryWrite } from '@/features/story/useStory';
import {
  addBillBody,
  draftOf,
  fuelBody,
  fuelLine,
  fuelTable,
  newDraft,
  recipeLine,
  reservationLine,
  reservationRows,
  updateBillBody,
} from '@/features/story/storage';
import { harnessDown, harnessUp, mountInspector, network, posted, settle } from './storyInspectorHarness';

vi.mock('@/features/world/ContainerPanel', () => ({ ContainerContents: () => null }));

afterEach(cleanup);

const recipe = (id: string) => STORY_DEFAULT_RECIPES.find((r) => r.id === id)!;

describe('station bills in the bill dialog', () => {
  it('saves cross-bench priority from the editor and changes it directly from a waiting bill', () => {
    const r = recipe('smelt_iron');
    const onSave = vi.fn();
    const view = render(<BillDialog recipe={r} initial={newDraft(r, 'forever')} isNew state={undefined}
      onPickChest={() => {}} onSave={onSave} onClose={() => {}} />);
    expect(screen.getByLabelText<HTMLSelectElement>('Bill priority').value).toBe('3');
    fireEvent.change(screen.getByLabelText('Bill priority'), { target: { value: '1' } });
    fireEvent.click(screen.getByRole('button', { name: 'Add bill' }));
    expect(onSave).toHaveBeenCalledWith(expect.objectContaining({ priority: 1 }));
    const bill: StoryBill = {
      id: 'priority-bill', recipe: r.id, mode: 'forever', target: 1, done: 0, paused: false,
      ingredients: { allow: [], deny: [] }, radius: 32, pawn: 'any', store: 'best', waitingReason: 'no iron ore',
    };
    expect(updateBillBody(bill, { ...draftOf(bill), priority: 1 })).toEqual({ op: 'update', id: bill.id, priority: 1 });
    expect(addBillBody([6, 64, 0], { ...newDraft(r), priority: 2 }).bill.priority).toBe(2);
    const write = vi.fn(() => Promise.resolve(undefined));
    view.rerender(<BillRow bill={bill} recipe={r} bench={{ pos: [6, 64, 0], kind: 'furnace', bills: [bill] }}
      state={undefined} canWrite write={write as never} onDragStart={() => {}} onDrop={() => {}} onEdit={() => {}} />);
    expect(screen.getByText('Waiting: no iron ore').className).not.toContain('text-danger');
    expect(screen.queryByText(/Suspended:/)).toBeNull();
    fireEvent.change(screen.getByLabelText(`Priority for ${r.label}`), { target: { value: '2' } });
    expect(write).toHaveBeenCalledWith('story.bills', { op: 'update', id: bill.id, priority: 2 });
  });

  it('an enchant bill offers count and forever, the option, the preferred enchantment and prefer-only', () => {
    const r = recipe('enchant:diamond_pickaxe');
    const onSave = vi.fn();
    render(
      <BillDialog recipe={r} initial={newDraft(r)} isNew state={undefined} onPickChest={() => {}} onSave={onSave} onClose={() => {}} />,
    );
    const modes = [...screen.getByLabelText<HTMLSelectElement>('Repeat mode').options].map((o) => o.value);
    expect(modes).toEqual(['count', 'forever']);
    expect(screen.getByTestId('bill-requires').textContent).toContain('XP level 3+ · 3 lapis');
    fireEvent.change(screen.getByLabelText('Enchant option'), { target: { value: '2' } });
    expect(screen.getByTestId('bill-requires').textContent).toContain('XP level 2+ · 2 lapis');
    const only = screen.getByRole<HTMLInputElement>('checkbox', { name: /Only that one/ });
    expect(only.disabled).toBe(true); // nothing preferred yet
    fireEvent.change(screen.getByLabelText('Preferred enchantment'), { target: { value: 'fortune' } });
    fireEvent.click(screen.getByRole('checkbox', { name: /Only that one/ }));
    fireEvent.click(screen.getByRole('button', { name: 'Add bill' }));
    expect(onSave).toHaveBeenCalledWith(expect.objectContaining({ recipe: r.id, mode: 'count', option: 2, prefer: 'fortune', preferOnly: true }));
  });

  it('a brew shows its fuel, its water and its steps from water bottles', () => {
    const r = recipe('brew:swiftness');
    render(<BillDialog recipe={r} initial={newDraft(r)} isNew state={undefined} onPickChest={() => {}} onSave={() => {}} onClose={() => {}} />);
    const needs = screen.getByTestId('bill-requires');
    expect(needs.textContent).toContain('fuel: blaze powder');
    expect(needs.textContent).toContain('water nearby');
    expect([...screen.getByLabelText('Brewing steps').querySelectorAll('li')].map((l) => l.textContent)).toEqual([
      'Water Bottle + Nether Wart → Awkward Potion',
      'Awkward Potion + Sugar → Potion of Swiftness',
    ]);
    expect([...screen.getByLabelText<HTMLSelectElement>('Repeat mode').options].map((o) => o.value)).toEqual(['count', 'until', 'forever']);
    expect(screen.queryByLabelText('Enchant option')).toBeNull();
  });
});

describe('reservations', () => {
  it('lists what builds hold of the items shown and releases a claim', () => {
    const list = [
      { item: [15, 0] as [number, number], count: 3, build: 'b9306', forItem: 'bucket', chain: true },
      { item: [5, -1] as [number, number], count: 8, build: 'b12', forItem: 'oak_fence', chain: true },
      { item: [4, 0] as [number, number], count: 20, build: 'b3', forItem: 'cobblestone', chain: false },
    ];
    const rows = reservationRows(list, new Set(['15:0', '5:2']));
    expect(rows.map((r) => r.build)).toEqual(['b12', 'b9306']); // planks of any kind match birch planks; cobblestone isn't shown here
    expect(reservationLine(rows[1]!)).toBe('3 iron ore → build #b9306 (bucket, crafting it)');
    const write = vi.fn(() => Promise.resolve(undefined));
    render(<ReservationList reservations={list} items={new Set(['15:0'])} canWrite write={write as never} />);
    fireEvent.click(screen.getByRole('button', { name: 'Release' }));
    expect(write).toHaveBeenCalledWith('story.bills', { op: 'release_claim', build: 'b9306', item: 'bucket' });
    expect(StoryBillsRequest.safeParse({ op: 'release_claim', build: 'b9306', item: 'bucket' }).success).toBe(true);
  });
});

describe('furnace fuel', () => {
  it('the picker shows the choices the version burns and says which one is set', () => {
    const onPick = vi.fn();
    render(<FuelPicker value="auto" choices={['auto', 'coal', 'planks', 'logs']} onPick={onPick} />);
    expect(screen.getByRole('button', { name: /Fuel: Auto/ }).getAttribute('aria-pressed')).toBe('true');
    expect(screen.queryByRole('button', { name: 'Fuel: Blaze rod' })).toBeNull();
    fireEvent.click(screen.getByRole('button', { name: 'Fuel: Coal' }));
    expect(onPick).toHaveBeenCalledWith('coal');
    expect(fuelBody([6, 64, 0], 'coal')).toEqual({ op: 'fuel', pos: [6, 64, 0], fuel: 'coal' });
    expect(StoryWorkbenchesRequest.safeParse(fuelBody([6, 64, 0], 'coal')).success).toBe(true);
    expect(StoryWorkbenchesRequest.safeParse({ op: 'fuel', pos: [6, 64, 0], fuel: 'wood' }).success).toBe(false);
  });

  it('a bill says its fuel need, short or in the furnace already, and the dialog lists the fuel table', () => {
    const base = { choice: 'coal', item: [263, 0] as [number, number], name: 'coal', perItem: 0.125 };
    expect(fuelLine({ ...base, need: 1, have: 12, short: false })).toBe('fuel: 1 coal (have 12)');
    expect(fuelLine({ ...base, need: 1, have: 0, short: true })).toBe('needs fuel: 1 coal — none in storage');
    expect(fuelLine({ ...base, need: 0, have: 3, short: false })).toBe('fuel: in the furnace already');
    const iron = STORY_DEFAULT_RECIPES.find((x) => x.id === 'smelt_iron')!;
    expect(fuelTable(iron)).toContain('coal 1 per 8 items');
    expect(recipeLine(iron)).toBe('1 iron ore + fuel → 1 iron ingot');
    const onSave = vi.fn();
    render(
      <BillDialog recipe={iron} initial={newDraft(iron)} isNew state={undefined} onPickChest={() => {}} onSave={onSave} onClose={() => {}} />,
    );
    expect(screen.getByTestId('bill-fuels').textContent).toContain('planks 1 per 1.5 items');
    fireEvent.change(screen.getByLabelText('Bill fuel'), { target: { value: 'charcoal' } });
    fireEvent.click(screen.getByRole('button', { name: 'Add bill' }));
    expect(onSave).toHaveBeenCalledWith(expect.objectContaining({ fuel: 'charcoal' }));
    const add = addBillBody([6, 64, 0], { ...newDraft(iron), fuel: 'charcoal' });
    expect(add.bill).toMatchObject({ fuel: 'charcoal' });
    expect(StoryBillsRequest.safeParse(add).success).toBe(true);
    const bill: StoryBill = {
      id: 'b9', recipe: iron.id, mode: 'count', target: 2, done: 0, paused: false, ingredients: { allow: [], deny: [] }, radius: 32,
      pawn: 'any', minSkill: null, store: 'best', fuel: 'coal',
    };
    expect(updateBillBody(bill, { ...draftOf(bill), fuel: null })).toEqual({ op: 'update', id: 'b9', fuel: 'inherit' });
  });
});

describe('a bench inspector on the shown colony state', () => {
  beforeEach(harnessUp);
  afterEach(harnessDown);

  const pos: [number, number, number] = [6, 64, 0];
  const bill = (id: string): StoryBill => ({
    id,
    recipe: 'smelt_iron',
    mode: 'forever',
    target: 1,
    done: 0,
    paused: false,
    priority: 3,
    ingredients: { allow: [], deny: [] },
    radius: 32,
    pawn: 'any',
    store: 'best',
  });
  const colony = (bills: StoryBill[]) =>
    StoryColonyState.parse({
      rev: 10,
      at: 10,
      since: null,
      full: true,
      workbenches: [{ pos, kind: 'furnace', fuel: 'auto', fuelChoices: ['auto', 'coal', 'planks'], bills }],
    });
  /** A write that is not shown at once (releasing the bench): the request alone. */
  const plainWrite = (id: string) =>
    ((route: string, body: unknown) => api(route as never, { params: { id }, body } as never)) as unknown as StoryWrite;
  /** The bench's inspector with what the Controller's toast would say. */
  function mount(id: string, bills = [bill('a')]) {
    const said: string[] = [];
    const page = mountInspector(
      id,
      colony(bills),
      ({ state, track }: { state: StoryColonyState | undefined; track: Track }) => (
        <BillsInspector
          bench={state!.workbenches![0]!}
          state={state}
          canWrite
          write={plainWrite(id)}
          track={track}
          onError={(e) => said.push(storyWriteError(e).text)}
          onPickChest={() => Promise.resolve(null)}
          onClose={() => {}}
        />
      ),
    );
    return { ...page, said };
  }
  const order = () =>
    [...screen.getByTestId('bills').querySelectorAll('[data-bill]')].map((li) => li.getAttribute('data-bill'));
  const priority = () => screen.getByLabelText<HTMLSelectElement>('Priority for smelt_iron').value;

  it("a bill's priority, fuel, pause and delete show at once, one request after the other", async () => {
    const calls = network();
    mount('bench-shown');
    fireEvent.change(screen.getByLabelText('Priority for smelt_iron'), { target: { value: '1' } });
    expect(priority()).toBe('1');
    fireEvent.change(screen.getByLabelText('Fuel for smelt_iron'), { target: { value: 'coal' } });
    expect(screen.getByLabelText<HTMLSelectElement>('Fuel for smelt_iron').value).toBe('coal');
    fireEvent.click(screen.getByRole('button', { name: 'Pause smelt_iron' }));
    expect(screen.getByRole('button', { name: 'Resume smelt_iron' })).toBeInTheDocument();
    await settle();
    // Priority goes first; the fuel waits for it on the bill's key, the pause has its own.
    expect(posted(calls).map((c) => c.body)).toEqual([
      { op: 'update', id: 'a', priority: 1 },
      { op: 'pause', id: 'a' },
    ]);
    for (const c of posted(calls)) c.reply({ ok: true, rev: 11 });
    await settle();
    expect(posted(calls).map((c) => c.body)).toContainEqual({ op: 'update', id: 'a', fuel: 'coal' });
    expect(priority()).toBe('1');
    fireEvent.click(screen.getByRole('button', { name: 'Delete smelt_iron' }));
    expect(order()).toEqual([]);
  });

  it.each([
    {
      name: 'priority, refused',
      act: () => fireEvent.change(screen.getByLabelText('Priority for smelt_iron'), { target: { value: '1' } }),
      shown: () => expect(priority()).toBe('1'),
      back: () => expect(priority()).toBe('3'),
      status: 400,
      body: { error: 'priority must be 1..4' },
      text: 'priority must be 1..4',
    },
    {
      name: 'pause, busy',
      act: () => fireEvent.click(screen.getByRole('button', { name: 'Pause smelt_iron' })),
      shown: () => expect(screen.getByRole('button', { name: 'Resume smelt_iron' })).toBeInTheDocument(),
      back: () => expect(screen.getByRole('button', { name: 'Pause smelt_iron' })).toBeInTheDocument(),
      status: 503,
      body: { error: 'busy; retry shortly', code: 'busy', retryAfterMs: 1000 },
      text: BUSY_TEXT,
    },
    {
      name: 'delete, busy',
      act: () => fireEvent.click(screen.getByRole('button', { name: 'Delete smelt_iron' })),
      shown: () => expect(order()).toEqual([]),
      back: () => expect(order()).toEqual(['a']),
      status: 503,
      body: { error: 'busy; retry shortly', code: 'busy', retryAfterMs: 1000 },
      text: BUSY_TEXT,
    },
  ])(
    '$name: the old value comes back with a message, nothing is sent again',
    async ({ act: click, shown, back, status, body, text }) => {
      const calls = network();
      const { said } = mount(`bench-refused-${status}-${text.length}`);
      click();
      shown();
      await settle();
      posted(calls)[0]!.reply(body, status);
      await settle();
      back();
      expect(said).toEqual([text]);
      expect(posted(calls)).toHaveLength(1);
    },
  );

  it('a dragged bill moves at once and goes back when the colony refuses the order', async () => {
    const calls = network();
    const { said } = mount('bench-reorder', [bill('a'), bill('b'), bill('c')]);
    const row = (id: string) => screen.getByTestId('bills').querySelector(`[data-bill="${id}"]`)!;
    fireEvent.dragStart(row('c'));
    fireEvent.drop(row('a'));
    expect(order()).toEqual(['c', 'a', 'b']);
    await settle();
    expect(posted(calls).map((c) => c.body)).toEqual([{ op: 'reorder', bench: pos, ids: ['c', 'a', 'b'] }]);
    posted(calls)[0]!.reply({ error: "ids must name this bench's bills" }, 400);
    await settle();
    expect(order()).toEqual(['a', 'b', 'c']);
    expect(said).toEqual(["ids must name this bench's bills"]);
  });

  it('releasing the bench says it is saving, and a refusal comes back as a message', async () => {
    const calls = network();
    const { said } = mount('bench-release');
    fireEvent.click(screen.getByRole('button', { name: 'Release' }));
    expect(screen.getByRole('button', { name: 'Releasing…' })).toBeDisabled();
    await settle();
    posted(calls)[0]!.reply({ error: 'busy; retry shortly', code: 'busy', retryAfterMs: 1000 }, 503);
    await settle();
    expect(screen.getByRole('button', { name: 'Release' })).not.toBeDisabled();
    expect(said).toEqual([BUSY_TEXT]);
  });
});

describe('a bed inspector on the shown colony state', () => {
  beforeEach(harnessUp);
  afterEach(harnessDown);

  const pawn = (name: string) => ({
    name,
    display: name,
    online: true,
    state: 'idle',
    priorities: {},
    schedule: [],
    skills: {},
    inventory: [],
  });
  const bedA: [number, number, number] = [1, 64, 1];
  const bedB: [number, number, number] = [3, 64, 1];

  it('a new owner shows at once (their other bed freed), stays usable, and goes back with a message when refused', async () => {
    const calls = network();
    const said: string[] = [];
    mountInspector(
      'bed-owner',
      StoryColonyState.parse({
        rev: 10,
        at: 10,
        since: null,
        full: true,
        pawns: [pawn('ann'), pawn('bob')],
        beds: [
          { pos: bedA, pawn: 'bob' },
          { pos: bedB, pawn: 'ann' },
        ],
      }),
      ({ state, track }: { state: StoryColonyState | undefined; track: Track }) => (
        <>
          <BedInspector
            bed={state!.beds![0]!}
            state={state}
            canWrite
            write={vi.fn()}
            track={track}
            onError={(e) => said.push(storyWriteError(e).text)}
            onClose={() => {}}
          />
          <output data-testid="other-bed">{state!.beds![1]!.pawn ?? 'nobody'}</output>
        </>
      ),
    );
    fireEvent.change(screen.getByLabelText('Set owner'), { target: { value: 'ann' } });
    expect(screen.getByLabelText('Set owner')).toHaveValue('ann');
    expect(screen.getByLabelText('Set owner')).not.toBeDisabled();
    expect(screen.getByTestId('other-bed')).toHaveTextContent('nobody');
    await settle();
    expect(posted(calls).map((c) => c.body)).toEqual([{ op: 'assign', bed: { x: 1, y: 64, z: 1 }, pawn: 'ann' }]);
    posted(calls)[0]!.reply({ error: 'busy; retry shortly', code: 'busy', retryAfterMs: 1000 }, 503);
    await settle();
    expect(screen.getByLabelText('Set owner')).toHaveValue('bob');
    expect(screen.getByTestId('other-bed')).toHaveTextContent('ann');
    expect(said).toEqual([BUSY_TEXT]);
  });

  it("the Controller's selection gives the inspector its tracked write and a toast for a refusal", async () => {
    const busy = new ApiError(503, 'busy; retry shortly', { code: 'busy', retryAfterMs: 1000 });
    const track = vi.fn().mockRejectedValue(busy);
    const say = vi.fn();
    const state = StoryColonyState.parse({
      rev: 10,
      at: 10,
      since: null,
      full: true,
      pawns: [pawn('ann')],
      beds: [{ pos: bedA, pawn: null }],
    });
    const c = { selection: { ...EMPTY_SELECTION, bed: bedA }, state, canWrite: true, write: vi.fn(), track, say };
    render(selectionInspector(c as unknown as Story));
    fireEvent.change(screen.getByLabelText('Set owner'), { target: { value: 'ann' } });
    expect(track).toHaveBeenCalledWith(expect.objectContaining({ key: 'bed:1,64,1' }), 'story.beds', {
      op: 'assign',
      bed: { x: 1, y: 64, z: 1 },
      pawn: 'ann',
    });
    await settle();
    expect(say).toHaveBeenCalledWith(BUSY_TEXT, 'warn');
  });
});
