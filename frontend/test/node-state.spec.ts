import { describe, expect, it } from 'vitest'
import { describeNode } from '~/composables/useNodeState'
import type { Node, NodeHealth } from '~/types/api'

/**
 * The one word a node card shows. The order the cases are tried in is the whole content of this
 * function: a node that is blocked while the record still points at it must not read "Active",
 * because that is the moment an operator needs to know to move traffic.
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
    bundleSchemaVersion: 2,
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
    egressPlan: { defaultAddress: null, labels: [] },
    egressAddresses: [],
    ...overrides
  }
}

describe('describeNode', () => {
  it('says standby for a healthy node nobody is being sent to', () => {
    expect(describeNode(node())).toEqual({ word: 'Standby', tone: 'standby' })
  })

  it('reserves the live tone for the node clients actually connect to', () => {
    expect(describeNode(node({ isActive: true }))).toEqual({ word: 'Active', tone: 'live' })
  })

  // The case failover exists for: up, serving nothing, and still holding the record.
  it('says blocked before active, because the record pointing here is what makes it urgent', () => {
    expect(describeNode(node({ isActive: true, health: 'blocked' }))).toEqual({ word: 'Blocked', tone: 'error' })
  })

  it('says down before active for the same reason', () => {
    expect(describeNode(node({ isActive: true, health: 'down' }))).toEqual({ word: 'Down', tone: 'error' })
  })

  it('says silent when nothing has vouched for the node lately', () => {
    expect(describeNode(node({ health: 'silent' }))).toEqual({ word: 'Silent', tone: 'warning' })
  })

  // A revoked node receives no configuration; whatever else is true of it is not the headline.
  it('says revoked above everything else', () => {
    expect(describeNode(node({ revoked: true, isActive: true, health: 'down' })))
      .toEqual({ word: 'Revoked', tone: 'muted' })
  })

  it('says a node is updating while it is behind the fleet revision', () => {
    expect(describeNode(node({ appliedRevision: 6, inSync: false }))).toEqual({ word: 'Updating', tone: 'warning' })
  })

  // Behind the identity is not the same news as behind the fleet: the node answers with the
  // previous key pair, so every current config fails on it while it reports itself healthy.
  it('says previous identity rather than updating for a node that has not applied a rotation', () => {
    expect(describeNode(node({ appliedRevision: 6, inSync: false, onCurrentIdentity: false })))
      .toEqual({ word: 'Previous identity', tone: 'error' })
  })

  // Said before "Active" for the same reason as down and blocked: the record pointing at a node
  // that can serve nobody is the worst version of this.
  it('says previous identity even while the record still points there', () => {
    expect(describeNode(node({ isActive: true, onCurrentIdentity: false })))
      .toEqual({ word: 'Previous identity', tone: 'error' })
  })

  // But a node that is down says so: it being an identity behind is not the headline.
  it('still leads with down for a node that is both behind and failing', () => {
    expect(describeNode(node({ health: 'down', onCurrentIdentity: false })))
      .toEqual({ word: 'Down', tone: 'error' })
  })
})
