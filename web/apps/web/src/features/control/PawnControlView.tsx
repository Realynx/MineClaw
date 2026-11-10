'use client';

import { useCallback, useEffect, useLayoutEffect, useRef, useState, useSyncExternalStore } from 'react';
import { useRouter } from 'next/navigation';
import type { PawnControlBroke, PawnControlEndReason } from '@mineclaw/contracts';
import { blockName, rayBox, type AimRay, type AimTarget, type EntityEvent, type WorldViewerHandle, type WorldViewerEngine } from '@mineclaw/world-viewer';
import { getViewerQuality, subscribeViewerQuality } from '@mineclaw/world-viewer/settings';
import { useWorldHost } from '@/features/world/integration';
import { Crosshair } from '@/features/world/InWorld';
import { useBodyInventory, useItemIcons } from '@/features/live/inventory/InventoryPanel';
import { StoryAudio } from '@/features/story/StoryAudio';
import { pawnNumber } from '@/features/story/pawnNumber';
import { bindControllerKeyboard, requestControllerCapture } from '@/features/story/controllerInput';
import { bindPlayerInput, pickSlot } from './playerInput';
import { serverAim, usePawnControl } from './usePawnControl';
import { ControlHud } from './ControlHud';
import { PlayerInventory, PlayerSlot } from './PlayerInventory';
import { takeoverCapture, type TakeoverCapture } from './captureHandoff';
import type { ControlCamera } from './controlCamera';
import { ControlBreak, placedCell } from './controlBreak';
import { blockSound, Footsteps, SoundEchoes } from './controlSounds';

/** The ended screen's heading for an ending the server named. */
const ENDINGS: Record<PawnControlEndReason, string> = {
  died: 'Your pawn died',
  expired: 'Control expired',
  replaced: 'Control moved to another window',
  offline: 'The pawn went offline',
};

type Ghost = { x: number; y: number; z: number; id: number; meta: number };
const sameGhost = (a: Ghost | null, b: Ghost | null) =>
  a === b || (!!a && !!b && a.x === b.x && a.y === b.y && a.z === b.z && a.id === b.id && a.meta === b.meta);

