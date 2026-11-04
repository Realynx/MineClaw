/**
 * `/story?design=N` (from the design pages): the design is picked up as a placement ghost on the map. Its block list comes
 * from the saved blueprint or build plan; else, when the account may design, from the Design Studio's newest revision.
 */
import { useEffect, useRef } from 'react';
import { api } from '@/lib/api/client';
import type { MapTool } from '../StoryMap';
import { designBlocks, designCells, mineCheck, type DesignBlock } from '../tools';
import type { Story } from './commands';
import type { Say } from './state';

interface DesignEnv {
  say: Say;
  can: Story['can'];
  setTool: (t: MapTool) => void;
}

/** The design's name (`design #N` when it can't be read). */
const designName = (id: number) =>
  api('designs.get', { params: { id } }).then(
    (d) => d.design.name,
    () => `design #${id}`,
  );

/** The blocks of the saved blueprint, else of the saved build plan. */
async function savedBlocks(id: number): Promise<DesignBlock[]> {
  const blocks = designBlocks(await api('blueprints.get', { params: { id } }).catch(() => null));
  if (blocks.length) return blocks;
  return designBlocks(await api('buildplans.get', { params: { id } }).catch(() => null));
}

/**
 * The Design Studio opens a design's newest revision as cells (with its dug cells: a basement goes under the clicked level),
 * converted to the server's version: a design drawn for the other one keeps what maps (substitutions said).
 */
async function studioSession(id: number) {
  const server = await api('designer.models')
    .then((m) => m.version)
    .catch(() => null);
  return api('designer.create', {
    body: { designId: id, ...(server === 'b1.8.1' || server === '1.12.2' ? { convertTo: server } : {}) },
  }).catch(() => null);
}

type StudioSession = Awaited<ReturnType<typeof studioSession>>;

/** Refused when a block of the design doesn't exist on this server's version. */
function compatRefusal(name: string, s: StudioSession): string | null {
  if (!s?.compat || s.compat.compatible) return null;
  const missing = s.compat.missing.map((m) => `${m.name} (${m.count})`).join(', ');
  return `${name} is drawn for ${s.compat.from}; ${s.compat.to} has no ${missing}: convert it in the Design Studio first`;
}

/** What the conversion substituted (empty when nothing). */
function convertedNote(s: StudioSession): string {
  if (!s?.compat?.substituted.length) return '';
  return ` (converted from ${s.compat.from}: ${s.compat.substituted.map((m) => `${m.count} ${m.name}`).join(', ')})`;
}

/** A mine design keeps its absolute y: it is refused when it would dig into the bedrock floor. */
function pickUpMine(env: DesignEnv, id: number, name: string, blocks: DesignBlock[], converted: string) {
  const m = mineCheck(blocks);
  if (m.refusal) return env.say(`${name}: ${m.refusal}`, 'warn');
  env.setTool({ kind: 'design', id, name, blocks, mine: { min: m.min, max: m.max } });
  env.say(
    `${name}: a mine at y ${m.min}..${m.max}, absolute: move to a spot and click to queue it (Q/E turns)${converted}`,
  );
}

/** Picks design `id` up as the map's design tool (or says why it can't). */
async function pickUpDesign(env: DesignEnv, id: number) {
  const name = await designName(id);
  let blocks = await savedBlocks(id);
  let converted = '';
  if (!blocks.length && env.can('design')) {
    const s = await studioSession(id);
    const refusal = compatRefusal(name, s);
    if (refusal) return env.say(refusal, 'warn');
    converted = convertedNote(s);
    blocks = designCells(s);
    if (s?.kind === 'mine' && blocks.length) return pickUpMine(env, id, name, blocks, converted);
  }
  if (!blocks.length) return env.say(`${name} has no block list the colony can build from`, 'warn');
  env.setTool({ kind: 'design', id, name, blocks });
  env.say(`${name}: move to a spot and click to queue it (Q/E turns)${converted}`);
}

/** Picks up `?design=N` once per value, when the colony is here. */
export function useDesignParam({ params, state, say, can, setTool }: Story) {
  const designParam = params.get('design');
  const designDone = useRef<string | null>(null);
  useEffect(() => {
    if (!designParam || !state || designDone.current === designParam) return;
    designDone.current = designParam;
    const id = Number(designParam);
    if (!Number.isInteger(id) || id < 1) return;
    void pickUpDesign({ say, can, setTool }, id);
  }, [designParam, state, say, can, setTool]);
}
