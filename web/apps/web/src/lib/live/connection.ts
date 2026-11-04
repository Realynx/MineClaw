/**
 * The single connection indicator's state: the browser↔Nest stream state combined with Nest's own `status` event
 * (upstream bridge state and bot reachability). Brief hiccups are smoothed so the indicator does not flicker (explorer-spec
 * §17.6–7): a non-open stream only shows as "reconnecting" after a grace period.
 */
import type { LiveStatus } from '@mineclaw/contracts';
import type { StreamState } from './stream';

export type ConnectionLevel = 'live' | 'connecting' | 'reconnecting' | 'degraded' | 'offline';

export interface ConnectionView {
  level: ConnectionLevel;
  label: string;
  detail: string;
}

export function connectionView(stream: StreamState, status: LiveStatus | undefined, sinceChangeMs: number): ConnectionView {
  const grace = sinceChangeMs < 3000;
  if (stream === 'idle' || stream === 'connecting') {
    return { level: 'connecting', label: 'Connecting', detail: 'Opening the live stream…' };
  }
  if (stream === 'reconnecting' || stream === 'closed') {
    if (grace) return liveFromStatus(status);
    return stream === 'closed'
      ? { level: 'offline', label: 'Offline', detail: 'The live stream is closed.' }
      : { level: 'reconnecting', label: 'Reconnecting', detail: 'Lost the live stream; retrying.' };
  }
  return liveFromStatus(status);
}

function liveFromStatus(status: LiveStatus | undefined): ConnectionView {
  if (!status) return { level: 'live', label: 'Live', detail: 'Connected.' };
  const issue = statusIssue(status);
  if (issue) return { level: issue.level, label: issue.label, detail: status.message ?? issue.detail };
  return { level: 'live', label: 'Live', detail: `Connected${latencyText(status.latencyMs)}.` };
}

/** What is wrong upstream (the bridge down or reconnecting, the bot down), with its default detail; null when all is well. */
function statusIssue(status: LiveStatus): ConnectionView | null {
  if (status.upstream === 'down') return { level: 'offline', label: 'Bridge down', detail: 'The web server cannot reach the bridge.' };
  if (status.upstream === 'reconnecting' || status.upstream === 'connecting')
    return { level: 'reconnecting', label: 'Reconnecting', detail: 'The bridge is reconnecting.' };
  if (status.bot === 'down') return { level: 'degraded', label: 'Bot offline', detail: 'The bot is not reachable. Memory pages still work.' };
  return null;
}

const latencyText = (ms: number | null | undefined) => (ms !== null && ms !== undefined ? ` · ${Math.round(ms)} ms` : '');
