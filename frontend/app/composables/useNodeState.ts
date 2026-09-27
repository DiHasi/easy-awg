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
  if (node.status === 'down') {
    return { word: 'Down', tone: 'error' }
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
export const toneBadge: Record<Tone, string> = {
  live: 'bg-live/10 text-live ring-1 ring-inset ring-live/30',
  success: 'bg-success/10 text-success',
  warning: 'bg-warning/10 text-warning',
  error: 'bg-error/10 text-error',
  muted: 'bg-elevated text-muted',
  standby: 'bg-elevated text-toned'
}
