'use client';

import { useCallback, useEffect, useRef, useState } from 'react';
import type { PawnControlActionRequest, PawnControlActionResponse, PawnControlInventory, PawnControlSession, PawnControlState } from '@mineclaw/contracts';
import { api, errorText, getCsrfToken } from '@/lib/api/client';
import { idleInput } from './playerInput';
import { PawnControlSocket } from './controlSocket';
import { ControlCamera } from './controlCamera';

export type ControlAction = Omit<PawnControlActionRequest, 'session' | 'seq'>;

/** A short HTTP lease, then ordered real-time input and server position updates on one persistent connection. */
export function usePawnControl(body: string) {
  const input = useRef(idleInput());
  const pose = useRef<PawnControlState | null>(null);
  const camera = useRef(new ControlCamera());
  const lease = useRef<PawnControlSession | null>(null);
  const socket = useRef<PawnControlSocket | null>(null);
  const [state, setState] = useState<PawnControlState | null>(null);
  const [inventory, setInventory] = useState<PawnControlInventory | null>(null);
  const [inventoryAt, setInventoryAt] = useState(0);
  const [status, setStatus] = useState<'connecting' | 'active' | 'ended'>('connecting');
  const [error, setError] = useState('');
  const [attempt, setAttempt] = useState(0);
  const actionSeq = useRef(0);
  const heldActionBusy = useRef<number | null>(null);
  const lastActionApplied = useRef(0);
  const lastInventoryApplied = useRef(0);
  const inventoryChange = useRef(0);
  const lastCompletedAction = useRef(-1);
  const generation = useRef(0);
  const end = useCallback((reason = '') => {
    const current = lease.current;
    lease.current = null; generation.current++;
    socket.current?.dispose(); socket.current = null;
    Object.assign(input.current, idleInput(input.current.yaw, input.current.pitch, input.current.hotbar));
    if (document.pointerLockElement) document.exitPointerLock();
    setStatus('ended'); setError(reason);
    // Closing the socket releases on the server. HTTP also covers a failed upgrade before the attach frame arrived.
    if (current) void api('bodies.controlRelease', { params: { body }, body: { session: current.session } }).catch(() => {});
  }, [body]);

  useEffect(() => {
    let disposed = false, lastUi = 0, attached = false;
    const currentGeneration = ++generation.current;
    const isCurrent = () => !disposed && currentGeneration === generation.current;
    const invalidate = () => { if (currentGeneration === generation.current) generation.current++; };
    setStatus('connecting'); setError(''); setState(null); setInventory(null); setInventoryAt(0); pose.current = null;
    camera.current.clear();
    actionSeq.current = 0; lastActionApplied.current = 0; lastCompletedAction.current = -1; heldActionBusy.current = null;
    lastInventoryApplied.current = 0; inventoryChange.current++;
    const acquire = async () => {
      try {
        const acquired = await api('bodies.controlAcquire', { params: { body }, body: { requestId: crypto.randomUUID() } });
        if (!isCurrent()) {
          void api('bodies.controlRelease', { params: { body }, body: { session: acquired.session } }).catch(() => {});
          return;
        }
        lease.current = acquired; pose.current = acquired.state;
        camera.current.push(acquired.state, performance.now());
        Object.assign(input.current, idleInput(acquired.state.yaw, acquired.state.pitch, acquired.state.heldSlot));
        setState(acquired.state); setInventory(acquired.inventory ?? null); setInventoryAt(acquired.inventory ? Date.now() : 0);
        const csrf = getCsrfToken();
        if (!csrf) throw new Error('The control connection needs a fresh login.');
        socket.current = new PawnControlSocket(body, acquired, csrf, (reply, at, sampledAtMs) => {
          if (!isCurrent()) return;
          lease.current = reply; pose.current = reply.state; camera.current.push(reply.state, at, sampledAtMs);
          if (reply.action && !reply.action.pending && reply.action.seq > lastCompletedAction.current) {
            lastCompletedAction.current = reply.action.seq;
            if (reply.action.seq >= lastActionApplied.current) {
              if (reply.action.ok === false && reply.action.message !== 'action cancelled') setError(reply.action.message ?? 'The action could not be completed.');
              else setError('');
            }
          }
          if (reply.inventory) { setInventory(reply.inventory); setInventoryAt(Date.now()); }
          if (!attached || at - lastUi >= 100) { lastUi = at; setState(reply.state); }
          if (!attached) { attached = true; setStatus('active'); }
        }, (reason) => { if (isCurrent()) end(reason); });
      } catch (e) { if (isCurrent()) end(errorText(e)); }
    };
    // Strict Mode's discarded effect never acquires a real pawn.
    const begin = setTimeout(() => { void acquire(); }, 0);
    const timer = setInterval(() => {
      if (isCurrent() && attached) socket.current?.input({ ...input.current });
    }, 50);
    const hidden = () => { if (document.hidden) end('Control released while this tab was hidden.'); };
    const unload = () => {
      const current = lease.current, csrf = getCsrfToken();
      socket.current?.dispose(); socket.current = null;
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
      invalidate();
      document.removeEventListener('visibilitychange', hidden);
      window.removeEventListener('pagehide', unload);
      socket.current?.dispose(); socket.current = null;
      const current = lease.current; lease.current = null;
      if (current) void api('bodies.controlRelease', { params: { body }, body: { session: current.session } }).catch(() => {});
      if (document.pointerLockElement) document.exitPointerLock();
    };
  }, [body, attempt, end]);

  const action = useCallback((command: ControlAction): Promise<PawnControlActionResponse | null> => {
    const current = socket.current, at = generation.current;
    if (!current?.active || !lease.current) return Promise.resolve(null);
    const held = command.kind === 'attack' || command.kind === 'use';
    if (held && heldActionBusy.current !== null) return Promise.resolve(null);
    const seq = ++actionSeq.current;
    const readOnly = command.kind === 'inventory';
    if (!readOnly && command.kind !== 'cancel' && command.kind !== 'release') inventoryChange.current++;
    const inventoryRevision = inventoryChange.current;
    if (held) heldActionBusy.current = seq;
    if (command.kind === 'release' || command.kind === 'cancel') heldActionBusy.current = null;
    if (command.kind === 'cancel') lastActionApplied.current = seq;
    // The socket preserves input→click ordering without pausing later movement while an acknowledgement is pending.
    if ((held || command.kind === 'dig' || command.kind === 'drop' || command.kind === 'cancel') && !current.input({ ...input.current })) return Promise.resolve(null);
    return current.action({ seq, ...command }).then((reply) => {
      if (at !== generation.current || (!readOnly && seq < lastActionApplied.current)) return null;
      if (readOnly) {
        // A read survives unlock/cancel, but a snapshot requested before a new click cannot undo that click.
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
      return reply;
    }).catch((e: unknown) => {
      if (at === generation.current && seq >= lastActionApplied.current) setError(errorText(e));
      return null;
    }).finally(() => { if (heldActionBusy.current === seq && at === generation.current) heldActionBusy.current = null; });
  }, []);

  return { input, pose, camera, state, inventory, inventoryAt, status, error, action, end, retry: () => setAttempt((n) => n + 1) };
}
