import {
  roleSummary,
  STORY_DEFAULT_ROLES,
  StoryColonyState,
  StoryPrioritiesRequest,
  StoryRolesRequest,
  type StoryPawn,
  type StoryRole,
} from '@mineclaw/contracts';
import { StorySim } from '@mineclaw/contracts/testing';
import { cleanup, fireEvent, render, screen, within } from '@testing-library/react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { nextIcon } from '@/features/story/roles/RolesEditor';
import { WorkPanel } from '@/features/story/roles/WorkPanel';

afterEach(cleanup);

const pawn = (name: string, extra: Partial<StoryPawn> = {}): StoryPawn => ({
  name,
  display: name,
  kind: 'pawn',
  online: true,
  state: 'idle',
  queue: [],
  skills: {},
  traits: [],
  priorities: { farm: 2, mine: 3, haul: 4 },
  schedule: [],
  inventory: [],
  ...extra,
});

const roles: StoryRole[] = STORY_DEFAULT_ROLES.map((r) => ({ ...r, pawns: [] }));

function grid(pawns: StoryPawn[], selected: string[] = []) {
  const onSet = vi.fn();
  const onRole = vi.fn();
  const onRoleOp = vi.fn();
  render(
    <WorkPanel
      pawns={pawns}
      ai={[]}
      canWrite
      onSet={onSet}
      roles={roles}
      selected={selected}
      onRole={onRole}
      onRoleOp={onRoleOp}
    />,
  );
  return { onSet, onRole, onRoleOp };
}

const row = (name: string) => document.querySelector(`[data-row="${name}"]`) as HTMLElement;
const cell = (name: string, t: string) => document.querySelector(`[data-cell="${name}:${t}"]`) as HTMLButtonElement;

describe('the Work grid with roles', () => {
  it('shows a role row with the role’s priorities, from-role cells, and Custom rows as before', () => {
    grid([pawn('dara', { role: 'farmer' }), pawn('ivo')]);
    expect(row('dara').dataset.role).toBe('farmer');
    expect(cell('dara', 'farm').textContent).toContain('1'); // the role's Farm 1, not the pawn's own 2
    expect(cell('dara', 'farm').dataset.fromRole).toBe('farmer');
    expect(cell('dara', 'mine').getAttribute('aria-label')).toContain('off (from Farmer)');
    expect(row('ivo').dataset.role).toBe('custom');
    expect(cell('ivo', 'farm').textContent).toContain('2');
    expect(cell('ivo', 'farm').dataset.fromRole).toBeUndefined();
    expect(screen.getByLabelText<HTMLSelectElement>('dara role').value).toBe('farmer');
    expect(screen.getByLabelText<HTMLSelectElement>('ivo role').value).toBe('');
  });

  it('editing a cell of a role row detaches the pawn to Custom with an undo', () => {
    const { onSet, onRole } = grid([pawn('dara', { role: 'farmer' })]);
    fireEvent.click(cell('dara', 'farm')); // 1 → 2
    expect(onSet).toHaveBeenCalledWith(['dara'], { farm: 2 });
    expect(row('dara').dataset.role).toBe('custom');
    expect(cell('dara', 'farm').textContent).toContain('2');
    expect(cell('dara', 'haul').textContent).toContain('3'); // the role's other values are kept
    expect(cell('dara', 'mine').textContent).toContain('·');
    const note = screen.getByTestId('role-detached');
    expect(note.textContent).toContain('dara is now Custom (was Farmer)');
    fireEvent.click(within(note).getByRole('button', { name: /undo/i }));
    expect(onRole).toHaveBeenCalledWith(['dara'], 'farmer');
    expect(row('dara').dataset.role).toBe('farmer');
    expect(screen.queryByTestId('role-detached')).toBeNull();
  });

  it('sets a role per row and for the ticked pawns at once', () => {
    const { onRole } = grid([pawn('dara'), pawn('ivo'), pawn('noor')], ['ivo']);
    fireEvent.change(screen.getByLabelText('dara role'), { target: { value: 'miner' } });
    expect(onRole).toHaveBeenCalledWith(['dara'], 'miner');
    expect(cell('dara', 'mine').textContent).toContain('1');
    // ivo came ticked from the map selection; tick noor too.
    fireEvent.click(screen.getByLabelText('Select noor'));
    fireEvent.change(screen.getByTestId('role-bulk-select'), { target: { value: 'woodcutter' } });
    expect(onRole).toHaveBeenLastCalledWith(['ivo', 'noor'], 'woodcutter');
    expect(row('ivo').dataset.role).toBe('woodcutter');
    expect(row('noor').dataset.role).toBe('woodcutter');
    // Back to Custom: the values stay.
    fireEvent.change(screen.getByLabelText('dara role'), { target: { value: '' } });
    expect(onRole).toHaveBeenLastCalledWith(['dara'], null);
    expect(cell('dara', 'mine').textContent).toContain('1');
  });

  it('edits roles in the same grid style', () => {
    const { onRoleOp } = grid([pawn('dara')]);
    fireEvent.click(screen.getByTestId('roles-toggle'));
    const farmer = document.querySelector('[data-role-cell="farmer:farm"]') as HTMLButtonElement;
    fireEvent.click(farmer);
    expect(onRoleOp).toHaveBeenCalledWith({ op: 'update', role: { id: 'farmer', priorities: { farm: 2 } } });
    fireEvent.change(screen.getByLabelText('New role name'), { target: { value: 'Night miner' } });
    fireEvent.submit(screen.getByLabelText('New role name').closest('form')!);
    expect(onRoleOp).toHaveBeenLastCalledWith({ op: 'add', role: { name: 'Night miner' } });
    fireEvent.click(screen.getByLabelText('Delete Miner'));
    fireEvent.click(screen.getByRole('button', { name: 'Delete' }));
    expect(onRoleOp).toHaveBeenLastCalledWith({ op: 'remove', id: 'miner' });
    fireEvent.click(screen.getByRole('button', { name: /reset built-in roles/i }));
    expect(onRoleOp).toHaveBeenLastCalledWith({ op: 'reset_defaults' });
    for (const call of onRoleOp.mock.calls) expect(StoryRolesRequest.safeParse(call[0]).success).toBe(true);
  });

  it('cycles icons and summarises a role', () => {
    expect(nextIcon(null)).toBe('wheat');
    expect(nextIcon('wheat')).toBe('sprout');
    expect(nextIcon('star')).toBeNull();
    expect(roleSummary(roles[0]!)).toBe('farm 1 · cook 3 · haul 3 · doctor 4');
  });
});

