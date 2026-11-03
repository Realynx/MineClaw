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
  const latency =
    status?.latencyMs !== null && status?.latencyMs !== undefined ? ` · ${Math.round(status.latencyMs)} ms` : '';
  if (!status) return { level: 'live', label: 'Live', detail: 'Connected.' };
  if (status.upstream === 'down') {
    return { level: 'offline', label: 'Bridge down', detail: status.message ?? 'The web server cannot reach the bridge.' };
  }
  if (status.upstream === 'reconnecting' || status.upstream === 'connecting') {
    return { level: 'reconnecting', label: 'Reconnecting', detail: status.message ?? 'The bridge is reconnecting.' };
  }
  if (status.bot === 'down') {
    return { level: 'degraded', label: 'Bot offline', detail: status.message ?? 'The bot is not reachable. Memory pages still work.' };
  }
  return { level: 'live', label: 'Live', detail: `Connected${latency}.` };
}
