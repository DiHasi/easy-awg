<script setup lang="ts">
import type { DropdownMenuItem } from '@nuxt/ui'
import type { Client, ClientObfuscationOverrides, ClientStats } from '~/types/api'

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

const form = reactive({
  name: '',
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

const visible = computed(() => {
  const needle = search.value.trim().toLowerCase()
  return [...clients.value]
    .sort((a, b) => addressOrder(a.address) - addressOrder(b.address))
    .filter(client =>
      (filter.value === 'all' || peerState(client) === filter.value)
      && (!needle || client.name.toLowerCase().includes(needle) || client.address.includes(needle))
    )
})

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

function resetForm() {
  form.name = ''
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

async function createClient() {
  if (!form.name.trim()) {
    toast.add({ title: 'Name is required', color: 'error', icon: 'i-lucide-circle-alert' })
    return
  }

  saving.value = true

  try {
    const client = await api.post<Client>('/clients', {
      name: form.name.trim(),
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

function menuFor(client: Client): DropdownMenuItem[][] {
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

onMounted(loadClients)
usePolling(loadStats, 10000)
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
        @new-peer="createOpen = true"
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
          icon="i-lucide-plus"
          @click="createOpen = true"
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
          @click="createOpen = true"
        >
          New peer
        </UButton>
      </div>

      <p
        v-else-if="visible.length === 0"
        class="px-4 py-12 text-center text-sm text-muted"
      >
        No peer matches this search.
      </p>

      <ul
        v-else
        class="divide-y divide-default"
      >
        <li
          v-for="client in visible"
          :key="client.id"
          class="grid grid-cols-[minmax(0,1fr)_auto] items-center gap-x-4 gap-y-1 px-4 py-3 transition-colors hover:bg-elevated/40 sm:px-5 lg:grid-cols-[minmax(0,1.6fr)_7rem_7.5rem_minmax(0,1.1fr)_9rem] lg:py-2.5"
        >
          <div
            class="min-w-0"
            :class="peerState(client) === 'off' ? 'opacity-60' : ''"
          >
            <p class="flex min-w-0 items-center gap-2">
              <span class="truncate font-medium text-highlighted">{{ client.name }}</span>
              <UBadge
                v-if="client.obfuscation"
                size="sm"
                color="neutral"
                variant="subtle"
              >
                custom timings
              </UBadge>
            </p>
            <p class="font-mono text-xs text-muted">
              {{ client.address }}<span class="lg:hidden"> · {{ handshake(client) }}</span>
            </p>
            <!-- The traffic columns do not fit on a phone, so the numbers move here and open the
                 full picture on a tap. -->
            <button
              type="button"
              class="tabular mt-1 flex items-center gap-2.5 font-mono text-xs text-toned lg:hidden"
              :aria-label="`Traffic for ${client.name}`"
              @click="openStats(client)"
            >
              <span>↓ {{ traffic(client, 'down') }}</span>
              <span>↑ {{ traffic(client, 'up') }}</span>
              <UIcon
                name="i-lucide-activity"
                class="size-3.5 text-muted"
              />
            </button>
          </div>

          <div class="hidden lg:block">
            <StateMark :state="peerState(client)" />
          </div>
          <span class="hidden text-sm text-toned lg:block">{{ handshake(client) }}</span>
          <span class="tabular hidden font-mono text-xs text-toned lg:block">
            ↓ {{ traffic(client, 'down') }} &nbsp;↑ {{ traffic(client, 'up') }}
          </span>

          <div class="flex items-center justify-end gap-1.5">
            <StateMark
              class="lg:hidden"
              :state="peerState(client)"
            />
            <UButton
              size="sm"
              color="neutral"
              variant="outline"
              icon="i-lucide-qr-code"
              :aria-label="`Config for ${client.name}`"
              @click="openConfig(client)"
            >
              <span class="hidden sm:inline">Config</span>
            </UButton>
            <UDropdownMenu
              :items="menuFor(client)"
              :content="{ align: 'end' }"
            >
              <UButton
                size="sm"
                color="neutral"
                variant="ghost"
                icon="i-lucide-ellipsis-vertical"
                :loading="busyId === client.id"
                :aria-label="`More actions for ${client.name}`"
              />
            </UDropdownMenu>
          </div>
        </li>
      </ul>
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
