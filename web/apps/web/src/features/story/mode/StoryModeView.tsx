/**
 * Story Mode's picture: the map with its gestures wired to the actions, and the HUD over it (controls, full screen, the
 * colonist bar and tool options, notices, help, the new-colony card, the inspector / tab windows / material bar dock, the
 * toast, the tab bar, chat) plus the confirm dialogs and the context menu.
 */
import { isWaterTool } from '@mineclaw/contracts';
import { Maximize2, Minimize2 } from 'lucide-react';
import { ChatOverlay } from '@/features/world/InWorld';
import { cn } from '@/lib/utils';
import { AttackConfirm, PickupDialog } from '../assign/GearActions';
import { draftedOf, rememberConfirm } from '../assign/gear';
import { MaterialBar } from '../MaterialBar';
import { clickBody, EMPTY_SELECTION, slowestSpeed } from '../model';
import {
  ColonistBar,
  ContextMenu,
  ControlCluster,
  HelpOverlay,
  OptionsBar,
  panel,
  TimeSlider,
  Toast,
} from '../StoryHud';
import { StoryMap } from '../StoryMap';
import { StoryNotices } from '../StoryNotices';
import { NewColony, TabBar } from '../StoryPanels';
import { clampLayers, nextLevel } from '../tools';
import { WaterBar, WaterConfirm } from '../WaterTools';
import {
  addPawns,
  attack,
  bringOnline,
  chatTargets,
  createColony,
  formation,
  gear,
  onCondition,
  onJump,
  removePawn,
  retryAll,
  selectOnMap,
  sendChat,
  showBody,
  stalledCount,
} from './actions';
import type { Story } from './commands';
import { openContext, pickMenu } from './menu';
import { releaseTool } from './release';
import { selectionInspector } from './SelectionInspector';
import type { V3 } from './state';
import { PreserveContext } from '../zones/PreservePanel';
import { colonyZones, preserveWriter, tabPanel } from './tabPanel';
import { queueWater, usePendingWaterHolo } from './water';

type Props = { c: Story };

/** Selects every visible pawn (or AI body) of the kind double-clicked. */
function selectSame(c: Story, kind: 'pawn' | 'ai', visible: string[]) {
  c.setSelection({
    ...EMPTY_SELECTION,
    bodies: visible.filter((n) =>
      kind === 'pawn' ? c.state?.pawns.some((p) => p.name === n) : c.state?.ai?.some((x) => x.name === n),
    ),
  });
}

/** The colony map: every gesture goes to an action. */
export function StoryMapSlot({ c }: Props) {
  const { map, keys, state, selection, tool, matHover, build, noColony, split, setTool, say } = c;
  const holo = usePendingWaterHolo(c.pendingWater);
  return (
    <StoryMap
      ref={map}
      state={state}
      selection={selection}
      tool={tool}
      build={matHover ? { ...build, material: matHover.material } : build}
      previewHold={matHover !== null}
      holoPreview={holo}
      mineFilter={c.mineFilter}
      heightMode={c.heightMode}
      layers={c.layers}
      level={c.lv}
      cutY={c.cutY}
      view={c.view}
      keys={keys}
      home={(state?.home as V3 | undefined) ?? null}
      settle={noColony}
      player={c.me.player ?? null}
      onCentre={noColony || c.tab === 'colony' ? c.setCentre : undefined}
      onSettled={c.setSettledY}
      onSelect={(sel) => selectOnMap(c, sel)}
      onSelectSame={(kind, visible) => selectSame(c, kind, visible)}
      onTool={(r, shift) => void releaseTool(c, r, shift)}
      onContext={(target, at) => openContext(c, target, at)}
      onFormation={(moves, queue, ignored) => void formation(c, moves, queue, ignored)}
      onDropTool={() => setTool({ kind: 'select' })}
      onCancelled={() => say('Cancelled')}
      attackMode={draftedOf(state, split.pawns).length > 0}
      clock={c.clock}
    />
  );
}

/** Bottom right: pause, speed, the view, the level, x-ray, help and the time of day. */
function Controls({ c }: Props) {
  const { state } = c;
  if (!state) return null;
  return (
    <ControlCluster
      clock={c.clock}
      name={state.name ?? c.id ?? ''}
      paused={!!state.paused}
      canWrite={c.canWrite}
      onPause={c.togglePause}
      speed={state.options?.speed ?? 'normal'}
      speedEffective={slowestSpeed(state.pawns)}
      onSpeed={c.setSpeed}
      view={c.view}
      onView={() => c.setView((v) => (v === 'top' ? 'angled' : 'top'))}
      onFly={() => c.setView((v) => (v === 'fly' ? 'top' : 'fly'))}
      level={c.lv}
      ground={c.ground}
      height={c.build.height}
      xray={c.xrayOn}
      onLevel={(dir) => c.setLevel(nextLevel(c.lv, c.build.height, dir, false))}
      onXray={() => c.setXray(c.xrayOn ? 'off' : 'on')}
      onHelp={() => c.setHelp((h) => !h)}
    >
      <TimeSlider
        clock={c.clock}
        control={state.timeControl ?? c.colonies.data?.timeControl}
        canWrite={c.canWrite}
        onSet={c.setTime}
        onSetRange={c.setTimeRange}
      />
    </ControlCluster>
  );
}

