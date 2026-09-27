<script setup lang="ts">
import QRCode from 'qrcode'
import type { Client, ClientObfuscationOverrides, ClientShare, ClientStats } from '~/types/api'

definePageMeta({ middleware: 'auth' })

const api = useControlApi()
const toast = useToast()
const confirm = useConfirm()
const { fleet, dns, serving } = useFleetState()

const clients = ref<Client[]>([])
const stats = ref<Record<string, ClientStats>>({})
const rates = ref<Record<string, { down: number, up: number }>>({})
const loading = ref(true)
const errorMessage = ref<string | null>(null)
const actionId = ref<string | null>(null)

const createOpen = ref(false)
const editOpen = ref(false)
const qrOpen = ref(false)
const shareOpen = ref(false)
const saving = ref(false)
const useObfuscation = ref(false)

const editingClient = ref<Client | null>(null)
const editName = ref('')
const qrClient = ref<Client | null>(null)
const qrDataUrl = ref<string | null>(null)
const share = ref<ClientShare | null>(null)

type PeerState = 'up' | 'idle' | 'off'
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

// Counters are cumulative, so a rate only means something as a delta between two samples.
let previousStats: Record<string, ClientStats> | null = null
let previousAt: number | null = null

function peerState(client: Client): PeerState {
  if (!client.enabled) {
    return 'off'
  }
  return stats.value[client.id]?.online ? 'up' : 'idle'
}

const counts = computed(() => {
  const result = { all: clients.value.length, up: 0, idle: 0, off: 0 }
  for (const client of clients.value) {
    result[peerState(client)]++
  }
  return result
})

const filters = computed(() => [
  { value: 'all' as const, label: 'All', count: counts.value.all },
  { value: 'up' as const, label: 'Up', count: counts.value.up },
  { value: 'idle' as const, label: 'Idle', count: counts.value.idle },
  { value: 'off' as const, label: 'Off', count: counts.value.off }
])

// Numbered by address across the whole list, so an item keeps its number while filtering.
const sorted = computed(() => [...clients.value].sort((a, b) => addressOrder(a.address) - addressOrder(b.address)))
const itemIndex = computed(() => Object.fromEntries(sorted.value.map((client, index) => [client.id, index])))

const visible = computed(() => {
  const needle = search.value.trim().toLowerCase()
  return sorted.value.filter(client =>
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
    loading.value = false
  }
}

async function loadStats() {
  try {
    const items = await api.get<ClientStats[]>('/clients/stats')
    updateRates(items)
    stats.value = Object.fromEntries(items.map(item => [item.id, item]))
  } catch {
    // A dropped stats poll is not worth an error banner; the next tick usually recovers.
  }
}

function updateRates(items: ClientStats[]) {
  const now = Date.now()
  const snapshot = Object.fromEntries(items.map(item => [item.id, item]))

  if (!previousStats || !previousAt) {
    previousStats = snapshot
    previousAt = now
    return
  }

  const elapsed = Math.max((now - previousAt) / 1000, 1)
  rates.value = Object.fromEntries(items.map((item) => {
    const previous = previousStats?.[item.id]
    // Only count traffic between two samples where the peer was online in both, so an offline
    // client does not appear to be transferring the moment it reconnects.
    const active = item.online && previous?.online
    return [item.id, {
      down: active ? Math.max(item.transmittedBytes - (previous?.transmittedBytes ?? 0), 0) / elapsed : 0,
      up: active ? Math.max(item.receivedBytes - (previous?.receivedBytes ?? 0), 0) / elapsed : 0
    }]
  }))

  previousStats = snapshot
  previousAt = now
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
    succeed('Peer issued')
  } catch (error) {
    fail('Could not create the peer', error)
  } finally {
    saving.value = false
  }
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

  actionId.value = client.id

  try {
    const updated = await api.put<Client>(`/clients/${client.id}`, { name: editName.value.trim() })
    clients.value = clients.value.map(item => item.id === updated.id ? updated : item)
    editOpen.value = false
    succeed('Peer renamed')
  } catch (error) {
    fail('Could not rename the peer', error)
  } finally {
    actionId.value = null
  }
}

