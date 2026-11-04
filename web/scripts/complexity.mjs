#!/usr/bin/env node
// Cyclomatic-complexity report for the TypeScript web code and the bot's plain JS scripts. Report only: it never
// changes the lint gate (the rule is configured here, not in any eslint.config.js) and never edits code.
//
//   node web/scripts/complexity.mjs [--lang ts|js|all] [--format table|json] [--threshold 10] [--all] [--top N]
//                                   [--out file] [--fail-over N] [--only <path>]... [--exclude <path>]...
//
//   --only / --exclude take repo-relative path prefixes (a trailing * also matches a file-name prefix), e.g.
//   --only web/apps/web/src/features/story --only 'web/packages/contracts/src/story*'
//
//   ts: web/apps/web/src, web/apps/api/src, web/packages/*/src (*.ts, *.tsx; tests, fixtures and .d.ts excluded)
//   js: every .js under the repo's src/ (the SDK modules in src/MineClaw.Scripting/Sdk/js and Story's story_pawn.js),
//       parsed as plain ES2022+ scripts
//
// Counting is ESLint's built-in `complexity` rule (classic variant), wrapped only to get each function's full extent
// and a better name: 1 + if, ?:, &&, ||, ??, &&=, ||=, ??=, for, for-in, for-of, while, do, catch, each non-default
// `case`, each default parameter value, and each optional-chain link (?. and ?.()). Every function, method, arrow
// and class-field initializer is measured on its own.
// The JSON shape matches tools/Complexity (C#): { tool, language, convention, threshold, totals, byProject, functions }.
// Exit code 0 always, unless --fail-over N is given and some function exceeds N; 2 on bad usage.
import { readdirSync, statSync, writeFileSync } from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { ESLint } from 'eslint';
import { builtinRules } from 'eslint/use-at-your-own-risk';
import tseslint from 'typescript-eslint';

const WEB = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const REPO = path.resolve(WEB, '..');

const CONVENTION =
  'ESLint `complexity` (classic): 1 + if, ?:, &&, ||, ??, logical assignments, for, for-in, for-of, while, do, catch, ' +
  'non-default case, default parameter values, optional chaining (?. and ?.()). Each function/arrow/method measured on its own.';

const TS_IGNORES = [
  '**/node_modules/**',
  '**/dist/**',
  '**/*.d.ts',
  '**/*.test.ts',
  '**/*.test.tsx',
  '**/*.spec.ts',
  '**/*.spec.tsx',
  '**/__tests__/**',
  '**/fixtures/**',
];

const SKIP_DIRS = new Set(['bin', 'obj', 'node_modules', '.git']);

const toPosix = (p) => p.split(path.sep).join('/');

function findFiles(dir, ext) {
  return readdirSync(dir).flatMap((name) => {
    const full = path.join(dir, name);
    if (statSync(full).isDirectory()) return SKIP_DIRS.has(name) ? [] : findFiles(full, ext);
    return name.endsWith(ext) ? [full] : [];
  });
}

/** Each language: the ESLint base path, the files, the minimal parser config and how a file maps to a project. */
const LANGS = {
  ts: {
    language: 'typescript',
    cwd: WEB,
    patterns: () => ['apps/web/src/**/*.{ts,tsx}', 'apps/api/src/**/*.ts', 'packages/*/src/**/*.{ts,tsx}'],
    ignores: TS_IGNORES,
    config: {
      files: ['**/*.ts', '**/*.tsx'],
      languageOptions: { parser: tseslint.parser, parserOptions: { ecmaFeatures: { jsx: true } } },
    },
    projectOf: (rel) => rel.split('/').slice(0, 2).join('/'),
  },
  js: {
    language: 'javascript',
    cwd: REPO,
    patterns: () => findFiles(path.join(REPO, 'src'), '.js').map((f) => toPosix(path.relative(REPO, f))),
    config: {
      files: ['**/*.js'],
      languageOptions: {
        ecmaVersion: 'latest',
        sourceType: 'script',
        parserOptions: { ecmaFeatures: { globalReturn: true } },
      },
    },
    projectOf: (rel) => rel.split('/').slice(1, -1).join('/'),
  },
};

// ---------------------------------------------------------------------------------------------------------------
// mc/complexity: ESLint's own `complexity` rule (so the counting is exactly ESLint's) run with max 0 so it reports
// every function, re-reported with the function's full extent (the built-in reports only the head) and a descriptive
// name: anonymous functions get their variable / property / JSX prop / call site and the nearest named owner.
// ---------------------------------------------------------------------------------------------------------------
const complexityRule = builtinRules.get('complexity');
const spanRule = {
  meta: { ...complexityRule.meta, messages: { span: '{{json}}' } },
  create(context) {
    const report = (d) =>
      context.report({
        node: d.node,
        messageId: 'span',
        data: {
          json: JSON.stringify({ name: describe(d.node, d.data.name, context.sourceCode), cc: d.data.complexity }),
        },
      });
    return complexityRule.create(Object.create(context, { report: { value: report } }));
  },
};