describe('roles in the mock director', () => {
  const make = () => {
    const sim = new StorySim({ world: { blockAt: () => [0, 0], ground: () => 63, entities: () => [] }, timeRateMs: 0 });
    const { id } = sim.create({ name: 'Roles', pawns: 3 });
    const names = StoryColonyState.parse(sim.state(id)).pawns.map((p) => p.name);
    const st = () => StoryColonyState.parse(sim.state(id));
    return { sim, id, names, st };
  };

  it('a role drives the pawns’ priorities; editing it updates them; a cell edit detaches', () => {
    const { sim, id, names, st } = make();
    expect(StoryPrioritiesRequest.safeParse({ pawns: names.slice(0, 2), role: 'farmer' }).success).toBe(true);
    sim.priorities(id, { pawns: names.slice(0, 2), role: 'farmer' });
    expect(st().roles!.find((r) => r.id === 'farmer')!.pawns).toEqual(names.slice(0, 2));
    sim.roles(id, { op: 'update', role: { id: 'farmer', priorities: { farm: 3 } } });
    expect(st().pawns.slice(0, 2).map((p) => p.priorities.farm)).toEqual([3, 3]);
    sim.priorities(id, { pawns: [names[0]!], work: { mine: 2 } });
    const p0 = st().pawns[0]!;
    expect(p0.role).toBeNull();
    expect([p0.priorities.farm, p0.priorities.mine]).toEqual([3, 2]);
    expect(() => sim.priorities(id, { pawns: [names[0]!], role: 'astronaut' })).toThrow(/no such role/);
  });

  it('deleting a role leaves its pawns Custom with the same values; reset restores the built-ins', () => {
    const { sim, id, names, st } = make();
    sim.priorities(id, { pawns: [names[2]!], role: 'fisher' });
    sim.roles(id, { op: 'remove', id: 'fisher' });
    const p = st().pawns[2]!;
    expect(p.role).toBeNull();
    expect(p.priorities.fish).toBe(1);
    expect(st().roles!.some((r) => r.id === 'fisher')).toBe(false);
    const out = sim.roles(id, { op: 'reset_defaults' });
    expect(out.roles.map((r) => r.id)).toEqual(STORY_DEFAULT_ROLES.map((r) => r.id));
    expect(() => sim.roles(id, { op: 'add', role: { name: 'Farmer' } })).toThrow(/taken/);
  });
});
