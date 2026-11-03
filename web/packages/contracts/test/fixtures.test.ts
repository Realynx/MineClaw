import { describe, expect, it } from 'vitest';
import type { z } from 'zod';
import * as C from '../src/index.js';
import { loadFixture } from './helpers.js';

/** fixture key → schema the public (camelCase) form must satisfy. */
const cases: Record<'db' | 'tools' | 'live', Record<string, z.ZodType>> = {
  db: {
    overview: C.Overview,
    memoryKinds: C.MemoryKindsResponse,
    memoryPage: C.MemoryPage,
    memoryDetailClaim: C.MemoryDetail,
    memoryDetailChest: C.MemoryDetail,
    search: C.SearchResponse,
    raptorNode: C.RaptorNodeDetail,
    raptorGraph: C.RaptorGraph,
    raptorLeaves: C.RaptorLeavesResponse,
    personality: C.PersonalityResponse,
  },
  tools: {
    shardPacks: C.ShardPacksResponse,
    shardPreview: C.ShardPreview,
    shardImportPreview: C.ShardImportPreviewResponse,
    shardImportResult: C.ShardImportResult,
    scriptLibrary: C.ScriptLibraryResponse,
    scriptDetail: C.ScriptVersionDetail,
    scriptDiff: C.ScriptDiff,
    designDetail: C.DesignDetail,
    designDiff: C.DesignDiff,
    editSchema: C.EditSchemaResponse,
    memoryEditResult: C.MemoryEditResult,
    rebuildJob: C.RebuildJob,
    adminAudit: C.AdminAuditResponse,
    compaction: C.ActivityCompactionReport,
    webAudit: C.WebAuditPage,
    me: C.Me,
  },
  live: {
    bots: C.BotsResponse,
    status: C.StatusResponse,
    events: C.EventsResponse,
    hiveTree: C.HiveTreeNode,
    locate: C.LocateResult,
    worldEntities: C.WorldEntities,
    worldBlockEntities: C.WorldBlockEntities,
    worldOverlays: C.WorldOverlays,
    controller: C.BodyControllerInfo,
    brainContext: C.BrainContextPage,
    brainTurn: C.BrainTurnDetail,
    visionList: C.VisionList,
    bodyScripts: C.LiveScriptsResponse,
    bodyScript: C.BodyScriptReply,
    feed: C.FeedResponse,
    now: C.BotNow,
    nowStringPos: C.BotNow,
    chatAccepted: C.ChatAccepted,
    memoryActivity: C.MemoryActivityResponse,
    memoryHeat: C.MemoryHeatResponse,
  },
};

describe.each(Object.entries(cases))('fixtures/%s.json', (file, schemas) => {
  const data = loadFixture(file as 'db' | 'tools' | 'live');
  it.each(Object.entries(schemas))('%s parses after toCamel', (key, schema) => {
    expect(data[key], `fixture ${key} missing`).toBeDefined();
    const result = schema.safeParse(C.toCamel(data[key]));
    if (!result.success) throw new Error(`${key}: ${JSON.stringify(result.error.issues, null, 2)}`);
  });
});

