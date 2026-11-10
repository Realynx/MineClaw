'use client';

import { useCallback, useEffect, useRef, useState } from 'react';
import { PAWN_CONTROL_END_REASONS, type PawnControlActionCode, type PawnControlActionRequest, type PawnControlActionResponse, type PawnControlActionStatus, type PawnControlAim, type PawnControlBroke, type PawnControlEndReason, type PawnControlInventory, type PawnControlSession, type PawnControlState } from '@mineclaw/contracts';
import { api, errorText, getCsrfToken } from '@/lib/api/client';
import { idleInput, type PlayerInputChange } from './playerInput';
import { PawnControlSocket } from './controlSocket';
import { ControlCamera } from './controlCamera';

export type ControlAction = Omit<PawnControlActionRequest, 'session' | 'seq'>;

// Endings the player causes on purpose: a click that replaced the action, or a dig dropped by looking away.
const QUIET_CODES: readonly string[] = ['cancelled', 'aim_changed'] satisfies PawnControlActionCode[];
// A server that sends no `code` yet is recognised by its message. This is the only place that reads the text.
const QUIET_MESSAGES = ['action cancelled', 'Dig stopped because the aim, held item or control input changed.'];

/** The banner for a finished action: its message when it failed, nothing when it worked or ended quietly. */
function actionFailure(result: Pick<PawnControlActionStatus, 'ok' | 'message' | 'code'>): string {
  if (result.ok !== false) return '';
  if (QUIET_CODES.includes(result.code ?? '') || QUIET_MESSAGES.includes(result.message ?? '')) return '';
  return result.message ?? 'The action could not be completed.';
}

/** The ending the server named on its closing frame, or null (a release, a lost connection, an older server). */
const endReason = (code: string | undefined): PawnControlEndReason | null =>
  (PAWN_CONTROL_END_REASONS as readonly string[]).includes(code ?? '') ? code as PawnControlEndReason : null;

/** The input that last moved the look or the keys (`usePawnControl().aimInput`). */
export interface AimInput { seq: number; pending: boolean }

/** An input frame went out: it carries any change still pending. */
function aimSent(aim: AimInput, seq: number): void {
  if (aim.pending) { aim.seq = seq; aim.pending = false; }
}

/** The server's aim for `state` once that state applied the last input that moved the look or the keys: undefined
 * while it has not (or the server does not report aim), so the browser keeps its own pick; null = nothing in reach. */
export function serverAim(state: PawnControlState | null, aim: AimInput): PawnControlAim | null | undefined {
  if (!state || state.aim === undefined || aim.pending || state.ackSeq === undefined || state.ackSeq < aim.seq) return undefined;
  return state.aim;
}

export interface PawnControlEvents {
  /** The pawn broke a block: its state frame's `broke` counter grew. Once per break, with the block as it was. */
  onBroke?: (broke: PawnControlBroke) => void;
}

