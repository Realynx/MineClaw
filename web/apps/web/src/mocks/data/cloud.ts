/**
 * The memory cloud in mock mode: the synthetic RAPTOR tree's 480 leaves (same ids as the graph, the heat and the access stream)
 * padded with 2520 cloud-only leaves under the same layer-1 summaries, laid out by the shared generator the Nest mock bridge
 * uses too. 3000 points in 8 clusters (the L2 summaries), like a few days of a real bot.
 */
import type { MemoryCloudResponse, RaptorNodeDetail } from '@mineclaw/contracts';
import { memoryCloudMock } from '@mineclaw/contracts/testing';
import { STATE_KEY, cloudTree, extraLeafDetail } from './raptor';

export const EXTRA_FROM = 100_000;
/** Cloud-only leaves on top of the tree's 480. The perf check raises it through localStorage `mcx-mock-cloud-extra`. */
function extraLeaves(): number {
  try {
    const v = Number(globalThis.localStorage?.getItem('mcx-mock-cloud-extra'));
    return Number.isInteger(v) && v > 0 && v <= 200_000 ? v : 2520;
  } catch {
    return 2520;
  }
}

let cached: MemoryCloudResponse | null = null;

export function memoryCloud(): MemoryCloudResponse {
  cached ??= memoryCloudMock(cloudTree(), { extraLeaves: extraLeaves(), extraFrom: EXTRA_FROM, seed: 23, stateKey: STATE_KEY });
  return cached;
}

/** Detail for a cloud-only leaf (ids from EXTRA_FROM). */
export function extraDetail(id: number): RaptorNodeDetail | null {
  if (id < EXTRA_FROM) return null;
  const c = memoryCloud();
  const i = c.points.id.indexOf(id);
  if (i < 0) return null;
  return extraLeafDetail(id, c.points.parents[i]![0]!, c.points.title[i]!, c.kinds[c.points.kind[i]!] ?? 'fact');
}
