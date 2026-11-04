import type { StoryArchitectEntry } from '@mineclaw/contracts';
import { cleanup, fireEvent, render } from '@testing-library/react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { MaterialBar } from '@/features/story/MaterialBar';
import {
  cycleMaterial,
  defaultMaterial,
  loadMaterialMemory,
  materialsFor,
  materialToolOf,
  pushRecent,
  recentFor,
  rememberMaterial,
  saveMaterialMemory,
  searchMaterials,
  stockText,
} from '@/features/story/materials';
import { keyAction, type BuildOpts } from '@/features/story/tools';

afterEach(cleanup);

const e = (
  id: string,
  label: string,
  material: [number, number],
  kinds: string[],
  extra: Partial<StoryArchitectEntry> = {},
): StoryArchitectEntry => ({
  id,
  label,
  tab: 'structure',
  tier: 'hand',
  unlocked: true,
  reason: null,
  item: material,
  material,
  kind: kinds[0]!,
  kinds,
  chain: [],
  have: 0,
  craftable: 0,
  from: [],
  short: [],
  ...extra,
});

/** The mock's catalogue, in short. */
const PALETTE: StoryArchitectEntry[] = [
  e('planks', 'Oak Planks', [5, 0], ['wall', 'floor', 'block', 'roof'], { have: 40 }),
  e('cobblestone', 'Cobblestone', [4, 0], ['wall', 'floor', 'block', 'roof'], { tier: 'gathered', have: 12 }),
  e('stone', 'Stone', [1, 0], ['wall', 'floor', 'block', 'roof'], {
    tier: 'furnace',
    unlocked: false,
    reason: 'Needs a furnace',
  }),
  e('glass', 'Glass', [20, 0], ['window', 'wall', 'block'], { tier: 'furnace', craftable: 3 }),
  e('stone_slab', 'Stone Slab', [44, 0], ['floor', 'block', 'roof'], { tier: 'workbench', short: ['2 Cobblestone'] }),
  e('oak_stairs', 'Oak Wood Stairs', [53, 0], ['stairs'], { tier: 'workbench' }),
  e('fence', 'Oak Fence', [85, 0], ['fence']),
  e('wooden_door', 'Oak Door', [64, 0], ['door'], { item: [324, 0] }),
  e('bed', 'Bed', [26, 0], ['bed'], { tab: 'furniture' }),
];

describe('the material selector: which materials a tool takes', () => {
  it('filters the palette per tool (each material once, unlocked first, locked ones with the reason)', () => {
    const floor = materialsFor('floor', PALETTE);
    expect(floor.map((o) => o.label)).toEqual(['Oak Planks', 'Cobblestone', 'Stone Slab', 'Stone']);
    expect(floor.at(-1)).toMatchObject({ unlocked: false, reason: 'Needs a furnace' });
    expect(materialsFor('wall', PALETTE).map((o) => o.key)).toEqual(['5:0', '4:0', '20:0', '1:0']);
    expect(materialsFor('stairs', PALETTE).map((o) => o.label)).toEqual(['Oak Wood Stairs']);
    expect(materialsFor('door', PALETTE)[0]).toMatchObject({ material: [64, 0], item: [324, 0] });
    expect(materialsFor('fence', PALETTE).map((o) => o.key)).toEqual(['85:0']);
  });

  it('without a palette: the static materials with the stockpile counts', () => {
    const wall = materialsFor('wall', null, new Map([['4:0', 7]]));
    expect(wall.find((o) => o.key === '4:0')).toMatchObject({ material: 'cobblestone', have: 7, unlocked: true });
    expect(wall.some((o) => o.label === 'Chest' || o.label === 'Torch')).toBe(false);
    expect(materialsFor('stairs', null).map((o) => o.key)).toEqual(['53:0', '67:0']);
  });

  it('only structure tools take one', () => {
    expect(materialToolOf({ kind: 'build', id: 'floor' })).toBe('floor');
    expect(materialToolOf({ kind: 'build', id: 'window' })).toBeNull();
    expect(materialToolOf({ kind: 'build', id: 'furniture:bed' })).toBeNull();
    expect(materialToolOf({ kind: 'designate', id: 'mine' })).toBeNull();
  });

  it('search, stock lines', () => {
    const wall = materialsFor('wall', PALETTE);
    expect(searchMaterials(wall, 'cob').map((o) => o.label)).toEqual(['Cobblestone']);
    expect(searchMaterials(wall, '  ').length).toBe(wall.length);
    expect(stockText(wall[0]!)).toBe('have 40');
    expect(stockText(wall.find((o) => o.label === 'Glass')!)).toBe('craftable 3');
    expect(stockText(wall.find((o) => o.label === 'Stone')!)).toBe('Needs a furnace');
    expect(stockText(materialsFor('floor', PALETTE).find((o) => o.label === 'Stone Slab')!)).toBe(
      'short: 2 cobblestone',
    );
  });

  it('cycles over the unlocked ones, wrapping (Tab / Shift+Tab, ] / [)', () => {
    const wall = materialsFor('wall', PALETTE); // planks, cobble, glass, (stone: locked)
    expect(cycleMaterial(wall, '5:0', 1)?.key).toBe('4:0');
    expect(cycleMaterial(wall, '20:0', 1)?.key).toBe('5:0'); // skips the locked stone, wraps
    expect(cycleMaterial(wall, '5:0', -1)?.key).toBe('20:0');
    expect(cycleMaterial(wall, 'nope', 1)?.key).toBe('5:0');
    expect(cycleMaterial([], null, 1)).toBeNull();
  });

  it('remembers the last material per tool and the recent ones', () => {
    const wall = materialsFor('wall', PALETTE);
    expect(defaultMaterial(wall, '4:0')?.key).toBe('4:0');
    expect(defaultMaterial(wall, '1:0')?.key).toBe('5:0'); // locked now: the first usable one
    expect(defaultMaterial(wall, null)?.key).toBe('5:0');
    let m = rememberMaterial({ last: {}, recent: [] }, 'wall', '4:0');
    m = rememberMaterial(m, 'floor', '44:0');
    m = rememberMaterial(m, 'wall', '20:0');
    expect(m.last).toEqual({ wall: '20:0', floor: '44:0' });
    expect(m.recent).toEqual(['20:0', '44:0', '4:0']);
    expect(recentFor(wall, m.recent).map((o) => o.key)).toEqual(['20:0', '4:0']); // the slab is no wall
    expect(pushRecent(['a', 'b', 'c'], 'b')).toEqual(['b', 'a', 'c']);
    saveMaterialMemory(m);
    expect(loadMaterialMemory()).toEqual(m);
    localStorage.setItem('mcx-story-materials', '{bad json');
    expect(loadMaterialMemory()).toEqual({ last: {}, recent: [] });
  });

  it('keys: Tab, Shift+Tab, ] and [ cycle and 1–5 pick a recent one, only with a material tool in hand', () => {
    expect(keyAction({ code: 'Tab', key: 'Tab' }, true, false, true)).toBe('materialNext');
    expect(keyAction({ code: 'Tab', key: 'Tab', shiftKey: true }, true, false, true)).toBe('materialPrev');
    expect(keyAction({ code: 'BracketRight', key: ']' }, true, false, true)).toBe('materialNext');
    expect(keyAction({ code: 'BracketLeft', key: '[' }, true, false, true)).toBe('materialPrev');
    expect(keyAction({ code: 'Digit2', key: '2' }, true, false, true)).toBe('recent:2');
    expect(keyAction({ code: 'Numpad5', key: '5' }, true, false, true)).toBe('recent:5');
    expect(keyAction({ code: 'Digit6', key: '6' }, true, false, true)).toBeNull();
    expect(keyAction({ code: 'Tab', key: 'Tab' }, true, false, false)).toBeNull();
    expect(keyAction({ code: 'Digit2', key: '2' }, false, false, false)).toBeNull();
    expect(keyAction({ code: 'KeyQ', key: 'q' }, true, false, true)).toBe('rotateCcw'); // the others stay
  });
});

