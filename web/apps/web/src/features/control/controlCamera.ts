import type { PawnControlState } from '@mineclaw/contracts';
import { advanceControlBody, bodyBox, moveControlBody, type ControlBody, type ControlCollision } from './controlMotion';
import { idleInput, type PlayerInput } from './playerInput';

type Position = Pick<PawnControlState, 'x' | 'y' | 'z' | 'dim'>;
type Authority = Position & Partial<Pick<PawnControlState, 'onGround' | 'food'>>;
const distance = (a: Position, b: Position) => Math.hypot(a.x - b.x, a.y - b.y, a.z - b.z);

/** Immediate local camera movement; server snapshots correct bounded prediction rather than delaying every frame. */
export class ControlCamera {
  private body: ControlBody | null = null;
  private authority: Authority | null = null;
  private input: PlayerInput = idleInput();
  private query: ControlCollision | null = null;
  private at = 0;
  private receivedAt = 0;
  private sampledAt: number | undefined;
  private paused = false;
  private stale = false;
  private correction = { x: 0, y: 0, z: 0 };
  private history: Array<Position & { at: number }> = [];
  private speedScale = 1;
  private movementAt = 0;
  private lastAuthorityMoveAt = 0;
  private alignmentMs = 75;

  clear(): void {
    this.body = this.authority = null; this.query = null; this.input = idleInput();
    this.at = this.receivedAt = 0; this.sampledAt = undefined; this.paused = this.stale = false;
    this.correction = { x: 0, y: 0, z: 0 };
    this.history = []; this.speedScale = 1; this.movementAt = this.lastAuthorityMoveAt = 0;
    this.alignmentMs = 75;
  }

  private reset(position: Authority, now: number): void {
    this.body = { ...position, vx: 0, vy: 0, vz: 0, onGround: position.onGround ?? true, height: 1.8, jumpCooldown: 0 };
    this.at = now; this.correction = { x: 0, y: 0, z: 0 };
    this.history = [{ ...position, at: now }];
  }

  push(position: Authority, receivedAt: number, sampledAtMs?: number): void {
    const previous = this.authority;
    const reset = !this.body || !previous || previous.dim !== position.dim || distance(previous, position) > 4
      || receivedAt - this.receivedAt > 350 || this.stale
      || (sampledAtMs !== undefined && this.sampledAt !== undefined && sampledAtMs < this.sampledAt);
    if (reset) { this.reset(position, receivedAt); this.speedScale = 1; this.alignmentMs = 75; this.lastAuthorityMoveAt = receivedAt; }
    else {
      this.advance(receivedAt);
      const body = this.body!;
      if (distance(body, position) > 2) this.reset(position, receivedAt);
      else {
        if (distance(previous, position) > 0.002) this.lastAuthorityMoveAt = receivedAt;
        this.learnSpeed(previous, position, receivedAt, sampledAtMs);
        // A server pose describes earlier movement. Match it to the recent local path instead of repeatedly dragging
        // the immediate camera back to a delayed receipt. Stationary/blocked authority stops this allowance after250ms.
        const moving = !this.paused && receivedAt - this.lastAuthorityMoveAt < 250;
        if (moving && distance(previous, position) > 0.002) {
          const lag = receivedAt - this.matchHistory(position, receivedAt).at;
          if (lag >= 10 && lag <= 150) this.alignmentMs += (lag - this.alignmentMs) * 0.1;
        }
        const matched = moving ? this.historyAt(receivedAt - Math.max(50, this.alignmentMs)) : body;
        this.correction = { x: position.x - matched.x, y: position.y - matched.y, z: position.z - matched.z };
      }
    }
    this.authority = { ...position }; this.receivedAt = receivedAt; this.sampledAt = sampledAtMs; this.stale = false;
  }

  /** Called at the input event, so a key transition cannot retroactively move the previous frame's interval. */
  setInput(input: PlayerInput, now: number, stop = false): void {
    this.advance(now);
    if (input.forward !== this.input.forward || input.strafe !== this.input.strafe || input.sprint !== this.input.sprint
      || input.sneak !== this.input.sneak || input.jump !== this.input.jump) this.movementAt = now;
    this.input = { ...input }; this.paused = stop;
    if (stop && this.body) {
      this.body.vx = this.body.vy = this.body.vz = 0;
      this.history = [{ ...this.body, at: now }];
    }
  }

  private historyAt(at: number): Position {
    for (let i = 1; i < this.history.length; i++) {
      const a = this.history[i - 1]!, b = this.history[i]!;
      if (b.at < at) continue;
      const t = Math.max(0, Math.min(1, (at - a.at) / Math.max(1, b.at - a.at)));
      return { x: a.x + (b.x - a.x) * t, y: a.y + (b.y - a.y) * t, z: a.z + (b.z - a.z) * t, dim: b.dim };
    }
    return this.body!;
  }

