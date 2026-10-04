<script setup lang="ts">
import type { DropdownMenuItem } from '@nuxt/ui'
import type { Client, ClientObfuscationOverrides, ClientStats } from '~/types/api'
import { UNGROUPED } from '~/composables/usePeerArrangement'

definePageMeta({ middleware: 'auth' })

const api = useControlApi()
const toast = useToast()
const confirm = useConfirm()
const { fleet, dns, serving } = useFleetState()
const nodeActions = useNodeActions()

const clients = ref<Client[]>([])
const stats = ref<Record<string, ClientStats>>({})
const clientsLoaded = ref(false)
const statsLoaded = ref(false)
const errorMessage = ref<string | null>(null)

/**
 * The list waits for the traffic poll as well as for the peers themselves. The two requests used
 * to race and the table rendered from whichever answered first, so every peer sat there as idle,
 * never handshook and carrying nothing - a definite claim about every tunnel, made before the
 * panel had been told a thing about any of them.
 */
const loading = computed(() => !clientsLoaded.value || !statsLoaded.value)
const busyId = ref<string | null>(null)

const createOpen = ref(false)
const editOpen = ref(false)
const configOpen = ref(false)
const enrollOpen = ref(false)
const saving = ref(false)
const useObfuscation = ref(false)

const editingClient = ref<Client | null>(null)
const editName = ref('')
const configClient = ref<Client | null>(null)
const statsOpen = ref(false)
const statsClient = ref<Client | null>(null)

type PeerState = 'up' | 'idle' | 'off' | 'unknown'
const filter = ref<'all' | PeerState>('all')
const search = ref('')

/**
 * Who holds what, and in what order the list reads. Server-side, so the arrangement made on a
 * desk is the arrangement a phone opens on; the flat client list stays the source of truth and
 * the sections are views over it.
 */
const arrangement = usePeerArrangement(clients)
const { groups, buckets, saving: arranging } = arrangement

const drag = useDragSort((dragged, target) => {
  if (dragged.kind === 'peer') {
    arrangement.movePeer(dragged.id, target.bucket, target.beforeId)
  } else {
    arrangement.moveGroup(dragged.id, target.beforeId)
  }
})

const form = reactive({
  name: '',
  groupId: '',
  jc: undefined as number | undefined,
  jmin: undefined as number | undefined,
  jmax: undefined as number | undefined,
  i1: '',
  i2: '',
  i3: '',
  i4: '',
  i5: '',
  contentPaddingAddition: '',
  rekeyAfterTime: '',
  rekeyTimeout: '',
  rejectAfterTime: '',
  keepaliveTimeout: '',
  maxHandshakeAttempts: '',
  persistentKeepalive: '',
  disableCookies: undefined as boolean | undefined
})

/**
 * AmneziaWG 3.x tunables a single client may diverge on. Two clients that rekey on the same
 * schedule and pad to the same length are a correlatable pair, so per-client values are the
 * point rather than a nicety.
 */
const clientTuningFields = [
  { key: 'contentPaddingAddition', label: 'ContentPaddingAddition' },
  { key: 'rekeyAfterTime', label: 'RekeyAfterTime' },
  { key: 'rekeyTimeout', label: 'RekeyTimeout' },
  { key: 'rejectAfterTime', label: 'RejectAfterTime' },
  { key: 'keepaliveTimeout', label: 'KeepaliveTimeout' },
  { key: 'maxHandshakeAttempts', label: 'MaxHandshakeAttempts' },
  { key: 'persistentKeepalive', label: 'PersistentKeepalive' }
] as const

const groupChoices = computed(() => [
  { label: 'No group', value: '' },
  ...groups.value.map(group => ({ label: group.name, value: group.id }))
])

/**
 * Whether a peer is disabled is the panel's own record and known immediately; whether an enabled
 * one is carrying traffic is the nodes' and arrives with the stats. Until it does the answer is
 * that there is no answer - `idle` is what the tunnel does, not what the panel has yet to hear.
 */
function peerState(client: Client): PeerState {
  if (!client.enabled) {
    return 'off'
  }
  if (!statsLoaded.value) {
    return 'unknown'
  }
  return stats.value[client.id]?.online ? 'up' : 'idle'
}

