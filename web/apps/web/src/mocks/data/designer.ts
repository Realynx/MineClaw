/** The mock Design Studio (MSW): the shared in-memory studio from @mineclaw/contracts, its saves listed with the designs. */
import { createDesignerMock, type DesignSummary } from '@mineclaw/contracts';

export const designer = createDesignerMock(100);

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
    hasPicture: false,
  }));
}