export function PawnControlView({ body, returnTo }: { body: string; returnTo: string }) {
  const router = useRouter(), { mod, source } = useWorldHost();
  const viewer = useRef<WorldViewerHandle | null>(null), host = useRef<HTMLDivElement | null>(null);
  /** The pawn's own dig, break, place and step feedback: the game server sends none of it to the acting player. */
  const breaks = useRef(new ControlBreak()), steps = useRef(new Footsteps());
  const playSounds = useRef<((events: EntityEvent[]) => void) | null>(null);
  /** Other pawns near this one report its breaks and placements into the world stream too: each is heard once. */
  const echoes = useRef(new SoundEchoes());
  const heard = useRef<((events: EntityEvent[]) => EntityEvent[]) | null>((events) => echoes.current.stream(events, performance.now()));
  const playOwn = useCallback((events: EntityEvent[]) => {
    const kept = echoes.current.own(events, performance.now());
    if (kept.length) playSounds.current?.(kept);
  }, []);
  const shownState = useRef<unknown>(null), shownPose = useRef<{ x: number; y: number; z: number } | null>(null);
  const listening = useRef<{ engine: WorldViewerEngine; off: () => void } | null>(null);
  const latestHotbar = useRef<ReadonlyArray<{ id: number; meta: number } | undefined>>([]);
  /** The placement ghost shown now: the cell a right click would fill with the held block. */
  const ghost = useRef<Ghost | null>(null);
  const onBroke = useCallback((b: PawnControlBroke) => {
    breaks.current.broke(b, performance.now());
    // The prediction may walk into the dug cell at once; the world's own change replaces this.
    listening.current?.engine.overrideCollision(b.x, b.y, b.z, 0, 0, 1000);
  }, []);
  const control = usePawnControl(body, { onBroke });
  const latest = useRef(control);
  useLayoutEffect(() => { latest.current = control; });
  const [locked, setLocked] = useState(false), [inventoryOpen, setInventoryOpen] = useState(false);
  const [inventoryBusy, setInventoryBusy] = useState(false), [hotbar, setHotbar] = useState(0);
  const [inputReady, setInputReady] = useState(false);
  const inputSurface = useRef<HTMLElement | null>(null);
  const [captureNotice, setCaptureNotice] = useState('');
  const capture = useRef<TakeoverCapture | null>(null);
  const captureBody = useRef(body);
  const [rootFullscreen, setRootFullscreen] = useState(false);
  useEffect(() => {
    if (captureBody.current !== body) { capture.current?.release(); capture.current = null; captureBody.current = body; }
    capture.current = takeoverCapture(body) ?? capture.current;
    const owned = capture.current;
    const unmount = owned?.mount();
    const sync = () => setRootFullscreen(document.fullscreenElement === document.documentElement);
    sync(); document.addEventListener('fullscreenchange', sync);
    return () => {
      document.removeEventListener('fullscreenchange', sync);
      unmount?.();
    };
  }, [body]);
  useEffect(() => { if (control.status === 'ended') capture.current?.release(); }, [control.status]);
  useEffect(() => () => {
    listening.current?.engine.setBreak(null);
    if (ghost.current) listening.current?.engine.setHologramPreview(null);
    ghost.current = null;
    listening.current?.off(); listening.current = null;
  }, []);
  const inventory = useBodyInventory(body, control.status === 'active');
  const version = inventory.data?.version ?? '1.12.2', icons = useItemIcons(version);
  const nameOf = (id: number, meta: number) => mod?.itemName(id)?.replaceAll('_', ' ') ?? `Item ${id}:${meta}`;
  const openInventory = useCallback(() => {
    setInventoryOpen(true);
    void latest.current.action({ kind: 'inventory' });
  }, []);
  const closeInventory = useCallback(() => {
    void latest.current.action({ kind: 'close_inventory' }).then((reply) => { if (reply?.ok) setInventoryOpen(false); });
  }, []);
  /** Where the right click about to be sent would put the held block, judged by the crosshair before it is sent. */
  const placement = useCallback(() => {
    const engine = listening.current?.engine, aim = engine?.aim(), body = shownPose.current;
    if (!engine || aim?.kind !== 'block' || !body) return null;
    const c = latest.current;
    const cell = placedCell(aim, latestHotbar.current[c.input.current.hotbar], c.input.current.sneak, body);
    return cell ? { ...cell, dim: c.pose.current?.dim ?? 0 } : null;
  }, []);
  /** Middle click: the hotbar slot holding the aimed block's item, if one does. */
  const pick = useCallback(() => {
    const aim = listening.current?.engine.aim();
    return aim?.kind === 'block' ? pickSlot(aim, latestHotbar.current) : null;
  }, []);
  /** The crosshair's target: the block the pawn's own ray hits, once the server has applied the look and keys the player
   * sees (so the outline and the click agree on partial blocks and from inside a plant), and the viewer's pick until
   * then. A bot under the crosshair stays: the server's aim names blocks only. */
  const resolveAim = useCallback((physical: AimTarget | null, ray: AimRay): AimTarget | null => {
    const c = latest.current, aim = serverAim(c.pose.current, c.aimInput.current);
    if (aim === undefined || physical?.kind === 'bot') return physical;
    if (aim === null) return null;
    if (physical?.kind === 'block' && physical.x === aim.x && physical.y === aim.y && physical.z === aim.z)
      return physical.face === aim.face ? physical : { ...physical, face: aim.face };
    const engine = viewer.current?.engine(), block = engine?.blockAt(aim.x, aim.y, aim.z);
    // The view does not have that block yet (or has it gone already): its own pick is the better guess.
    if (!engine || !block || block.id <= 0) return physical;
    const distance = rayBox(ray.origin, ray.direction, [aim.x, aim.y, aim.z], [aim.x + 1, aim.y + 1, aim.z + 1])
      ?? Math.hypot(aim.x + 0.5 - ray.origin[0], aim.y + 0.5 - ray.origin[1], aim.z + 0.5 - ray.origin[2]);
    return { kind: 'block', x: aim.x, y: aim.y, z: aim.z, id: block.id, meta: block.meta, face: aim.face,
      name: blockName(engine.runtime.version ?? 'b1.8.1', block.id), distance };
  }, []);
  /** The game server filled the cell: the prediction stands on it at once, and it sounds. */
  const placed = useCallback((p: { x: number; y: number; z: number; id: number; meta: number; dim: number }) => {
    listening.current?.engine.overrideCollision(p.x, p.y, p.z, p.id, p.meta, 1000);
    playOwn([blockSound('place', p.id, p.dim, p.x, p.y, p.z, Date.now())]);
  }, [playOwn]);
  useEffect(() => {
    setInputReady(false);
    if (!mod || !source || control.status !== 'active') return;
    let detach: (() => void) | undefined;
    const id = setInterval(() => {
      const surface = viewer.current?.engine()?.surface;
      if (!surface) return;
      clearInterval(id);
      setHotbar(control.input.current.hotbar);
      detach = bindPlayerInput(surface, {
        input: control.input.current, lockChanged: setLocked, inventory: openInventory, hotbarChanged: setHotbar, pick,
        inputChanged: (kind) => latest.current.inputChanged(kind),
        // Never a target or face: the server clicks what its own ray hits, which is what the outline shows.
        action: (kind, wholeStack, repeat) => {
          const place = kind === 'use' ? placement() : null;
          void latest.current.action({ kind, ...(wholeStack !== undefined ? { wholeStack } : {}), ...(repeat ? { repeat } : {}) }).then((reply) => {
            // ok only says the click went out; 'placed' says the game server filled the cell (it refuses one the body still
            // stands in on its side, a round trip behind the prediction).
            if (place && reply?.code === 'placed') placed(place);
          });
        },
      });
      inputSurface.current = surface;
      setInputReady(true);
      void capture.current?.transfer(surface, () => latest.current.status === 'active' && inputSurface.current === surface)
        .then((captured) => { if (captured) setLocked(true); });
    }, 30);
    return () => { clearInterval(id); inputSurface.current = null; detach?.(); };
  }, [mod, source, control.status, control.input, openInventory, placement, placed, pick]);
  useEffect(() => bindControllerKeyboard(() => !!viewer.current?.locked(), (message) =>
    setCaptureNotice(message?.replace('use Page Down to descend', 'use ordinary WASD movement') ?? '')), []);

  const windowId = control.inventory?.windowId;
  useEffect(() => {
    if (windowId !== undefined && windowId > 0) {
      if (document.pointerLockElement) document.exitPointerLock();
      setInventoryOpen(true);
    }
  }, [windowId]);
  const serverWindowId = control.state?.windowId;
  useEffect(() => {
    if (serverWindowId !== undefined && serverWindowId > 0 && serverWindowId !== windowId) {
      if (document.pointerLockElement) document.exitPointerLock();
      openInventory();
    }
  }, [serverWindowId, windowId, openInventory]);
  useEffect(() => {
    if (!inventoryOpen) return;
    const key = (e: KeyboardEvent) => { if (e.code === 'KeyE' || e.code === 'Escape') { e.preventDefault(); closeInventory(); } };
    window.addEventListener('keydown', key);
    const timer = setInterval(() => { if (!inventoryBusy) void latest.current.action({ kind: 'inventory' }); }, 750);
    return () => { window.removeEventListener('keydown', key); clearInterval(timer); };
  }, [inventoryOpen, inventoryBusy, closeInventory]);

  /** Shows the placement ghost, telling the viewer only when it changed. */
  const showGhost = useCallback((engine: WorldViewerEngine, next: Ghost | null) => {
    if (sameGhost(ghost.current, next)) return;
    ghost.current = next && { x: next.x, y: next.y, z: next.z, id: next.id, meta: next.meta };
    engine.setHologramPreview(next && [next.x, next.y, next.z, next.id, next.meta], next ? `control:${next.x},${next.y},${next.z},${next.id},${next.meta}` : undefined);
  }, []);
  /** Cracks, chips, bursts and the pawn's own sounds, for the frame at `now`. */
  const feedback = useCallback((engine: WorldViewerEngine, now: number, pose: { x: number; y: number; z: number; dim: number }, sneak: boolean) => {
    const c = latest.current, tracker = breaks.current;
    if (listening.current?.engine !== engine) {
      listening.current?.off();
      const changed = (dim: number, cells: ReadonlyArray<ArrayLike<number>>) => breaks.current.blocksChanged(dim, cells, performance.now());
      engine.runtime.blockListeners.add(changed);
      listening.current = { engine, off: () => engine.runtime.blockListeners.delete(changed) };
    }
    if (c.status !== 'active') {
      tracker.reset(); steps.current.reset(); echoes.current.reset(); shownState.current = null;
      engine.setBreak(null);
      showGhost(engine, null);
      return;
    }
    const state = c.pose.current;
    if (state && state !== shownState.current) { shownState.current = state; tracker.state(state, now); }
    const f = tracker.frame(now);
    engine.setBreak(f.crack);
    let sounds: EntityEvent[] | null = null;
    const at = Date.now(), dim = pose.dim;
    if (f.hit) {
      engine.blockBurst(f.hit.x, f.hit.y, f.hit.z, f.hit.id, f.hit.meta, 'block_hit', f.hit.face);
      (sounds ??= []).push(blockSound('hit', f.hit.id, dim, f.hit.x, f.hit.y, f.hit.z, at));
    }
    for (const b of f.bursts) {
      engine.blockBurst(b.x, b.y, b.z, b.id, b.meta, 'block_break');
      (sounds ??= []).push(blockSound('break', b.id, dim, b.x, b.y, b.z, at));
    }
    if (steps.current.move(pose.x, pose.z, c.camera.current.onGround(), sneak)) {
      // Vanilla takes the block 0.2 under the feet; in water it swims instead.
      const below = engine.blockAt(Math.floor(pose.x), Math.floor(pose.y - 0.2), Math.floor(pose.z));
      if (below && below.id > 0 && (below.id < 8 || below.id > 11)) (sounds ??= []).push(blockSound('step', below.id, dim, pose.x, pose.y, pose.z, at));
    }
    if (sounds) playOwn(sounds);
  }, [showGhost, playOwn]);
  const beforeFrame = useCallback((engine: WorldViewerEngine, now: number): void | false => {
    const input = latest.current.input.current;
    engine.setPlayerReach(latest.current.reach.current);
    const pose = latest.current.camera.current.position(now, input, (bounds, dim) => engine.playerCollision(bounds, dim),
      engine.runtime.version);
    if (!pose) return false; // no pose yet: the viewer holds the frame rather than draw (and load) the world origin
    // Only 1.12.2 lowers the eye of a sneaking player. The viewer learns the world's version with its first index
    // reply; `version` above comes from the inventory poll and says 1.12.2 until that has answered.
    const eyeY = pose.y + (input.sneak && (engine.runtime.version ?? '1.12.2') === '1.12.2' ? 1.54 : 1.62);
    engine.rig.flyPos.set(pose.x, eyeY, pose.z);
    engine.rig.flyYaw = input.yaw; engine.rig.flyPitch = input.pitch;
    shownPose.current = pose;
    feedback(engine, now, pose, input.sneak);
    if (latest.current.status === 'active') showGhost(engine, placement());
  }, [feedback, placement, showGhost]);
  const play = () => {
    const engine = viewer.current?.engine(), panel = host.current;
    if (control.status === 'active' && inputReady && engine && inputSurface.current === engine.surface && panel)
      void requestControllerCapture(engine, panel, () => latest.current.status === 'active').then((notice) =>
        setCaptureNotice(notice?.replace('use Page Down to descend', 'use ordinary WASD movement') ?? ''));
  };
  const leave = () => {
    control.end();
    capture.current?.release();
    if (document.fullscreenElement === host.current) void document.exitFullscreen?.().catch(() => {});
    router.push(returnTo);
  };
  const liveSlots = control.inventory;
  const ownItems = inventory.data?.slots ?? [];
  const useControlItems = liveSlots && control.inventoryAt >= inventory.dataUpdatedAt;
  const hotbarItems = Array.from({ length: 9 }, (_, i) => useControlItems
    ? liveSlots.slots.find((s) => s.slot === liveSlots.hotbarStart + i)
    : ownItems.find((s) => s.slot === i));
  useLayoutEffect(() => { latestHotbar.current = hotbarItems; });
  const heldItem = hotbarItems[hotbar];

  return <div ref={host} className={`${rootFullscreen ? 'fixed inset-0 z-50' : 'relative h-full min-h-96'} overflow-hidden bg-black`} data-testid="pawn-control">
    {mod && source && control.state ? <mod.WorldViewer ref={viewer} source={source} dim={control.state.dim} follow={body}
      cameraMode="fly" controls="player" xray="off" hideHint onBeforeFrame={beforeFrame} resolveAim={resolveAim}
      layers={{ notes: false, bubbles: true, changes: false }} /> : <div className="grid h-full place-items-center text-white">Opening the world…</div>}
    <div className="absolute inset-x-0 top-0 z-20 flex items-center justify-between gap-3 bg-gradient-to-b from-black/80 to-transparent p-3 text-white">
      <div><strong>Pawn {pawnNumber(body) || body}</strong><span className="ml-3 text-xs text-white/70">{control.status === 'active' ? 'You are in control · AI paused' : control.status === 'connecting' ? 'Taking over…' : 'Control ended'}</span></div>
      <button className="rounded-lg bg-black/60 px-3 py-2 text-sm hover:bg-black/90" onClick={leave}>Release & return</button>
    </div>
    {locked && control.status === 'active' && <Crosshair />}
    {!locked && !inventoryOpen && control.status === 'active' && <div className="absolute inset-0 z-10 grid place-items-center">
      <div className="rounded-xl bg-black/75 p-5 text-center text-white">
        <button className="rounded-lg bg-accent px-6 py-3 font-semibold text-white disabled:opacity-50" disabled={!inputReady} onClick={play}>Click to play</button>
        <p className="mt-3 text-xs leading-6 text-white/80">WASD move · Mouse look · Space jump · Shift sneak · Ctrl sprint<br />Left click mine / attack · Right click use / place · 1–9 or wheel hotbar<br />E inventory · Q drop · Esc release mouse</p>
      </div>
    </div>}
    {control.status === 'ended' && <div className="absolute inset-0 z-10 grid place-items-center"><div className="grid justify-items-center gap-3">
      {control.endReason && <div className="max-w-md rounded-xl bg-black/75 px-5 py-3 text-center text-white"><h2 className="text-lg font-semibold">{ENDINGS[control.endReason]}</h2>
        {control.error && <p className="mt-1 text-sm text-white/80">{control.error}</p>}</div>}
      <button className="rounded-xl bg-surface px-6 py-4" onClick={() => { setInventoryOpen(false); control.retry(); }}>Take over again</button></div></div>}
    {control.error && !(control.status === 'ended' && control.endReason) && <p role="status" className="absolute top-16 left-1/2 z-40 max-w-xl -translate-x-1/2 rounded bg-black/80 px-4 py-2 text-sm text-white">{control.error}</p>}
    {captureNotice && <p role="status" className="absolute top-28 left-1/2 z-40 max-w-xl -translate-x-1/2 rounded bg-black/80 px-4 py-2 text-sm text-white">{captureNotice}</p>}
    {control.state && <ControlHud hp={control.state.hp} food={control.state.food} armor={control.state.armor} air={control.state.air}
      slot={hotbar} held={heldItem ? nameOf(heldItem.id, heldItem.meta) : null}>
      <div className="flex gap-0.5">{hotbarItems.map((item, i) => <PlayerSlot key={i} icons={icons} item={item} selected={hotbar === i}
        label={`${i + 1}: ${item ? nameOf(item.id, item.meta) : 'Empty'}`} onClick={() => { control.input.current.hotbar = i; setHotbar(i); control.inputChanged('hotbar'); }} />)}</div>
    </ControlHud>}
    {inventoryOpen && control.inventory && control.status === 'active' && <PlayerInventory inventory={control.inventory} icons={icons} nameOf={nameOf} busy={inventoryBusy}
      onClose={closeInventory} onClick={(slot, button, shift) => {
        setInventoryBusy(true);
        void control.action({ kind: 'inventory_click', windowId: control.inventory!.windowId, slot, button, shift }).finally(() => setInventoryBusy(false));
      }} />}
    <StoryAudio viewer={viewer} view="fly" dim={control.state?.dim ?? 0} play={playSounds} heard={heard} />
    <ControlStatsLine camera={control.camera} />
  </div>;
}

