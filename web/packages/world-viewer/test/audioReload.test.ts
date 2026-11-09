import { afterEach, expect, it, vi } from 'vitest';
import { ControllerAudio, type SoundListener } from '../src/audio/controllerAudio';
import type { LocalPack } from '../src/assets/localStore';

const read = vi.hoisted(() => vi.fn());
vi.mock('../src/assets/localStore', () => ({ ASSETS_CHANGED: 'assets-changed', readLocalPack: read }));
afterEach(() => { read.mockReset(); vi.useRealTimers(); vi.restoreAllMocks(); vi.unstubAllGlobals(); });
const tick = async () => { for (let i = 0; i < 20; i++) await Promise.resolve(); };

const pack = (name = 'step') => ({
  sounds: { step: new Blob([name]) }, soundEvents: { walk: { sounds: ['step'] } },
}) as unknown as LocalPack;

function rig() {
  vi.useFakeTimers();
  const win = new EventTarget(), doc = Object.assign(new EventTarget(), { hidden: false, hasFocus: () => true });
  vi.stubGlobal('window', win); vi.stubGlobal('document', doc);
  const param = () => ({ value: 0, setTargetAtTime(value: number) { this.value = value; } });
  const node = () => ({ connect() {}, disconnect() {} });
  const start = vi.fn(), stop = vi.fn(), decode = vi.fn((bytes: ArrayBuffer) => Promise.resolve(bytes));
  const contexts: Context[] = [];
  class Context {
    state: AudioContextState = 'suspended'; currentTime = 0; destination = {};
    onstatechange: (() => void) | null = null;
    listener = Object.fromEntries(['positionX', 'positionY', 'positionZ', 'forwardX', 'forwardY', 'forwardZ', 'upX', 'upY', 'upZ'].map((k) => [k, param()]));
    constructor() { contexts.push(this); }
    resume = vi.fn(() => { this.state = 'running'; this.onstatechange?.(); return Promise.resolve(); });
    close = vi.fn(() => { this.state = 'closed'; return Promise.resolve(); });
    decodeAudioData = decode;
    createGain = () => ({ ...node(), gain: param() });
    createPanner = () => ({ ...node(), positionX: param(), positionY: param(), positionZ: param() });
    createBufferSource = () => ({ ...node(), playbackRate: param(), start, stop });
  }
  vi.stubGlobal('AudioContext', Context);
  read.mockResolvedValue(pack());
  let present = true, sequence = 0;
  const listener: SoundListener = { dim: 0, version: '1.12.2', position: [0, 64, 0], forward: [0, 0, -1], up: [0, 1, 0], topDown: true };
  const ready = vi.fn();
  const audio = new ControllerAudio(() => present ? listener : null, ready);
  const event = () => ({ kind: 'sound' as const, sound: 'walk', dim: 0, x: ++sequence / 10, y: 64, z: 0, at: Date.now() });
  const play = async () => { audio.receive([event()]); await tick(); };
  return { audio, win, doc, ready, event, play, start, stop, decode, context: () => contexts.at(-1)!, visible: (value: boolean) => { present = value; } };
}

it('never caches or plays sound decoded from a replaced pack whose database read finishes late', async () => {
  vi.stubGlobal('window', new EventTarget()); vi.stubGlobal('document', { hidden: false });
  vi.useFakeTimers();
  const param = () => ({ value: 0, setTargetAtTime(value: number) { this.value = value; } });
  const node = () => ({ connect() {}, disconnect() {} });
  const decode = vi.fn((bytes: ArrayBuffer) => Promise.resolve(bytes)), start = vi.fn();
  const panner = { ...node(), positionX: param(), positionY: param(), positionZ: param() };
  const ears = Object.fromEntries(['positionX', 'positionY', 'positionZ', 'forwardX', 'forwardY', 'forwardZ', 'upX', 'upY', 'upZ'].map((k) => [k, param()]));
  vi.stubGlobal('AudioContext', class {
    state = 'running'; currentTime = 0; destination = {};
    listener = ears;
    resume = async () => {}; close = async () => {}; decodeAudioData = decode;
    createGain = () => ({ ...node(), gain: param() });
    createPanner = () => panner;
    createBufferSource = () => ({ ...node(), playbackRate: param(), start, stop() {} });
  });
  let resolve!: (pack: LocalPack) => void;
  const pending = new Promise<LocalPack>((r) => { resolve = r; });
  const sound = (name: string) => ({ sounds: { step: { arrayBuffer: vi.fn(() => Promise.resolve(new TextEncoder().encode(name).buffer)) } }, soundEvents: { walk: { sounds: ['step'] } } }) as unknown as LocalPack;
  const old = sound('old'), fresh = sound('new');
  const oldRead = vi.spyOn(old.sounds.step, 'arrayBuffer');
  read.mockReturnValueOnce(pending).mockResolvedValue(fresh);
  const listener: SoundListener = { dim: 0, version: '1.12.2', position: [0, 100, 0], forward: [0, 0, -1], up: [0, 1, 0], topDown: true };
  const audio = new ControllerAudio(() => listener);
  try {
    await audio.enable();
    const at = Date.now(); audio.receive([{ kind: 'sound', sound: 'walk', dim: 0, x: 0, y: 0, z: 0, at }]);
    window.dispatchEvent(new Event('assets-changed'));
    resolve(old); await tick();
    expect(oldRead).not.toHaveBeenCalled();
    audio.receive([{ kind: 'sound', sound: 'walk', dim: 0, x: 1, y: 20, z: 0, at }]); await tick();
    expect(decode).toHaveBeenCalledOnce();
    expect(new TextDecoder().decode(decode.mock.calls[0][0])).toBe('new');
    expect(start).toHaveBeenCalledOnce();
    expect(ears.positionY.value).toBe(0); expect(panner.positionY.value).toBe(0);
    listener.position[1] = 250; vi.advanceTimersByTime(50);
    expect(ears.positionY.value).toBe(0); expect(panner.positionY.value).toBe(0);
    listener.topDown = false; listener.position[1] = 10; vi.advanceTimersByTime(50);
    expect(ears.positionY.value).toBe(10); expect(panner.positionY.value).toBe(20);
  } finally { audio.dispose(); }
});

