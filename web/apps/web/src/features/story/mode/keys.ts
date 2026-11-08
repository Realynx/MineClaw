/**
 * Story Mode's keyboard: WASD / arrows pan (held in `keys`, read by the map's frame loop), every other key goes through
 * `keyAction` (../tools.ts: the key → action map) and the action runs here (`KEY_HANDLERS`: one handler per action).
 */
import type { StorySpeed } from '@mineclaw/contracts';
import { useEffect, useLayoutEffect, useRef } from 'react';
import { isTyping } from '@/features/world/directives';
import { cycleMaterial, type MaterialOption } from '../materials';
import { EMPTY_SELECTION } from '../model';
import type { TabId } from '../StoryPanels';
import {
  clampHeight,
  clampLayers,
  HEIGHT_MODES,
  keyAction,
  nextLevel,
  PAN_KEYS,
  rotateFacing,
  type DesignateId,
  type HeightMode,
  type KeyAction,
} from '../tools';
import type { Story } from './commands';

type KeyHandler = (c: Story, arg: string, e: KeyboardEvent) => void;

/** Chooses a material from the keys (Tab / ] / [ cycle the shown ones, 1–5 the recent ones) and names it. */
function chooseByKey(c: Story, o: MaterialOption | null | undefined) {
  if (!o) return;
  c.chooseMaterial(o);
  c.say(`${o.label}`);
}

const cycleMaterialKey =
  (dir: 1 | -1): KeyHandler =>
  (c) =>
    chooseByKey(c, cycleMaterial(c.matShown ?? [], c.matCurrent, dir));

/** 1–5 with a height tool: its height mode (Layers says the count). */
function setHeightModeKey(c: Story, m: string) {
  const tool = c.heightTool;
  if (!tool) return;
  const mode = m as HeightMode;
  c.setHeightModes((cur) => ({ ...cur, [tool]: mode }));
  c.say(`${HEIGHT_MODES.find((h) => h.id === mode)!.label}${mode === 'layers' ? `: ${c.layers}` : ''}`);
}

/** + / − with a height tool: the Layers count (and the Layers mode). */
const layersKey =
  (d: 1 | -1): KeyHandler =>
  (c) => {
    c.setLayers((n) => clampLayers(n + d));
    const tool = c.heightTool;
    if (tool) c.setHeightModes((cur) => ({ ...cur, [tool]: 'layers' }));
  };

/** R toggles; a second press before the state caught up flips the last request rather than re-reading old state. */
function toggleDraft(c: Story) {
  const key = [...c.split.pawns].sort().join(',');
  const last = c.lastDraft.current;
  const anyUndrafted =
    last && last.key === key && Date.now() - last.at < 4000
      ? !last.drafted
      : c.split.pawns.some((n) => !c.state?.pawns.find((p) => p.name === n)?.drafted);
  c.lastDraft.current = { key, drafted: anyUndrafted, at: Date.now() };
  void c.draft(c.split.pawns, anyUndrafted);
}

/** Esc closes the topmost thing: a chest pick, the menu, help, chat, the tool, the tab, then the selection. */
function closeTopmost(c: Story) {
  if (c.picking.current) {
    c.picking.current(null);
    c.picking.current = null;
    c.setPickHint(false);
  } else if (c.menu) c.setMenu(null);
  else if (c.help) c.setHelp(false);
  else if (c.chat) c.setChat(false);
  else if (c.tool.kind !== 'select') c.setTool({ kind: 'select' });
  else if (c.tab) c.setTab(null);
  else c.setSelection(EMPTY_SELECTION);
}

/** A drag in progress is cancelled first (nothing happens on release); the key goes no further. */
function escape(c: Story, _arg: string, e: KeyboardEvent) {
  if (c.map.current?.cancelDrag()) {
    e.stopPropagation();
    return;
  }
  closeTopmost(c);
}

/** Home: the selected body, else the colony's home. */
function focus(c: Story) {
  const home = c.state?.home;
  if (c.selection.bodies[0]) c.focusBody(c.selection.bodies[0]);
  else if (home) c.map.current?.focus(home[0], home[2], home[1]);
}

const levelKey =
  (dir: 1 | -1): KeyHandler =>
  (c, _arg, e) =>
    c.setLevel(nextLevel(c.lv, c.build.height, dir, e.shiftKey));

