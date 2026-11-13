/**
 * The web's version and update checker (`/api/system/*`, Nest-native). The checker reads GitHub releases of `Web:Updates:Repo`;
 * a systemd install applies an update through a root-owned path unit (the web only writes `<Dir>/request.json`), container
 * installs show the command to run. See docs/web/api.md §10.
 */
import { z } from 'zod';

/** `X.Y.Z` or `X.Y.Z-pre` (no leading `v`, no build metadata): the same check the root updater applies. */
export const RELEASE_VERSION_RE = /^[0-9]+\.[0-9]+\.[0-9]+(-[0-9A-Za-z.-]+)?$/;
export const ReleaseVersion = z.string().max(64).regex(RELEASE_VERSION_RE, 'not a release version (X.Y.Z[-pre])');

/** How this install was packaged (`MINECLAW_INSTALL_KIND`; unset = `manual`, e.g. dev or deploy-web.ps1). */
export const InstallKind = z.enum(['systemd', 'docker', 'helm', 'manual']);
export type InstallKind = z.infer<typeof InstallKind>;

/** GET /api/system/version (any signed-in user). */
export const SystemVersion = z.object({
  version: z.string().describe('MINECLAW_VERSION, else RELEASE.json `version`, else the api package.json'),
  commit: z.string().nullable(),
  built: z.string().nullable(),
  installKind: InstallKind,
});
export type SystemVersion = z.infer<typeof SystemVersion>;

/** Longest release-notes body passed to browsers (characters); longer notes end with a link to the release. */
export const RELEASE_NOTES_MAX = 16_000;

export const UpdateRelease = z.object({
  version: ReleaseVersion,
  name: z.string().max(200),
  notes: z.string().max(RELEASE_NOTES_MAX + 200).describe('GitHub release body (markdown), truncated'),
  notesTruncated: z.boolean(),
  url: z.string().max(500).describe('https link to the release page'),
  publishedAt: z.string().nullable(),
  prerelease: z.boolean(),
});
export type UpdateRelease = z.infer<typeof UpdateRelease>;

export const UpdateRunState = z.enum(['running', 'succeeded', 'failed', 'rolled-back']);
export type UpdateRunState = z.infer<typeof UpdateRunState>;

/** `<Dir>/status.json`, written by the root updater (`mineclaw-update run-request`). */
export const UpdateRunStatus = z.object({
  state: UpdateRunState,
  version: z.string().max(64).nullish(),
  from: z.string().max(64).nullish(),
  startedAt: z.string().max(64).nullish(),
  finishedAt: z.string().max(64).nullish(),
  message: z.string().max(2000).nullish(),
  log: z.array(z.string().max(500)).max(50).nullish(),
});
export type UpdateRunStatus = z.infer<typeof UpdateRunStatus>;

/** `<Dir>/request.json` while the root updater has not picked it up yet. */
export const UpdateRequestFile = z.object({
  version: ReleaseVersion,
  requestedBy: z.string().max(64),
  requestedAt: z.string().max(64),
});
export type UpdateRequestFile = z.infer<typeof UpdateRequestFile>;

/** `trigger`: an admin can start it here (systemd). `command`: run the shown command (docker, helm). `none`: manual install. */
export const UpdateMode = z.enum(['trigger', 'command', 'none']);
export type UpdateMode = z.infer<typeof UpdateMode>;

/** GET /api/system/update (any signed-in user); also the reply of both POSTs. */
export const SystemUpdateStatus = z.object({
  enabled: z.boolean(),
  current: z.string(),
  latest: UpdateRelease.nullable(),
  outdated: z.boolean(),
  checkedAt: z.string().nullable(),
  lastError: z.string().nullable(),
  installKind: InstallKind,
  update: z.object({
    mode: UpdateMode,
    /** The upgrade command (docker, helm) for `latest`. */
    command: z.string().optional(),
    /** systemd: the updater's last run (`status.json`), null when there is none. */
    status: UpdateRunStatus.nullable().optional(),
    /** systemd: a request the updater has not picked up yet. */
    request: UpdateRequestFile.nullable().optional(),
  }),
});
export type SystemUpdateStatus = z.infer<typeof SystemUpdateStatus>;

/** POST /api/system/update (admin): the version must be the latest known release. */
export const SystemUpdateRequest = z.object({ version: ReleaseVersion }).strict();
export type SystemUpdateRequest = z.infer<typeof SystemUpdateRequest>;

/** Release version order (semver precedence; a prerelease sorts before its release). Null when either is not X.Y.Z[-pre]. */
export function compareReleaseVersions(a: string, b: string): number | null {
  const pa = parseReleaseVersion(a);
  const pb = parseReleaseVersion(b);
  if (!pa || !pb) return null;
  for (let i = 0; i < 3; i++) if (pa.core[i] !== pb.core[i]) return pa.core[i]! < pb.core[i]! ? -1 : 1;
  if (pa.pre.length === 0 || pb.pre.length === 0) return pa.pre.length === pb.pre.length ? 0 : pa.pre.length === 0 ? 1 : -1;
  for (let i = 0; i < Math.max(pa.pre.length, pb.pre.length); i++) {
    const x = pa.pre[i];
    const y = pb.pre[i];
    if (x === undefined) return -1;
    if (y === undefined) return 1;
    if (x === y) continue;
    const nx = /^[0-9]+$/.test(x);
    const ny = /^[0-9]+$/.test(y);
    if (nx && ny) return Number(x) < Number(y) ? -1 : 1;
    if (nx !== ny) return nx ? -1 : 1;
    return x < y ? -1 : 1;
  }
  return 0;
}

/** `v1.2.3-rc.1+build` → core [1,2,3], pre ['rc','1']; null when it is not a release version. */
export function parseReleaseVersion(v: string): { core: [number, number, number]; pre: string[] } | null {
  const m = /^v?([0-9]+)\.([0-9]+)\.([0-9]+)(?:-([0-9A-Za-z.-]+))?(?:\+[0-9A-Za-z.-]+)?$/.exec(v.trim());
  if (!m) return null;
  return { core: [Number(m[1]), Number(m[2]), Number(m[3])], pre: m[4] ? m[4].split('.') : [] };
}