const counts = computed(() => {
  const result = { total: clients.value.length, up: 0, idle: 0, off: 0, unknown: 0 }
  for (const client of clients.value) {
    result[peerState(client)]++
  }
  return result
})

const filters = computed(() => [
  { value: 'all' as const, label: 'All', count: counts.value.total },
  { value: 'up' as const, label: 'Online', count: counts.value.up },
  { value: 'idle' as const, label: 'Idle', count: counts.value.idle },
  { value: 'off' as const, label: 'Disabled', count: counts.value.off }
])

const narrowed = computed(() => filter.value !== 'all' || search.value.trim().length > 0)

/**
 * Dragging is off while a search or a filter is showing part of the list: a position among the
 * rows that happen to match is not a position in the list, and dropping one there would move a
 * peer somewhere the operator cannot see.
 */
const arrangeable = computed(() => !narrowed.value && !loading.value)

/** The sections as drawn: every bucket while the whole list is shown, only the hits while it is not. */
const sections = computed(() => {
  const needle = search.value.trim().toLowerCase()
  const matches = (client: Client) =>
    (filter.value === 'all' || peerState(client) === filter.value)
    && (!needle || client.name.toLowerCase().includes(needle) || client.address.includes(needle))

  return buckets.value
    .map(bucket => ({
      ...bucket,
      visible: bucket.clients.filter(matches),
      online: bucket.clients.filter(client => peerState(client) === 'up').length
    }))
    .filter(section => !narrowed.value || section.visible.length > 0)
})

const matchCount = computed(() => sections.value.reduce((total, section) => total + section.visible.length, 0))

/** With no groups the list is drawn bare, so grouping costs nothing to anyone not using it. */
const bare = computed(() => groups.value.length === 0)

async function loadClients() {
  try {
    clients.value = await api.get<Client[]>('/clients')
    errorMessage.value = null
  } catch (error) {
    errorMessage.value = describeError(error, 'Failed to load clients.')
  } finally {
    clientsLoaded.value = true
  }
}

async function loadStats() {
  try {
    const items = await api.get<ClientStats[]>('/clients/stats')
    stats.value = Object.fromEntries(items.map(item => [item.id, item]))
  } catch {
    // A dropped stats poll is not worth an error banner; the next tick usually recovers.
  } finally {
    // Settled either way, or a stats endpoint that keeps failing would hide the peers for good.
    statsLoaded.value = true
  }
}

/**
 * Reads the peers and the arrangement back, so a panel left open on one device catches up with a
 * group made or a peer dragged on another. Never mid-drag: the answer already in flight is the
 * newer truth, and a list replaced under a finger would land the drop in the wrong place.
 */
async function refresh() {
  await loadStats()
  if (!arranging.value && !drag.dragging.value) {
    await Promise.all([loadClients(), arrangement.load()])
  }
}

function resetForm() {
  form.name = ''
  form.groupId = ''
  form.jc = undefined
  form.jmin = undefined
  form.jmax = undefined
  form.i1 = ''
  form.i2 = ''
  form.i3 = ''
  form.i4 = ''
  form.i5 = ''
  for (const field of clientTuningFields) {
    form[field.key] = ''
  }
  form.disableCookies = undefined
  useObfuscation.value = false
}

function buildObfuscation(): ClientObfuscationOverrides | undefined {
  if (!useObfuscation.value) {
    return undefined
  }

  const clean = (value: string) => value.trim() || undefined
  const overrides: ClientObfuscationOverrides = {
    jc: form.jc,
    jmin: form.jmin,
    jmax: form.jmax,
    i1: clean(form.i1),
    i2: clean(form.i2),
    i3: clean(form.i3),
    i4: clean(form.i4),
    i5: clean(form.i5),
    contentPaddingAddition: clean(form.contentPaddingAddition),
    rekeyAfterTime: clean(form.rekeyAfterTime),
    rekeyTimeout: clean(form.rekeyTimeout),
    rejectAfterTime: clean(form.rejectAfterTime),
    keepaliveTimeout: clean(form.keepaliveTimeout),
    maxHandshakeAttempts: clean(form.maxHandshakeAttempts),
    persistentKeepalive: clean(form.persistentKeepalive),
    disableCookies: form.disableCookies
  }

  return Object.values(overrides).some(value => value !== undefined && value !== null) ? overrides : undefined
}

