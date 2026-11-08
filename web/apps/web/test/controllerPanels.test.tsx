import { StoryBuild, StoryPawn, StoryStorage } from '@mineclaw/contracts';
import { cleanup, fireEvent, render, screen, within } from '@testing-library/react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { buildGroups, reorderBuildGroup } from '@/features/story/buildGroups';
import { ColonyWorkspace, clampWindow, loadWindowLayout } from '@/features/story/ColonyWorkspace';
import { InventoryPanel } from '@/features/story/InventoryPanel';
import { storageInventory } from '@/features/story/inventory';
import { QueuePanel, TABS } from '@/features/story/StoryPanels';
import { WorkPanel } from '@/features/story/roles/WorkPanel';
import { NAV } from '@/components/shell/nav';
import { RetainedPanel } from '@/features/story/RetainedPanel';

afterEach(() => {
  cleanup();
  localStorage.clear();
  vi.restoreAllMocks();
});
const build = (id: string, extra: Partial<StoryBuild> = {}) =>
  StoryBuild.parse({ id, kind: 'wall', label: 'wall', status: 'queued', ...extra });
const chest = (x: number, extra: Partial<StoryStorage> = {}) => StoryStorage.parse({ chest: [x, 64, 0], ...extra });

describe('controller build queue', () => {
  const builds = [
    build('b1', { group: 'first', name: 'Storehouse', order: 0, status: 'done', blocksDone: 20, blocksTotal: 20 }),
    build('b2', { group: 'first', name: 'Storehouse', order: 1, blocksDone: 2, blocksTotal: 10 }),
    build('b3', { group: 'second', name: 'Storehouse', order: 2, blocksTotal: 12 }),
    build('b4', { order: 3 }),
  ];
  it('separates placement instances of the same design and keeps manual orders separate', () => {
    const groups = buildGroups(builds);
    expect(groups.map((g) => [g.key, g.builds.length, g.active.length])).toEqual([
      ['group:first', 2, 1],
      ['group:second', 1, 1],
      ['build:b4', 1, 1],
    ]);
    expect(reorderBuildGroup(groups, 'build:b4', 'group:first')).toEqual(['b4', 'b2', 'b3']);
  });
  it('shows combined progress and operates on the selected instance only', () => {
    const onOp = vi.fn();
    render(<QueuePanel builds={builds} canWrite onOp={onOp} />);
    expect(screen.getByText('22/30 blocks · 1 remaining parts')).toBeTruthy();
    fireEvent.click(screen.getByRole('button', { name: 'Pause Storehouse · b1' }));
    expect(onOp).toHaveBeenLastCalledWith('pause', ['b2']);
    fireEvent.click(screen.getByRole('button', { name: 'Cancel Storehouse · b1' }));
    expect(onOp).toHaveBeenLastCalledWith('remove', ['b1', 'b2']);
    expect(screen.getByText('Manual order')).toBeTruthy();
    fireEvent.click(screen.getByRole('button', { name: /Design \/ building instance.*Storehouse · b1/ }));
    expect(document.querySelector('[data-build="b1"]')).toBeTruthy();
    const finished = within(document.querySelector<HTMLElement>('[data-build="b1"]')!);
    expect(finished.queryByRole('button', { name: 'Build wall first' })).toBeNull();
    expect(finished.getByRole('button', { name: 'Pause wall' })).toBeDisabled();
    const pending = within(document.querySelector<HTMLElement>('[data-build="b2"]')!);
    expect(pending.queryByRole('button', { name: 'Build wall first' })).toBeNull();
  });
  it('groups legacy named placement batches without merging later copies or parts missing timestamps', () => {
    const legacy = [
      build('b10', { name: 'Storehouse', created: '2026-11-08T02:18:27.868Z', order: 0 }),
      build('b11', { name: 'Storehouse', created: '2026-11-08T02:18:27.884Z', order: 1 }),
      build('b12', { name: 'Storehouse', created: '2026-11-08T02:19:00.000Z', order: 2 }),
      build('b13', { name: 'Storehouse', created: '2026-11-08T02:19:00.009Z', order: 3 }),
      build('b14', { name: 'Storehouse', order: 4 }),
    ];
    expect(buildGroups(legacy).map((g) => g.builds.map((b) => b.id))).toEqual([
      ['b10', 'b11'],
      ['b12', 'b13'],
      ['b14'],
    ]);
    expect(buildGroups(legacy.map((b) => ({ ...b, order: 10 - b.order }))).map((g) => g.key)).toEqual([
      'build:b14',
      'legacy:b12',
      'legacy:b10',
    ]);
  });
});

describe('aggregate storage inventory', () => {
  it('counts the freshest double-chest observation once, preserves variants, and includes unfiltered chests', () => {
    const result = storageInventory([
      chest(1, { doubleWith: [2, 64, 0], knownAt: 100, contents: [[17, 0, 10]] }),
      chest(2, {
        doubleWith: [1, 64, 0],
        knownAt: 200,
        contents: [
          [17, 0, 20],
          [17, 1, 3],
        ],
      }),
      chest(3, {
        contents: [
          [17, 0, 5],
          [17, 0, 2],
        ],
      }),
      chest(4),
    ]);
    expect(result.chests).toBe(3);
    expect(result.unknown).toBe(1);
    expect(result.items.find((x) => x.meta === 0)).toMatchObject({ count: 27, chests: 2 });
    expect(result.items.find((x) => x.meta === 1)).toMatchObject({ count: 3, chests: 1 });
  });
  it('searches aggregated counts without hiding the inventory total', () => {
    render(
      <InventoryPanel
        storages={[
          chest(1, {
            contents: [
              [4, 0, 64],
              [264, 0, 3],
            ],
          }),
        ]}
      />,
    );
    expect(screen.getByText('67 items · 2 types · 1 chests')).toBeTruthy();
    fireEvent.change(screen.getByLabelText('Search inventory'), { target: { value: 'diamond' } });
    expect(screen.getByText('Diamond')).toBeTruthy();
    expect(screen.queryByText('Cobblestone')).toBeNull();
  });
});

