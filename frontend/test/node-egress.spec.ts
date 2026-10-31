import { describe, expect, it } from 'vitest'
import { egressChoices, egressCoverage, egressLabels } from '~/composables/useNodeEgress'
import type { EgressLabel, Node } from '~/types/api'

/**
 * What a peer can be sent through is read off the nodes, never stored: a label is one node's name
 * for one of its own addresses, and the reason a peer picks a name rather than an address is that
 * the next node resolves the same name to an address of its own.
 */
function node(name: string, labels: EgressLabel[]): Node {
  return {
    id: name,
    name,
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
    health: 'healthy',
    healthReason: 'fine',
    healthSource: 'agent',
    probesReachable: 0,
    probesReporting: 0,
    failoverPriority: 100,
    autoFailover: true,
    onCurrentIdentity: true,
    peersOnline: 0,
    peersIdle: 0,
    egressPlan: { defaultAddress: null, labels },
    egressAddresses: []
  }
}

const fleet = [
  node('ams-1', [{ address: '203.0.113.5', label: 'clean' }, { address: '203.0.113.6', label: 'resident' }]),
  node('fra-1', [{ address: '198.51.100.5', label: 'clean' }])
]

describe('egressLabels', () => {
  it('offers every name any node has, once, in a stable order', () => {
    expect(egressLabels(fleet)).toEqual(['clean', 'resident'])
  })

  it('offers nothing when no node has been given a name', () => {
    expect(egressLabels([node('ams-1', [])])).toEqual([])
  })
})

describe('egressChoices', () => {
  // The bug this guards: an item carrying an empty value throws instead of rendering, because
  // that is what the select reserves for its own placeholder. A "Node default" item in the list
  // therefore never appeared, and a peer that had been given a name could not be put back.
  it('never offers an item with an empty value', () => {
    expect(egressChoices(fleet)).toEqual([
      { label: 'clean', value: 'clean' },
      { label: 'resident', value: 'resident' }
    ])
  })

  it('offers nothing at all when no node has a name', () => {
    expect(egressChoices([node('ams-1', [])])).toEqual([])
  })
})

describe('egressCoverage', () => {
  // The honest answer to "what happens after a switch": one node has an address under this name
  // and the other does not, so a peer that asked for it keeps it on one and falls back on the other.
  it('says which nodes stand behind a name and which fall back', () => {
    expect(egressCoverage(fleet, 'resident')).toEqual({ offering: ['ams-1'], missing: ['fra-1'] })
  })

  it('says both when every node has it', () => {
    expect(egressCoverage(fleet, 'clean')).toEqual({ offering: ['ams-1', 'fra-1'], missing: [] })
  })

  it('has nothing to say about a peer that asked for nothing', () => {
    expect(egressCoverage(fleet, null)).toEqual({ offering: [], missing: [] })
  })
})