function fail(title: string, error: unknown) {
  toast.add({ title, description: describeError(error, ''), color: 'error', icon: 'i-lucide-circle-alert' })
}

function succeed(title: string) {
  toast.add({ title, color: 'success', icon: 'i-lucide-check' })
}

/** Opened from a person's own section, the new device is already filed under them. */
function openCreate(groupId: string | null = null) {
  resetForm()
  form.groupId = groupId ?? ''
  createOpen.value = true
}

async function createClient() {
  if (!form.name.trim()) {
    toast.add({ title: 'Name is required', color: 'error', icon: 'i-lucide-circle-alert' })
    return
  }

  saving.value = true

  try {
    const client = await api.post<Client>('/clients', {
      name: form.name.trim(),
      groupId: form.groupId || null,
      obfuscation: buildObfuscation()
    })

    clients.value = [...clients.value, client]
    createOpen.value = false
    resetForm()
    succeed(`${client.name} created`)
    // The next thing anyone does with a new peer is hand its config over.
    openConfig(client)
  } catch (error) {
    fail('Could not create the peer', error)
  } finally {
    saving.value = false
  }
}

async function createGroupFor() {
  const group = await arrangement.createGroup()
  if (group) {
    succeed(`${group.name} added`)
  }
}

function openConfig(client: Client) {
  configClient.value = client
  configOpen.value = true
}

function openStats(client: Client) {
  statsClient.value = client
  statsOpen.value = true
}

/** The node the newest handshake came through, so the modal can name where this peer is. */
const statsNode = computed(() => {
  const nodeId = statsClient.value ? stats.value[statsClient.value.id]?.nodeId : null
  return nodeId ? serving.value.find(node => node.id === nodeId) ?? null : null
})

function applyStats(updated: ClientStats) {
  stats.value = { ...stats.value, [updated.id]: updated }
}

function openEdit(client: Client) {
  editingClient.value = client
  editName.value = client.name
  editOpen.value = true
}

async function saveName() {
  const client = editingClient.value
  if (!client || !editName.value.trim()) {
    return
  }

  busyId.value = client.id

  try {
    const updated = await api.put<Client>(`/clients/${client.id}`, { name: editName.value.trim() })
    clients.value = clients.value.map(item => item.id === updated.id ? updated : item)
    editOpen.value = false
    succeed('Peer renamed')
  } catch (error) {
    fail('Could not rename the peer', error)
  } finally {
    busyId.value = null
  }
}

async function toggleClient(client: Client) {
  busyId.value = client.id

  try {
    const updated = await api.post<Client>(`/clients/${client.id}/${client.enabled ? 'disable' : 'enable'}`)
    clients.value = clients.value.map(item => item.id === updated.id ? updated : item)
    succeed(updated.enabled ? `${updated.name} enabled` : `${updated.name} disabled`)
  } catch (error) {
    fail('Could not change the peer', error)
  } finally {
    busyId.value = null
  }
}

async function deleteClient(client: Client) {
  const confirmed = await confirm({
    title: `Delete ${client.name}?`,
    description: `The config for ${client.name} (${client.address}) stops working on every node once they apply the next revision. A new peer gets a new key.`,
    confirmLabel: 'Delete peer',
    danger: true
  })
  if (!confirmed) {
    return
  }

  busyId.value = client.id

  try {
    await api.del(`/clients/${client.id}`)
    clients.value = clients.value.filter(item => item.id !== client.id)
    succeed(`${client.name} deleted`)
  } catch (error) {
    fail('Could not delete the peer', error)
  } finally {
    busyId.value = null
  }
}

/** Where a peer sits in its own section, so the menu can refuse a step it cannot take. */
function positionOf(client: Client) {
  const bucket = buckets.value.find(item => item.key === (client.groupId ?? UNGROUPED))
  return {
    index: bucket ? bucket.clients.findIndex(item => item.id === client.id) : -1,
    size: bucket?.clients.length ?? 0
  }
}