/** The camera's evidence, beside the viewer's frame rate overlay and only while that is on: how late the pawn's frames
 * are used, how far the picture is from the prediction, and what made it jump, hold or guess. */
function ControlStatsLine({ camera }: { camera: { readonly current: Pick<ControlCamera, 'stats'> } }) {
  const on = useSyncExternalStore(subscribeViewerQuality, () => getViewerQuality().perfOverlay, () => false);
  const [text, setText] = useState('');
  useEffect(() => {
    if (!on) return;
    const show = () => {
      const s = camera.current.stats();
      setText(`control ${Math.round(s.latencyMs)} ms · offset ${s.offset.toFixed(2)} (p95 ${s.offsetP95.toFixed(2)}) · snaps ${s.snaps}`
        + ` · holds ${s.holds} · repeated ${s.duplicates} · no terrain ${s.terrainMisses} · by tick ${s.ticked}/${s.samples}`);
    };
    show();
    const id = setInterval(show, 500);
    return () => clearInterval(id);
  }, [on, camera]);
  if (!on || !text) return null;
  return <p data-testid="control-stats" className="pointer-events-none absolute bottom-2 left-2 z-10 rounded bg-black/55 px-2 py-0.5 font-mono text-[11px] whitespace-nowrap text-white">{text}</p>;
}