/** A short HTTP lease, then ordered real-time input and server position updates on one persistent connection. */
export function usePawnControl(body: string, events: PawnControlEvents = {}) {
  const listeners = useRef(events);
  useEffect(() => { listeners.current = events; });
  /** The `broke` counter as last seen; a frame with a higher one reports the break. */
  const brokeSeen = useRef(0);
  const input = useRef(idleInput());
  const pose = useRef<PawnControlState | null>(null);
  const camera = useRef(new ControlCamera());
  const lease = useRef<PawnControlSession | null>(null);
  /** How far the pawn reaches, in blocks, as the server last said; undefined on a server that does not say. */
  const reach = useRef<number | undefined>(undefined);
  const socket = useRef<PawnControlSocket | null>(null);
  const [state, setState] = useState<PawnControlState | null>(null);
  const [inventory, setInventory] = useState<PawnControlInventory | null>(null);
  const [inventoryAt, setInventoryAt] = useState(0);
  const [status, setStatus] = useState<'connecting' | 'active' | 'ended'>('connecting');
  const [error, setError] = useState('');
  const [ending, setEnding] = useState<PawnControlEndReason | null>(null);
  const [attempt, setAttempt] = useState(0);
  const actionSeq = useRef(0);
  const heldActionBusy = useRef<number | null>(null);
  const lastActionApplied = useRef(0);
  const lastInventoryApplied = useRef(0);
  /** The tick of the newest inventory an action reply brought: a state frame of that tick or older may have been read
   * before the click and must not undo it. */
  const inventoryTick = useRef<number | undefined>(undefined);
  /** The version of the inventory shown, when the server labels them: a frame or reply only replaces it with a higher one.
   * The server sends each version once per socket, so the label (not the tick) decides which of the two is newer. */
  const inventoryVersion = useRef<number | undefined>(undefined);
  const inventoryChange = useRef(0);
  const lastCompletedAction = useRef(-1);
  const generation = useRef(0);
  const lastInputSent = useRef(-Infinity);
  const lookTimer = useRef<ReturnType<typeof setTimeout> | null>(null);
  /** The input frame that last changed the look or the movement keys: its `seq` once sent, `pending` until it is. The
   * server's aim answers for what the player sees once a state frame's ackSeq reaches that seq. */
  const aimInput = useRef<AimInput>({ seq: 0, pending: false });
  const sendInput = useCallback(() => {
    const seq = socket.current?.input({ ...input.current });
    if (seq) { lastInputSent.current = performance.now(); camera.current.sent(seq, lastInputSent.current); aimSent(aimInput.current, seq); }
  }, []);
  const inputChanged = useCallback((kind: PlayerInputChange) => {
    const now = performance.now();
    if (kind !== 'hotbar') aimInput.current.pending = true;
    if (kind !== 'look') camera.current.setInput(input.current, now, kind === 'stop');
    if (lookTimer.current !== null) { clearTimeout(lookTimer.current); lookTimer.current = null; }
    // Keyboard transitions and stops go immediately. Mouse look is bounded to40Hz, in addition to the20Hz heartbeat,
    // so a high-rate mouse cannot exhaust the gateway's frame limit or delay later key-up/cancel frames.
    const delay = kind === 'look' ? Math.max(0, 25 - (now - lastInputSent.current)) : 0;
    if (delay) {
      const active = socket.current;
      lookTimer.current = setTimeout(() => {
        lookTimer.current = null;
        if (socket.current === active) sendInput();
      }, delay);
    } else sendInput();
  }, [sendInput]);
  const end = useCallback((reason = '', code?: string) => {
    const current = lease.current;
    lease.current = null; generation.current++;
    socket.current?.dispose(); socket.current = null;
    Object.assign(input.current, idleInput(input.current.yaw, input.current.pitch, input.current.hotbar));
    camera.current.setInput(input.current, performance.now(), true);
    if (lookTimer.current !== null) { clearTimeout(lookTimer.current); lookTimer.current = null; }
    if (document.pointerLockElement) document.exitPointerLock();
    setStatus('ended'); setError(reason); setEnding(endReason(code));
    // Closing the socket releases on the server. HTTP also covers a failed upgrade before the attach frame arrived.
    if (current) void api('bodies.controlRelease', { params: { body }, body: { session: current.session } }).catch(() => {});
  }, [body]);

  useEffect(() => {
    let disposed = false, lastUi = 0, attached = false;
    const currentGeneration = ++generation.current;
    const isCurrent = () => !disposed && currentGeneration === generation.current;
    const invalidate = () => { if (currentGeneration === generation.current) generation.current++; };
    setStatus('connecting'); setError(''); setEnding(null); setState(null); setInventory(null); setInventoryAt(0); pose.current = null;
    inventoryTick.current = undefined; inventoryVersion.current = undefined;
    reach.current = undefined; brokeSeen.current = 0;
    camera.current.clear();
    lastInputSent.current = -Infinity; aimInput.current = { seq: 0, pending: false };
    actionSeq.current = 0; lastActionApplied.current = 0; lastCompletedAction.current = -1; heldActionBusy.current = null;
    lastInventoryApplied.current = 0; inventoryChange.current++;
    const acquire = async () => {
      try {
        const acquired = await api('bodies.controlAcquire', { params: { body }, body: { requestId: crypto.randomUUID() } });
        if (!isCurrent()) {
          void api('bodies.controlRelease', { params: { body }, body: { session: acquired.session } }).catch(() => {});
          return;
        }
        lease.current = acquired; pose.current = acquired.state; reach.current = acquired.reach;
        brokeSeen.current = acquired.state.broke?.count ?? 0;
        camera.current.push(acquired.state, performance.now());
        Object.assign(input.current, idleInput(acquired.state.yaw, acquired.state.pitch, acquired.state.heldSlot));
        setState(acquired.state); setInventory(acquired.inventory ?? null); setInventoryAt(acquired.inventory ? Date.now() : 0);
        inventoryVersion.current = acquired.inventory?.version;
        const csrf = getCsrfToken();
        if (!csrf) throw new Error('The control connection needs a fresh login.');
        socket.current = new PawnControlSocket(body, acquired, csrf, (reply, at) => {
          if (!isCurrent()) return;
          lease.current = reply; pose.current = reply.state; camera.current.push(reply.state, at);
          reach.current = reply.reach;
          // The counter, not the action status: a newer click can replace the status before any frame carries the dig's end.
          const broke = reply.state.broke;
          if (broke && broke.count !== brokeSeen.current) {
            const grew = broke.count > brokeSeen.current;
            brokeSeen.current = broke.count;
            if (grew) listeners.current.onBroke?.(broke);
          }
          if (reply.action && !reply.action.pending && reply.action.seq > lastCompletedAction.current) {
            lastCompletedAction.current = reply.action.seq;
            if (reply.action.seq >= lastActionApplied.current) setError(actionFailure(reply.action));
          }
          const tick = reply.state.tick, replied = inventoryTick.current;
          const version = reply.inventory?.version, shown = inventoryVersion.current;
          const fresh = version !== undefined && shown !== undefined ? version > shown
            : tick === undefined || replied === undefined || tick > replied;
          if (reply.inventory && fresh) {
            setInventory(reply.inventory); setInventoryAt(Date.now());
            if (version !== undefined) inventoryVersion.current = version;
          }
          if (!attached || at - lastUi >= 100) { lastUi = at; setState(reply.state); }
          if (!attached) { attached = true; setStatus('active'); }
        }, (reason, code) => { if (isCurrent()) end(reason, code); });
      } catch (e) { if (isCurrent()) end(errorText(e)); }
    };
    // Strict Mode's discarded effect never acquires a real pawn.
    const begin = setTimeout(() => { void acquire(); }, 0);
    const timer = setInterval(() => {
      if (isCurrent() && attached) sendInput();
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
      if (lookTimer.current !== null) { clearTimeout(lookTimer.current); lookTimer.current = null; }
      invalidate();
      document.removeEventListener('visibilitychange', hidden);
      window.removeEventListener('pagehide', unload);
      socket.current?.dispose(); socket.current = null;
      const current = lease.current; lease.current = null;
      if (current) void api('bodies.controlRelease', { params: { body }, body: { session: current.session } }).catch(() => {});
      if (current && document.pointerLockElement) document.exitPointerLock();
    };
  }, [body, attempt, end, sendInput]);

  const repliedInventory = useCallback((reply: PawnControlActionResponse) => {
    if (!reply.inventory) return;
    const version = reply.inventory.version, shown = inventoryVersion.current;
    // A frame already brought this version or a newer one.
    if (version !== undefined && shown !== undefined && version <= shown) return;
    setInventory(reply.inventory); setInventoryAt(Date.now());
    if (version !== undefined) inventoryVersion.current = version;
    const tick = reply.state.tick;
    if (tick !== undefined) inventoryTick.current = Math.max(tick, inventoryTick.current ?? tick);
  }, []);
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
    if (held || command.kind === 'dig' || command.kind === 'drop' || command.kind === 'cancel') {
      const sent = current.input({ ...input.current });
      if (!sent) return Promise.resolve(null);
      camera.current.sent(sent, performance.now()); aimSent(aimInput.current, sent);
    }
    return current.action({ seq, ...command }).then((reply) => {
      if (at !== generation.current || (!readOnly && seq < lastActionApplied.current)) return null;
      if (readOnly) {
        // A read survives unlock/cancel, but a snapshot requested before a new click cannot undo that click.
        if (inventoryRevision !== inventoryChange.current || seq < lastInventoryApplied.current) return null;
        if (reply.inventory) { lastInventoryApplied.current = seq; repliedInventory(reply); }
        if (!reply.ok && seq >= lastActionApplied.current) setError(reply.message ?? 'The inventory could not be read.');
        return reply;
      }
      lastActionApplied.current = seq;
      setError(actionFailure(reply));
      if (reply.inventory && seq >= lastInventoryApplied.current) {
        lastInventoryApplied.current = seq; repliedInventory(reply);
      }
      return reply;
    }).catch((e: unknown) => {
      if (at === generation.current && seq >= lastActionApplied.current) setError(errorText(e));
      return null;
    }).finally(() => { if (heldActionBusy.current === seq && at === generation.current) heldActionBusy.current = null; });
  }, [repliedInventory]);

  return { input, inputChanged, pose, camera, reach, aimInput, state, inventory, inventoryAt, status, error, endReason: ending, action, end, retry: () => setAttempt((n) => n + 1) };
}
