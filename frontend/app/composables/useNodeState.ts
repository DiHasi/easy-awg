import type { Node } from '~/types/api'

export type Tone = 'live' | 'success' | 'warning' | 'error' | 'muted' | 'standby'

/**
 * One word for where a node stands, in the order an operator needs it: a node that is down says
 * so even if the record still points at it, because that is the one fact that matters.
 */
export function describeNode(node: Node): { word: string, tone: Tone } {
  if (node.revoked) {
    return { word: 'Revoked', tone: 'muted' }
  }
  if (node.status === 'down' || node.health === 'down') {
    return { word: 'Down', tone: 'error' }
  }
  // Running and unreachable from where clients are. Said before "Active" for the same reason as
  // down: the record pointing here is exactly what makes it urgent.
  if (node.health === 'blocked') {
    return { word: 'Blocked', tone: 'error' }
  }
  if (node.health === 'silent') {
    return { word: 'Silent', tone: 'warning' }
  }
  // Running the key pair from before the last rotation, so every current config fails on it. Its
  // own word for itself is "healthy" and it is one poll from being right, but until then no
  // traffic may be sent here - said before "Active" because the record pointing at it is the
  // worst version of this, and kept apart from "Updating" because the two are not the same news.
  if (!node.onCurrentIdentity) {
    return { word: 'Previous identity', tone: 'error' }
  }
  if (node.isActive) {
    return { word: 'Active', tone: 'live' }
  }
  if (node.status === 'provisioning') {
    return { word: 'Setting up', tone: 'muted' }
  }
  if (node.status === 'retired') {
    return { word: 'Retired', tone: 'muted' }
  }
  if (!node.inSync) {
    return { word: 'Updating', tone: 'warning' }
  }
  if (node.status === 'degraded') {
    return { word: 'Degraded', tone: 'warning' }
  }
  return { word: 'Standby', tone: 'standby' }
}

// Spelled out in full so Tailwind finds every class when it scans the source.
/**
 * What a node is carrying, in one line.
 *
 * Never the `live` tone: that marks the path traffic actually takes, and a node still holding
 * people after the record moved away from it is the opposite of that claim - it is the problem.
 * A warning is exactly that case, and the case where nothing has vouched for the number lately.
 */
export function describePeerLoad(node: Node): { text: string, tone: Tone, peers: number } {
  const peers = node.peersOnline + node.peersIdle
  const parts: string[] = []

  if (node.peersOnline > 0) {
    parts.push(`${node.peersOnline} connected here`)
  }
  if (node.peersIdle > 0) {
    parts.push(`${node.peersIdle} pointed here, silent`)
  }

  // A zero on a node nothing has heard from must not read as "safe to power off", so the line
  // says where the number came from instead of stating it flat.
  const stale = node.health === 'silent' || node.health === 'unknown'
  const text = parts.length > 0
    ? parts.join(' · ') + (stale ? ', as of its last report' : '')
    : stale ? 'nothing reported lately' : 'nobody connected right now'

  const tone: Tone = (!node.isActive && node.peersOnline > 0) || (stale && peers > 0) ? 'warning' : 'muted'
  return { text, tone, peers }
}

export const toneBadge: Record<Tone, string> = {
  live: 'bg-live/10 text-live ring-1 ring-inset ring-live/30',
  success: 'bg-success/10 text-success',
  warning: 'bg-warning/10 text-warning',
  error: 'bg-error/10 text-error',
  muted: 'bg-elevated text-muted',
  standby: 'bg-elevated text-toned'
}
