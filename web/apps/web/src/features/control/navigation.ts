/** Return only to the controller or a Live Hive entry, never an external URL. */
export function controlReturnTo(value: string | null | undefined): string {
  if (!value?.startsWith('/') || value.startsWith('//') || value.includes('\\')) return '/story';
  try {
    const url = new URL(value, 'http://mineclaw.local');
    if (url.pathname !== '/story' && url.pathname !== '/live' && !url.pathname.startsWith('/live/')) return '/story';
    return url.pathname + url.search;
  } catch {
    return '/story';
  }
}

export function takeControlHref(pawn: string, returnTo: string): string {
  return `/control?pawn=${encodeURIComponent(pawn)}&returnTo=${encodeURIComponent(controlReturnTo(returnTo))}`;
}
