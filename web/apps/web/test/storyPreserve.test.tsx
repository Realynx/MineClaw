/**
 * Preserve zones (docs/web/story-mode.md "Preserve zones") in the web: the "Preserve blocks" settings section (switch, depth and
 * height sent on commit, re-take, status; disabled for a quarry; always on for a Preserve zone), the delta's `removed.ghosts`
 * merge, the paint body's range, the repair build's queue row and menu, and the map's preserved outline.
 */
import type { StoryColonyState, StoryZone, StoryZonePreserve } from '@mineclaw/contracts';
import { cleanup, fireEvent, render, screen } from '@testing-library/react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { mergeColony, mergeGhosts } from '@/features/story/model';
import { QueuePanel } from '@/features/story/StoryPanels';
import { contextMenu } from '@/features/story/tools';
import { OtherZonesPanel } from '@/features/story/zones/OtherZones';
import { buildTitle, isRepairBuild, parseRange, preserveBody, preserveSaid } from '@/features/story/zones/preserve';
import { PreserveContext, PreservePanel, ZonePreserve } from '@/features/story/zones/PreservePanel';
import { drawZones, zoneLabel } from '@/features/story/zones/zoneDraw';
import { zoneBody } from '@/features/story/zones/zonePaint';

afterEach(cleanup);

const P = (extra: Partial<StoryZonePreserve> = {}): StoryZonePreserve => ({
  on: true,
  below: 1,
  above: 3,
  y1: 63,
  y2: 67,
  cells: 60,
  takenAt: '2026-11-05T10:00:00Z',
  build: null,
  unknown: 0,
  held: 0,
  damage: { missing: 0, foreign: 0, turned: 0 },
  toRepair: 0,
  status: 'intact',
  ...extra,
});
const zone = (kind: string, preserve: StoryZonePreserve | null = null): StoryZone => ({
  id: 'z1',
  kind,
  area: [0, 64, 0, 3, 64, 2],
  cells: 12,
  name: 'Wheat field',
  preserve,
});