function FullscreenButton({ c }: Props) {
  const { on, toggle } = c.fullscreen;
  return (
    <button
      type="button"
      onClick={toggle}
      aria-label={on ? 'Exit full screen' : 'Full screen'}
      aria-pressed={on}
      title={on ? 'Exit full screen (Esc or Shift+F)' : 'Full screen (Shift+F)'}
      className={cn(panel, 'pointer-events-auto grid size-8 place-items-center hover:bg-surface-2')}
      data-testid="story-fullscreen"
    >
      {on ? <Minimize2 className="size-4" /> : <Maximize2 className="size-4" />}
    </button>
  );
}

/** The colonist bar: select, reorder, jump to, bring online, add and remove pawns (managing needs an active colony). */
function ColonistBarSlot({ c }: Props) {
  const { state } = c;
  if (!state) return null;
  const manage = c.canWrite && state.active !== false;
  return (
    <ColonistBar
      bodies={c.bodies}
      selected={c.selection.bodies}
      order={c.order}
      onOrder={c.setOrder}
      onClick={(name, mods) => {
        c.setSelection((s) => clickBody(s, name, mods));
        if (!mods.shift && !mods.ctrl) showBody(c, name);
      }}
      onBox={(names, add) =>
        c.setSelection((s) => ({
          ...EMPTY_SELECTION,
          bodies: add ? [...new Set([...s.bodies, ...names])] : names,
        }))
      }
      onJump={(name) => {
        c.setSelection({ ...EMPTY_SELECTION, bodies: [name] });
        c.focusBody(name);
      }}
      onBringAll={
        manage
          ? // Refused by the cap: the Colony tab shows why, with the explicit "Use regulars' seats".
            () => void bringOnline(c, null).then((b) => b.length && c.setTab('colony'))
          : null
      }
      pending={state.seats?.pending ?? 0}
      pendingReason={state.seats?.waitingReason ?? null}
      onAdd={manage ? (n) => addPawns(c, n) : null}
      onRemove={manage ? (name) => removePawn(c, name) : null}
    />
  );
}

/** The tool in hand's options (filter, height mode, Layers, Retry all). */
function ToolOptions({ c }: Props) {
  const { heightTool } = c;
  return (
    <OptionsBar
      tool={c.tool}
      build={c.build}
      mineFilter={c.mineFilter}
      onMineFilter={c.setMineFilter}
      heightMode={c.heightMode}
      onHeightMode={(m) => heightTool && c.setHeightModes((cur) => ({ ...cur, [heightTool]: m }))}
      layers={c.layers}
      onLayers={(n) => c.setLayers(clampLayers(n))}
      level={c.lv}
      stalled={stalledCount(c)}
      onRetryAll={() => retryAll(c)}
      onClose={() => c.setTool({ kind: 'select' })}
    />
  );
}

/** Top centre: the colonist bar, the tool options, the chest-pick hint. */
function TopBar({ c }: Props) {
  return (
    <div className="absolute inset-x-12 top-2 flex flex-col items-center gap-1.5">
      <ColonistBarSlot c={c} />
      <ToolOptions c={c} />
      {c.pickHint ? (
        <div className={cn(panel, 'pointer-events-auto px-3 py-1.5 text-xs font-medium')}>
          Click a chest on the map to store the bill&apos;s products there (Esc cancels)
        </div>
      ) : null}
    </div>
  );
}

/** No colony yet: pan to where it should settle, then found it. */
function NoColonyCard({ c }: Props) {
  return (
    <div className="absolute inset-0 grid place-items-center">
      <div className={cn(panel, 'pointer-events-auto w-96 space-y-2 p-4')}>
        <h2 className="text-base font-semibold">Story Mode</h2>
        <p className="text-xs text-muted">
          Run a colony of pawns: script-only bodies that take work by your priorities and schedule. Pan to where they
          should settle, then found it.
        </p>
        <NewColony canWrite={c.canWrite} at={c.centre} onCreate={(name, n) => void createColony(c, name, n)} />
      </div>
    </div>
  );
}

/** The water placement waiting for its confirm. */
function WaterConfirmSlot({ c }: Props) {
  const { pendingWater } = c;
  if (!pendingWater) return null;
  return (
    <WaterConfirm
      pending={pendingWater}
      onQueue={() => void queueWater(c, pendingWater)}
      onCancel={() => {
        c.setPendingWater(null);
        c.say('Water placement cancelled');
      }}
    />
  );
}

