import { STORY_DEFAULT_RECIPES, StoryBillsRequest, StoryWorkbenchesRequest, type StoryBill } from '@mineclaw/contracts';
import { cleanup, fireEvent, render, screen } from '@testing-library/react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { BillDialog, FuelPicker, ReservationList } from '@/features/story/StoryInspector';
import { BillRow } from '@/features/story/inspector/BillRow';
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
