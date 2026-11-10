/**
 * The mock bridge's live hive (snake_case, the bridge's wire form), shaped like a busy production hive so tests see real load:
 * main, one Opus hive (maid_chan_109) with a crew of subhive bodies, one of main's own subhive bodies and a script-only body,
 * plus a crowd of players, mobs and drops that move every entities frame. `MOCK_BODIES` (env) sets the body count (default 12).
 */
type Obj = Record<string, unknown>;

export interface MockBody {
  name: string;
  short: string;
  kind: 'main' | 'hive' | 'subhive' | 'script';
  owner: string | null;
  brain: string | null;
  dim: number;
  home: [number, number, number];
  task: string;
  held: string;
  script: { name: string; version: number; line: number } | null;
}

const MAIN = 'maid_chan';
const HIVE = 'maid_chan_109';
const SUB_IDS = [
  102, 114, 124, 131, 135, 141, 146, 152, 158, 163, 167, 172, 178, 183, 189, 194, 199, 205, 211, 216, 222, 228,
];
const SUB_TASKS: Array<[string, string, string]> = [
  ['chopping oak', 'chop_crew', 'iron axe'],
  ['hauling logs', 'chop_crew', 'oak log'],
  ['replanting saplings', 'chop_crew', 'oak sapling'],
  ['mining cobble', 'cobble_loop', 'stone pickaxe'],
];

export function mockBodies(count = Number(process.env.MOCK_BODIES) || 12): MockBody[] {
  const n = Math.max(4, Math.min(SUB_IDS.length + 4, count));
  const list: MockBody[] = [
    {
      name: MAIN,
      short: 'main',
      kind: 'main',
      owner: null,
      brain: 'opus',
      dim: 0,
      home: [4.5, 64, 6.5],
      task: 'mining cobble',
      held: 'stone pickaxe',
      script: { name: 'cobble_loop', version: 4, line: 12 },
    },
    {
      name: HIVE,
      short: '109',
      kind: 'hive',
      owner: MAIN,
      brain: 'opus',
      dim: 0,
      home: [-18.5, 64, 30.5],
      task: 'tree farm with the crew',
      held: 'iron axe',
      script: null,
    },
    {
      name: 'maid_chan_68',
      short: '68',
      kind: 'subhive',
      owner: MAIN,
      brain: null,
      dim: 0,
      home: [14.5, 64, -9.5],
      task: 'tending the wheat farm',
      held: 'iron hoe',
      script: { name: 'farm_tend', version: 4, line: 8 },
    },
    {
      name: 'maid_chan_70',
      short: '70',
      kind: 'script',
      owner: MAIN,
      brain: null,
      dim: 0,
      home: [9.5, 64, -4.5],
      task: 'sorting the storage room',
      held: 'chest',
      script: { name: 'sort_chests', version: 2, line: 30 },
    },
  ];
  for (let i = 0; i < n - 4; i++) {
    const id = SUB_IDS[i]!;
    const [task, script, held] = SUB_TASKS[i % SUB_TASKS.length]!;
    list.push({
      name: `maid_chan_${id}`,
      short: String(id),
      kind: 'subhive',
      owner: HIVE,
      brain: null,
      dim: 0,
      home: [-30 + (i % 6) * 4.5, 64, 22 + Math.floor(i / 6) * 5],
      task,
      held,
      script: { name: script, version: 3, line: 5 + ((i * 7) % 30) },
    });
  }
  return list;
}

const iso = (ms = Date.now()) => new Date(ms).toISOString().replace('Z', '+00:00');
const r2 = (v: number) => Math.round(v * 100) / 100;

const NAMES = ['poofimafox', 'kiwi', 'robinchan', 'lante'];
const MOBS: Array<[string, number]> = [
  ['pig', 90],
  ['cow', 92],
  ['sheep', 91],
  ['chicken', 93],
  ['zombie', 54],
  ['skeleton', 51],
  ['spider', 52],
];
const DROPS: Array<[string, number]> = [
  ['cobblestone', 4],
  ['oak_log', 17],
  ['dirt', 3],
  ['oak_sapling', 6],
  ['wheat', 296],
];

/** Live state of the mock hive: vitals, poses, scripts and a crowd that moves each step. */
export class MockHive {
  readonly bodies: MockBody[];
  private step = 0;

  constructor(count?: number) {
    this.bodies = mockBodies(count);
  }

  names(): string[] {
    return this.bodies.map((b) => b.name);
  }

  find(key: string): MockBody | undefined {
    return this.bodies.find((b) => b.name === key || b.short === key);
  }

  advance(): void {
    this.step++;
  }