/** The universal material selector, docked above the tab bar for every structure / furniture tool (water has its own bar). */
function BuildBar({ c }: Props) {
  const { tool } = c;
  if (tool.kind !== 'build') return null;
  if (isWaterTool(tool.id))
    return (
      <WaterBar
        toolId={tool.id}
        build={c.build}
        onBuild={(b) => c.setBuild((x) => ({ ...x, ...b }))}
        onClose={() => c.setTool({ kind: 'select' })}
      />
    );
  return (
    <MaterialBar
      toolId={tool.id}
      options={c.matShown}
      current={c.matCurrent}
      recent={c.matRecent}
      query={c.matQuery}
      onQuery={c.setMatQuery}
      onPick={(o) => c.chooseMaterial(o)}
      onHover={c.setMatHover}
      build={c.build}
      onBuild={(b) => c.setBuild((x) => ({ ...x, ...b }))}
      onClose={() => c.setTool({ kind: 'select' })}
    />
  );
}

/** Chat with the selected AI bodies. */
function ChatSlot({ c }: Props) {
  if (!c.state) return null;
  return (
    <div className="pointer-events-auto">
      <ChatOverlay
        open={c.chat}
        targets={chatTargets(c)}
        marker={null}
        canChat={c.canChat}
        results={null}
        onClose={() => c.setChat(false)}
        onSend={(text) => sendChat(c, text)}
      />
    </div>
  );
}

/** The badges on the tab bar: unfinished builds and directives in flight. */
function TabBarSlot({ c }: Props) {
  const queued = (c.state?.builds ?? []).filter((b) => b.status !== 'done').length;
  const directivesLive = (c.state?.directives ?? []).filter(
    (d) => d.status === 'sent' || d.status === 'acknowledged',
  ).length;
  return <TabBar open={c.tab} onOpen={c.setTab} badge={{ queue: queued, directives: directivesLive }} />;
}

/** Everything over the map. */
export function StoryHudLayer({ c }: Props) {
  return (
    <div className="pointer-events-none absolute inset-0">
      {/* top: controls (left), the colonist bar (centre), alerts (right) */}
      <div className="absolute right-2 bottom-2">
        <Controls c={c} />
      </div>
      <div className="absolute top-2 right-2">
        <FullscreenButton c={c} />
      </div>
      <TopBar c={c} />
      <div className="absolute top-[5.75rem] right-2">
        <StoryNotices
          state={c.state}
          colony={c.id}
          toast={c.toast}
          onJump={(t) => onJump(c, t)}
          onCondition={(cond) => onCondition(c, cond)}
        />
      </div>

      {/* middle overlays */}
      {c.help ? (
        <div className="absolute top-24 left-1/2 -translate-x-1/2">
          <HelpOverlay onClose={() => c.setHelp(false)} />
        </div>
      ) : null}
      {c.noColony ? <NoColonyCard c={c} /> : null}

      {/* bottom: inspector + the open tab's window, the tab bar, the toast, the chat */}
      <div className="absolute right-[22rem] bottom-9 left-2 flex flex-col items-start gap-2">
        <PreserveContext.Provider value={preserveWriter(c)}>
          <div
            className={
              c.tab === 'colony'
                ? 'pointer-events-auto flex max-w-full items-end gap-2 overflow-x-auto pb-1 [&>section]:shrink-0'
                : 'flex max-w-[calc(100vw-1rem)] items-end gap-2'
            }
            data-testid="story-panel-strip"
          >
            {selectionInspector(c)}
            {tabPanel(c)}
            {colonyZones(c)}
          </div>
        </PreserveContext.Provider>
        <WaterConfirmSlot c={c} />
        <BuildBar c={c} />
      </div>
      <div className="absolute bottom-12 left-1/2 -translate-x-1/2">
        <Toast text={c.toast?.text ?? null} tone={c.toast?.tone} />
      </div>
      <div className="absolute bottom-0 left-2">
        <TabBarSlot c={c} />
      </div>
      <ChatSlot c={c} />
    </div>
  );
}

/** The attack confirm, the pick-up chooser and the context menu. */
export function StoryDialogs({ c }: Props) {
  const { confirmAttack, pickupFor, menu } = c;
  return (
    <>
      <AttackConfirm
        who={confirmAttack?.who ?? null}
        pawns={confirmAttack?.pawns.length ?? 0}
        onCancel={() => c.setConfirmAttack(null)}
        onConfirm={() => {
          c.setConfirmAttack(null);
          if (!confirmAttack) return;
          rememberConfirm(confirmAttack.who);
          void attack(c, confirmAttack.who, confirmAttack.pawns, confirmAttack.queue, true);
        }}
      />
      <PickupDialog
        open={!!pickupFor}
        state={c.state}
        n={pickupFor?.length ?? 0}
        onClose={() => c.setPickupFor(null)}
        onPick={(item, count) => {
          c.setPickupFor(null);
          void gear(c, 'pickup', pickupFor ?? [], item, count);
        }}
      />
      {menu ? (
        <ContextMenu
          at={menu.at}
          title={menu.title}
          items={menu.items}
          onPick={(it, q) => void pickMenu(c, it, q)}
          onClose={() => c.setMenu(null)}
        />
      ) : null}
    </>
  );
}
