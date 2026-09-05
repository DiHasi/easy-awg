<script setup lang="ts">
import QRCode from 'qrcode'
import type { Client, ClientObfuscationOverrides, ClientShare, ClientStats } from '~/types/api'

definePageMeta({ middleware: 'auth' })

const api = useControlApi()
const toast = useToast()

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

const form = reactive({
  name: '',
  jc: undefined as number | undefined,
  jmin: undefined as number | undefined,
  jmax: undefined as number | undefined,
  i1: '',
  i2: '',
  i3: '',
  i4: '',
  i5: ''
})

// Counters are cumulative, so a rate only means something as a delta between two samples.
let previousStats: Record<string, ClientStats> | null = null
let previousAt: number | null = null
let statsTimer: ReturnType<typeof setInterval> | null = null

const enabledCount = computed(() => clients.value.filter(client => client.enabled).length)
const disabledCount = computed(() => clients.value.length - enabledCount.value)
const onlineCount = computed(() => clients.value.filter(client => stats.value[client.id]?.online).length)

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
    i5: clean(form.i5)
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
    succeed('Client created')
  } catch (error) {
    fail('Could not create client', error)
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
    succeed('Client renamed')
  } catch (error) {
    fail('Could not rename client', error)
  } finally {
    actionId.value = null
  }
}

async function toggleClient(client: Client) {
  actionId.value = client.id

  try {
    const updated = await api.post<Client>(`/clients/${client.id}/${client.enabled ? 'disable' : 'enable'}`)
    clients.value = clients.value.map(item => item.id === updated.id ? updated : item)
    succeed(updated.enabled ? 'Client enabled' : 'Client disabled')
  } catch (error) {
    fail('Could not change the client', error)
  } finally {
    actionId.value = null
  }
}