  private vitals(b: MockBody, i: number): { hp: number; food: number } {
    const t = Math.floor(this.step / 10);
    return {
      hp: Math.max(1, 20 - ((t + i) % 5) - (b.kind === 'subhive' && i % 7 === 0 ? 12 : 0)),
      food: 20 - ((t + i * 3) % 9),
    };
  }

  pose(b: MockBody, i: number): { x: number; y: number; z: number; yaw: number } {
    const a = this.step / 25 + i;
    return {
      x: r2(b.home[0] + Math.sin(a) * 2.5),
      y: b.home[1],
      z: r2(b.home[2] + Math.cos(a) * 2.5),
      yaw: Math.round(((a * 57.3) % 360) + 360) % 360,
    };
  }

  private scriptOf(b: MockBody, i: number): Obj | null {
    if (!b.script) return null;
    const line = b.script.line + ((Math.floor(this.step / 5) + i) % 6);
    return { name: b.script.name, status: 'running', line, paused: false, version: b.script.version };
  }

  private brainState(b: MockBody): string {
    if (b.kind === 'script') return 'script-only';
    if (b.kind === 'subhive')
      return `driven by ${b.owner === MAIN ? 'main' : (this.find(b.owner ?? '')?.short ?? b.owner)}`;
    return b.script ? `asleep (script ${b.script.name} 34%)` : 'awake';
  }

  /** `/api/status` (and the `bots` frame). `base` = the fixture's row of main (usage etc.). */
  status(base: Obj | undefined): Obj {
    const at = iso();
    return {
      at,
      main: MAIN,
      bots: this.bodies.map((b, i) => {
        const p = this.pose(b, i);
        const v = this.vitals(b, i);
        return {
          ...(b.kind === 'main' && base ? base : {}),
          name: b.name,
          short: b.short,
          status: 'online',
          at,
          pos: `${Math.floor(p.x)},${Math.floor(p.y)},${Math.floor(p.z)}`,
          dim: b.dim,
          hp: v.hp,
          food: v.food,
          task: b.task,
          brain: this.brainState(b),
          budget_mode: 'Normal',
          chat: (b.kind === 'main' && base?.chat) || [],
          kind: b.kind,
          owner: b.owner,
          script: this.scriptOf(b, i),
          thought: undefined,
          header: undefined,
        };
      }),
      hive: {
        list: this.bodies.filter((b) => b.kind === 'hive' || b.kind === 'script').map((b) => ({ name: b.name })),
        clones_today_usd: 0.42,
        clones_month_usd: 6.3,
        slots: this.bodies
          .filter((b) => b.kind === 'hive' || b.kind === 'script')
          .map((b) => ({
            name: b.name,
            number: Number(b.short),
            state: 'running',
            connected: true,
            task: b.task,
            pending: 0,
            deaths: 0,
          })),
      },
      claims: [{ owner: MAIN, what: 'cobble gen' }],
    };
  }

  /** `/api/bots`. */
  tiles(): Obj {
    return {
      at: iso(),
      bots: this.bodies.map((b, i) => {
        const p = this.pose(b, i);
        const v = this.vitals(b, i);
        return {
          name: b.name,
          short: b.short,
          main: b.kind === 'main',
          dim: b.dim,
          x: p.x,
          y: p.y,
          z: p.z,
          hp: v.hp,
          food: v.food,
          job: b.task,
          brain: this.brainState(b),
          age_seconds: 0.4,
          claims: b.kind === 'main' ? ['cobble gen'] : [],
          kind: b.kind,
          owner: b.owner,
          script: this.scriptOf(b, i),
          online: true,
        };
      }),
    };
  }

  /** Story Mode's AI bodies (public camelCase; the story mock converts to snake_case). */
  aiBodies(): Obj[] {
    return this.bodies
      .filter((b) => b.kind !== 'script')
      .map((b, i) => {
        const p = this.pose(b, i);
        const v = this.vitals(b, i);
        return {
          name: b.name,
          kind: b.kind,
          owner: b.owner,
          online: true,
          hp: v.hp,
          food: v.food,
          pos: [p.x, p.y, p.z],
          dim: b.dim,
          task: b.task,
          brainState: this.brainState(b),
          ...(b.script ? { script: b.script.name } : {}),
        };
      });
  }

  /** `/api/hive/tree`. */
  tree(): Obj {
    const node = (b: MockBody): Obj => ({
      id: b.short,
      name: b.name,
      kind: b.kind,
      brain: b.brain,
      owner: b.owner,
      state: 'running',
      task: b.script && b.kind === 'script' ? b.script.name : b.task,
      children: this.bodies.filter((c) => c.owner === b.name).map(node),
    });
    return { ...node(this.bodies[0]!), id: 'main' };
  }