const nameOfKey = (key) => (key?.type === 'Identifier' ? key.name : key?.type === 'Literal' ? String(key.value) : null);

const SITES = {
  VariableDeclarator: (p) => nameOfKey(p.id),
  Property: (p) => nameOfKey(p.key),
  MethodDefinition: (p) => nameOfKey(p.key),
  PropertyDefinition: (p) => nameOfKey(p.key),
  AssignmentExpression: (p, sc) => sc.getText(p.left).slice(0, 40),
  JSXExpressionContainer: (p) => (p.parent?.type === 'JSXAttribute' ? `${p.parent.name.name}={}` : null),
  CallExpression: (p, sc) => callSite(p, sc),
  NewExpression: (p, sc) => callSite(p, sc),
};

/** The name a function is known by: its id, or the variable/property/method/JSX prop it is assigned to, or its call site. */
function siteOf(node, sourceCode) {
  if (node.id?.name) return node.id.name;
  const site = node.parent && SITES[node.parent.type];
  return site ? site(node.parent, sourceCode) : null;
}

function callSite(call, sourceCode) {
  const callee = sourceCode.getText(call.callee).replace(/\s+/g, '').slice(0, 40);
  const first = call.arguments[0];
  const tag = first?.type === 'Literal' && typeof first.value === 'string' ? `'${first.value}'` : '';
  return `${callee}(${tag}) callback`;
}

/** e.g. "Arrow function useMemo() callback in StoryMap", "Method 'plan' in StorySim", "Function 'tileFor'". */
function describe(node, eslintName, sourceCode) {
  const site = siteOf(node, sourceCode);
  const name = site && !eslintName.includes(`'${site}'`) ? `${eslintName} ${site}` : eslintName;
  const owner = ownerOf(node, sourceCode);
  return owner ? `${name} in ${owner}` : name;
}

function ownerOf(node, sourceCode) {
  for (let a = node.parent; a; a = a.parent) {
    if (a.type === 'ClassDeclaration' || a.type === 'ClassExpression') return a.id?.name ?? null;
    const site = a.type.includes('Function') ? siteOf(a, sourceCode) : null;
    if (site) return site;
  }
  return null;
}

/** Lints one language with only mc/complexity on, so every function reports its complexity. */
async function measure(lang) {
  const eslint = new ESLint({
    cwd: lang.cwd,
    overrideConfigFile: true,
    overrideConfig: [
      { ignores: lang.ignores ?? [] },
      { ...lang.config, linterOptions: { noInlineConfig: true, reportUnusedDisableDirectives: 'off' } },
      {
        files: lang.config.files,
        plugins: { mc: { rules: { complexity: spanRule } } },
        rules: { 'mc/complexity': ['warn', 0] },
      },
    ],
    errorOnUnmatchedPattern: false,
  });
  const results = (await eslint.lintFiles(lang.patterns())).filter((r) => !r.messages.some(isIgnoredNotice));
  return {
    files: results.map((r) => toPosix(path.relative(REPO, r.filePath))),
    functions: results.flatMap((r) => functionsOf(r, lang)),
  };
}

const isIgnoredNotice = (m) => m.ruleId === null && !m.fatal && /ignored/i.test(m.message);

function functionsOf(result, lang) {
  const file = toPosix(path.relative(REPO, result.filePath));
  for (const m of result.messages.filter((x) => x.fatal)) console.error(`${file}:${m.line}: parse error: ${m.message}`);
  const project = lang.projectOf(toPosix(path.relative(lang.cwd, result.filePath)));
  return result.messages
    .filter((m) => m.ruleId === 'mc/complexity')
    .map((m) => {
      const { name, cc } = JSON.parse(m.message);
      const endLine = m.endLine ?? m.line;
      return {
        file,
        line: m.line,
        endLine,
        member: name,
        kind: kindOf(name),
        complexity: cc,
        loc: endLine - m.line + 1,
        project,
      };
    });
}

function kindOf(name) {
  const lower = name.toLowerCase();
  const kinds = [
    'arrow function',
    'method',
    'getter',
    'setter',
    'constructor',
    'class field initializer',
    'class static block',
  ];
  return kinds.find((k) => lower.includes(k)) ?? 'function';
}

// ---------------------------------------------------------------------------------------------------------------
// Report
// ---------------------------------------------------------------------------------------------------------------
function rollup(project, fns, threshold) {
  const over = fns.filter((f) => f.complexity > threshold);
  return {
    project,
    functions: fns.length,
    over: over.length,
    excess: over.reduce((s, f) => s + f.complexity - threshold, 0),
    max: fns.reduce((m, f) => Math.max(m, f.complexity), 0),
  };
}

