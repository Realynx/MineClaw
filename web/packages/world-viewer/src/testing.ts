// @mineclaw/world-viewer/testing: encoders matching the C# writers and a fixture-backed fake WorldSource, for tests, stories
// and demos without a bot.

export { encodeMcwc, encodeSnapshot, encodeLight, encodeColumn, lightSections, type EncodeEntry, type LightSection } from './wire/encode';
export { snapshotHash, lightHash, combineHash } from './wire/hash';
export { createFixtureSource, FixtureWorld, surface, tunnelY, PLAN, type FixtureOptions } from './fixtures/world';
export { InlineMeshHost } from './mesh/host';
export { MeshEngine } from './mesh/engine';
export { meshSection } from './mesh/mesher';
export { buildStateTable } from './mesh/states';
export { ColumnStore } from './mesh/columns';
export { WorldCache, planBatches, indexRect } from './world/cache';
export { WorldSync } from './world/sync';
export { EntityStore } from './world/entities';
export { BlockDeltaFeed } from './world/deltas';
export { SectionManager } from './render/sections';