  /** `/api/viewer/now?bot=`. */
  now(key: string, thought: { text: string; thinking: string; tool: string; args: string } | null): Obj | null {
    const i = this.bodies.findIndex((b) => b.name === key || b.short === key);
    const b = this.bodies[i];
    if (!b) return null;
    const p = this.pose(b, i);
    const v = this.vitals(b, i);
    const at = iso();
    const ownBrain = b.kind === 'main' || b.kind === 'hive';
    return {
      bot: b.name,
      at,
      brain_state: this.brainState(b),
      thought: ownBrain && thought ? { text: thought.text, thinking: thought.thinking, at } : null,
      tool: ownBrain && thought ? { name: thought.tool, args_short: thought.args, at } : null,
      task: b.task,
      script: this.scriptOf(b, i),
      body: { hp: v.hp, food: v.food, pos: [p.x, p.y, p.z], dim: b.dim, held: b.held },
      kind: b.kind,
      owner: b.owner,
    };
  }

  /** `/api/bodies/scripts`. */
  scripts(): Obj {
    return {
      at: iso(),
      bodies: this.bodies
        .map((b, i) => ({ b, s: this.scriptOf(b, i) }))
        .filter((x) => x.s)
        .map(({ b, s }) => ({
          body: b.name,
          short: b.short,
          script: s!.name,
          status: s!.status,
          line: s!.line,
          paused: false,
          version: s!.version,
          owner: b.owner,
        })),
    };
  }

  /** `/api/world/entities`: every body plus a crowd of players, mobs and drops (≈ 10 per body). */
  entities(rev: number): Obj {
    const bots = this.bodies.map((b, i) => {
      const p = this.pose(b, i);
      const v = this.vitals(b, i);
      return {
        name: b.name,
        short: b.short,
        main: b.kind === 'main',
        dim: b.dim,
        x: p.x,
        y: p.y,
        z: p.z,
        yaw: p.yaw,
        pitch: 10,
        pose_age: 0,
        hp: v.hp,
        food: v.food,
        job: b.task,
        brain: this.brainState(b),
        held: b.held.replace(/ /g, '_'),
        claims: [],
        live: true,
        age_seconds: 0.2,
        kind: b.kind,
        owner: b.owner,
      };
    });
    const entities: Obj[] = [];
    const crowd = this.bodies.length * 10;
    for (let k = 0; k < crowd; k++) {
      const a = this.step / 15 + k * 1.7;
      const cx = ((k * 37) % 80) - 40,
        cz = ((k * 53) % 80) - 30;
      const x = r2(cx + Math.sin(a) * 1.5),
        z = r2(cz + Math.cos(a) * 1.5);
      if (k % 10 === 0) {
        entities.push({
          kind: 'player',
          name: NAMES[(k / 10) % NAMES.length],
          dim: 0,
          x,
          y: 64,
          z,
          id: 5000 + k,
          yaw: (k * 40) % 360,
          head_yaw: (k * 40) % 360,
          flags: 0,
          by: MAIN,
        });
      } else if (k % 3 === 0) {
        const [name, item] = DROPS[k % DROPS.length]!;
        entities.push({
          kind: 'item',
          name,
          dim: 0,
          x,
          y: 64.1,
          z,
          id: 5000 + k,
          item,
          damage: 0,
          count: 1 + (k % 4),
          vx: 0,
          vy: 0,
          vz: 0,
          age: 30,
          by: MAIN,
        });
      } else {
        const [name, type] = MOBS[k % MOBS.length]!;
        entities.push({
          kind: 'mob',
          name,
          dim: 0,
          x,
          y: 64,
          z,
          id: 5000 + k,
          type,
          yaw: (a * 57.3) % 360,
          head_yaw: (a * 57.3) % 360,
          vx: 0.01,
          vy: 0,
          vz: 0.01,
          age: 120,
          by: MAIN,
        });
      }
    }
    return { at: Date.now(), rev, full: false, bots, entities, removed: [] };
  }

  /** A `blocks` frame: one crew body breaks or places a log near its spot (every other frame). */
  blocks(rev: number): Obj {
    const b = this.bodies[4 + (this.step % Math.max(1, this.bodies.length - 4))] ?? this.bodies[0]!;
    const x = Math.floor(b.home[0]) + 1,
      z = Math.floor(b.home[2]) + 1,
      y = 65;
    const placed = Math.floor(this.step / 2) % 2 === 0;
    return {
      at: Date.now(),
      rev,
      full: false,
      more: false,
      blocks: { '0': [[x, y, z, placed ? 17 : 0, 0]] },
      columns: [],
    };
  }
}