describe('the Preserve blocks section', () => {
  it('switches Preserve on and off with the zones route body', () => {
    const onPreserve = vi.fn();
    const { rerender } = render(<PreservePanel z={zone('farm')} canWrite onPreserve={onPreserve} />);
    const sw = screen.getByRole<HTMLInputElement>('switch', { name: 'Preserve blocks' });
    expect(sw.checked).toBe(false);
    expect(screen.queryByTestId('preserve-range')).toBeNull();
    fireEvent.click(sw);
    expect(onPreserve).toHaveBeenLastCalledWith(expect.objectContaining({ id: 'z1' }), { on: true });
    rerender(<PreservePanel z={zone('farm', P())} canWrite onPreserve={onPreserve} />);
    fireEvent.click(screen.getByRole('switch', { name: 'Preserve blocks' }));
    expect(onPreserve).toHaveBeenLastCalledWith(expect.anything(), { on: false });
    expect(preserveBody(zone('farm'), { on: false })).toEqual({ op: 'preserve', id: 'z1', on: false });
  });

  it('shows the y range, status and counts; depth and height go on commit, a bad value goes back; re-take', () => {
    const onPreserve = vi.fn();
    const p = P({ y1: 60, y2: 67, below: 4, unknown: 3, held: 2, status: 'intact (3 blocks not seen yet)' });
    render(<PreservePanel z={zone('farm', p)} canWrite onPreserve={onPreserve} />);
    expect(screen.getByTestId('preserve-range').textContent).toBe('y 60 → 67');
    expect(screen.getByTestId('preserve-status').textContent).toBe('intact (3 blocks not seen yet)');
    expect(screen.getByTestId('preserve-counts').textContent).toBe(
      '60 blocks kept · 3 not seen yet · 2 held by your builds or designations',
    );
    const depth = screen.getByLabelText<HTMLInputElement>('Depth below');
    expect(depth.value).toBe('4');
    fireEvent.change(depth, { target: { value: '6' } });
    expect(onPreserve).not.toHaveBeenCalled(); // typing alone sends nothing
    fireEvent.blur(depth);
    expect(onPreserve).toHaveBeenLastCalledWith(expect.anything(), { below: 6 });
    const height = screen.getByLabelText<HTMLInputElement>('Height above');
    fireEvent.change(height, { target: { value: '40' } });
    fireEvent.blur(height);
    expect(height.value).toBe('3');
    expect(onPreserve).toHaveBeenCalledTimes(1);
    fireEvent.change(height, { target: { value: '3' } });
    fireEvent.blur(height); // unchanged: nothing sent
    expect(onPreserve).toHaveBeenCalledTimes(1);
    fireEvent.click(screen.getByRole('button', { name: 'Re-take snapshot' }));
    expect(onPreserve).toHaveBeenLastCalledWith(expect.anything(), { retake: true });
  });

  it('a damaged zone says what it repairs in the warning tone', () => {
    const status = '12 blocks to repair: 9 missing, 3 foreign; waiting for 6 dirt';
    render(<PreservePanel z={zone('farm', P({ toRepair: 12, status }))} canWrite onPreserve={vi.fn()} />);
    const s = screen.getByTestId('preserve-status');
    expect(s.textContent).toBe(status);
    expect(s.className).toContain('text-warn');
  });

  it('a quarry cannot preserve (the switch is disabled with the reason); a Preserve zone is always on', () => {
    render(<PreservePanel z={zone('mine')} canWrite onPreserve={vi.fn()} />);
    expect((screen.getByRole<HTMLInputElement>('switch', { name: 'Preserve blocks' })).disabled).toBe(true);
    expect(screen.getByTestId('zone-preserve').textContent).toContain('A quarry digs its blocks');
    cleanup();
    render(<PreservePanel z={zone('preserve', P())} canWrite onPreserve={vi.fn()} />);
    expect(screen.queryByRole('switch')).toBeNull();
    expect(screen.getByTestId('zone-preserve').textContent).toContain('Always on');
    expect(screen.getByTestId('preserve-range').textContent).toBe('y 63 → 67');
  });

  it('read-only without the story role; nothing outside the Story page (no writer)', () => {
    render(<PreservePanel z={zone('farm', P())} canWrite={false} onPreserve={vi.fn()} />);
    expect((screen.getByRole<HTMLInputElement>('switch')).disabled).toBe(true);
    expect((screen.getByLabelText<HTMLInputElement>('Depth below')).disabled).toBe(true);
    cleanup();
    expect(render(<ZonePreserve z={zone('farm')} />).container.innerHTML).toBe('');
    cleanup();
    const onPreserve = vi.fn();
    render(
      <PreserveContext.Provider value={{ canWrite: true, onPreserve }}>
        <ZonePreserve z={zone('chop')} />
      </PreserveContext.Provider>,
    );
    fireEvent.click(screen.getByRole('switch'));
    expect(onPreserve).toHaveBeenCalledWith(expect.objectContaining({ kind: 'chop' }), { on: true });
  });

  it('the Other zones window lists Preserve zones first, with Remove only for them', () => {
    const onRemove = vi.fn();
    const state = {
      zones: [zone('chop'), { ...zone('preserve', P()), id: 'z2', name: null }],
    } as unknown as StoryColonyState;
    render(<OtherZonesPanel state={state} canWrite onShow={vi.fn()} onRemove={onRemove} />);
    const rows = screen.getAllByTestId('other-zone');
    expect(rows.map((r) => r.getAttribute('data-zone'))).toEqual(['z2', 'z1']);
    expect(screen.getAllByRole('button', { name: /^Remove/ })).toHaveLength(1);
    fireEvent.click(screen.getByRole('button', { name: 'Remove Preserve z2' }));
    expect(onRemove).toHaveBeenCalledWith(expect.objectContaining({ id: 'z2' }));
  });

  it('parses a range and says what changed', () => {
    expect([parseRange('0'), parseRange(' 32 '), parseRange('33'), parseRange('-1'), parseRange('2.5'), parseRange('')]).toEqual([
      0,
      32,
      null,
      null,
      null,
      null,
    ]);
    expect(preserveSaid({ on: false }, null)).toBe('Preserve off: the blocks are no longer kept');
    expect(preserveSaid({ retake: true }, P())).toBe('New snapshot: the blocks are kept as they are now');
    expect(preserveSaid({ below: 2 }, P({ y1: 62 }))).toBe('Preserving y 62 → 67');
  });
});

describe('removed ghosts', () => {
  type G = [number, number, number, number, number, string | number, string, ('dig' | 'place')?];
  const g = (rev: number, blocks: G[]) => ({ rev, blocks }) as NonNullable<StoryColonyState['ghosts']>;
  const prev = g(3, [
    [0, 64, 0, 3, 0, 'b9', 'todo', 'place'],
    [1, 64, 0, 3, 0, 'b9', 'todo', 'place'],
    [1, 64, 0, 4, 0, 'b2', 'todo', 'place'],
  ]);

  it('a removed ghost leaves (only that build’s cell); one sent again in the same delta stays; unknown ones change nothing', () => {
    expect(mergeGhosts(prev, undefined, null, ['b9|1,64,0'])!.blocks).toEqual([prev.blocks[0], prev.blocks[2]]);
    const back: G = [0, 64, 0, 3, 0, 'b9', 'clear', 'dig'];
    expect(mergeGhosts(prev, g(5, [back]), null, ['b9|0,64,0'])!.blocks).toContainEqual(back);
    expect(mergeGhosts(prev, undefined, null, ['b9|7,7,7'])).toBe(prev);
    expect(mergeGhosts(prev, g(4, []), null, [])).toBe(prev);
  });

  it('a delta with removed.ghosts drops them from what the client holds', () => {
    const base = { id: 'c', rev: 3, full: true, ghosts: prev, zones: [], pawns: [] } as unknown as StoryColonyState;
    const delta = { id: 'c', rev: 4, full: false, removed: { ghosts: ['b9|0,64,0', 'b9|1,64,0'] } } as unknown as StoryColonyState;
    const out = mergeColony(base, delta);
    expect(out.ghosts!.blocks).toEqual([prev.blocks[2]]);
    expect(out.removed).toBeNull();
  });
});