describe('fixture specifics', () => {
  const live = loadFixture('live');

  it('equipment slots are [id, meta] or [id, meta, rgb] (a dyed item)', () => {
    const e = { kind: 'mob', dim: 0, x: 0, y: 64, z: 0 };
    const ok = C.WorldEntity.safeParse({ ...e, equipment: { head: [298, 0, 0x3366cc], chest: [299, 0], feet: null } });
    expect(ok.success).toBe(true);
    expect(ok.data?.equipment?.head).toEqual([298, 0, 0x3366cc]);
    expect(C.WorldEntity.safeParse({ ...e, equipment: { head: [298, 0, 1, 2] } }).success).toBe(false);
  });

  it('equipment slots may end in a {glint} flags object, after the optional dye', () => {
    const e = { kind: 'player', dim: 0, x: 0, y: 64, z: 0 };
    const ok = C.WorldEntity.safeParse({ ...e, equipment: { main: [276, 0, { glint: true }], feet: [301, 0, 0x3366cc, { glint: true }], head: [298, 0] } });
    expect(ok.success).toBe(true);
    expect(C.equipmentGlints(ok.data?.equipment?.main)).toBe(true);
    expect(C.equipmentGlints(ok.data?.equipment?.feet)).toBe(true);
    expect(C.equipmentGlints(ok.data?.equipment?.head)).toBe(false);
    expect(ok.data?.equipment?.feet?.[2]).toBe(0x3366cc);
    // The object only ever comes last.
    expect(C.WorldEntity.safeParse({ ...e, equipment: { main: [276, { glint: true }, 0] } }).success).toBe(false);
  });

  it('carries the last looks: glint, carried block, shoulder parrots, stuck arrows, the golem rose', () => {
    const w = C.WorldEntities.parse(C.toCamel(live.worldEntities));
    const fox = w.entities.find((x) => x.id === 77)!;
    expect(fox.shoulders).toEqual({ left: 'red_blue', right: 'grey' });
    expect(fox.arrows).toBe(3);
    expect(w.entities.find((x) => x.id === 3302)?.glint).toBe(true);
    expect(w.entities.find((x) => x.id === 88)?.carried).toEqual([12, 1]);
    expect(w.entities.find((x) => x.id === 89)?.look).toBe('holding_rose');
    expect(C.equipmentGlints(w.bots[0]?.equipment?.main)).toBe(true);
  });

  it('sign rich text keeps the plain lines beside the styled runs', () => {
    const b = C.WorldBlockEntities.parse(C.toCamel(live.worldBlockEntities));
    const sign = b.columns[0]!.entities[0]!;
    expect(sign.lines?.[0]).toBe('Foxie');
    expect(sign.rich?.[0]?.[0]).toEqual({ text: 'Fox', color: 'red', bold: true });
    expect(sign.rich?.[2]?.[0]?.obfuscated).toBe(true);
    expect(sign.rich?.[3]).toEqual([]);
  });
  const db = loadFixture('db');
  const tools = loadFixture('tools');

  it('keeps world column i64 hashes exact', () => {
    const idx = C.WorldIndex.parse(C.toCamel(C.parseBridgeJson(live.worldIndexRaw as string)));
    expect(idx.columns[0]?.hash).toBe('-7046029254386353131');
    expect(idx.columns[1]?.hash).toBe('42');
    expect(C.hashKey(idx.columns[0]!.hash)).toBe('-7046029254386353131');
  });

  it('carries script params/args/blackboard opaque', () => {
    const reply = C.BodyScriptReply.parse(C.toCamel(live.bodyScript));
    expect(reply.snapshot?.params).toEqual({ n: 80, store_at: 'home' });
    expect(reply.snapshot?.args).toEqual({ max_items: 64 });
    expect(reply.snapshot?.machine?.blackboard).toEqual({ last_chest: [10, 64, 3], mined_total: 640 });
    expect(reply.snapshot?.checkpointsPassed).toBe(2);
  });

  it('reads NaN doubles', () => {
    const o = C.Overview.parse(C.toCamel(db.overview));
    expect(Number.isNaN(C.toNumber(o.raptor?.layers[2]?.avgChildren))).toBe(true);
    const d = C.ScriptVersionDetail.parse(C.toCamel(tools.scriptDetail));
    expect(Number.isNaN(C.toNumber(d.script.successRate))).toBe(true);
  });

  it('discriminates memory records by kind', () => {
    const d = C.MemoryDetail.parse(C.toCamel(db.memoryDetailChest));
    expect(d.record.kind).toBe('chests');
    if (d.record.kind === 'chests') expect(d.record.contents[0]?.count).toBe(640);
  });

  it('parses both BotNow position forms', () => {
    const a = C.BotNow.parse(C.toCamel(live.now));
    const b = C.BotNow.parse(C.toCamel(live.nowStringPos));
    expect(C.parsePos(a.body?.pos)).toEqual([4.5, 64, 6.5]);
    expect(C.parsePos(b.body?.pos)).toEqual([12, 70, -3]);
  });

  it('memory heat lines up with graph node ids', () => {
    const graph = C.RaptorGraph.parse(C.toCamel(db.raptorGraph));
    const heat = C.MemoryHeatResponse.parse(C.toCamel(live.memoryHeat));
    expect(heat.stateKey).toBe(graph.stateKey);
    const ids = new Set(graph.nodes.map((n) => n.id));
    expect(heat.nodes.some((n) => ids.has(n.nodeId))).toBe(true);
    expect(heat.nodes[2]?.brains[0]?.evicted).toBe(true);
  });
});
