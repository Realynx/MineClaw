'use client';

import { Gamepad2 } from 'lucide-react';
import Link from 'next/link';
import { useRouter } from 'next/navigation';
import { useEffect, useState } from 'react';
import { Button } from '@/components/ui/primitives';
import { pawnNumber } from '@/features/story/pawnNumber';
import { cn } from '@/lib/utils';
import { controlReturnTo, takeControlHref } from './navigation';
import { beginTakeoverCapture } from './captureHandoff';

/** Enter direct control from either pawn inspector; the destination acquires the control session. */
export function TakeControlButton({
  pawn,
  canControl,
  online,
  returnTo = '/story',
  className,
}: {
  pawn: string;
  canControl: boolean;
  online: boolean;
  returnTo?: string;
  className?: string;
}) {
  const router = useRouter();
  const [back, setBack] = useState(() => controlReturnTo(returnTo));
  useEffect(() => {
    const path = window.location.pathname;
    if (path === '/story' || path === '/live' || path.startsWith('/live/'))
      setBack(controlReturnTo(path + window.location.search));
    else setBack(controlReturnTo(returnTo));
  }, [returnTo]);
  const label = `Take control of pawn ${pawnNumber(pawn) || pawn}`;
  const content = <><Gamepad2 className="size-3.5" aria-hidden /> Take control</>;
  if (!canControl || !online) return (
    <Button size="sm" disabled className={className} aria-label={label}
      title={!canControl ? 'Story permission is required to control a pawn' : 'Bring this pawn online to take control'}>
      {content}
    </Button>
  );
  return (
    <Link href={takeControlHref(pawn, back)} aria-label={label}
      onClick={(event) => {
        if (event.button !== 0 || event.metaKey || event.ctrlKey || event.shiftKey || event.altKey) return;
        event.preventDefault();
        beginTakeoverCapture(pawn, () => router.push(takeControlHref(pawn, back)));
      }}
      className={cn('inline-flex h-7 shrink-0 items-center gap-1.5 rounded-md border border-border bg-surface px-2.5 text-xs font-medium text-fg shadow-card hover:border-border-strong hover:bg-surface-2 focus-visible:outline-2', className)}>
      {content}
    </Link>
  );
}
