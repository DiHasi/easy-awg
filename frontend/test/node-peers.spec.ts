import { describe, expect, it } from 'vitest'
import { describePeerLoad } from '~/composables/useNodeState'
import { nodeOf } from '~/composables/usePeerNodes'
import type { Client, ClientStats, Node, NodeHealth } from '~/types/api'

/**
 * What a node card says it is carrying, and which node a peer's row names.
 *
 * Both answer the same operator question - "will powering this off disconnect anybody" - so the
 * cases worth pinning down are the ones where a reassuring answer would be the wrong one: a node
 * the record no longer points at but which is still carrying people, and a node nothing has heard
 * from, whose zero must not read as empty.
 */
function node(overrides: Partial<Node> = {}): Node {
  return {
    id: 'n1',
    name: 'ams-1',
    publicIp: '203.0.113.10',
    isActive: false,
    status: 'healthy',
    appliedRevision: 7,
    fleetRevision: 7,
    inSync: true,
    interfaceUp: true,
    bundleSchemaVersion: 3,
    supportsCurrentSchema: true,
    revoked: false,
    enrolledAt: '2026-01-01T00:00:00+00:00',
    health: 'healthy' as NodeHealth,
    healthReason: 'fine',
    healthSource: 'agent',
    probesReachable: 0,
    probesReporting: 0,
    failoverPriority: 100,
    autoFailover: true,
    onCurrentIdentity: true,
    peersOnline: 0,
    peersIdle: 0,
    egressPlan: { defaultAddress: null, labels: [] },
    egressAddresses: [],
    ...overrides
  }
}

describe('describePeerLoad', () => {
  it('says plainly when nobody is connected', () => {
    expect(describePeerLoad(node())).toEqual({ text: 'nobody connected right now', tone: 'muted', peers: 0 })
  })

  it('counts the two groups apart, because they lose different things', () => {
    const load = describePeerLoad(node({ peersOnline: 3, peersIdle: 2 }))
    expect(load.text).toBe('3 connected here · 2 pointed here, silent')
    expect(load.peers).toBe(5)
  })

  // The case this exists for: traffic is being sent elsewhere and people are still on this node.
  it('warns when a node the record no longer points at is still carrying people', () => {
    expect(describePeerLoad(node({ peersOnline: 2 })).tone).toBe('warning')
  })

  it('does not warn about the node clients are supposed to be on', () => {
    expect(describePeerLoad(node({ isActive: true, peersOnline: 2 })).tone).toBe('muted')
  })

  // `live` marks the path traffic actually takes. A node holding a straggler is the opposite of
  // that claim, so the tone must never be borrowed for it.
  it('never borrows the live tone', () => {
    for (const load of [
      describePeerLoad(node({ isActive: true, peersOnline: 9 })),
      describePeerLoad(node({ peersOnline: 9 })),
      describePeerLoad(node())
    ]) {
      expect(load.tone).not.toBe('live')
    }
  })

  /**
   * A silent node's readings are frozen at its last report, so its peers age out of the window on
   * their own. The number must not read as "safe to power off" when what it means is that nothing
   * has vouched for anybody lately.
   */
  it('says where the number came from when nothing has been heard from the node', () => {
    const load = describePeerLoad(node({ health: 'silent', peersIdle: 4 }))
    expect(load.text).toBe('4 pointed here, silent, as of its last report')
    expect(load.tone).toBe('warning')
  })

  it('does not claim a node nobody has heard from is empty', () => {
    expect(describePeerLoad(node({ health: 'unknown' })).text).toBe('nothing reported lately')
  })

  // Revoking stops configuration, not traffic, so the line is drawn for a revoked node too.
  it('still describes a revoked node that is carrying people', () => {
    expect(describePeerLoad(node({ revoked: true, peersOnline: 1 })).peers).toBe(1)
  })
})

describe('nodeOf', () => {
  const peer = { id: 'c1' } as Client
  const stats = (nodeId?: string | null): Record<string, ClientStats> => ({
    c1: { id: 'c1', receivedBytes: 0, transmittedBytes: 0, online: true, nodeId }
  })

  // The bug this guards: resolving against the serving nodes hid exactly the peers worth seeing,
  // because a revoked node is filtered out of that list and is still carrying them.
  it('names a revoked node rather than pretending the peer is nowhere', () => {
    const revoked = node({ id: 'n2', name: 'fra-1', revoked: true })
    expect(nodeOf(peer, stats('n2'), [node(), revoked])?.name).toBe('fra-1')
  })

  it('has nothing to say about a peer no node has seen', () => {
    expect(nodeOf(peer, stats(null), [node()])).toBeNull()
  })

  it('has nothing to say about a node the panel no longer holds', () => {
    expect(nodeOf(peer, stats('gone'), [node()])).toBeNull()
  })

  it('has nothing to say without a peer', () => {
    expect(nodeOf(null, stats('n1'), [node()])).toBeNull()
  })
})