const rotateKey =
  (dir: 1 | -1): KeyHandler =>
  (c) =>
    c.setBuild((b) => ({ ...b, facing: rotateFacing(b.facing, dir) }));

const heightKey =
  (d: 1 | -1): KeyHandler =>
  (c) =>
    c.setBuild((b) => ({ ...b, height: clampHeight(b.height + d) }));

/** A tool's key picks it up; pressed again, it is put down. */
function toolKey(c: Story, id: string) {
  const t = id as DesignateId;
  c.setTool((cur) => (cur.kind === 'designate' && cur.id === t ? { kind: 'select' } : { kind: 'designate', id: t }));
}

/** A tab's key opens it; pressed again, it closes. */
function tabKey(c: Story, id: string) {
  const t = id as TabId;
  c.setTab((cur) => (cur === t ? null : t));
}

/** Each action (the part of a `KeyAction` before its `:`) → what it does; the part after is `arg`. */
const KEY_HANDLERS: Record<string, KeyHandler> = {
  materialNext: cycleMaterialKey(1),
  materialPrev: cycleMaterialKey(-1),
  recent: (c, n) => chooseByKey(c, c.matRecent[Number(n) - 1]),
  height: setHeightModeKey,
  layersUp: layersKey(1),
  layersDown: layersKey(-1),
  draft: toggleDraft,
  pause: (c) => c.togglePause(),
  speed: (c, speed) => c.setSpeed(speed as StorySpeed),
  escape,
  chat: (c) => c.setChat(true),
  view: (c) => c.setView((v) => (v === 'top' ? 'angled' : 'top')),
  fly: (c) => c.setView((v) => (v === 'fly' ? 'top' : 'fly')),
  xray: (c) => c.setXray(c.xrayOn ? 'off' : 'on'),
  levelUp: levelKey(1),
  levelDown: levelKey(-1),
  rotateCcw: rotateKey(-1),
  rotateCw: rotateKey(1),
  heightUp: heightKey(1),
  heightDown: heightKey(-1),
  help: (c) => c.setHelp((h) => !h),
  fullscreen: (c) => c.fullscreen.toggle(),
  focus,
  tool: toolKey,
  tab: tabKey,
};

/** Runs a key's action. */
export function runKeyAction(c: Story, a: KeyAction, e: KeyboardEvent) {
  const colon = a.indexOf(':');
  const name = colon < 0 ? a : a.slice(0, colon);
  KEY_HANDLERS[name]?.(c, a.slice(colon + 1), e);
}

/** A key pressed anywhere on the page (not while typing or with a dialog open). */
function onKeyDown(c: Story, e: KeyboardEvent) {
  if (isTyping(e.target) || document.querySelector('[role="dialog"][data-state="open"]')) return;
  // Flight owns movement (including Space/Q/E/PgUp/PgDn); never pause, rotate tools or issue work while flying.
  // V always gets back to the map even with the pointer locked. Esc only releases the pointer.
  if (c.view === 'fly' && e.code !== 'KeyV') return;
  if (e.repeat && e.code === 'KeyV') return;
  if (PAN_KEYS[e.code]) {
    c.keys.current.add(e.code);
    e.preventDefault();
    return;
  }
  const a = keyAction(e, c.tool.kind === 'build', c.heightTool !== null, c.matTool !== null, c.tool.kind === 'select');
  if (!a) return;
  e.preventDefault();
  runKeyAction(c, a, e);
}

/** Listens on the window for the screen's lifetime; the handlers always see the latest screen state. */
export function useStoryKeys(c: Story) {
  const latest = useRef(c);
  useLayoutEffect(() => {
    latest.current = c;
  });
  useEffect(() => {
    const keys = latest.current.keys.current;
    const down = (e: KeyboardEvent) => onKeyDown(latest.current, e);
    const up = (e: KeyboardEvent) => keys.delete(e.code);
    const blur = () => keys.clear();
    window.addEventListener('keydown', down);
    window.addEventListener('keyup', up);
    window.addEventListener('blur', blur);
    return () => {
      window.removeEventListener('keydown', down);
      window.removeEventListener('keyup', up);
      window.removeEventListener('blur', blur);
    };
  }, []);
}