const byFile = (a, b) => (a.file < b.file ? -1 : a.file > b.file ? 1 : 0);

function buildReport(language, fileCount, measured, opts) {
  const { threshold } = opts;
  const byProject = [...Map.groupBy(measured, (f) => f.project)]
    .map(([p, fns]) => rollup(p, fns, threshold))
    .sort((a, b) => b.excess - a.excess || a.project.localeCompare(b.project));
  const { functions, over, excess } = rollup('', measured, threshold);
  const listed = (opts.all ? measured : measured.filter((f) => f.complexity > threshold)).sort(
    (a, b) => b.complexity - a.complexity || byFile(a, b) || a.line - b.line,
  );
  return {
    tool: 'web/scripts/complexity.mjs',
    language,
    convention: CONVENTION,
    threshold,
    totals: { files: fileCount, functions, over, excess },
    byProject,
    functions: listed,
  };
}

const col = (v, w) => String(v).padStart(w);

function table(report, top) {
  const rows = [`${col('CC', 4)} ${col('LOC', 5)}  ${'location'.padEnd(70)} member`];
  for (const f of report.functions.slice(0, top)) {
    rows.push(`${col(f.complexity, 4)} ${col(f.loc, 5)}  ${`${f.file}:${f.line}`.padEnd(70)} ${f.member}`);
  }
  rows.push('', `${col('over', 5)} ${col('excess', 7)} ${col('max', 5)} ${col('funcs', 6)}  project`);
  for (const p of report.byProject) {
    rows.push(`${col(p.over, 5)} ${col(p.excess, 7)} ${col(p.max, 5)} ${col(p.functions, 6)}  ${p.project}`);
  }
  rows.push('', `${report.language} totals: ${JSON.stringify(report.totals)}  (threshold ${report.threshold})`, '');
  return rows.join('\n');
}

// ---------------------------------------------------------------------------------------------------------------
// CLI
// ---------------------------------------------------------------------------------------------------------------
const trimSlash = (p) => p.replaceAll('\\', '/').replace(/\/+$/, '');
const isUnder = (file, prefix) =>
  file === prefix || file.startsWith(`${prefix}/`) || (prefix.endsWith('*') && file.startsWith(prefix.slice(0, -1)));

/** --only / --exclude: repo-relative path prefixes (a trailing * matches a file-name prefix, e.g. web/packages/contracts/src/story*). */
const selected = (opts) => (file) =>
  (opts.only.length === 0 || opts.only.some((p) => isUnder(file, p))) && !opts.exclude.some((p) => isUnder(file, p));

function parseArgs(argv) {
  const opts = { lang: 'all', format: 'table', threshold: 10, all: false, top: Infinity, out: null, failOver: null };
  opts.only = [];
  opts.exclude = [];
  const valued = {
    '--only': (v) => opts.only.push(trimSlash(v)),
    '--exclude': (v) => opts.exclude.push(trimSlash(v)),
    '--lang': (v) => (opts.lang = v),
    '--format': (v) => (opts.format = v),
    '--threshold': (v) => (opts.threshold = Number(v)),
    '--top': (v) => (opts.top = Number(v)),
    '--out': (v) => (opts.out = v),
    '--fail-over': (v) => (opts.failOver = Number(v)),
  };
  for (let i = 0; i < argv.length; i++) {
    if (argv[i] === '--all') opts.all = true;
    else if (valued[argv[i]] && i + 1 < argv.length) valued[argv[i]](argv[++i]);
    else throw new Error(`unknown or incomplete option '${argv[i]}'`);
  }
  if (!['ts', 'js', 'all'].includes(opts.lang)) throw new Error('--lang must be ts, js or all');
  if (!['table', 'json'].includes(opts.format)) throw new Error('--format must be table or json');
  return opts;
}

async function main() {
  let opts;
  try {
    opts = parseArgs(process.argv.slice(2));
  } catch (e) {
    console.error(e.message);
    return 2;
  }
  const langs = opts.lang === 'all' ? [LANGS.ts, LANGS.js] : [LANGS[opts.lang]];
  const reports = [];
  for (const lang of langs) {
    const { files, functions } = await measure(lang);
    const keep = selected(opts);
    const kept = files.filter(keep);
    reports.push(
      buildReport(
        lang.language,
        kept.length,
        functions.filter((f) => keep(f.file)),
        opts,
      ),
    );
  }
  const json = () => JSON.stringify(reports.length === 1 ? reports[0] : reports, null, 2) + '\n';
  const text = opts.format === 'json' ? json() : reports.map((r) => table(r, opts.top)).join('\n');
  if (opts.out) writeFileSync(opts.out, text);
  else process.stdout.write(text);
  const worst = Math.max(0, ...reports.flatMap((r) => r.byProject.map((p) => p.max)));
  return opts.failOver !== null && worst > opts.failOver ? 1 : 0;
}

process.exitCode = await main();