function menuFor(client: Client): DropdownMenuItem[][] {
  const { index, size } = positionOf(client)

  // The same moves as a drag, for a phone, a keyboard, or a list narrowed by a search. Filing a
  // peer this way puts it at the end of that person's devices.
  const moves: DropdownMenuItem[] = [
    {
      label: 'Move to',
      icon: 'i-lucide-folder-input',
      children: [
        ...groups.value.map(group => ({
          label: group.name,
          icon: 'i-lucide-user',
          disabled: client.groupId === group.id,
          onSelect: () => arrangement.movePeer(client.id, group.id, null)
        })),
        {
          label: 'Ungrouped',
          icon: 'i-lucide-inbox',
          disabled: !client.groupId,
          onSelect: () => arrangement.movePeer(client.id, UNGROUPED, null)
        }
      ]
    },
    {
      label: 'Move up',
      icon: 'i-lucide-arrow-up',
      disabled: index <= 0,
      onSelect: () => arrangement.nudgePeer(client, -1)
    },
    {
      label: 'Move down',
      icon: 'i-lucide-arrow-down',
      disabled: index < 0 || index >= size - 1,
      onSelect: () => arrangement.nudgePeer(client, 1)
    }
  ]

  return [
    [
      { label: 'Traffic', icon: 'i-lucide-activity', onSelect: () => openStats(client) },
      { label: 'Rename', icon: 'i-lucide-pencil', onSelect: () => openEdit(client) },
      {
        label: client.enabled ? 'Disable' : 'Enable',
        icon: client.enabled ? 'i-lucide-pause' : 'i-lucide-play',
        onSelect: () => toggleClient(client)
      }
    ],
    moves,
    [{ label: 'Delete', icon: 'i-lucide-trash-2', color: 'error', onSelect: () => deleteClient(client) }]
  ]
}

function handshake(client: Client) {
  return relativeTime(stats.value[client.id]?.latestHandshakeAt, statsLoaded.value ? 'never' : '—')
}

/** An em dash for a number nobody has reported yet, rather than a zero that reads as measured. */
function traffic(client: Client, direction: 'down' | 'up') {
  if (!statsLoaded.value) {
    return '—'
  }

  const peer = stats.value[client.id]
  return formatBytes((direction === 'down' ? peer?.transmittedBytes : peer?.receivedBytes) ?? 0)
}

onMounted(() => {
  void loadClients()
  void arrangement.load()
})

usePolling(refresh, 10000)
</script>

