/** The mock Design Studio (MSW): the shared in-memory studio from @mineclaw/contracts, its saves listed with the designs. */
import { createDesignerMock, type DesignSummary } from '@mineclaw/contracts';

/** Kept in sessionStorage so a reload reopens the session as the bot would (its db keeps sessions). */
const KEY = 'mineclaw.mock.designer';
export const designer = createDesignerMock(100, {
  load: () => {
    try {
      return globalThis.sessionStorage?.getItem(KEY) ?? null;
    } catch {
      return null;
    }
  },
  save: (json) => {
    try {
      globalThis.sessionStorage?.setItem(KEY, json);
    } catch {
      /* blocked storage: the mock forgets on reload */
    }
  },
});

/** Studio saves as Designs-list rows (newest first). */
export function savedDesigns(): DesignSummary[] {
  return designer.saved.map((d) => ({
    id: d.id,
    name: d.name,
    status: 'accepted',
    requester: d.requester,
    rev: d.revisions[0]?.rev ?? 1,
    revisions: Math.max(1, d.revisions.length),
    updated: d.updated,
    hasPicture: !!d.previewPng,
    version: d.version ?? null,
    kind: d.kind ?? null,
  }));
}
