'use client';

/**
 * A redstone design's simulation in the studio page: the controller (./RedstoneSimController) tied to the page's canvas,
 * its world (where the simulated states are shown), the viewer's scene (the layer over the circuit), the animation frame
 * and the pointer. Any other kind of design gets an inert result, so the page calls this whatever it has open.
 *
 * The page itself only follows `live` and `failure`, which change rarely; the panel and the debugger follow the
 * controller's snapshot on their own, so a running simulation never re-renders the whole studio.
 */
import type { Pos } from '@mineclaw/redstone';
import type { AimTarget, WorldViewerHandle } from '@mineclaw/world-viewer';
import { useCallback, useEffect, useMemo, useRef, useState, useSyncExternalStore, type RefObject } from 'react';
import { plotMode } from '../canvas';
import type { Dir, Version } from '../facing';
import type { Studio } from '../studio';
import type * as ViewerMod from '../studio-viewer';
import type { StudioWorld } from '../studioWorld';
import { authoringStoreOf, bindRedstoneAuthoring } from './authoring';
import { aimedCell, bindSimPointer } from './redstoneInput';
import { RedstoneSimController } from './RedstoneSimController';
import type { SimControllerApi } from './simController.types';

export interface RedstoneSim {
  /** The simulation controller; null when the design is not a redstone one. */
  api: SimControllerApi | null;
  /** A simulation is paused or running: the pointer cannot edit the canvas, and clicks go to the circuit. */
  live: boolean;
  /** Why the simulator could not build or run the design. */
  failure: string | null;
  /** The cell the inspector shows. */
  selected: Pos | null;
  inspect: (at: Pos) => void;
  /** The cell under the pointer now. */
  aimed: () => Pos | null;
  /** Brings a cell into view. */
  jump: (at: Pos) => void;
}

const noStore = () => () => undefined;
/** How far from a cell the camera stops when the debugger jumps to it. */
const JUMP_DISTANCE = 12;

export function useRedstoneSim(p: {
  /** The design is a redstone one. */
  enabled: boolean;
  studio: Studio;
  version: Version;
  world: StudioWorld;
  mod: Pick<typeof ViewerMod, 'PowerOverlay' | 'createSimulation'>;
  viewer: RefObject<WorldViewerHandle | null>;
  stage: RefObject<HTMLElement | null>;
  /** The viewer is up: its scene can take the layer over the circuit. */
  ready: boolean;
  /** The viewer's frame rate (the display rate backs off when it drops). */
  fps: number;
  aim: AimTarget | null;
  /** The way the camera looks: the power numbers turn to read upright from there. */
  look: Dir;
}): RedstoneSim {
  const { enabled, studio, version, world, mod, viewer, stage, ready } = p;
  const power = useMemo(() => (enabled ? new mod.PowerOverlay() : null), [enabled, mod]);
  const controller = useMemo(
    () =>
      enabled
        ? new RedstoneSimController({
            studio,
            version,
            create: mod.createSimulation,
            display: { cells: (cells) => world.setDisplay(cells), marks: (marks) => power?.set(marks) },
          })
        : null,
    [enabled, studio, version, mod, world, power],
  );

  // The saved probes, breakpoints, start levels and settings go in before anything else, and every change goes out.
  useEffect(
    () => (controller ? bindRedstoneAuthoring(authoringStoreOf(studio), controller) : undefined),
    [controller, studio],
  );
  useEffect(() => controller?.connect(), [controller]);
  useEffect(() => {
    if (!controller) return;
    let raf = requestAnimationFrame(function loop() {
      controller.frame();
      raf = requestAnimationFrame(loop);
    });
    return () => cancelAnimationFrame(raf);
  }, [controller]);
  useEffect(() => controller?.setFps(p.fps), [controller, p.fps]);

  // The layer over the circuit joins the viewer's scene once it is up.
  useEffect(() => {
    const scene = ready ? viewer.current?.engine()?.runtime.scene : null;
    if (!power || !scene) return;
    scene.add(power.group);
    return () => {
      power.group.removeFromParent();
    };
  }, [power, ready, viewer]);
  useEffect(() => () => power?.dispose(), [power]);
  useEffect(() => power?.setFacing(p.look), [power, p.look]);

  const subscribe = useCallback((fn: () => void) => controller?.subscribe(fn) ?? noStore(), [controller]);
  const isLive = useCallback(() => controller?.live ?? false, [controller]);
  const live = useSyncExternalStore(subscribe, isLive, isLive);
  const getFailure = useCallback(() => controller?.failure ?? null, [controller]);
  const failure = useSyncExternalStore(subscribe, getFailure, getFailure);

  const [selected, setSelected] = useState<Pos | null>(null);
  const inspect = useCallback(
    (at: Pos) =>
      setSelected((old) =>
        old && old[0] === at[0] && old[1] === at[1] && old[2] === at[2] ? old : [at[0], at[1], at[2]],
      ),
    [],
  );
  const aim = useRef(p.aim);
  useEffect(() => {
    aim.current = p.aim;
  });
  const aimed = useCallback(() => aimedCell(aim.current), []);
  const jump = useCallback(
    (at: Pos) => viewer.current?.engine()?.focus(at[0], at[1] + plotMode().baseY, at[2], { distance: JUMP_DISTANCE }),
    [viewer],
  );

  // While the simulation is live a click in the world is the circuit's, before the studio's own tools see it.
  useEffect(() => {
    const el = stage.current;
    if (!controller || !el) return;
    return bindSimPointer(el, {
      live: () => controller.live,
      aim: async () => aimedCell((await viewer.current?.engine()?.aimNow()) ?? null),
      click: (at) => controller.click(at),
      inspect,
    });
  }, [controller, stage, viewer, inspect]);

  return useMemo(
    () => ({ api: controller, live, failure, selected, inspect, aimed, jump }),
    [controller, live, failure, selected, inspect, aimed, jump],
  );
}
