import { useState, type ReactNode } from 'react';

/** Open lazily, then preserve a conversation's draft and pending requests while other tabs are used. */
export function RetainedPanel({ active, children }: { active: boolean; children: ReactNode }) {
  const [visited, setVisited] = useState(active);
  if (active && !visited) setVisited(true);
  return visited || active ? <div hidden={!active}>{children}</div> : null;
}
