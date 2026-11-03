'use client';

import { LogIn } from 'lucide-react';
import { Dialog } from '@/components/ui/overlays';
import { Button } from '@/components/ui/primitives';

/** Shown when a request answers 401 after we were signed in (idle 12 h / absolute 7 d timeout). */
export function SessionExpired({ href }: { href: string }) {
  return (
    <Dialog
      open
      onOpenChange={() => undefined}
      title="Your session has ended"
      description="For safety, sessions end after 12 hours idle or 7 days in total. Sign in again to carry on where you were."
      size="sm"
      footer={
        <Button variant="primary" onClick={() => window.location.assign(href)}>
          <LogIn className="size-4" /> Sign in again
        </Button>
      }
    />
  );
}
