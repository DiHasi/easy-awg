import type { Node } from '~/types/api'

export type Tone = 'live' | 'success' | 'warning' | 'error' | 'muted' | 'standby'

/**
 * One word for where a node stands, in the order an operator needs it: a node that is down
 * says so even if the record still points at it, because that is the one fact that matters.
 */
export function describeNode(node: Node): { word: string, tone: Tone } {
  if (node.revoked) {
    return { word: 'revoked', tone: 'muted' }
  }
  if (node.status === 'down') {
    return { word: 'down', tone: 'error' }
  }
  if (node.isActive) {
    return { word: 'active', tone: 'live' }
  }
  if (node.status === 'provisioning') {
    return { word: 'provisioning', tone: 'muted' }
  }
  if (node.status === 'retired') {
    return { word: 'retired', tone: 'muted' }
  }
  if (!node.inSync) {
    return { word: 'converging', tone: 'warning' }
  }
  if (node.status === 'degraded') {
    return { word: 'degraded', tone: 'warning' }
  }
  return { word: 'ready', tone: 'standby' }
}

// Spelled out in full so Tailwind finds every class when it scans the source.
export const toneText: Record<Tone, string> = {
  live: 'text-live',
  success: 'text-success',
  warning: 'text-warning',
  error: 'text-error',
  muted: 'text-muted',
  standby: 'text-standby'
}

export const toneFill: Record<Tone, string> = {
  live: 'fill-live',
  success: 'fill-success',
  warning: 'fill-warning',
  error: 'fill-error',
  muted: 'fill-(--ui-text-muted)',
  standby: 'fill-standby'
}
