import type { DnsStatus, Node } from '~/types/api'

/**
 * Switching, revoking and removing nodes. Shared by the overview graph and the nodes page so the
 * same button asks the same question and reports the same way wherever it appears. Call in setup.
 */
export function useNodeActions() {
  const api = useControlApi()
  const toast = useToast()
  const confirm = useConfirm()
  const { dns, refresh } = useFleetState()

  const busyId = ref<string | null>(null)

  function fail(title: string, error: unknown) {
    toast.add({ title, description: describeError(error, ''), color: 'error', icon: 'i-lucide-circle-alert' })
  }

  async function activate(node: Node) {
    if (!node.publicIp) {
      toast.add({
        title: 'No address reported yet',
        description: `${node.name} has not told the panel where it is reachable, so there is nothing to point DNS at.`,
        color: 'warning',
        icon: 'i-lucide-circle-alert'
      })
      return
    }

    const record = dns.value ? `${dns.value.recordName} ${dns.value.recordType}` : 'the failover record'
    const ttl = dns.value ? ` Clients move over as resolvers expire the old answer, within ${dns.value.ttl} s.` : ''
    const confirmed = await confirm({
      title: `Make ${node.name} active?`,
      description: `${record} will point at ${node.publicIp}.${ttl} No client config is reissued.`,
      confirmLabel: 'Make active'
    })
    if (!confirmed) {
      return
    }

    busyId.value = node.id

    try {
      const status = await api.post<DnsStatus>(`/nodes/${node.id}/activate`)
      await refresh()
      toast.add({
        title: `${node.name} is now active`,
        description: status.providerConfigured
          ? undefined
          : `Set ${status.recordName} ${status.recordType} to ${node.publicIp} at your DNS provider.`,
        icon: 'i-lucide-check',
        color: 'success'
      })
    } catch (error) {
      fail('Could not switch over', error)
    } finally {
      busyId.value = null
    }
  }

  async function revoke(node: Node) {
    const confirmed = await confirm({
      title: `Revoke ${node.name}?`,
      description: 'It stops receiving configuration immediately, but keeps serving traffic on its last configuration until you stop the agent on the server.',
      confirmLabel: 'Revoke',
      danger: true
    })
    if (!confirmed) {
      return
    }

    busyId.value = node.id

    try {
      await api.post(`/nodes/${node.id}/revoke`)
      await refresh()
      toast.add({ title: `${node.name} revoked`, icon: 'i-lucide-check', color: 'success' })
    } catch (error) {
      fail('Could not revoke', error)
    } finally {
      busyId.value = null
    }
  }

  async function remove(node: Node) {
    const confirmed = await confirm({
      title: `Remove ${node.name}?`,
      description: 'This only forgets the node here. Stop the agent on the server itself as well, or it keeps serving its last configuration.',
      confirmLabel: 'Remove',
      danger: true
    })
    if (!confirmed) {
      return
    }

    busyId.value = node.id

    try {
      await api.del(`/nodes/${node.id}`)
      await refresh()
      toast.add({ title: `${node.name} removed`, icon: 'i-lucide-check', color: 'success' })
    } catch (error) {
      fail('Could not remove', error)
    } finally {
      busyId.value = null
    }
  }

  return { busyId, activate, revoke, remove }
}