async function deleteClient(client: Client) {
  if (!confirm(`Delete "${client.name}"? Its config stops working immediately.`)) {
    return
  }

  actionId.value = client.id

  try {
    await api.del(`/clients/${client.id}`)
    clients.value = clients.value.filter(item => item.id !== client.id)
    succeed('Client deleted')
  } catch (error) {
    fail('Could not delete client', error)
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

function formatBytes(bytes: number) {
  if (!bytes) {
    return '0 B'
  }

  const units = ['B', 'KB', 'MB', 'GB', 'TB']
  const index = Math.min(Math.floor(Math.log(bytes) / Math.log(1024)), units.length - 1)
  return `${(bytes / 1024 ** index).toFixed(index === 0 ? 0 : 1)} ${units[index]}`
}

function formatRate(bytesPerSecond?: number) {
  return `${formatBytes(bytesPerSecond ?? 0)}/s`
}

function handshakeLabel(client: Client) {
  const at = stats.value[client.id]?.latestHandshakeAt
  if (!at) {
    return 'never'
  }

  const seconds = Math.max(0, Math.round((Date.now() - new Date(at).getTime()) / 1000))
  if (seconds < 60) {
    return `${seconds}s ago`
  }
  if (seconds < 3600) {
    return `${Math.round(seconds / 60)}m ago`
  }
  if (seconds < 86400) {
    return `${Math.round(seconds / 3600)}h ago`
  }
  return `${Math.round(seconds / 86400)}d ago`
}

onMounted(async () => {
  await loadClients()
  await loadStats()
  statsTimer = setInterval(loadStats, 3000)
})

onBeforeUnmount(() => {
  if (statsTimer) {
    clearInterval(statsTimer)
  }
})
</script>

<template>
  <UContainer class="py-6">
    <div class="flex flex-col gap-6">
      <section class="flex flex-col gap-4 md:flex-row md:items-end md:justify-between">
        <div class="min-w-0">
          <h1 class="text-2xl font-semibold tracking-tight text-highlighted">
            Clients
          </h1>
          <p class="mt-1 text-sm text-muted">
            {{ clients.length }} total · {{ enabledCount }} enabled · {{ disabledCount }} disabled · {{ onlineCount }} online
          </p>
        </div>

        <div class="flex flex-wrap gap-2">
          <UButton
            icon="i-lucide-refresh-cw"
            color="neutral"
            variant="subtle"
            :loading="loading"
            @click="loadClients"
          >
            Refresh
          </UButton>
          <UButton
            icon="i-lucide-plus"
            @click="createOpen = true"
          >
            Add client
          </UButton>
        </div>
      </section>

      <UAlert
        v-if="errorMessage"
        color="error"
        variant="subtle"
        icon="i-lucide-circle-alert"
        title="Could not load clients"
        :description="errorMessage"
      />

      <div class="overflow-hidden rounded-lg border border-default bg-default">
        <div
          v-if="loading"
          class="flex min-h-48 items-center justify-center text-sm text-muted"
        >
          <UIcon
            name="i-lucide-loader-circle"
            class="mr-2 size-5 animate-spin"
          />
          Loading clients
        </div>

        <div
          v-else-if="clients.length === 0"
          class="flex min-h-48 flex-col items-center justify-center gap-4 px-6 py-10 text-center"
        >
          <div class="flex size-12 items-center justify-center rounded-full bg-muted">
            <UIcon
              name="i-lucide-users"
              class="size-6 text-muted"
            />
          </div>
          <div>
            <h2 class="text-base font-medium text-highlighted">
              No clients yet
            </h2>
            <p class="mt-1 text-sm text-muted">
              Create a client to generate its configuration.
            </p>
          </div>
          <UButton
            icon="i-lucide-plus"
            @click="createOpen = true"
          >
            Add client
          </UButton>
        </div>

        <div
          v-else
          class="divide-y divide-default"
        >
          <article
            v-for="client in clients"
            :key="client.id"
            class="flex flex-col gap-3 p-4 lg:flex-row lg:items-center lg:justify-between"
          >
            <div class="min-w-0 lg:w-64">
              <div class="flex flex-wrap items-center gap-2">
                <h2 class="break-words text-base font-medium text-highlighted">
                  {{ client.name }}
                </h2>
                <UBadge
                  v-if="stats[client.id]?.online"
                  color="success"
                  variant="subtle"
                  icon="i-lucide-wifi"
                >
                  Online
                </UBadge>
                <UBadge
                  v-else-if="!client.enabled"
                  color="neutral"
                  variant="subtle"
                >
                  Disabled
                </UBadge>
              </div>
              <p class="mt-1 font-mono text-sm text-muted">
                {{ client.address }}
              </p>
            </div>

            <dl class="grid grid-cols-2 gap-x-6 gap-y-1 text-sm sm:grid-cols-4 lg:flex-1">
              <div>
                <dt class="text-xs text-muted">
                  Handshake
                </dt>
                <dd class="text-default">
                  {{ handshakeLabel(client) }}
                </dd>
              </div>
              <div>
                <dt class="text-xs text-muted">
                  Speed
                </dt>
                <dd class="text-default">
                  ↓ {{ formatRate(rates[client.id]?.down) }} · ↑ {{ formatRate(rates[client.id]?.up) }}
                </dd>
              </div>
              <div>
                <dt class="text-xs text-muted">
                  Downloaded
                </dt>
                <dd class="text-default">
                  {{ formatBytes(stats[client.id]?.transmittedBytes ?? 0) }}
                </dd>
              </div>
              <div>
                <dt class="text-xs text-muted">
                  Uploaded
                </dt>
                <dd class="text-default">
                  {{ formatBytes(stats[client.id]?.receivedBytes ?? 0) }}
                </dd>
              </div>
            </dl>

            <div class="flex shrink-0 flex-wrap gap-1">
              <UButton
                icon="i-lucide-download"
                color="neutral"
                variant="subtle"
                aria-label="Download config"
                :loading="actionId === client.id"
                @click="downloadConfig(client)"
              />
              <UButton
                icon="i-lucide-qr-code"
                color="neutral"
                variant="subtle"
                aria-label="Show QR code"
                @click="openQr(client)"
              />
              <UButton
                icon="i-lucide-share-2"
                color="neutral"
                variant="subtle"
                aria-label="Create share link"
                :loading="actionId === client.id"
                @click="createShare(client)"
              />
              <UButton
                icon="i-lucide-pencil"
                color="neutral"
                variant="subtle"
                aria-label="Rename"
                @click="openEdit(client)"
              />
              <UButton
                :icon="client.enabled ? 'i-lucide-power-off' : 'i-lucide-power'"
                :color="client.enabled ? 'warning' : 'success'"
                variant="subtle"
                :aria-label="client.enabled ? 'Disable' : 'Enable'"
                :loading="actionId === client.id"
                @click="toggleClient(client)"
              />
              <UButton
                icon="i-lucide-trash-2"
                color="error"
                variant="subtle"
                aria-label="Delete"
                :loading="actionId === client.id"
                @click="deleteClient(client)"
              />
            </div>
          </article>
        </div>
      </div>
    </div>

    <UModal
      v-model:open="createOpen"
      title="Add a client"
    >
      <template #body>
        <div class="flex flex-col gap-4">
          <UFormField label="Name">
            <UInput
              v-model="form.name"
              placeholder="phone"
              class="w-full"
              @keyup.enter="createClient"
            />
          </UFormField>

          <USwitch
            v-model="useObfuscation"
            label="Override obfuscation for this client"
            description="Leave off to inherit the fleet defaults."
          />

          <div
            v-if="useObfuscation"
            class="flex flex-col gap-3 rounded-md border border-default p-3"
          >
            <div class="grid grid-cols-3 gap-2">
              <UFormField label="Jc">
                <UInput
                  v-model.number="form.jc"
                  type="number"
                  class="w-full"
                />
              </UFormField>
              <UFormField label="Jmin">
                <UInput
                  v-model.number="form.jmin"
                  type="number"
                  class="w-full"
                />
              </UFormField>
              <UFormField label="Jmax">
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
              :label="field.toUpperCase()"
            >
              <UInput
                v-model="form[field]"
                class="w-full"
                placeholder="<b 0x...>"
              />
            </UFormField>
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
            Create
          </UButton>
        </div>
      </template>
    </UModal>

    <UModal
      v-model:open="editOpen"
      title="Rename client"
    >
      <template #body>
        <UFormField label="Name">
          <UInput
            v-model="editName"
            class="w-full"
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
          <UButton @click="saveName">
            Save
          </UButton>
        </div>
      </template>
    </UModal>

    <UModal
      v-model:open="qrOpen"
      :title="qrClient ? `QR code for ${qrClient.name}` : 'QR code'"
    >
      <template #body>
        <div class="flex min-h-72 items-center justify-center">
          <img
            v-if="qrDataUrl"
            :src="qrDataUrl"
            alt="Client configuration QR code"
            class="rounded-md bg-white p-2"
          >
          <UIcon
            v-else
            name="i-lucide-loader-circle"
            class="size-6 animate-spin text-muted"
          />
        </div>
      </template>
    </UModal>

    <UModal
      v-model:open="shareOpen"
      title="Share link"
    >
      <template #body>
        <div class="flex flex-col gap-4">
          <UAlert
            color="warning"
            variant="subtle"
            icon="i-lucide-triangle-alert"
            title="Anyone with this link gets the config"
            description="It works without signing in and expires in 24 hours."
          />

          <div class="rounded-md border border-default bg-muted/40 p-3">
            <code class="block break-all font-mono text-xs text-default">{{ share?.url }}</code>
          </div>

          <UButton
            icon="i-lucide-copy"
            color="neutral"
            variant="subtle"
            block
            @click="copyShareUrl"
          >
            Copy link
          </UButton>
        </div>
      </template>
    </UModal>
  </UContainer>
</template>
