import { PageBody } from '@/components/shell/Shell';
import { Skeleton } from '@/components/ui/primitives';

/** Replace the previous live page while a route loads, releasing its renderer and world requests immediately. */
export default function LoadingPage() {
  return (
    <PageBody>
      <div role="status" aria-live="polite" className="space-y-5">
        <p className="text-sm text-muted">Opening page…</p>
        <Skeleton className="h-8 w-48" />
        <Skeleton className="h-48 w-full" />
      </div>
    </PageBody>
  );
}