describe('colony movable windows', () => {
  it('rejects corrupt positions and keeps windows accessible after a viewport shrink', () => {
    localStorage.setItem('windows', '{"good":{"x":80,"y":35},"bad":{"x":"5","y":null}}');
    expect(loadWindowLayout('windows')).toEqual({ good: { x: 80, y: 35 } });
    expect(clampWindow({ x: 900, y: 700 }, 500, 300, 340, 400)).toEqual({ x: 160, y: 0 });
  });
  it('saves moved positions through close/reopen and resets them', () => {
    vi.spyOn(HTMLElement.prototype, 'clientWidth', 'get').mockReturnValue(1200);
    vi.spyOn(HTMLElement.prototype, 'clientHeight', 'get').mockReturnValue(800);
    const windows = [{ id: 'colony', title: 'Colony', content: <section>Settings</section> }];
    const first = render(<ColonyWorkspace storageKey="windows" windows={windows} />);
    fireEvent.keyDown(screen.getByRole('button', { name: 'Move Colony window' }), { key: 'ArrowRight' });
    expect(loadWindowLayout('windows').colony).toEqual({ x: 16, y: 0 });
    first.unmount();
    render(<ColonyWorkspace storageKey="windows" windows={windows} />);
    expect(document.querySelector<HTMLElement>('[data-window="colony"]')!.style.left).toBe('16px');
    fireEvent.click(screen.getByRole('button', { name: 'Reset windows' }));
    expect(loadWindowLayout('windows')).toEqual({});
  });
});

describe('work drag selection', () => {
  it('selects and deselects ranges without changing priorities, retaining select-all', () => {
    const pawns = ['101', '102', '103'].map((name) =>
      StoryPawn.parse({
        name,
        display: name,
        online: true,
        state: 'idle',
        priorities: {},
        schedule: [],
        skills: {},
        inventory: [],
      }),
    );
    const onSet = vi.fn(),
      onRole = vi.fn();
    render(<WorkPanel pawns={pawns} ai={[]} canWrite onSet={onSet} onRole={onRole} />);
    const names = pawns.map((p) => screen.getByRole('button', { name: `Select row ${p.name}` }));
    names.forEach((node) => {
      node.setPointerCapture = vi.fn();
    });
    document.querySelectorAll<HTMLElement>('tr[data-row]').forEach((row, i) => {
      vi.spyOn(row, 'getBoundingClientRect').mockReturnValue({ top: i * 30, bottom: i * 30 + 30 } as DOMRect);
    });
    fireEvent.pointerDown(names[0]!, { button: 0, pointerId: 1, clientX: 10, clientY: 10 });
    fireEvent.pointerMove(names[0]!, { buttons: 1, pointerId: 1, clientX: 10, clientY: 75 });
    fireEvent.pointerUp(names[0]!, { pointerId: 1 });
    expect(screen.getByRole('checkbox', { name: 'Select every pawn' })).toBeChecked();
    expect(onSet).not.toHaveBeenCalled();
    fireEvent.click(names[0]!); // browser click following drag is suppressed
    fireEvent.pointerDown(names[1]!, { button: 0, pointerId: 2, clientX: 10, clientY: 45 });
    fireEvent.pointerMove(names[1]!, { buttons: 1, pointerId: 2, clientX: 10, clientY: 75 });
    fireEvent.pointerUp(names[1]!, { pointerId: 2 });
    fireEvent.click(names[1]!);
    expect(screen.getByRole('checkbox', { name: 'Select 101' })).toBeChecked();
    expect(screen.getByRole('checkbox', { name: 'Select 102' })).not.toBeChecked();
    fireEvent.click(screen.getByRole('checkbox', { name: 'Select every pawn' }));
    expect(screen.getByRole('checkbox', { name: 'Select 103' })).toBeChecked();
  });
});

it('places Controller above Live hive and Inventory beside Colony', () => {
  expect(NAV[0]!.items.slice(0, 2).map((x) => x.label)).toEqual(['Controller', 'Live hive']);
  expect(TABS.slice(-2).map((x) => x.id)).toEqual(['colony', 'inventory']);
});

it('keeps a Directives draft mounted across tab switches but does not open it before first use', () => {
  const view = render(
    <RetainedPanel active={false}>
      <input aria-label="Draft" defaultValue="" />
    </RetainedPanel>,
  );
  expect(screen.queryByLabelText('Draft')).toBeNull();
  view.rerender(
    <RetainedPanel active>
      <input aria-label="Draft" defaultValue="" />
    </RetainedPanel>,
  );
  fireEvent.change(screen.getByLabelText('Draft'), { target: { value: 'Make these pawns miners' } });
  view.rerender(
    <RetainedPanel active={false}>
      <input aria-label="Draft" defaultValue="" />
    </RetainedPanel>,
  );
  expect(screen.getByLabelText('Draft')).not.toBeVisible();
  view.rerender(
    <RetainedPanel active>
      <input aria-label="Draft" defaultValue="" />
    </RetainedPanel>,
  );
  expect(screen.getByLabelText('Draft')).toHaveValue('Make these pawns miners');
});
