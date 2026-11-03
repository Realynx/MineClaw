'use client';

import { Suspense } from 'react';
import { StudioPage } from '@/features/studio/StudioPage';

/** /designs/studio (a new design), /designs/studio?id=N (open design N), ?session=S (reopen a session). */
export default function DesignStudioRoute() {
  return (
    <Suspense fallback={null}>
      <StudioPage />
    </Suspense>
  );
}
