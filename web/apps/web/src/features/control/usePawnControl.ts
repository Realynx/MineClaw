'use client';

import { useCallback, useEffect, useRef, useState } from 'react';
import type { PawnControlActionRequest, PawnControlActionResponse, PawnControlInventory, PawnControlSession, PawnControlState } from '@mineclaw/contracts';
import { api, errorText, getCsrfToken } from '@/lib/api/client';
import { idleInput, type PlayerInput } from './playerInput';

export type ControlAction = Omit<PawnControlActionRequest, 'session' | 'seq'>;

/** One browser owns one short lease; inputs are latest-only, never an HTTP backlog. */
export function usePawnControl(body: string) {
  const input = useRef(idleInput());
  const pose = useRef<PawnControlState | null>(null);
  const lease = useRef<PawnControlSession | null>(null);
  const [state, setState] = useState<PawnControlState | null>(null);
  const [inventory, setInventory] = useState<PawnControlInventory | null>(null);
  const [inventoryAt, setInventoryAt] = useState(0);
  const [status, setStatus] = useState<'connecting' | 'active' | 'ended'>('connecting');
  const [error, setError] = useState('');
  const [attempt, setAttempt] = useState(0);
  const actionSeq = useRef(0);
  const heldActionBusy = useRef<number | null>(null);
  const actionQueue = useRef<Promise<unknown>>(Promise.resolve());
  const actionEpoch = useRef(0);
  const pressInFlight = useRef<number | null>(null);
  const flushInput = useRef<((snapshot: PlayerInput, valid: () => boolean) => Promise<boolean>) | null>(null);
  const lastActionApplied = useRef(0);
  const lastInventoryApplied = useRef(0);
  const inventoryChange = useRef(0);
  const lastCompletedAction = useRef(-1);
  const generation = useRef(0);
  const end = useCallback((reason = '') => {
    const current = lease.current;
    lease.current = null;
    generation.current++;
    actionEpoch.current++;
    Object.assign(input.current, idleInput(input.current.yaw, input.current.pitch, input.current.hotbar));
    if (document.pointerLockElement) document.exitPointerLock();
    setStatus('ended'); setError(reason);
    if (current) void api('bodies.controlRelease', { params: { body }, body: { session: current.session } }).catch(() => { /* lease expires without further input */ });
  }, [body]);

  useEffect(() => {
    let disposed = false, busy = false, seq = 0, lastUi = 0;
    let inputFlight: Promise<boolean> | null = null;
    const currentGeneration = ++generation.current;
    const isCurrent = () => !disposed && currentGeneration === generation.current;
    setStatus('connecting'); setError(''); setState(null); setInventory(null); setInventoryAt(0); pose.current = null;
    actionSeq.current = 0; lastActionApplied.current = 0; lastCompletedAction.current = -1; heldActionBusy.current = null;
    lastInventoryApplied.current = 0; inventoryChange.current++;
    actionQueue.current = Promise.resolve(); actionEpoch.current++;
    pressInFlight.current = null;
    const acquire = async () => {
      try {
        const acquired = await api('bodies.controlAcquire', { params: { body }, body: { requestId: crypto.randomUUID() } });
        if (!isCurrent()) {
          void api('bodies.controlRelease', { params: { body }, body: { session: acquired.session } }).catch(() => {});
          return;
        }
        lease.current = acquired;
        pose.current = acquired.state;
        Object.assign(input.current, idleInput(acquired.state.yaw, acquired.state.pitch, acquired.state.heldSlot));
        setState(acquired.state); setStatus('active');
        setInventory(acquired.inventory ?? null);
        setInventoryAt(acquired.inventory ? Date.now() : 0);
      } catch (e) { if (isCurrent()) end(errorText(e)); }
    };
    // Strict Mode's discarded effect never acquires a real pawn.
    const begin = setTimeout(() => { void acquire(); }, 0);
    const postInput = (snapshot: PlayerInput): Promise<boolean> => {
      const current = lease.current;
      if (!isCurrent() || !current) return Promise.resolve(false);
      busy = true;
      const pending = api('bodies.controlInput', {
        params: { body }, body: { session: current.session, seq: ++seq, ...snapshot },
        signal: AbortSignal.timeout(1500),
      }).then((reply) => {
        if (!isCurrent()) return false;
        lease.current = reply; pose.current = reply.state;
        if (reply.action && !reply.action.pending && reply.action.seq > lastCompletedAction.current) {
          lastCompletedAction.current = reply.action.seq;
          if (reply.action.ok === false && reply.action.message !== 'action cancelled') setError(reply.action.message ?? 'The action could not be completed.');
          else setError('');
        }
        if (reply.inventory) { setInventory(reply.inventory); setInventoryAt(Date.now()); }
        if (performance.now() - lastUi > 100) { lastUi = performance.now(); setState(reply.state); }
        return true;
      }).catch((e: unknown) => { if (isCurrent()) end(errorText(e)); return false; }).finally(() => {
        if (inputFlight === pending) { inputFlight = null; busy = false; }
      });
      inputFlight = pending;
      return pending;
    };
    flushInput.current = async (snapshot, valid) => {
      // Share the heartbeat's sequence and transport: an old input response cannot overwrite a click's newer aim.
      if (inputFlight) await inputFlight;
      if (!isCurrent() || !valid()) return false;
      // A queued click keeps its intended aim/hand, but must not revive a movement key released while it waited.
      return postInput({ ...input.current, yaw: snapshot.yaw, pitch: snapshot.pitch, hotbar: snapshot.hotbar, sneak: snapshot.sneak });
    };
    const timer = setInterval(() => {
      if (!isCurrent() || !lease.current || busy || pressInFlight.current !== null) return;
      void postInput({ ...input.current });
    }, 50);
    const hidden = () => { if (document.hidden) end('Control released while this tab was hidden.'); };
    const unload = () => {
      const current = lease.current, csrf = getCsrfToken();
      if (!current || !csrf) return;
      lease.current = null;
      void fetch(`/api/bodies/${encodeURIComponent(body)}/control/release`, {
        method: 'POST', credentials: 'same-origin', keepalive: true,
        headers: { 'Content-Type': 'application/json', 'X-CSRF-Token': csrf }, body: JSON.stringify({ session: current.session }),
      }).catch(() => {});
    };
    document.addEventListener('visibilitychange', hidden);
    window.addEventListener('pagehide', unload);
    return () => {
      disposed = true; clearTimeout(begin); clearInterval(timer);
      flushInput.current = null;
      document.removeEventListener('visibilitychange', hidden);
      window.removeEventListener('pagehide', unload);
      const current = lease.current; lease.current = null;
      if (current) void api('bodies.controlRelease', { params: { body }, body: { session: current.session } }).catch(() => {});
      if (document.pointerLockElement) document.exitPointerLock();
    };
  }, [body, attempt, end]);

  const action = useCallback((command: ControlAction): Promise<PawnControlActionResponse | null> => {
    const current = lease.current, at = generation.current;
    if (!current) return Promise.resolve(null);
    const held = command.kind === 'attack' || command.kind === 'use';
    if (held && heldActionBusy.current !== null) return Promise.resolve(null);
    const seq = ++actionSeq.current;
    const readOnly = command.kind === 'inventory';
    if (!readOnly && command.kind !== 'cancel' && command.kind !== 'release') inventoryChange.current++;
    const inventoryRevision = inventoryChange.current;
    const pressedInput = { ...input.current };
    if (held) heldActionBusy.current = seq;
    if (command.kind === 'release') heldActionBusy.current = null;
    // Blur/Escape must stop immediately and discard queued presses. Mouse release follows its press's bounded server
    // acknowledgement instead: independent HTTP requests can otherwise deliver release first and erase a quick click.
    if (command.kind === 'cancel') {
      actionEpoch.current++;
      heldActionBusy.current = null;
      lastActionApplied.current = seq;
      actionQueue.current = Promise.resolve();
      pressInFlight.current = null;
    }
    const epoch = actionEpoch.current;
    const send = async (): Promise<PawnControlActionResponse | null> => {
      if (at !== generation.current || (!readOnly && epoch !== actionEpoch.current) || lease.current?.session !== current.session) return null;
      try {
        if (held || command.kind === 'dig') {
          pressInFlight.current = seq;
          const valid = () => at === generation.current && epoch === actionEpoch.current;
          if (!await flushInput.current?.(pressedInput, valid) || !valid()) return null;
        }
        const reply = await api('bodies.controlAction', { params: { body }, body: { session: current.session, seq, ...command } });
        if (at !== generation.current || (!readOnly && seq < lastActionApplied.current)) return null;
        if (readOnly) {
          // Reading a window must survive unlock/cancel, but a snapshot requested before a new click cannot undo it.
          if (inventoryRevision !== inventoryChange.current || seq < lastInventoryApplied.current) return null;
          if (reply.inventory) { lastInventoryApplied.current = seq; setInventory(reply.inventory); setInventoryAt(Date.now()); }
          if (!reply.ok && seq >= lastActionApplied.current) setError(reply.message ?? 'The inventory could not be read.');
          return reply;
        }
        lastActionApplied.current = seq;
        if (!reply.ok) setError(reply.message ?? 'The action could not be completed.');
        else setError('');
        if (reply.inventory && seq >= lastInventoryApplied.current) {
          lastInventoryApplied.current = seq; setInventory(reply.inventory); setInventoryAt(Date.now());
        }
        // Input replies are the camera clock; a slower action must not rewind the camera.
        return reply;
      } catch (e) { if (at === generation.current && seq >= lastActionApplied.current) setError(errorText(e)); return null; }
      finally { if (pressInFlight.current === seq && at === generation.current) pressInFlight.current = null; }
    };
    const pending = readOnly || command.kind === 'cancel' ? send() : actionQueue.current.then(send);
    if (!readOnly) actionQueue.current = pending;
    return pending.finally(() => { if (heldActionBusy.current === seq && at === generation.current) heldActionBusy.current = null; });
  }, [body]);

  return { input, pose, state, inventory, inventoryAt, status, error, action, end, retry: () => setAttempt((n) => n + 1) };
}
