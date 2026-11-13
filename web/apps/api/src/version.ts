/**
 * The running version: env `MINECLAW_VERSION` (images set it) > `RELEASE.json` at the release root (release tarballs carry
 * `{version, commit, built}`; deploy-web.ps1 writes `{id, commit, built}` without a version) > the api package.json.
 * The api runs from `<root>/apps/api`, so the release root is three levels above this file (dist/ or src/).
 */
import { readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { InstallKind, type InstallKind as InstallKindT } from '@mineclaw/contracts';

export interface VersionInfo {
  version: string;
  commit: string | null;
  built: string | null;
}

export interface VersionSources {
  env?: NodeJS.ProcessEnv;
  releaseFile?: string;
  packageFile?: string;
}

const DEFAULT_RELEASE = fileURLToPath(new URL('../../../RELEASE.json', import.meta.url));
const DEFAULT_PACKAGE = fileURLToPath(new URL('../package.json', import.meta.url));

/** PowerShell's Set-Content may write a byte-order mark (U+FEFF) that JSON.parse rejects. */
export function stripBom(s: string): string {
  return s.charCodeAt(0) === 0xfeff ? s.slice(1) : s;
}

function readJson(path: string): Record<string, unknown> | null {
  try {
    const v = JSON.parse(stripBom(readFileSync(path, 'utf8'))) as unknown;
    return v && typeof v === 'object' && !Array.isArray(v) ? (v as Record<string, unknown>) : null;
  } catch {
    return null;
  }
}

function text(v: unknown, max = 80): string | null {
  return typeof v === 'string' && v.trim() && v.length <= max ? v.trim() : null;
}

export function readVersionInfo(src: VersionSources = {}): VersionInfo {
  const env = src.env ?? process.env;
  const release = readJson(src.releaseFile ?? DEFAULT_RELEASE);
  const pkg = readJson(src.packageFile ?? DEFAULT_PACKAGE);
  const version =
    text(env.MINECLAW_VERSION)?.replace(/^v/, '') ??
    text(release?.version)?.replace(/^v/, '') ??
    text(pkg?.version) ??
    '0.0.0';
  return { version, commit: text(release?.commit), built: text(release?.built) };
}

/** `MINECLAW_INSTALL_KIND`: systemd | docker | helm; anything else (or unset) is `manual`. */
export function installKindOf(env: NodeJS.ProcessEnv = process.env): InstallKindT {
  const parsed = InstallKind.safeParse((env.MINECLAW_INSTALL_KIND ?? '').trim().toLowerCase());
  return parsed.success ? parsed.data : 'manual';
}

/** Read once at startup. */
export const VERSION_INFO: VersionInfo = readVersionInfo();
export const VERSION: string = VERSION_INFO.version;
