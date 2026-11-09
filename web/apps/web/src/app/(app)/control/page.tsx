'use client';

import { Suspense } from 'react';
import { useSearchParams } from 'next/navigation';
import { useAuth } from '@/lib/auth';
import { WorldHost } from '@/features/world/integration';
import { PawnControlView } from '@/features/control/PawnControlView';
import { controlReturnTo } from '@/features/control/navigation';

function ControlPage() {
  const params = useSearchParams(), { can } = useAuth();
  const pawn = params.get('pawn');
  if (!can('story')) return <p className="p-6">You need colony control access to take over a pawn.</p>;
  if (!pawn) return <p className="p-6">Select a pawn in Live Hive or the Controller, then choose Take over.</p>;
  return <WorldHost><PawnControlView key={pawn} body={pawn} returnTo={controlReturnTo(params.get('returnTo'))} /></WorldHost>;
}

export default function Page() {
  return <Suspense fallback={<p className="p-6">Opening pawn controls…</p>}><ControlPage /></Suspense>;
}