it('resumes unlocked audio after focus and visibility return without replaying sounds from while away', async () => {
  const r = rig();
  try {
    await r.audio.enable(); await r.play();
    expect(r.start).toHaveBeenCalledOnce();
    r.win.dispatchEvent(new Event('blur'));
    expect(r.stop).toHaveBeenCalledOnce();
    r.context().state = 'suspended'; r.context().onstatechange?.();
    r.doc.hidden = true; r.doc.dispatchEvent(new Event('visibilitychange'));
    const old = r.event(); r.audio.receive([old]);
    await vi.advanceTimersByTimeAsync(1000);
    r.doc.hidden = false; r.doc.dispatchEvent(new Event('visibilitychange'));
    r.win.dispatchEvent(new Event('focus')); await tick();
    expect(r.context().resume).toHaveBeenCalledTimes(2);
    expect(r.ready).toHaveBeenLastCalledWith(true);
    r.audio.receive([old]); await tick();
    expect(r.start).toHaveBeenCalledOnce();
    await r.play(); expect(r.start).toHaveBeenCalledTimes(2);
  } finally { r.audio.dispose(); }
});

it('preserves mute across focus changes and cancels a decode when the Controller view disappears', async () => {
  const r = rig();
  try {
    await r.audio.enable();
    r.audio.setVolume(0);
    r.win.dispatchEvent(new Event('blur'));
    r.context().state = 'suspended'; r.context().onstatechange?.();
    r.win.dispatchEvent(new Event('focus')); await tick();
    expect(r.context().resume).toHaveBeenCalledOnce();
    await r.play(); expect(r.start).not.toHaveBeenCalled();
    r.audio.setVolume(0.4); await r.audio.enable();
    let resolve!: (buffer: ArrayBuffer) => void;
    r.decode.mockImplementationOnce(() => new Promise((res) => { resolve = res; }));
    await r.play();
    r.visible(false); await vi.advanceTimersByTimeAsync(50);
    r.visible(true); await vi.advanceTimersByTimeAsync(50);
    resolve(new ArrayBuffer(1)); await tick();
    expect(r.start).not.toHaveBeenCalled();
    await r.play(); expect(r.start).toHaveBeenCalledOnce();
  } finally { r.audio.dispose(); }
});

it.each(['focus', 'cooldown'] as const)('retries a failed asset read on %s without hammering the database for every sound', async (retry) => {
  const r = rig();
  read.mockRejectedValueOnce(new Error('database temporarily unavailable'));
  try {
    await r.audio.enable(); await r.play();
    for (let i = 0; i < 4; i++) await r.play();
    expect(read).toHaveBeenCalledOnce();
    if (retry === 'focus') {
      r.win.dispatchEvent(new Event('blur')); r.win.dispatchEvent(new Event('focus'));
    } else await vi.advanceTimersByTimeAsync(5000);
    await r.play();
    expect(read).toHaveBeenCalledTimes(2);
    expect(r.start).toHaveBeenCalledOnce();
  } finally { r.audio.dispose(); }
});

it('retries transient decoder failures and keeps audio enabled when a JAR replacement refreshes assets', async () => {
  const r = rig();
  r.decode.mockRejectedValueOnce(new Error('decoder interrupted'));
  try {
    await r.audio.enable(); await r.play(); await r.play();
    expect(r.decode).toHaveBeenCalledOnce();
    r.win.dispatchEvent(new Event('blur')); r.win.dispatchEvent(new Event('focus'));
    await r.play(); expect(r.start).toHaveBeenCalledOnce();
    read.mockResolvedValue(pack('replacement'));
    r.win.dispatchEvent(new Event('assets-changed'));
    await r.play();
    expect(r.start).toHaveBeenCalledTimes(2);
    expect(read).toHaveBeenCalledTimes(2);
    expect(r.context().resume).toHaveBeenCalledOnce();
    expect(new TextDecoder().decode(r.decode.mock.lastCall?.[0])).toBe('replacement');
  } finally { r.audio.dispose(); }
});
