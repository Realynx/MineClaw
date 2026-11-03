'use client';

import { Suspense } from 'react';
import { StoryMode } from '@/features/story/StoryMode';

/** Story Mode: run a colony of script-only pawns from a top-down map (docs/web/story-mode.md). */
export default function StoryPage() {
  return (
    <Suspense fallback={null}>
      <StoryMode />
    </Suspense>
  );
}
