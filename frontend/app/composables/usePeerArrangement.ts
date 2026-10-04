import PeerGroupModal from '~/components/PeerGroupModal.vue'
import type { Client, ClientGroup } from '~/types/api'

/**
 * How the peer list is filed and in what order it is read.
 *
 * One group is one person; the peers inside it are the devices they hold. All of it is the
 * panel's own bookkeeping - no node is told about a group, and no config is reissued when a peer
 * moves - which is why none of these calls touches the fleet revision. It is kept on the server
 * rather than in this browser so that the panel opened on a phone shows the same arrangement as
 * the one on a desk.
 *
 * The flat client list stays the single source of truth: a peer's bucket is its `groupId` and its
 * position is where it sits in that list, so a drop is one splice rather than a tree to keep in
 * step. Every move is applied here first and sent afterwards; the server answers with the list as
 * it now stands, which is what resolves a drag that raced a change made somewhere else.
 */

/** Bucket key for the peers filed nowhere. Group ids are hex, so this cannot collide with one. */
export const UNGROUPED = 'ungrouped'

export type PeerBucket = {
  key: string
  group: ClientGroup | null
  clients: Client[]
}

export function usePeerArrangement(clients: Ref<Client[]>) {
  const api = useControlApi()
  const toast = useToast()
  const confirm = useConfirm()
  const nameDialog = useOverlay().create(PeerGroupModal)

  const groups = ref<ClientGroup[]>([])
  const loaded = ref(false)
  const saving = ref(false)

  /** The groups in order, then the ungrouped peers, which always read last. */
  const buckets = computed<PeerBucket[]>(() => [
    ...groups.value.map(group => ({
      key: group.id,
      group,
      clients: clients.value.filter(client => client.groupId === group.id)
    })),
    {
      key: UNGROUPED,
      group: null,
      clients: clients.value.filter(client => !client.groupId)
    }
  ])

  function fail(title: string, error: unknown) {
    toast.add({ title, description: describeError(error, ''), color: 'error', icon: 'i-lucide-circle-alert' })
  }

  async function load() {
    try {
      groups.value = await api.get<ClientGroup[]>('/groups')
    } catch {
      // The arrangement is not worth an error banner over the peers themselves: a failed read
      // leaves every peer in the ungrouped list, which is still the whole truth about the fleet.
    } finally {
      loaded.value = true
    }
  }

  function arrangement() {
    return buckets.value.map(bucket => ({
      groupId: bucket.group?.id ?? null,
      clientIds: bucket.clients.map(client => client.id)
    }))
  }

  /**
   * Sends the whole arrangement, every bucket of it. Peers the panel has never heard of are not
   * named and so are not moved, which is what keeps a drag here from sweeping up a peer created
   * on another device a moment ago.
   */
  async function persistPeers(before: Client[]) {
    saving.value = true

    try {
      clients.value = await api.put<Client[]>('/clients/arrangement', { groups: arrangement() })
    } catch (error) {
      clients.value = before
      fail('Could not save the arrangement', error)
    } finally {
      saving.value = false
    }
  }

  async function persistGroups(before: ClientGroup[]) {
    saving.value = true

    try {
      groups.value = await api.put<ClientGroup[]>('/groups/order', { ids: groups.value.map(group => group.id) })
    } catch (error) {
      groups.value = before
      fail('Could not save the group order', error)
    } finally {
      saving.value = false
    }
  }

  /**
   * Moves one peer into a bucket, before `beforeId` or at the end of it. Both the group and the
   * position come out of the one splice: the buckets are views over this list.
   */
  function movePeer(peerId: string, bucketKey: string, beforeId: string | null) {
    const before = clients.value
    const peer = before.find(client => client.id === peerId)
    if (!peer || peerId === beforeId) {
      return
    }

    const groupId = bucketKey === UNGROUPED ? null : bucketKey
    const rest = before.filter(client => client.id !== peerId)
    const moved: Client = { ...peer, groupId }

    const at = beforeId ? rest.findIndex(client => client.id === beforeId) : -1
    if (at >= 0) {
      rest.splice(at, 0, moved)
    } else {
      // The end of the target bucket, which is not the end of the list: the buckets are read in
      // group order, and a peer appended after a later group's peers would sort into that group.
      const last = rest.reduce(
        (index, client, position) => (client.groupId ?? null) === groupId ? position : index,
        -1
      )
      rest.splice(last + 1, 0, moved)
    }

    // Dropping a peer back where it already was is not a change worth a request.
    const settled = rest.every((client, index) =>
      client.id === before[index]?.id && (client.groupId ?? null) === (before[index]?.groupId ?? null))
    if (settled) {
      return
    }

    clients.value = rest
    void persistPeers(before)
  }

  function moveGroup(groupId: string, beforeId: string | null) {
    const before = groups.value
    const group = before.find(item => item.id === groupId)
    if (!group || groupId === beforeId) {
      return
    }

    const rest = before.filter(item => item.id !== groupId)
    const at = beforeId ? rest.findIndex(item => item.id === beforeId) : rest.length
    rest.splice(at < 0 ? rest.length : at, 0, group)

    if (rest.every((item, index) => item.id === before[index]?.id)) {
      return
    }

    groups.value = rest
    void persistGroups(before)
  }

  /** The same move one step at a time, for anyone not holding a pointer. */
  function nudgePeer(client: Client, delta: 1 | -1) {
    const key = client.groupId ?? UNGROUPED
    const bucket = buckets.value.find(item => item.key === key)
    if (!bucket) {
      return
    }

    const from = bucket.clients.findIndex(item => item.id === client.id)
    const to = from + delta
    if (from < 0 || to < 0 || to >= bucket.clients.length) {
      return
    }

    movePeer(client.id, key, delta < 0 ? bucket.clients[to]!.id : bucket.clients[to + 1]?.id ?? null)
  }

  async function createGroup() {
    const name = await nameDialog.open({ title: 'New group' })
    if (!name) {
      return null
    }

    try {
      const group = await api.post<ClientGroup>('/groups', { name })
      groups.value = [...groups.value, group]
      return group
    } catch (error) {
      fail('Could not create the group', error)
      return null
    }
  }

  async function renameGroup(group: ClientGroup) {
    const name = await nameDialog.open({ title: 'Rename group', name: group.name })
    if (!name || name === group.name) {
      return
    }

    try {
      const updated = await api.put<ClientGroup>(`/groups/${group.id}`, { name })
      groups.value = groups.value.map(item => item.id === updated.id ? updated : item)
    } catch (error) {
      fail('Could not rename the group', error)
    }
  }

  async function deleteGroup(group: ClientGroup) {
    const held = clients.value.filter(client => client.groupId === group.id).length
    const confirmed = await confirm({
      title: `Delete ${group.name}?`,
      description: held === 0
        ? 'The group is empty, so this only removes the label.'
        // Worth spelling out: deleting the person a config is filed under is not deleting
        // the config, and nobody loses their tunnel over it.
        : `${held === 1 ? 'The peer' : `All ${held} peers`} in it stay and move to the ungrouped list. No config is reissued and no node re-applies anything.`,
      confirmLabel: 'Delete group',
      danger: true
    })
    if (!confirmed) {
      return
    }

    try {
      await api.del(`/groups/${group.id}`)
      groups.value = groups.value.filter(item => item.id !== group.id)
      clients.value = clients.value.map(client => client.groupId === group.id ? { ...client, groupId: null } : client)
    } catch (error) {
      fail('Could not delete the group', error)
    }
  }

  return {
    groups,
    loaded,
    saving,
    buckets,
    load,
    movePeer,
    moveGroup,
    nudgePeer,
    createGroup,
    renameGroup,
    deleteGroup
  }
}
