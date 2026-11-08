import { afterEach, expect, it, vi } from 'vitest';
import { ControllerAudio, type SoundListener } from '../src/audio/controllerAudio';
import type { LocalPack } from '../src/assets/localStore';

const read = vi.hoisted(() => vi.fn());
vi.mock('../src/assets/localStore', () => ({ ASSETS_CHANGED: 'assets-changed', readLocalPack: read }));
afterEach(() => { vi.useRealTimers(); vi.restoreAllMocks(); vi.unstubAllGlobals(); });
const tick = async () => { for (let i = 0; i < 8; i++) await Promise.resolve(); };

it('never caches or plays sound decoded from a replaced pack whose database read finishes late', async () => {
  vi.stubGlobal('window', new EventTarget()); vi.stubGlobal('document', { hidden: false });
  vi.useFakeTimers();
  const param = () => ({ value: 0, setTargetAtTime(value: number) { this.value = value; } });
  const node = () => ({ connect() {}, disconnect() {} });
  const decode = vi.fn(async (bytes: ArrayBuffer) => bytes), start = vi.fn();
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
  const sound = (name: string) => ({ sounds: { step: { arrayBuffer: vi.fn(async () => new TextEncoder().encode(name).buffer) } }, soundEvents: { walk: { sounds: ['step'] } } }) as unknown as LocalPack;
  const old = sound('old'), fresh = sound('new');
  read.mockReturnValueOnce(pending).mockResolvedValue(fresh);
  const listener: SoundListener = { dim: 0, version: '1.12.2', position: [0, 100, 0], forward: [0, 0, -1], up: [0, 1, 0], topDown: true };
  const audio = new ControllerAudio(() => listener);
  try {
    await audio.enable();
    const at = Date.now(); audio.receive([{ kind: 'sound', sound: 'walk', dim: 0, x: 0, y: 0, z: 0, at }]);
    window.dispatchEvent(new Event('assets-changed'));
    resolve(old); await tick();
    expect(old.sounds.step.arrayBuffer).not.toHaveBeenCalled();
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