describe('the docked bar', () => {
  const build: BuildOpts = { height: 3, roof: 'flat', overhang: false, facing: 's', material: [5, 0] };
  const bar = (toolId: 'wall' | 'floor' | 'roof' | 'furniture:bed', extra = {}) => {
    const props = {
      toolId,
      options: toolId.startsWith('furniture:') ? null : materialsFor(toolId as 'wall', PALETTE),
      current: '5:0',
      recent: [],
      query: '',
      onQuery: vi.fn(),
      onPick: vi.fn(),
      onHover: vi.fn(),
      build,
      onBuild: vi.fn(),
      onClose: vi.fn(),
      ...extra,
    };
    render(<MaterialBar {...props} />);
    return props;
  };

  it('lists the tool’s materials with their stock and lock state; picking and hovering report them', () => {
    const p = bar('floor');
    const root = document.querySelector('[data-testid="material-bar"]')!;
    expect(root.getAttribute('data-docked')).toBe('bottom-left');
    const stone = root.querySelector<HTMLButtonElement>('[data-material="1:0"]')!;
    expect(stone.disabled).toBe(true);
    expect(stone.textContent).toContain('Needs a furnace');
    expect(root.querySelector('[data-material="5:0"]')!.getAttribute('aria-pressed')).toBe('true');
    fireEvent.click(root.querySelector('[data-material="4:0"]')!);
    expect(p.onPick).toHaveBeenCalledWith(expect.objectContaining({ key: '4:0' }));
    fireEvent.pointerEnter(root.querySelector('[data-material="44:0"]')!);
    expect(p.onHover).toHaveBeenCalledWith(expect.objectContaining({ key: '44:0' }));
    expect(root.querySelector('[data-testid="wall-height"]')).toBeNull(); // a floor has no height
  });

  it('the tool’s options sit in the same bar: wall height, roof style; furniture has facing and no materials', () => {
    const w = bar('wall');
    expect(document.querySelector('[data-testid="wall-height"]')!.textContent).toBe('3');
    fireEvent.click(document.querySelector('[aria-label="Higher"]')!);
    expect(w.onBuild).toHaveBeenCalledWith({ height: 4 });
    cleanup();
    const r = bar('roof');
    fireEvent.click(document.querySelector('[role="radio"][aria-checked="false"]')!);
    expect(r.onBuild).toHaveBeenCalledWith({ roof: 'slab' });
    cleanup();
    bar('furniture:bed');
    expect(document.querySelector('[data-testid="material-list"]')).toBeNull();
    expect(document.querySelector('[data-testid="facing"]')!.textContent).toBe('s');
  });

  it('a single block turns with Q / E (and the turn buttons) when its material faces somewhere', () => {
    const block = (material: BuildOpts['material']) =>
      bar('block' as 'wall', { build: { ...build, material }, options: materialsFor('block', PALETTE) });
    block([5, 0]); // planks: nothing to turn
    expect(document.querySelector('[data-testid="facing"]')).toBeNull();
    cleanup();
    const f = block([61, 0]); // a furnace: the turn control, and a click turns it
    expect(document.querySelector('[data-testid="facing"]')!.textContent).toBe('s');
    fireEvent.click(document.querySelector('[aria-label="Turn right (E)"]')!);
    expect(f.onBuild).toHaveBeenCalledWith({ facing: 'w' });
  });
});
