'use client';

import { useCallback, useEffect, useLayoutEffect, useRef, useState } from 'react';
import { useRouter } from 'next/navigation';
import type { WorldViewerHandle, WorldViewerEngine } from '@mineclaw/world-viewer';
import { useWorldHost } from '@/features/world/integration';
import { Crosshair } from '@/features/world/InWorld';
import { useBodyInventory, useItemIcons } from '@/features/live/inventory/InventoryPanel';
import { StoryAudio } from '@/features/story/StoryAudio';
import { pawnNumber } from '@/features/story/pawnNumber';
import { bindControllerKeyboard, requestControllerCapture } from '@/features/story/controllerInput';
import { bindPlayerInput } from './playerInput';
import { usePawnControl } from './usePawnControl';
import { PlayerInventory, PlayerSlot } from './PlayerInventory';

export function PawnControlView({ body, returnTo }: { body: string; returnTo: string }) {
  const router = useRouter(), { mod, source } = useWorldHost();
  const viewer = useRef<WorldViewerHandle | null>(null), host = useRef<HTMLDivElement | null>(null);
  const control = usePawnControl(body);
  const latest = useRef(control);
  useLayoutEffect(() => { latest.current = control; });
  const [locked, setLocked] = useState(false), [inventoryOpen, setInventoryOpen] = useState(false);
  const [inventoryBusy, setInventoryBusy] = useState(false), [hotbar, setHotbar] = useState(0);
  const [inputReady, setInputReady] = useState(false);
  const inputSurface = useRef<HTMLElement | null>(null);
  const [captureNotice, setCaptureNotice] = useState('');
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
        input: control.input.current, lockChanged: setLocked, inventory: openInventory, hotbarChanged: setHotbar,
        action: (kind, wholeStack) => { void latest.current.action({ kind, ...(wholeStack !== undefined ? { wholeStack } : {}) }); },
      });
      inputSurface.current = surface;
      setInputReady(true);
    }, 30);
    return () => { clearInterval(id); inputSurface.current = null; detach?.(); };
  }, [mod, source, control.status, control.input, openInventory]);
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

  const beforeFrame = useCallback((engine: WorldViewerEngine, now: number) => {
    const pose = latest.current.camera.current.position(now), input = latest.current.input.current;
    if (!pose) return;
    const eyeY = pose.y + (input.sneak ? 1.54 : 1.62);
    engine.rig.flyPos.set(pose.x, eyeY, pose.z);
    engine.rig.flyYaw = input.yaw; engine.rig.flyPitch = input.pitch;
  }, []);
  const play = () => {
    const engine = viewer.current?.engine(), panel = host.current;
    if (control.status === 'active' && inputReady && engine && inputSurface.current === engine.surface && panel)
      void requestControllerCapture(engine, panel, () => latest.current.status === 'active').then((notice) =>
        setCaptureNotice(notice?.replace('use Page Down to descend', 'use ordinary WASD movement') ?? ''));
  };
  const leave = () => {
    control.end();
    if (document.fullscreenElement === host.current) void document.exitFullscreen?.().catch(() => {});
    router.push(returnTo);
  };
  const liveSlots = control.inventory;
  const ownItems = inventory.data?.slots ?? [];
  const useControlItems = liveSlots && control.inventoryAt >= inventory.dataUpdatedAt;
  const hotbarItems = Array.from({ length: 9 }, (_, i) => useControlItems
    ? liveSlots.slots.find((s) => s.slot === liveSlots.hotbarStart + i)
    : ownItems.find((s) => s.slot === i));

  return <div ref={host} className="relative h-full min-h-96 overflow-hidden bg-black" data-testid="pawn-control">
    {mod && source && control.state ? <mod.WorldViewer ref={viewer} source={source} dim={control.state.dim} follow={body}
      cameraMode="fly" controls="player" xray="off" hideHint onBeforeFrame={beforeFrame}
      layers={{ notes: false, bubbles: true }} /> : <div className="grid h-full place-items-center text-white">Opening the world…</div>}
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
    {control.status === 'ended' && <div className="absolute inset-0 z-10 grid place-items-center"><button className="rounded-xl bg-surface px-6 py-4" onClick={() => { setInventoryOpen(false); control.retry(); }}>Take over again</button></div>}
    {control.error && <p role="status" className="absolute top-16 left-1/2 z-40 max-w-xl -translate-x-1/2 rounded bg-black/80 px-4 py-2 text-sm text-white">{control.error}</p>}
    {captureNotice && <p role="status" className="absolute top-28 left-1/2 z-40 max-w-xl -translate-x-1/2 rounded bg-black/80 px-4 py-2 text-sm text-white">{captureNotice}</p>}
    {control.state && <div className="absolute bottom-4 left-1/2 z-10 -translate-x-1/2 text-white">
      <div className="mb-2 flex justify-between text-xs [text-shadow:1px_1px_2px_black]"><span>♥ {Math.ceil(control.state.hp)} / 20</span><span>Food {Math.ceil(control.state.food)} / 20</span></div>
      <div className="flex gap-0.5">{hotbarItems.map((item, i) => <PlayerSlot key={i} icons={icons} item={item} selected={hotbar === i}
        label={`${i + 1}: ${item ? nameOf(item.id, item.meta) : 'Empty'}`} onClick={() => { control.input.current.hotbar = i; setHotbar(i); }} />)}</div>
    </div>}
    {inventoryOpen && control.inventory && control.status === 'active' && <PlayerInventory inventory={control.inventory} icons={icons} nameOf={nameOf} busy={inventoryBusy}
      onClose={closeInventory} onClick={(slot, button, shift) => {
        setInventoryBusy(true);
        void control.action({ kind: 'inventory_click', windowId: control.inventory!.windowId, slot, button, shift }).finally(() => setInventoryBusy(false));
      }} />}
    <StoryAudio viewer={viewer} view="fly" dim={control.state?.dim ?? 0} />
  </div>;
}