async function toggleClient(client: Client) {
  actionId.value = client.id

  try {
    const updated = await api.post<Client>(`/clients/${client.id}/${client.enabled ? 'disable' : 'enable'}`)
    clients.value = clients.value.map(item => item.id === updated.id ? updated : item)
    succeed(updated.enabled ? 'Peer enabled' : 'Peer disabled')
  } catch (error) {
    fail('Could not change the peer', error)
  } finally {
    actionId.value = null
  }
}

async function deleteClient(client: Client) {
  const confirmed = await confirm({
    title: `Delete ${client.name}`,
    description: `The config for ${client.name} (${client.address}) stops working on every node as soon as they apply the next revision. This cannot be undone; a new peer gets a new key.`,
    confirmLabel: 'Delete peer',
    danger: true
  })
  if (!confirmed) {
    return
  }

  actionId.value = client.id

  try {
    await api.del(`/clients/${client.id}`)
    clients.value = clients.value.filter(item => item.id !== client.id)
    succeed('Peer deleted')
  } catch (error) {
    fail('Could not delete the peer', error)
  } finally {
    actionId.value = null
  }
}

async function downloadConfig(client: Client) {
  actionId.value = client.id

  try {
    const response = await fetch(api.url(`/clients/${client.id}/config`), { credentials: 'include' })
    if (!response.ok) {
      throw new Error(`HTTP ${response.status}`)
    }

    const blob = await response.blob()
    const url = URL.createObjectURL(blob)
    const link = document.createElement('a')
    link.href = url
    link.download = `${client.name}.conf`
    link.click()
    URL.revokeObjectURL(url)
  } catch (error) {
    fail('Could not download the config', error)
  } finally {
    actionId.value = null
  }
}

async function openQr(client: Client) {
  qrClient.value = client
  qrDataUrl.value = null
  qrOpen.value = true

  try {
    const response = await fetch(api.url(`/clients/${client.id}/config`), { credentials: 'include' })
    const config = await response.text()
    qrDataUrl.value = await QRCode.toDataURL(config, { width: 320, margin: 1 })
  } catch (error) {
    fail('Could not build the QR code', error)
    qrOpen.value = false
  }
}

async function createShare(client: Client) {
  actionId.value = client.id

  try {
    share.value = await api.post<ClientShare>(`/clients/${client.id}/share`)
    shareOpen.value = true
  } catch (error) {
    fail('Could not create a share link', error)
  } finally {
    actionId.value = null
  }
}

async function copyShareUrl() {
  if (share.value) {
    await navigator.clipboard.writeText(share.value.url)
    succeed('Link copied')
  }
}

function handshake(client: Client) {
  return relativeTime(stats.value[client.id]?.latestHandshakeAt)
}

onMounted(loadClients)
usePolling(loadStats, 3000)
</script>

