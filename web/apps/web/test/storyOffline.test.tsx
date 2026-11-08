import { regularsSeatsWouldHelp, type StoryPawn } from '@mineclaw/contracts';
import { act, cleanup, fireEvent, render, screen } from '@testing-library/react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { Dialog, PortalContainer } from '@/components/ui/overlays';
import { clusterFailed, placeLabels, retryIn, shortReason, type FailedMark } from '@/features/story/failedLabels';
import { barBodies, ColonistBar, offlinePawns } from '@/features/story/StoryHud';
import { BringOnlineBox } from '@/features/story/StoryInspector';
import { keyAction } from '@/features/story/tools';
import { useFullscreen } from '@/features/story/useFullscreen';

afterEach(cleanup);

const pawn = (name: string, extra: Partial<StoryPawn> = {}): StoryPawn => ({
  name,
  display: name.replace('maid_s1_', ''),
  kind: 'pawn',
  online: true,
  state: 'idle',
  queue: [],
  skills: {},
  traits: [],
  priorities: {},
  schedule: [],
  inventory: [],
  ...extra,
});

describe('offline pawns in the colonist bar', () => {
  it('shows only numeric account suffixes while keeping full account keys for selection', () => {
    const bodies = barBodies([pawn('maid_chan_109', { display: 'Cleo' }), pawn('maid_chat_1337', { display: 'River' })], []);
    render(
      <ColonistBar bodies={bodies} selected={['maid_chan_109']} order={[]}
        onOrder={vi.fn()} onClick={vi.fn()} onBox={vi.fn()} onJump={vi.fn()} />,
    );
    const first = document.querySelector('[data-portrait="maid_chan_109"]')!;
    const second = document.querySelector('[data-portrait="maid_chat_1337"]')!;
    expect(first.textContent).toBe('109');
    expect(second.textContent).toBe('1337');
    expect(first).toHaveAttribute('aria-pressed', 'true');
    expect(first).toHaveAttribute('aria-label', '109: Idle');
    expect(screen.queryByText('Cleo')).toBeNull();
    expect(screen.queryByText('River')).toBeNull();
  });

  it('box-selects only visible portraits in a scrolled row and anchors its line to viewport coordinates', () => {
    const names = [100, 101, 102, 103].map((n) => `maid_chan_${n}`);
    const onBox = vi.fn();
    const capture = vi.spyOn(HTMLElement.prototype, 'setPointerCapture').mockImplementation(() => undefined);
    const rect = vi.spyOn(Element.prototype, 'getBoundingClientRect').mockImplementation(function (this: Element) {
      const i = names.indexOf(this.getAttribute('data-portrait') ?? '');
      const left = i < 0 ? 100 : 40 + i * 60;
      const width = i < 0 ? 120 : 58;
      return { left, right: left + width, width, top: 100, bottom: 160, height: 60, x: left, y: 100, toJSON: () => ({}) };
    });
    try {
      const { container } = render(
        <ColonistBar bodies={barBodies(names.map((name) => pawn(name)), [])} selected={[]} order={[]}
          onOrder={vi.fn()} onClick={vi.fn()} onBox={onBox} onJump={vi.fn()} />,
      );
      const firstVisible = container.querySelector('[data-portrait="maid_chan_101"]')!;
      const ptr = (type: 'pointerDown' | 'pointerMove' | 'pointerUp', x: number) =>
        fireEvent[type](firstVisible, { button: 0, pointerId: 1, clientX: x, clientY: 120 });
      ptr('pointerDown', 120);
      ptr('pointerMove', 500);
      expect(container.querySelector('.fixed')).toHaveStyle({ left: '120px', width: '100px', top: '158px' });
      ptr('pointerUp', 500);
      expect(onBox).toHaveBeenLastCalledWith([names[1], names[2]], false);
      ptr('pointerDown', 190);
      ptr('pointerMove', 0);
      ptr('pointerUp', 0);
      expect(onBox).toHaveBeenLastCalledWith([names[1], names[2]], false);
      expect(onBox).toHaveBeenCalledTimes(2);
    } finally {
      capture.mockRestore();
      rect.mockRestore();
    }
  });

  const pawns = [
    pawn('maid_s1_dara'),
    pawn('maid_s1_ivo', { online: false, state: 'offline', offlineReason: 'not whitelisted — re-adding' }),
    pawn('maid_s1_noor', {
      online: false,
      state: 'offline',
      offlineReason: 'server full (player cap: 1 free, 2 kept for regulars)',
    }),
  ];

  it('marks offline pawns with their reason', () => {
    const bodies = barBodies(pawns, []);
    expect(bodies.map((b) => b.offline)).toEqual([false, true, true]);
    expect(bodies[1]!.offlineReason).toBe('not whitelisted — re-adding');
    expect(bodies[1]!.job).toBe('Offline');
    expect(offlinePawns(bodies).map((b) => b.name)).toEqual(['maid_s1_ivo', 'maid_s1_noor']);
  });

  it('greys them with a badge, shows the reason on hover and offers to bring them online', () => {
    const bring = vi.fn();
    render(
      <ColonistBar
        bodies={barBodies(pawns, [])}
        selected={[]}
        order={[]}
        onOrder={() => undefined}
        onClick={() => undefined}
        onBox={() => undefined}
        onJump={() => undefined}
        onBringAll={bring}
      />,
    );
    expect(screen.getAllByTestId('offline-badge')).toHaveLength(2);
    const ivo = document.querySelector('[data-portrait="maid_s1_ivo"]')!;
    expect(ivo.getAttribute('data-offline')).toBe('true');
    expect(ivo.className).toContain('opacity-60');
    expect(ivo.getAttribute('aria-label')).toBe('maid_s1_ivo: offline, not whitelisted — re-adding');
    fireEvent.pointerEnter(ivo);
    expect(screen.getByTestId('offline-reason').textContent).toBe('Offline: not whitelisted — re-adding');
    const btn = screen.getByTestId('bar-bring-online');
    expect(btn.textContent).toContain('Bring 2 online');
    fireEvent.click(btn);
    expect(bring).toHaveBeenCalledOnce();
  });

  it('has no bring-online button when everyone is in game or the viewer cannot write', () => {
    render(
      <ColonistBar
        bodies={barBodies([pawn('maid_s1_dara')], [])}
        selected={[]}
        order={[]}
        onOrder={() => undefined}
        onClick={() => undefined}
        onBox={() => undefined}
        onJump={() => undefined}
        onBringAll={() => undefined}
      />,
    );
    expect(screen.queryByTestId('bar-bring-online')).toBeNull();
    expect(screen.queryByTestId('offline-badge')).toBeNull();
  });
});

