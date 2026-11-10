import { StoryBuild, StoryPawn } from '@mineclaw/contracts';
import { cleanup, render, screen, within } from '@testing-library/react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { buildGroups, buildWork, groupWork } from '@/features/story/buildGroups';
import { pawnBadges } from '@/features/story/pawnBadges';
import { WorkPanel } from '@/features/story/roles/WorkPanel';
import { OptionsBar } from '@/features/story/StoryHud';
import { QueuePanel } from '@/features/story/StoryPanels';
import { COLORS, GLYPHS } from '@/features/story/visuals';
import { workAppearance } from '@/features/story/workAppearance';

// A placed mine design is mining work: its parts (kind `mine`, one group) read as mining in the queue, and its dig jobs arrive
// with job type `mine`; a normal design and the blocks a mine puts in stay construction.

afterEach(cleanup);

const build = (id: string, extra: Partial<StoryBuild> = {}) =>
  StoryBuild.parse({ id, kind: 'wall', label: 'wall', status: 'queued', ...extra });

const mineParts = [
  build('m1', { kind: 'mine', label: 'Strip mine', group: 'mx', name: 'Strip mine', order: 0, blocksTotal: 3800 }),
  build('m2', { kind: 'mine', label: 'Strip mine', group: 'mx', name: 'Strip mine', order: 1, blocksTotal: 3700 }),
];
const hut = [build('h1', { group: 'hx', name: 'Hut', order: 2, blocksTotal: 40 })];
const loose = [build('l1', { kind: 'mine', label: 'Mine part', order: 3 }), build('w1', { order: 4 })];

describe('mine designs in the build queue', () => {
  it('counts a mine build as mining and every other build as construction', () => {
    expect(buildWork(mineParts[0])).toBe('mine');
    expect(buildWork(hut[0])).toBe('build');
    expect(buildWork(build('r', { kind: 'preserve' }))).toBe('build');
    expect(buildWork(undefined)).toBe('build');
    const groups = buildGroups([...mineParts, ...hut, ...loose]);
    expect(groups.map((g) => [g.key, groupWork(g)])).toEqual([
      ['group:mx', 'mine'],
      ['group:hx', 'build'],
      ['build:l1', 'mine'],
      ['build:w1', 'build'],
    ]);
  });

  it('labels a mine design under mining and a normal design as a building', () => {
    render(<QueuePanel builds={[...mineParts, ...hut, ...loose]} canWrite onOp={vi.fn()} />);
    const mine = document.querySelector<HTMLElement>('[data-build-group="group:mx"]')!;
    expect(mine.dataset.work).toBe('mine');
    expect(within(mine).getByText('Mine design / mining')).toBeTruthy();
    expect(within(mine).getByRole('button', { name: 'Mine Strip mine · m1 first' })).toBeTruthy();

    const hutRow = document.querySelector<HTMLElement>('[data-build-group="group:hx"]')!;
    expect(hutRow.dataset.work).toBeUndefined();
    expect(within(hutRow).getByText('Design / building instance')).toBeTruthy();
    expect(within(hutRow).getByRole('button', { name: 'Build Hut · h1 first' })).toBeTruthy();

    const looseMine = document.querySelector<HTMLElement>('[data-build="l1"]')!;
    expect(looseMine.dataset.work).toBe('mine');
    expect(within(looseMine).getByText('Mining order')).toBeTruthy();
    expect(within(looseMine).getByRole('button', { name: 'Mine Mine part first' }).title).toBe('Mine first');

    const wall = document.querySelector<HTMLElement>('[data-build="w1"]')!;
    expect(within(wall).getByText('Manual order')).toBeTruthy();
    expect(within(wall).getByRole('button', { name: 'Build wall first' }).title).toBe('Build first');
  });
});

describe('mine designs as mining work', () => {
  const pawn = (job: StoryPawn['job']): StoryPawn =>
    ({ name: 'maid_chan_7', display: 'Ada', online: true, job }) as StoryPawn;

  it('shows a pawn digging a mine design with the mining badge and one placing its torch with the build badge', () => {
    const [dig] = pawnBadges([pawn({ type: 'mine', label: 'removing a block (Strip mine)' })]);
    expect(dig).toMatchObject({ icon: GLYPHS.mine, activity: 'removing a block (Strip mine)' });
    expect(workAppearance('mine')).toEqual({ path: GLYPHS.mine, color: COLORS.designation.mine });
    const [torch] = pawnBadges([pawn({ type: 'build', label: 'placing a torch (Strip mine)' })]);
    expect(torch!.icon).toBe(GLYPHS.build);
  });

  it('says on the Work tab that mine designs are dug under Mine', () => {
    const p = StoryPawn.parse({
      name: 'maid_chan_7',
      display: 'Ada',
      priorities: {},
      schedule: [],
      skills: {},
      inventory: [],
    });
    render(<WorkPanel pawns={[p]} ai={[]} canWrite onSet={vi.fn()} />);
    expect(screen.getByRole('button', { name: 'Mine' }).title).toBe(
      'Sort by Mine skill · quarries, mine designations and mine designs',
    );
    expect(screen.getByRole('button', { name: 'Build' }).title).toBe(
      'Sort by Build skill · construction, with the torches and ladders a mine design puts in',
    );
    expect(screen.getByRole('button', { name: 'Chop' }).title).toBe('Sort by Chop skill');
  });

  it('heads a picked-up mine design as mining work', () => {
    const blocks = [
      [0, 40, 0, 0, 0],
      [1, 40, 0, 0, 0],
      [1, 41, 0, 50, 5],
    ] as never;
    render(
      <OptionsBar
        tool={{ kind: 'design', id: 9, name: 'Strip mine', blocks, mine: { min: 40, max: 41 } }}
        build={{ height: 3, roof: 'flat', overhang: false, facing: 'w', material: [5, 0] }}
        mineFilter="all"
        onMineFilter={vi.fn()}
        onClose={vi.fn()}
      />,
    );
    const bar = screen.getByRole('toolbar', { name: 'Mine design: Strip mine options' });
    expect(bar.textContent).toContain(
      '2 cells to mine at y 40..41: click to queue it here as mining work (Q/E turns); its 1 placed blocks are construction.',
    );
  });
});