<template>
  <div class="flex flex-col">
    <SheetSection
      title="Topology"
      meta="where these peers are answered"
    >
      <template #actions>
        <UButton
          to="/nodes"
          variant="ghost"
          size="sm"
          trailing-icon="i-lucide-arrow-right"
        >
          Sheet 2
        </UButton>
      </template>

      <FleetSchematic
        :nodes="serving"
        :dns="dns"
        :clients-total="clients.length"
        :clients-online="counts.up"
        :subnet="fleet?.subnet"
        :listen-port="fleet?.listenPort"
      />
    </SheetSection>

    <SheetSection
      title="Peer list"
      flush
    >
      <template #meta>
        items {{ visible.length }} of {{ clients.length }} · by address
      </template>

      <template #actions>
        <UFieldGroup>
          <UButton
            v-for="option in filters"
            :key="option.value"
            size="sm"
            :color="filter === option.value ? 'primary' : 'neutral'"
            :variant="filter === option.value ? 'solid' : 'outline'"
            :aria-pressed="filter === option.value"
            @click="filter = option.value"
          >
            {{ option.label }}&nbsp;<span class="font-mono opacity-70">{{ option.count }}</span>
          </UButton>
        </UFieldGroup>
        <UInput
          v-model="search"
          size="sm"
          icon="i-lucide-search"
          placeholder="name or address"
          aria-label="Find a peer"
          class="w-40 sm:w-48"
        />
        <UButton
          color="primary"
          variant="solid"
          size="sm"
          icon="i-lucide-plus"
          @click="createOpen = true"
        >
          New peer
        </UButton>
      </template>

      <UAlert
        v-if="errorMessage"
        class="m-3 w-auto sm:m-4"
        color="error"
        variant="subtle"
        icon="i-lucide-circle-alert"
        title="Could not read the peer list"
        :description="errorMessage"
      />

      <div
        class="hidden grid-cols-[3rem_minmax(0,1.5fr)_minmax(0,1fr)_minmax(0,1fr)_minmax(0,1.2fr)_minmax(0,1.3fr)_13.5rem] items-center gap-x-3 border-b border-accented px-4 py-1.5 lg:grid"
        aria-hidden="true"
      >
        <span class="caps text-muted">Item</span>
        <span class="caps text-muted">Peer</span>
        <span class="caps text-muted">Address</span>
        <span class="caps text-muted">State</span>
        <span class="caps text-muted">Now</span>
        <span class="caps text-muted">Carried</span>
        <span class="caps text-end text-muted">Actions</span>
      </div>

      <p
        v-if="loading"
        class="px-4 py-10 text-center font-mono text-xs text-muted"
      >
        reading the peer list…
      </p>

      <div
        v-else-if="clients.length === 0"
        class="m-3 flex flex-col items-center gap-3 border border-dashed border-default px-6 py-10 text-center sm:m-4"
      >
        <span class="caps text-highlighted">No peers issued</span>
        <p class="max-w-md text-sm text-muted">
          A peer is one client config. It names the record rather than a server, so it keeps
          working whichever node the record points at.
        </p>
        <UButton
          color="primary"
          variant="solid"
          icon="i-lucide-plus"
          @click="createOpen = true"
        >
          New peer
        </UButton>
      </div>

      <p
        v-else-if="visible.length === 0"
        class="px-4 py-10 text-center font-mono text-xs text-muted"
      >
        no peer matches this filter
      </p>

      <ul v-else>
        <li
          v-for="client in visible"
          :key="client.id"
          class="grid grid-cols-[2.25rem_minmax(0,1fr)_auto] items-center gap-x-3 gap-y-2 border-b border-muted px-3 py-2.5 last:border-b-0 sm:px-4 lg:grid-cols-[3rem_minmax(0,1.5fr)_minmax(0,1fr)_minmax(0,1fr)_minmax(0,1.2fr)_minmax(0,1.3fr)_13.5rem] lg:py-1.5"
          :class="peerState(client) === 'off' ? 'text-muted' : ''"
        >
          <span class="font-mono text-[11px] text-muted">{{ itemNumber(itemIndex[client.id] ?? 0) }}</span>

          <div class="min-w-0">
            <div class="flex flex-wrap items-baseline gap-x-2">
              <span
                class="break-all font-mono text-[13.5px]"
                :class="peerState(client) === 'off' ? '' : 'text-highlighted'"
              >{{ client.name }}</span>
              <span
                v-if="client.obfuscation"
                class="caps text-muted"
              >own timings</span>
            </div>
            <p class="font-mono text-[11px] text-muted lg:hidden">
              {{ client.address }} · {{ handshake(client) }}
              <template v-if="peerState(client) === 'up'">
                · ↓ {{ formatRate(rates[client.id]?.down) }} ↑ {{ formatRate(rates[client.id]?.up) }}
              </template>
            </p>
          </div>

          <span class="hidden font-mono text-xs text-toned lg:block">{{ client.address }}</span>

          <div class="flex flex-col items-end lg:items-start">
            <StateMark :state="peerState(client)" />
            <span class="hidden font-mono text-[11px] text-muted lg:block">{{ handshake(client) }}</span>
          </div>

          <span class="tabular hidden font-mono text-xs lg:block">
            <template v-if="peerState(client) === 'up'">
              ↓ {{ formatRate(rates[client.id]?.down) }}&nbsp; ↑ {{ formatRate(rates[client.id]?.up) }}
            </template>
            <span
              v-else
              class="text-dimmed"
            >—</span>
          </span>

          <span class="tabular hidden font-mono text-xs text-toned lg:block">
            ↓ {{ formatBytes(stats[client.id]?.transmittedBytes ?? 0) }}&nbsp; ↑ {{ formatBytes(stats[client.id]?.receivedBytes ?? 0) }}
          </span>

          <div class="col-span-3 flex justify-end gap-0.5 lg:col-span-1">
            <UButton
              icon="i-lucide-qr-code"
              variant="ghost"
              size="sm"
              square
              :aria-label="`QR code for ${client.name}`"
              title="QR code"
              @click="openQr(client)"
            />
            <UButton
              icon="i-lucide-download"
              variant="ghost"
              size="sm"
              square
              :aria-label="`Download config for ${client.name}`"
              title="Download config"
              :loading="actionId === client.id"
              @click="downloadConfig(client)"
            />
            <UButton
              icon="i-lucide-link"
              variant="ghost"
              size="sm"
              square
              :aria-label="`Share link for ${client.name}`"
              title="Share link"
              @click="createShare(client)"
            />
            <UButton
              icon="i-lucide-pencil-line"
              variant="ghost"
              size="sm"
              square
              :aria-label="`Rename ${client.name}`"
              title="Rename"
              @click="openEdit(client)"
            />
            <UButton
              :icon="client.enabled ? 'i-lucide-pause' : 'i-lucide-play'"
              variant="ghost"
              size="sm"
              square
              :aria-label="`${client.enabled ? 'Disable' : 'Enable'} ${client.name}`"
              :title="client.enabled ? 'Disable' : 'Enable'"
              @click="toggleClient(client)"
            />
            <UButton
              icon="i-lucide-trash-2"
              color="error"
              variant="ghost"
              size="sm"
              square
              :aria-label="`Delete ${client.name}`"
              title="Delete"
              @click="deleteClient(client)"
            />
          </div>
        </li>
      </ul>
    </SheetSection>

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
            label="Own timings for this peer"
            description="Off inherits the fleet defaults from sheet 3. The wire format is fleet-wide and cannot differ."
          />

          <fieldset
            v-if="useObfuscation"
            class="flex flex-col gap-4 border border-default p-3"
          >
            <legend class="caps px-1 text-muted">
              May differ per client
            </legend>

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
          </fieldset>
        </div>
      </template>

      <template #footer>
        <div class="flex w-full justify-end gap-2">
          <UButton
            variant="ghost"
            @click="createOpen = false"
          >
            Cancel
          </UButton>
          <UButton
            color="primary"
            variant="solid"
            :loading="saving"
            @click="createClient"
          >
            Issue peer
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
            variant="ghost"
            @click="editOpen = false"
          >
            Cancel
          </UButton>
          <UButton
            color="primary"
            variant="solid"
            :loading="actionId === editingClient?.id"
            @click="saveName"
          >
            Save
          </UButton>
        </div>
      </template>
    </UModal>

    <UModal
      v-model:open="qrOpen"
      :title="qrClient ? `Fig. 1 — ${qrClient.name}.conf` : 'Fig. 1'"
    >
      <template #body>
        <figure class="m-0 flex flex-col items-center gap-3">
          <div class="relative p-3">
            <span
              v-for="corner in ['top-0 left-0 border-t border-l', 'top-0 right-0 border-t border-r', 'bottom-0 left-0 border-b border-l', 'bottom-0 right-0 border-b border-r']"
              :key="corner"
              class="absolute size-4 border-accented"
              :class="corner"
              aria-hidden="true"
            />
            <!-- Always black on white: a scanner reads contrast, not the sheet's palette. -->
            <img
              v-if="qrDataUrl"
              :src="qrDataUrl"
              alt="Configuration QR code"
              class="size-72 bg-white p-2"
            >
            <div
              v-else
              class="flex size-72 items-center justify-center font-mono text-xs text-muted"
            >
              drawing…
            </div>
          </div>
          <figcaption class="text-center font-mono text-[11px] text-muted">
            scan in the AmneziaWG app · contains the peer's private key
          </figcaption>
        </figure>
      </template>
    </UModal>

    <UModal
      v-model:open="shareOpen"
      title="Share link"
      :description="share ? `For ${share.clientName}, valid until ${formatUtc(share.expiresAt)}.` : undefined"
    >
      <template #body>
        <div class="flex flex-col gap-4">
          <UAlert
            color="warning"
            variant="subtle"
            icon="i-lucide-triangle-alert"
            title="Anyone holding this link gets the config"
            description="It works without signing in until it expires. Send it the way you would send a password."
          />

          <div class="border border-default bg-muted p-3">
            <code class="block break-all font-mono text-xs text-default">{{ share?.url }}</code>
          </div>

          <UButton
            icon="i-lucide-copy"
            block
            @click="copyShareUrl"
          >
            Copy link
          </UButton>
        </div>
      </template>
    </UModal>
  </div>
</template>
