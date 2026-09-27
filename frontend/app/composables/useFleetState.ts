import type { DnsStatus, Fleet, Node } from '~/types/api'

/**
 * Fleet, nodes and the failover record, shared between the sheet frame and the pages.
 *
 * The title block on every sheet quotes the fleet revision and how many nodes have applied it,
 * and the topology drawing needs all three together: which node is active is DNS state, and a
 * drawing of nodes without it would say nothing about where clients are actually going.
 */
export function useFleetState() {
  const api = useControlApi()

  const fleet = useState<Fleet | null>('awg-fleet', () => null)
  const nodes = useState<Node[]>('awg-nodes', () => [])
  const dns = useState<DnsStatus | null>('awg-dns', () => null)
  const loaded = useState<boolean>('awg-fleet-loaded', () => false)
  const errorMessage = useState<string | null>('awg-fleet-error', () => null)
  const refreshedAt = useState<string | null>('awg-fleet-refreshed', () => null)

  async function refresh() {
    try {
      const [nextFleet, nextNodes, nextDns] = await Promise.all([
        api.get<Fleet>('/fleet'),
        api.get<Node[]>('/nodes'),
        api.get<DnsStatus>('/dns')
      ])

      fleet.value = nextFleet
      nodes.value = nextNodes
      dns.value = nextDns
      errorMessage.value = null
      refreshedAt.value = new Date().toISOString()
    } catch (error) {
      errorMessage.value = describeError(error, 'Failed to read the fleet.')
    } finally {
      loaded.value = true
    }
  }

  /** Revoked nodes no longer take configuration, so they are not part of the drawing. */
  const serving = computed(() => nodes.value.filter(node => !node.revoked && node.status !== 'retired'))

  const appliedCount = computed(() => serving.value.filter(node => node.inSync).length)

  return { fleet, nodes, dns, loaded, errorMessage, refreshedAt, serving, appliedCount, refresh }
}