describe('Bring online', () => {
  it('offers the regulars’ seats only when they would help, with a warning', () => {
    expect(regularsSeatsWouldHelp('server full (player cap: 1 free, 2 kept for regulars)')).toBe(true);
    expect(regularsSeatsWouldHelp('server full (7/7 seats taken)')).toBe(false);
    expect(regularsSeatsWouldHelp('server full (player cap: 3 free, 0 kept for regulars)')).toBe(false);
    expect(regularsSeatsWouldHelp('not whitelisted — re-adding')).toBe(false);

    const onBring = vi.fn();
    render(
      <BringOnlineBox
        reason="server full (player cap: 1 free, 2 kept for regulars)"
        blocked={[{ pawn: 'maid_s1_noor', reason: 'server full (player cap: 1 free, 2 kept for regulars)' }]}
        canWrite
        label="Bring online"
        onBring={onBring}
      />,
    );
    expect(screen.getByTestId('pawn-offline-reason').textContent).toContain('server full');
    expect(screen.getByTestId('online-blocked').textContent).toContain('2 kept for regulars');
    expect(screen.getByText(/A regular may then find the server full/)).toBeTruthy();
    fireEvent.click(screen.getByRole('button', { name: /Bring online/ }));
    fireEvent.click(screen.getByTestId('use-regulars-seats'));
    expect(onBring.mock.calls).toEqual([[false], [true]]);
  });

  it('a refusal the regulars’ seats cannot fix has no such action', () => {
    render(
      <BringOnlineBox
        blocked={[{ pawn: 'maid_s1_noor', reason: 'server full (7/7 seats taken)' }]}
        canWrite
        label="Bring all online (1 offline)"
        onBring={() => undefined}
        names={() => 'Noor'}
      />,
    );
    expect(screen.queryByTestId('use-regulars-seats')).toBeNull();
    expect(screen.getByTestId('online-blocked').textContent).toBe('Noor: server full (7/7 seats taken)');
  });
});