  private matchHistory(position: Position, receivedAt: number): Position & { at: number } {
    let nearest = { ...this.body!, at: receivedAt }, error = distance(nearest, position);
    for (let i = 1; i < this.history.length; i++) {
      const a = this.history[i - 1]!, b = this.history[i]!;
      if (a.at < receivedAt - 250) continue;
      const dx = b.x - a.x, dy = b.y - a.y, dz = b.z - a.z, length = dx * dx + dy * dy + dz * dz;
      const t = length ? Math.max(0, Math.min(1, ((position.x - a.x) * dx + (position.y - a.y) * dy + (position.z - a.z) * dz) / length)) : 0;
      const candidate = { ...nearest, x: a.x + dx * t, y: a.y + dy * t, z: a.z + dz * t, dim: position.dim, at: a.at + (b.at - a.at) * t };
      const next = distance(candidate, position);
      if (next < error) { nearest = candidate; error = next; }
    }
    return nearest;
  }

  private learnSpeed(previous: Authority, position: Authority, at: number, sampledAt?: number): void {
    if (!this.body || !this.query || at - this.movementAt < 350 || !position.onGround || !previous.onGround || this.input.jump) return;
    const strength = Math.min(1, Math.hypot(this.input.forward, this.input.strafe));
    const elapsed = sampledAt !== undefined && this.sampledAt !== undefined ? sampledAt - this.sampledAt : at - this.receivedAt;
    if (!strength || elapsed < 20 || elapsed > 150) return;
    const terrain = this.query(bodyBox(this.body), position.dim);
    if (!terrain.loaded || terrain.medium) return;
    const friction = (terrain.slipperiness ?? 0.6) * 0.91;
    const sprint = this.input.sprint && this.input.forward >= 0.8 && !this.input.sneak && (position.food ?? 20) > 6;
    const expected = (sprint ? 0.13 : 0.1) * 0.16277136 / friction ** 3 * 0.98 * (this.input.sneak ? 0.3 : 1) * strength / (1 - friction);
    const observed = Math.hypot(position.x - previous.x, position.z - previous.z) / (elapsed / 50) / expected;
    // A collision or packet gap is not a speed effect. Only stable, plausible ground travel changes local acceleration.
    if (observed >= 0.65 && observed <= 4) this.speedScale += (observed - this.speedScale) * 0.5;
  }

  position(now: number, input?: PlayerInput, query?: ControlCollision): Position | null {
    if (query) this.query = query;
    if (input && (Object.keys(input) as Array<keyof PlayerInput>).some((key) => input[key] !== this.input[key])) this.setInput(input, now);
    this.advance(now);
    return this.body ? { x: this.body.x, y: this.body.y, z: this.body.z, dim: this.body.dim } : null;
  }

  private advance(now: number): void {
    if (!this.body || !this.authority || now <= this.at) return;
    if (now - this.receivedAt >= 350) {
      if (!this.stale) this.reset(this.authority, now);
      this.stale = true; this.at = now; return;
    }
    const elapsed = Math.min(100, now - this.at); this.at = now;
    if (!this.query) { this.reset(this.authority, now); return; }
    for (let remaining = elapsed; remaining > 0;) {
      const ms = Math.min(10, remaining); remaining -= ms;
      const before = { x: this.body.x, y: this.body.y, z: this.body.z };
      if (!this.paused && !advanceControlBody(this.body, this.input, ms, this.query, this.authority.food, this.speedScale)) {
        this.reset(this.authority, now); return;
      }
      const error = Math.hypot(this.correction.x, this.correction.y, this.correction.z);
      if (error > 0.005) {
        const amount = Math.min(1 - Math.exp(-ms / 100), 0.008 * ms / error);
        const ground = this.body.onGround;
        moveControlBody(this.body, this.correction.x * amount, this.correction.y * amount, this.correction.z * amount, this.query);
        this.body.onGround = ground;
        this.correction.x *= 1 - amount; this.correction.y *= 1 - amount; this.correction.z *= 1 - amount;
      }
      // Prediction may lead while input is in transit, but cannot wander away from the real body during a stall.
      const dx = this.body.x - this.authority.x, dz = this.body.z - this.authority.z;
      const leadLimit = Math.min(2, Math.max(1, this.speedScale * (this.input.sprint ? 5.62 : 4.32) * 0.2));
      if (Math.hypot(dx, dz) > leadLimit || Math.abs(this.body.y - this.authority.y) > 1.5) {
        this.body.vx = this.body.vy = this.body.vz = 0;
        Object.assign(this.body, before); // retain the last collision-checked position; never clamp through a wall
      }
      this.history.push({ x: this.body.x, y: this.body.y, z: this.body.z, dim: this.body.dim, at: now - remaining });
      while (this.history.length > 1 && this.history[0]!.at < now - 300) this.history.shift();
      if (this.history.length > 80) this.history.shift();
    }
  }
}
