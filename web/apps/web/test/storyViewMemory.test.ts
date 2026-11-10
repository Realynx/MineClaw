import { afterEach, beforeEach, expect, it, vi } from 'vitest';
import { clearView, loadView, saveView, viewFor, type StoryView } from '@/features/story/viewMemory';

const KEY = 'mcx-story-view:fox';
const view: StoryView = { colony: 'story-1', x: 216.5, z: 400.25, zoom: 60 };

/** The module as a page load finds it: nothing saved in this page yet, only what the browser stored. */
async function reloaded() {
  vi.resetModules();
  return import('@/features/story/viewMemory');
}

beforeEach(() => {
  localStorage.clear();
  clearView('fox');
  clearView('robin');
});
afterEach(() => vi.unstubAllGlobals());

it('keeps the view across a page load, per account', async () => {
  saveView('fox', view);
  expect(JSON.parse(localStorage.getItem(KEY)!)).toEqual(view);
  expect(loadView('fox')).toEqual(view);
  const next = await reloaded();
  expect(next.loadView('fox')).toEqual(view);
  expect(next.loadView('robin')).toBeNull();
});

it('says nothing about a colony other than the one the view is of', () => {
  expect(viewFor(view, 'story-1')).toBe(view);
  expect(viewFor(view, 'story-2')).toBeNull();
  expect(viewFor(view, null)).toBeNull();
  expect(viewFor(null, 'story-1')).toBeNull();
  expect(viewFor(undefined, undefined)).toBeNull();
});

it.each([
  ['not JSON', '{bad json'],
  ['not an object', '"story-1"'],
  ['null', 'null'],
  ['no colony', JSON.stringify({ x: 1, z: 2, zoom: 44 })],
  ['an empty colony', JSON.stringify({ ...view, colony: '' })],
  ['a colony that is not text', JSON.stringify({ ...view, colony: 7 })],
  ['a missing coordinate', JSON.stringify({ colony: 'story-1', x: 1, zoom: 44 })],
  ['a coordinate as text', JSON.stringify({ ...view, x: '216.5' })],
  ['a null zoom', JSON.stringify({ ...view, zoom: null })],
  ['a number JSON cannot hold', '{"colony":"story-1","x":1e999,"z":2,"zoom":44}'],
])('ignores a stored value that is %s', (_what, raw) => {
  localStorage.setItem(KEY, raw);
  expect(loadView('fox')).toBeNull();
});

it('keeps the zoom within what the map can show, when read and when saved', () => {
  // Zooming out has no practical limit: a far view is remembered as it was.
  localStorage.setItem(KEY, JSON.stringify({ ...view, zoom: 4000 }));
  expect(loadView('fox')?.zoom).toBe(4000);
  localStorage.setItem(KEY, JSON.stringify({ ...view, zoom: 5e9 }));
  expect(loadView('fox')?.zoom).toBe(1_000_000);
  localStorage.setItem(KEY, JSON.stringify({ ...view, zoom: 0.5 }));
  expect(loadView('fox')?.zoom).toBe(10);
  saveView('fox', { ...view, zoom: 5e9 });
  expect(JSON.parse(localStorage.getItem(KEY)!)).toEqual({ ...view, zoom: 1_000_000 });
});

it('does not save what is not a view', () => {
  saveView('fox', { ...view, x: Number.NaN });
  saveView('fox', { ...view, colony: '' });
  expect(localStorage.getItem(KEY)).toBeNull();
  expect(loadView('fox')).toBeNull();
});

it('forgets the view when its colony is gone', async () => {
  saveView('fox', view);
  clearView('fox');
  expect(localStorage.getItem(KEY)).toBeNull();
  expect(loadView('fox')).toBeNull();
  expect((await reloaded()).loadView('fox')).toBeNull();
});

it('carries the view over a re-entry in the same page when storage is blocked', async () => {
  const blocked = vi.fn(() => {
    throw new Error('blocked');
  });
  vi.stubGlobal('localStorage', { getItem: blocked, setItem: blocked, removeItem: blocked });
  expect(loadView('fox')).toBeNull();
  saveView('fox', view);
  expect(loadView('fox')).toEqual(view);
  expect((await reloaded()).loadView('fox')).toBeNull(); // a page load starts from the storage alone
  clearView('fox');
  expect(loadView('fox')).toBeNull();
  expect(blocked).toHaveBeenCalledTimes(5); // every access was tried and refused: read, write, read, remove, read
});

it('opens on what this page saved last, whatever another tab stored since', () => {
  saveView('fox', view);
  localStorage.setItem(KEY, JSON.stringify({ colony: 'story-2', x: 0, z: 0, zoom: 44 }));
  expect(loadView('fox')).toEqual(view);
});
