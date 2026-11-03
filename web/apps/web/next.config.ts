import path from 'node:path';
import { fileURLToPath } from 'node:url';
import type { NextConfig } from 'next';

const here = path.dirname(fileURLToPath(import.meta.url));
/** The npm-workspaces root (web/): standalone output traces workspace packages from here. */
const workspaceRoot = path.join(here, '..', '..');

/**
 * Where `/api/*` goes in dev. In production Nest sits in front (0.0.0.0:8080) and proxies everything that is not `/api` to
 * this server, so the rewrite is only a dev convenience (Nest on :4000; it has a mock-bridge mode).
 */
const apiUrl = process.env.MINECLAW_API_URL ?? 'http://127.0.0.1:4000';

const config: NextConfig = {
  output: 'standalone',
  outputFileTracingRoot: workspaceRoot,
  turbopack: { root: workspaceRoot },
  // `npm run build:mock` / `dev:mock` build into .next-mock so a mock build never replaces the real one.
  distDir: process.env.NEXT_DIST_DIR ?? '.next',
  reactStrictMode: true,
  poweredByHeader: false,
  devIndicators: false,
  // Next 16 writes an AGENTS.md into the app on `next dev`; this repo keeps agent guidance elsewhere.
  agentRules: false,
  rewrites: () => Promise.resolve([{ source: '/api/:path*', destination: `${apiUrl}/api/:path*` }]),
  // The RAPTOR tree page was folded into Memory activity (2026-11-03); old links keep their query (node, hits, q).
  redirects: () => Promise.resolve([{ source: '/raptor', destination: '/memory-activity', permanent: false }]),
};

export default config;