describe('painting, repairs and the map', () => {
  it('a Preserve zone is sent with its depth and height; other kinds without', () => {
    const range = { below: 2, above: 5 };
    expect(zoneBody({ kind: 'zone', id: 'preserve', op: 'add' }, [3, 1, 0, 0], 64, null, undefined, 'quarry', range)).toEqual({
      op: 'add',
      kind: 'preserve',
      area: [0, 64, 0, 3, 64, 1],
      below: 2,
      above: 5,
    });
    expect(zoneBody({ kind: 'zone', id: 'chop', op: 'add' }, [0, 0, 1, 1], 64, null, undefined, 'quarry', range)).toEqual({
      op: 'add',
      kind: 'chop',
      area: [0, 64, 0, 1, 64, 1],
    });
  });

  it('a repair build reads "Repairs: <zone>" and has no Cancel (in the queue or the blueprint menu)', () => {
    const repair = {
      id: 'b9',
      kind: 'preserve',
      label: 'repairs: Wheat field',
      status: 'queued',
      blocksTotal: 4,
      blocksDone: 1,
      order: -1,
      materials: [],
    };
    const wall = { ...repair, id: 'b2', kind: 'wall', label: 'wall', order: 2 };
    expect(isRepairBuild(repair)).toBe(true);
    expect(buildTitle(repair)).toBe('Repairs: Wheat field');
    expect(buildTitle(wall)).toBe('wall');
    const onOp = vi.fn();
    render(<QueuePanel builds={[repair, wall] as never} canWrite onOp={onOp} />);
    expect(screen.getByText('Repairs: Wheat field')).toBeTruthy();
    expect(screen.queryByRole('button', { name: 'Cancel Repairs: Wheat field' })).toBeNull();
    expect(screen.getByTestId('repair-build')).toBeTruthy();
    fireEvent.click(screen.getByRole('button', { name: 'Pause Repairs: Wheat field' }));
    expect(onOp).toHaveBeenCalledWith('pause', ['b9']);
    expect(screen.getByRole('button', { name: 'Cancel wall' })).toBeTruthy();
    cleanup();
    // An intact zone's repairs rest as done: not counted as a finished build.
    render(<QueuePanel builds={[{ ...repair, status: 'done' }, wall] as never} canWrite onOp={onOp} />);
    expect(screen.queryByText(/finished/)).toBeNull();
    const ghost = { kind: 'ghost' as const, build: 'b9', label: 'Repairs: Wheat field', x: 0, y: 64, z: 0 };
    expect(contextMenu({ ...ghost, repair: true }, true, false).map((i) => i.action)).not.toContain('cancel-build');
    expect(contextMenu(ghost, true, false).map((i) => i.action)).toContain('cancel-build');
  });

  it('a Preserve zone is labelled with its range; a zone with Preserve on gets a dotted outline in the Preserve colour', () => {
    expect(zoneLabel({ ...zone('preserve', P()), name: null }, 0, null)).toBe('Preserve · y 63 → 67');
    const strokes: Array<{ style: unknown; dash: number[] }> = [];
    let dash: number[] = [];
    const ctx = {
      beginPath() {},
      moveTo() {},
      lineTo() {},
      closePath() {},
      fill() {},
      fillRect() {},
      fillText() {},
      measureText: () => ({ width: 10 }),
      setLineDash(d: number[]) {
        dash = d;
      },
      stroke() {
        strokes.push({ style: this.strokeStyle, dash });
      },
      strokeStyle: '' as unknown,
    };
    const proj = (x: number, _y: number, z: number) => ({ x: x * 10, y: z * 10 });
    drawZones(ctx as unknown as CanvasRenderingContext2D, [zone('farm', P())], proj, 2, () => null);
    expect(strokes).toEqual([
      { style: '#e879c9', dash: [0.5, 5] },
      { style: '#7cc35bcc', dash: [6, 4] },
    ]);
    strokes.length = 0;
    drawZones(ctx as unknown as CanvasRenderingContext2D, [zone('farm')], proj, 2, () => null);
    expect(strokes).toHaveLength(1);
  });
});