describe('failed designation labels', () => {
  it('maps the bot’s reasons to short phrases', () => {
    const busy =
      'double_plant at 208,64,412 is busy: another hive bot is working on that part right now (a hive claim, not protection)';
    expect(shortReason(busy)).toEqual({ title: 'Busy: claimed by another bot', tag: 'busy — claimed' });
    expect(shortReason("can't reach 4,70,9 (no path)").title).toBe('Out of reach');
    expect(shortReason('reported done, but the tall_grass at 1,64,2 is still there').title).toBe("Didn't come down");
    expect(shortReason('stone at 1,2,3 is protected (part of a build)').title).toBe('Protected block');
    expect(shortReason(null).title).toBe('Failed');
    const odd = shortReason('the moon is in the wrong phase for this kind of work');
    expect(odd.title.length).toBeLessThanOrEqual(26);
    expect(odd.title.endsWith('…')).toBe(true);
  });

  const busy = 'double_plant at 5,64,2 is busy: another hive bot is working on that part right now';
  const mark = (i: number, x: number, z: number, reason = busy, kind = 'cut'): FailedMark => ({
    id: `d${i}`,
    kind,
    reason,
    cell: [x, 64, z],
  });

  it('groups adjacent designations failing the same way into one label', () => {
    // A 4×3 cut area all refused "claimed", a lone one far away, and one next to the area that failed differently.
    const area = Array.from({ length: 12 }, (_, i) => mark(i, i % 4, Math.floor(i / 4)));
    const far = mark(20, 40, 40);
    const other = mark(21, 1, 1, "can't reach it");
    const clusters = clusterFailed([...area, far, other]);
    expect(clusters.map((c) => c.text)).toEqual([
      '12 plants: busy — claimed',
      'Busy: claimed by another bot',
      'Out of reach',
    ]);
    expect(clusters[0]!.ids).toHaveLength(12);
    expect(clusters[0]!.reason).toBe(busy);
    // The anchor is one of the cluster's own cells, near its middle.
    expect(area.some((m) => m.cell.join() === clusters[0]!.anchor.join())).toBe(true);
    // Trees and plants don't merge even with the same reason.
    expect(clusterFailed([mark(1, 0, 0), mark(2, 1, 0, busy, 'chop')])).toHaveLength(2);
  });

  it('never stacks labels: an overlapping one is skipped, off-screen ones too', () => {
    const items = [
      { item: 'a', at: { x: 100, y: 100 }, text: '12 plants: busy — claimed' },
      { item: 'b', at: { x: 110, y: 104 }, text: 'Out of reach' }, // on top of a
      { item: 'c', at: { x: 100, y: 140 }, text: 'Out of reach' }, // below a: fine
      { item: 'd', at: { x: -400, y: 10 }, text: 'Out of reach' }, // off-screen
      { item: 'e', at: null, text: 'x' },
    ];
    expect(placeLabels(items, { w: 800, h: 600 }).map((p) => p.item)).toEqual(['a', 'c']);
  });

  it('says when a failed designation is tried again', () => {
    const now = Date.parse('2026-11-03T22:00:00Z');
    expect(retryIn('2026-11-03T22:07:00Z', now)).toBe('in 7 min');
    expect(retryIn('2026-11-03T22:00:40Z', now)).toBe('in 40 s');
    expect(retryIn('2026-11-03T21:59:00Z', now)).toBe('now');
    expect(retryIn(null, now)).toBe('now');
  });
});

describe('full screen', () => {
  it('Shift+F toggles it; F alone stays the Forbid tool', () => {
    expect(keyAction({ code: 'KeyF', key: 'F', shiftKey: true }, false)).toBe('fullscreen');
    expect(keyAction({ code: 'KeyF', key: 'f' }, false)).toBe('tool:forbid');
  });

  it('dialogs mount inside the full-screen element (else they would be invisible)', () => {
    const host = document.createElement('div');
    host.dataset.testid = 'fs-host';
    document.body.appendChild(host);
    render(
      <PortalContainer.Provider value={host}>
        <Dialog open onOpenChange={() => undefined} title="Add bill">
          <p>inside</p>
        </Dialog>
      </PortalContainer.Provider>,
    );
    expect(host.contains(screen.getByText('inside'))).toBe(true);
    cleanup();
    // Without a container: document.body, as before.
    render(
      <Dialog open onOpenChange={() => undefined} title="Add bill">
        <p>outside</p>
      </Dialog>,
    );
    expect(host.contains(screen.getByText('outside'))).toBe(false);
    host.remove();
  });

  function Probe() {
    const fs = useFullscreen<HTMLDivElement>();
    return (
      <div ref={fs.attach} data-testid="probe" data-on={String(fs.on)}>
        <button type="button" onClick={fs.toggle}>
          toggle
        </button>
      </div>
    );
  }

  it('follows the browser: on after the element goes full screen, off after Esc (fullscreenchange)', () => {
    let current: Element | null = null;
    Object.defineProperty(document, 'fullscreenElement', { configurable: true, get: () => current });
    const exit = vi.fn(() => {
      current = null;
      document.dispatchEvent(new Event('fullscreenchange'));
      return Promise.resolve();
    });
    Object.defineProperty(document, 'exitFullscreen', { configurable: true, value: exit });
    render(<Probe />);
    const el = screen.getByTestId('probe');
    const request = vi.fn(() => {
      current = el;
      document.dispatchEvent(new Event('fullscreenchange'));
      return Promise.resolve();
    });
    el.requestFullscreen = request;
    expect(el.dataset.on).toBe('false');
    act(() => {
      fireEvent.click(screen.getByText('toggle'));
    });
    expect(request).toHaveBeenCalledOnce();
    expect(el.dataset.on).toBe('true');
    // Esc: the browser leaves full screen by itself; the state follows.
    act(() => {
      current = null;
      document.dispatchEvent(new Event('fullscreenchange'));
    });
    expect(el.dataset.on).toBe('false');
    // The button again, then the button to leave.
    act(() => {
      fireEvent.click(screen.getByText('toggle'));
    });
    expect(el.dataset.on).toBe('true');
    act(() => {
      fireEvent.click(screen.getByText('toggle'));
    });
    expect(exit).toHaveBeenCalledOnce();
    expect(el.dataset.on).toBe('false');
  });
});
