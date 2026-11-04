import { STORY_DEFAULT_RECIPES } from '@mineclaw/contracts';
import { cleanup, fireEvent, render, screen } from '@testing-library/react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { BillDialog } from '@/features/story/StoryInspector';
import { newDraft } from '@/features/story/storage';

afterEach(cleanup);

const recipe = (id: string) => STORY_DEFAULT_RECIPES.find((r) => r.id === id)!;

describe('station bills in the bill dialog', () => {
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