<template>
  <div class="flex flex-col gap-4 lg:gap-6">
    <h1 class="sr-only">
      Overview
    </h1>

    <AppCard
      title="Fleet"
      icon="i-lucide-network"
      description="Where client traffic goes right now. Switch nodes on the node itself."
    >
      <FleetGraph
        :nodes="serving"
        :dns="dns"
        :fleet="fleet"
        :clients="{ total: counts.total, online: counts.up, idle: counts.idle, off: counts.off, unknown: counts.unknown }"
        :busy-id="nodeActions.busyId.value"
        @activate="nodeActions.activate"
        @revoke="nodeActions.revoke"
        @remove="nodeActions.remove"
        @failover="nodeActions.editFailover"
        @new-peer="openCreate()"
        @enroll="enrollOpen = true"
      />
    </AppCard>

    <AppCard
      id="peers"
      title="Peers"
      icon="i-lucide-users"
      :description="loading
        ? 'Reading the peers and what they have moved'
        : `${counts.total} configs issued · ${counts.up} online now`"
      flush
      class="scroll-mt-20"
    >
      <template #actions>
        <UButton
          icon="i-lucide-user-plus"
          color="neutral"
          variant="outline"
          @click="createGroupFor"
        >
          New group
        </UButton>
        <UButton
          icon="i-lucide-plus"
          @click="openCreate()"
        >
          New peer
        </UButton>
      </template>

      <div class="flex flex-wrap items-center gap-2 border-b border-default px-4 py-3 sm:px-5">
        <UInput
          v-model="search"
          icon="i-lucide-search"
          placeholder="Find by name or address"
          aria-label="Find a peer"
          class="w-full sm:w-72"
        />
        <div class="max-w-full overflow-x-auto">
          <UFieldGroup>
            <UButton
              v-for="option in filters"
              :key="option.value"
              size="sm"
              color="neutral"
              :variant="filter === option.value ? 'solid' : 'outline'"
              :aria-pressed="filter === option.value"
              @click="filter = option.value"
            >
              {{ option.label }}
              <!-- Counting them means knowing which is online, which the stats poll has not said yet. -->
              <span
                v-if="!loading"
                class="tabular opacity-70"
              >{{ option.count }}</span>
            </UButton>
          </UFieldGroup>
        </div>

        <!-- Said once, where the handles went: a narrowed list has no positions to drag between. -->
        <p
          v-if="narrowed && !bare"
          class="text-xs text-dimmed"
        >
          Clear the search to rearrange
        </p>
        <p
          v-else-if="arranging"
          class="flex items-center gap-1.5 text-xs text-muted"
        >
          <UIcon
            name="i-lucide-loader-circle"
            class="size-3.5 animate-spin"
          />
          Saving the arrangement
        </p>
      </div>

      <UAlert
        v-if="errorMessage"
        class="m-4 w-auto"
        color="error"
        variant="subtle"
        icon="i-lucide-circle-alert"
        title="Could not load peers"
        :description="errorMessage"
      />

      <div
        class="hidden grid-cols-[minmax(0,1.6fr)_7rem_7.5rem_minmax(0,1.1fr)_9rem] items-center gap-x-4 border-b border-default px-5 py-2 text-xs font-medium text-muted lg:grid"
        aria-hidden="true"
      >
        <span>Peer</span>
        <span>Status</span>
        <span>Last handshake</span>
        <span>Transferred</span>
        <span class="text-end">Actions</span>
      </div>

      <div
        v-if="loading"
        class="flex items-center justify-center gap-2 px-4 py-12 text-sm text-muted"
      >
        <UIcon
          name="i-lucide-loader-circle"
          class="size-5 animate-spin"
        />
        Loading peers
      </div>

      <div
        v-else-if="clients.length === 0"
        class="flex flex-col items-center gap-3 px-6 py-12 text-center"
      >
        <span class="flex size-12 items-center justify-center rounded-full bg-elevated">
          <UIcon
            name="i-lucide-users"
            class="size-6 text-muted"
          />
        </span>
        <div>
          <p class="font-medium text-highlighted">
            No peers yet
          </p>
          <p class="mt-1 max-w-sm text-sm text-muted">
            A peer is one client config. It names the record rather than a server, so it keeps
            working whichever node is active.
          </p>
        </div>
        <UButton
          icon="i-lucide-plus"
          @click="openCreate()"
        >
          New peer
        </UButton>
      </div>

      <p
        v-else-if="matchCount === 0"
        class="px-4 py-12 text-center text-sm text-muted"
      >
        No peer matches this search.
      </p>

      <div
        v-else
        class="divide-y divide-default"
      >
        <PeerGroup
          v-for="section in sections"
          :key="section.key"
          :bucket="section"
          :count="section.clients.length"
          :online="section.online"
          :bare="bare"
          :arrangeable="arrangeable"
          :carried="section.group ? drag.isDragged('group', section.group.id) : false"
          :drop-before="section.group
            ? drag.isDropBefore('group', section.group.id)
            : drag.isDropBefore('group', null)"
          :drop-at-end="drag.isDropBefore('peer', null, section.key)"
          @grab="(event: PointerEvent) => section.group && drag.begin(event, 'group', section.group.id, section.key)"
          @rename="section.group && arrangement.renameGroup(section.group)"
          @remove="section.group && arrangement.deleteGroup(section.group)"
          @new-peer="openCreate(section.group?.id ?? null)"
        >
          <PeerRow
            v-for="client in section.visible"
            :key="client.id"
            :client="client"
            :state="peerState(client)"
            :handshake="handshake(client)"
            :down="traffic(client, 'down')"
            :up="traffic(client, 'up')"
            :menu="menuFor(client)"
            :busy="busyId === client.id"
            :arrangeable="arrangeable"
            :carried="drag.isDragged('peer', client.id)"
            :drop-before="drag.isDropBefore('peer', client.id, section.key)"
            @grab="(event: PointerEvent) => drag.begin(event, 'peer', client.id, section.key)"
            @config="openConfig(client)"
            @stats="openStats(client)"
          />
        </PeerGroup>
      </div>
    </AppCard>

    <UModal
      v-model:open="createOpen"
      title="New peer"
      description="Issues one client config with its own key and the next free address."
    >
      <template #body>
        <div class="flex flex-col gap-5">
          <UFormField label="Name">
            <UInput
              v-model="form.name"
              placeholder="phone"
              class="w-full"
              autofocus
              @keyup.enter="createClient"
            />
          </UFormField>

          <UFormField
            v-if="groups.length > 0"
            label="Group"
            description="Whose device this is. A group is the panel's own filing: nothing in the config depends on it."
          >
            <USelect
              v-model="form.groupId"
              :items="groupChoices"
              class="w-full"
            />
          </UFormField>

          <USwitch
            v-model="useObfuscation"
            label="Custom timings for this peer"
            description="Leave off to use the fleet defaults from Settings. The wire format is fleet-wide and cannot differ."
          />

          <div
            v-if="useObfuscation"
            class="flex flex-col gap-4 rounded-lg border border-default bg-elevated/40 p-3"
          >
            <div class="grid grid-cols-3 gap-2">
              <UFormField
                :ui="paramField"
                label="Jc"
              >
                <UInput
                  v-model.number="form.jc"
                  type="number"
                  class="w-full"
                />
              </UFormField>
              <UFormField
                :ui="paramField"
                label="Jmin"
              >
                <UInput
                  v-model.number="form.jmin"
                  type="number"
                  class="w-full"
                />
              </UFormField>
              <UFormField
                :ui="paramField"
                label="Jmax"
              >
                <UInput
                  v-model.number="form.jmax"
                  type="number"
                  class="w-full"
                />
              </UFormField>
            </div>

            <UFormField
              v-for="field in (['i1', 'i2', 'i3', 'i4', 'i5'] as const)"
              :key="field"
              :ui="paramField"
              :label="field.toUpperCase()"
            >
              <UInput
                v-model="form[field]"
                class="w-full"
                placeholder="<b 0x...>"
              />
            </UFormField>

            <div class="grid grid-cols-1 gap-3 sm:grid-cols-2">
              <UFormField
                v-for="field in clientTuningFields"
                :key="field.key"
                :ui="paramField"
                :label="field.label"
              >
                <UInput
                  v-model="form[field.key]"
                  class="w-full"
                  placeholder="140 or 120-160"
                />
              </UFormField>
            </div>

            <USwitch
              v-model="form.disableCookies"
              label="DisableCookies"
            />
          </div>
        </div>
      </template>

      <template #footer>
        <div class="flex w-full justify-end gap-2">
          <UButton
            color="neutral"
            variant="ghost"
            @click="createOpen = false"
          >
            Cancel
          </UButton>
          <UButton
            :loading="saving"
            @click="createClient"
          >
            Create peer
          </UButton>
        </div>
      </template>
    </UModal>

    <UModal
      v-model:open="editOpen"
      title="Rename peer"
      description="Only the label changes. The key, address and config stay the same, so nothing is reissued."
    >
      <template #body>
        <UFormField label="Name">
          <UInput
            v-model="editName"
            class="w-full"
            autofocus
            @keyup.enter="saveName"
          />
        </UFormField>
      </template>
      <template #footer>
        <div class="flex w-full justify-end gap-2">
          <UButton
            color="neutral"
            variant="ghost"
            @click="editOpen = false"
          >
            Cancel
          </UButton>
          <UButton
            :loading="busyId === editingClient?.id"
            @click="saveName"
          >
            Save
          </UButton>
        </div>
      </template>
    </UModal>

    <PeerConfigModal
      v-model:open="configOpen"
      :client="configClient"
    />

    <PeerStatsModal
      v-model:open="statsOpen"
      :client="statsClient"
      :stats="statsClient ? stats[statsClient.id] : null"
      :node="statsNode"
      @reset="applyStats"
    />

    <EnrollNodeModal v-model:open="enrollOpen" />
  </div>
</template>
