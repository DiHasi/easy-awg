<script setup lang="ts">
import QRCode from 'qrcode'

type ClientObfuscationOverrides = {
  jc?: number | null
  jmin?: number | null
  jmax?: number | null
  i1?: string | null
  i2?: string | null
  i3?: string | null
  i4?: string | null
  i5?: string | null
}

type Client = {
  id: string
  name: string
  address: string
  publicKey: string
  enabled: boolean
  createdAt: string
  updatedAt: string
  obfuscation?: ClientObfuscationOverrides | null
}

type ApiError = {
  code: string
  message: string
}

type CreateClientBody = {
  name: string
  obfuscation?: ClientObfuscationOverrides
}

type UpdateClientBody = {
  name: string
}

type ClientStats = {
  id: string
  latestHandshakeAt?: string | null
  receivedBytes: number
  transmittedBytes: number
  online: boolean
}

type ClientStatsPayload = ClientStats & {
  Id?: string
  LatestHandshakeAt?: string | null
  ReceivedBytes?: number
  TransmittedBytes?: number
  Online?: boolean
}

type ClientTrafficRate = {
  downloadBytesPerSecond: number
  uploadBytesPerSecond: number
}

type ClientShare = {
  token: string
  clientId: string
  clientName: string
  expiresAt: string
}

const toast = useToast()
const config = useRuntimeConfig()

const clients = ref<Client[]>([])
const stats = ref<Record<string, ClientStats>>({})
const trafficRates = ref<Record<string, ClientTrafficRate>>({})
const loading = ref(true)
const saving = ref(false)
const actionId = ref<string | null>(null)
const errorMessage = ref<string | null>(null)
const statsConnected = ref(false)
const expandedClientIds = ref<Set<string>>(new Set())
const createOpen = ref(false)
const editOpen = ref(false)
const qrOpen = ref(false)
const qrLoading = ref(false)
const qrClient = ref<Client | null>(null)
const qrDataUrl = ref<string | null>(null)
const editingClient = ref<Client | null>(null)
const editName = ref('')
const useObfuscation = ref(false)
let statsSource: EventSource | null = null
let previousStatsSnapshot: Record<string, ClientStats> | null = null
let previousStatsAt: number | null = null

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

const apiBase = computed(() => {
  const value = String(config.public.apiBase || '').replace(/\/$/, '')
  return value || ''
})

const enabledCount = computed(() => clients.value.filter(client => client.enabled).length)
const disabledCount = computed(() => clients.value.length - enabledCount.value)

function apiUrl(path: string) {
  return `${apiBase.value}/api${path}`
}

async function apiFetch<T>(path: string, options: RequestInit = {}): Promise<T> {
  const response = await fetch(apiUrl(path), {
    ...options,
    headers: {
      ...(options.body ? { 'Content-Type': 'application/json' } : {}),
      ...options.headers
    }
  })

  if (!response.ok) {
    let message = `HTTP ${response.status}`
    try {
      const apiError = await response.json() as ApiError
      message = apiError.message || apiError.code || message
    } catch {
      const text = await response.text()
      message = text || message
    }

    throw new Error(message)
  }

  if (response.status === 204) {
    return undefined as T
  }

  return await response.json() as T
}

async function loadClients() {
  loading.value = true
  errorMessage.value = null

  try {
    clients.value = await apiFetch<Client[]>('/clients')
  } catch (error) {
    errorMessage.value = error instanceof Error ? error.message : 'Failed to load clients.'
  } finally {
    loading.value = false
  }
}

function startStatsStream() {
  if (statsSource) {
    statsSource.close()
  }

  statsSource = new EventSource(apiUrl('/clients/events'))

  statsSource.addEventListener('open', () => {
    statsConnected.value = true
  })

  statsSource.addEventListener('error', () => {
    statsConnected.value = false
  })

  statsSource.addEventListener('client-stats', (event) => {
    const items = JSON.parse(event.data) as ClientStatsPayload[]
    const normalized = items.map(normalizeStats)
    updateTrafficRates(normalized)
    stats.value = Object.fromEntries(normalized.map(item => [item.id, item]))
  })
}

function updateTrafficRates(items: ClientStats[]) {
  const now = Date.now()

  if (!previousStatsSnapshot || !previousStatsAt) {
    previousStatsSnapshot = Object.fromEntries(items.map(item => [item.id, item]))
    previousStatsAt = now
    trafficRates.value = Object.fromEntries(items.map(item => [
      item.id,
      { downloadBytesPerSecond: 0, uploadBytesPerSecond: 0 }
    ]))
    return
  }

  const elapsedSeconds = Math.max((now - previousStatsAt) / 1000, 1)
  const nextRates = Object.fromEntries(items.map((item) => {
    const previous = previousStatsSnapshot?.[item.id]
    const isActive = item.online && previous?.online
    const downloadDelta = isActive ? Math.max(item.transmittedBytes - (previous?.transmittedBytes ?? item.transmittedBytes), 0) : 0
    const uploadDelta = isActive ? Math.max(item.receivedBytes - (previous?.receivedBytes ?? item.receivedBytes), 0) : 0

    return [
      item.id,
      {
        downloadBytesPerSecond: downloadDelta / elapsedSeconds,
        uploadBytesPerSecond: uploadDelta / elapsedSeconds
      }
    ]
  }))

  previousStatsSnapshot = Object.fromEntries(items.map(item => [item.id, item]))
  previousStatsAt = now
  trafficRates.value = nextRates
}

function normalizeStats(item: ClientStatsPayload): ClientStats {
  return {
    id: item.id ?? item.Id ?? '',
    latestHandshakeAt: item.latestHandshakeAt ?? item.LatestHandshakeAt ?? null,
    receivedBytes: item.receivedBytes ?? item.ReceivedBytes ?? 0,
    transmittedBytes: item.transmittedBytes ?? item.TransmittedBytes ?? 0,
    online: item.online ?? item.Online ?? false
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
  useObfuscation.value = false
}

function cleanText(value: string) {
  const trimmed = value.trim()
  return trimmed.length > 0 ? trimmed : undefined
}

function buildObfuscation(): ClientObfuscationOverrides | undefined {
  if (!useObfuscation.value) {
    return undefined
  }

  const obfuscation: ClientObfuscationOverrides = {
    jc: form.jc ?? undefined,
    jmin: form.jmin ?? undefined,
    jmax: form.jmax ?? undefined,
    i1: cleanText(form.i1),
    i2: cleanText(form.i2),
    i3: cleanText(form.i3),
    i4: cleanText(form.i4),
    i5: cleanText(form.i5)
  }

  return Object.values(obfuscation).some(value => value !== undefined && value !== null)
    ? obfuscation
    : undefined
}

async function createClient() {
  const name = form.name.trim()
  if (!name) {
    toast.add({ title: 'Name is required', color: 'error', icon: 'i-lucide-circle-alert' })
    return
  }

  saving.value = true

  try {
    const body: CreateClientBody = {
      name,
      obfuscation: buildObfuscation()
    }

    const client = await apiFetch<Client>('/clients', {
      method: 'POST',
      body: JSON.stringify(body)
    })

    clients.value = [...clients.value, client]
    createOpen.value = false
    resetForm()
    toast.add({ title: 'Client created', color: 'success', icon: 'i-lucide-check' })
  } catch (error) {
    toast.add({
      title: 'Could not create client',
      description: error instanceof Error ? error.message : undefined,
      color: 'error',
      icon: 'i-lucide-circle-alert'
    })
  } finally {
    saving.value = false
  }
}

function openEditClient(client: Client) {
  editingClient.value = client
  editName.value = client.name
  editOpen.value = true
}

async function saveClientName() {
  const client = editingClient.value
  const name = editName.value.trim()

  if (!client) {
    return
  }

  if (!name) {
    toast.add({ title: 'Name is required', color: 'error', icon: 'i-lucide-circle-alert' })
    return
  }

  actionId.value = client.id

  try {
    const body: UpdateClientBody = { name }
    const next = await apiFetch<Client>(`/clients/${client.id}`, {
      method: 'PUT',
      body: JSON.stringify(body)
    })

    clients.value = clients.value.map(item => item.id === next.id ? next : item)
    editOpen.value = false
    editingClient.value = null
    editName.value = ''
    toast.add({ title: 'Client renamed', color: 'success', icon: 'i-lucide-check' })
  } catch (error) {
    toast.add({
      title: 'Could not rename client',
      description: error instanceof Error ? error.message : undefined,
      color: 'error',
      icon: 'i-lucide-circle-alert'
    })
  } finally {
    actionId.value = null
  }
}

async function toggleClient(client: Client) {
  actionId.value = client.id

  try {
    const next = await apiFetch<Client>(`/clients/${client.id}/${client.enabled ? 'disable' : 'enable'}`, {
      method: 'POST'
    })

    clients.value = clients.value.map(item => item.id === next.id ? next : item)
    toast.add({
      title: next.enabled ? 'Client enabled' : 'Client disabled',
      color: 'success',
      icon: next.enabled ? 'i-lucide-power' : 'i-lucide-power-off'
    })
  } catch (error) {
    toast.add({
      title: 'Could not update client',
      description: error instanceof Error ? error.message : undefined,
      color: 'error',
      icon: 'i-lucide-circle-alert'
    })
  } finally {
    actionId.value = null
  }
}

async function deleteClient(client: Client) {
  if (!confirm(`Delete ${client.name}?`)) {
    return
  }

  actionId.value = client.id

  try {
    await apiFetch<unknown>(`/clients/${client.id}`, { method: 'DELETE' })
    clients.value = clients.value.filter(item => item.id !== client.id)
    toast.add({ title: 'Client deleted', color: 'success', icon: 'i-lucide-trash-2' })
  } catch (error) {
    toast.add({
      title: 'Could not delete client',
      description: error instanceof Error ? error.message : undefined,
      color: 'error',
      icon: 'i-lucide-circle-alert'
    })
  } finally {
    actionId.value = null
  }
}

function isClientExpanded(client: Client) {
  return expandedClientIds.value.has(client.id)
}

function toggleClientExpanded(client: Client) {
  const next = new Set(expandedClientIds.value)
  if (next.has(client.id)) {
    next.delete(client.id)
  } else {
    next.add(client.id)
  }

  expandedClientIds.value = next
}

async function shareConfig(client: Client) {
  actionId.value = client.id

  try {
    const share = await apiFetch<ClientShare>(`/clients/${client.id}/share`, {
      method: 'POST'
    })
    const url = `${window.location.origin}/share/${share.token}`
    window.open(url, '_blank', 'noopener,noreferrer')
    toast.add({ title: 'Share link created', color: 'success', icon: 'i-lucide-share-2' })
  } catch (error) {
    toast.add({
      title: 'Could not create share link',
      description: error instanceof Error ? error.message : undefined,
      color: 'error',
      icon: 'i-lucide-circle-alert'
    })
  } finally {
    actionId.value = null
  }
}

function configFileName(client: Client, extension: string) {
  const name = client.name.replace(/[^a-z0-9_.-]+/gi, '-')
  return `${name}.${extension}`
}

async function fetchClientConfig(client: Client) {
  const response = await fetch(apiUrl(`/clients/${client.id}/config`))
  if (!response.ok) {
    throw new Error(`HTTP ${response.status}`)
  }

  return await response.text()
}

async function downloadConfig(client: Client) {
  actionId.value = client.id

  try {
    const configText = await fetchClientConfig(client)
    const blob = new Blob([configText], { type: 'text/plain;charset=utf-8' })
    const url = URL.createObjectURL(blob)
    const link = document.createElement('a')
    link.href = url
    link.download = configFileName(client, 'conf')
    document.body.appendChild(link)
    link.click()
    link.remove()
    URL.revokeObjectURL(url)
  } catch (error) {
    toast.add({
      title: 'Could not download config',
      description: error instanceof Error ? error.message : undefined,
      color: 'error',
      icon: 'i-lucide-circle-alert'
    })
  } finally {
    actionId.value = null
  }
}

async function openQrCode(client: Client) {
  qrOpen.value = true
  qrLoading.value = true
  qrClient.value = client
  qrDataUrl.value = null
  actionId.value = client.id

  try {
    const configText = await fetchClientConfig(client)
    qrDataUrl.value = await QRCode.toDataURL(configText, {
      errorCorrectionLevel: 'M',
      margin: 2,
      width: 320
    })
  } catch (error) {
    qrOpen.value = false
    toast.add({
      title: 'Could not generate QR code',
      description: error instanceof Error ? error.message : undefined,
      color: 'error',
      icon: 'i-lucide-circle-alert'
    })
  } finally {
    qrLoading.value = false
    actionId.value = null
  }
}

function downloadQrCode() {
  if (!qrClient.value || !qrDataUrl.value) {
    return
  }

  const link = document.createElement('a')
  link.href = qrDataUrl.value
  link.download = configFileName(qrClient.value, 'png')
  document.body.appendChild(link)
  link.click()
  link.remove()
}

function formatRelativeTime(value?: string | null) {
  if (!value) {
    return 'Never'
  }

  const diffSeconds = Math.round((new Date(value).getTime() - Date.now()) / 1000)
  const absSeconds = Math.abs(diffSeconds)
  const units: Array<[Intl.RelativeTimeFormatUnit, number]> = [
    ['year', 60 * 60 * 24 * 365],
    ['month', 60 * 60 * 24 * 30],
    ['week', 60 * 60 * 24 * 7],
    ['day', 60 * 60 * 24],
    ['hour', 60 * 60],
    ['minute', 60],
    ['second', 1]
  ]

  const [unit, secondsPerUnit] = units.find(([, seconds]) => absSeconds >= seconds) ?? ['second', 1]
  const valueInUnits = Math.round(diffSeconds / secondsPerUnit)

  return new Intl.RelativeTimeFormat('en', { numeric: 'auto' }).format(valueInUnits, unit)
}

function formatBytes(value?: number) {
  const bytes = value ?? 0
  if (bytes < 1024) {
    return `${bytes.toFixed(1)} B`
  }

  const units = ['KB', 'MB', 'GB', 'TB']
  let size = bytes / 1024
  let unit = 0
  while (size >= 1024 && unit < units.length - 1) {
    size /= 1024
    unit++
  }

  return `${size.toFixed(1)} ${units[unit]}`
}

function formatSpeed(value?: number) {
  return `${formatBytes(value)}/s`
}

function isOnline(client: Client) {
  return stats.value[client.id]?.online ?? false
}

function downloadSpeed(client: Client) {
  return isOnline(client) ? trafficRates.value[client.id]?.downloadBytesPerSecond : 0
}

function uploadSpeed(client: Client) {
  return isOnline(client) ? trafficRates.value[client.id]?.uploadBytesPerSecond : 0
}

function totalDownloadedBytes(client: Client) {
  const clientStats = stats.value[client.id]
  return clientStats?.transmittedBytes ?? 0
}

function totalUploadedBytes(client: Client) {
  const clientStats = stats.value[client.id]
  return clientStats?.receivedBytes ?? 0
}

onMounted(async () => {
  await loadClients()
  startStatsStream()
})

onBeforeUnmount(() => {
  statsSource?.close()
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
            {{ clients.length }} total · {{ enabledCount }} enabled · {{ disabledCount }} disabled
          </p>
          <p class="mt-1 flex items-center gap-1 text-xs text-muted">
            <UIcon
              :name="statsConnected ? 'i-lucide-radio' : 'i-lucide-radio-tower'"
              :class="statsConnected ? 'text-success' : 'text-muted'"
            />
            {{ statsConnected ? 'Live stats connected' : 'Live stats disconnected' }}
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
          class="flex min-h-64 items-center justify-center"
        >
          <div class="flex items-center gap-3 text-sm text-muted">
            <UIcon
              name="i-lucide-loader-circle"
              class="size-5 animate-spin"
            />
            Loading clients
          </div>
        </div>

        <div
          v-else-if="clients.length === 0"
          class="flex min-h-64 flex-col items-center justify-center gap-4 px-6 text-center"
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
              Create a client to generate the first configuration.
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
          class="md:hidden"
        >
          <div class="divide-y divide-default">
            <article
              v-for="client in clients"
              :key="client.id"
              class="p-4"
            >
              <button
                type="button"
                class="flex w-full min-w-0 items-start justify-between gap-3 text-left"
                :aria-expanded="isClientExpanded(client)"
                @click="toggleClientExpanded(client)"
              >
                <div class="min-w-0">
                  <div class="flex min-w-0 items-center gap-2">
                    <h2 class="min-w-0 break-words text-base font-medium text-highlighted">
                      {{ client.name }}
                    </h2>
                    <UBadge
                      v-if="isOnline(client)"
                      color="success"
                      variant="subtle"
                      icon="i-lucide-wifi"
                    >
                      Online
                    </UBadge>
                  </div>
                  <p class="mt-1 font-mono text-sm text-default">
                    {{ client.address }}
                  </p>
                </div>
                <UIcon
                  name="i-lucide-chevron-down"
                  class="mt-1 size-5 shrink-0 text-muted transition-transform"
                  :class="isClientExpanded(client) ? 'rotate-180' : ''"
                />
              </button>

              <div
                v-if="isClientExpanded(client)"
                class="mt-4 grid grid-cols-3 gap-2"
              >
                <UButton
                  icon="i-lucide-pencil"
                  color="neutral"
                  variant="subtle"
                  block
                  :loading="actionId === client.id"
                  aria-label="Edit client name"
                  @click="openEditClient(client)"
                />
                <UButton
                  icon="i-lucide-download"
                  color="neutral"
                  variant="subtle"
                  block
                  :loading="actionId === client.id"
                  aria-label="Download config"
                  @click="downloadConfig(client)"
                />
                <UButton
                  icon="i-lucide-share-2"
                  color="neutral"
                  variant="subtle"
                  block
                  :loading="actionId === client.id"
                  aria-label="Share config"
                  @click="shareConfig(client)"
                />
                <UButton
                  icon="i-lucide-qr-code"
                  color="neutral"
                  variant="subtle"
                  block
                  :loading="actionId === client.id"
                  aria-label="Generate QR code"
                  @click="openQrCode(client)"
                />
                <UButton
                  :icon="client.enabled ? 'i-lucide-power-off' : 'i-lucide-power'"
                  :color="client.enabled ? 'warning' : 'success'"
                  variant="subtle"
                  block
                  :loading="actionId === client.id"
                  :aria-label="client.enabled ? 'Disable client' : 'Enable client'"
                  @click="toggleClient(client)"
                />
                <UButton
                  icon="i-lucide-trash-2"
                  color="error"
                  variant="subtle"
                  block
                  :loading="actionId === client.id"
                  aria-label="Delete client"
                  @click="deleteClient(client)"
                />
              </div>

              <div class="mt-4 grid grid-cols-2 gap-3">
                <div class="rounded-md bg-muted/40 p-3">
                  <p class="text-xs text-muted">
                    Down
                  </p>
                  <p class="mt-1 flex items-center gap-1 text-sm font-medium text-highlighted">
                    <UIcon
                      name="i-lucide-arrow-down"
                      class="size-4 text-muted"
                    />
                    {{ formatSpeed(downloadSpeed(client)) }}
                  </p>
                </div>

                <div class="rounded-md bg-muted/40 p-3">
                  <p class="text-xs text-muted">
                    Up
                  </p>
                  <p class="mt-1 flex items-center gap-1 text-sm font-medium text-highlighted">
                    <UIcon
                      name="i-lucide-arrow-up"
                      class="size-4 text-muted"
                    />
                    {{ formatSpeed(uploadSpeed(client)) }}
                  </p>
                </div>

                <div class="rounded-md bg-muted/40 p-3">
                  <p class="text-xs text-muted">
                    Total received
                  </p>
                  <p class="mt-1 flex items-center gap-1 text-sm font-medium text-highlighted">
                    <UIcon
                      name="i-lucide-arrow-down-to-line"
                      class="size-4 text-muted"
                    />
                    {{ formatBytes(totalDownloadedBytes(client)) }}
                  </p>
                </div>

                <div class="rounded-md bg-muted/40 p-3">
                  <p class="text-xs text-muted">
                    Total sent
                  </p>
                  <p class="mt-1 flex items-center gap-1 text-sm font-medium text-highlighted">
                    <UIcon
                      name="i-lucide-arrow-up-to-line"
                      class="size-4 text-muted"
                    />
                    {{ formatBytes(totalUploadedBytes(client)) }}
                  </p>
                </div>

                <div class="col-span-2 rounded-md bg-muted/40 p-3">
                  <p class="text-xs text-muted">
                    Last handshake
                  </p>
                  <p class="mt-1 text-sm font-medium text-highlighted">
                    {{ formatRelativeTime(stats[client.id]?.latestHandshakeAt) }}
                  </p>
                </div>
              </div>
            </article>
          </div>
        </div>

        <div
          v-if="!loading && clients.length > 0"
          class="hidden overflow-x-auto md:block"
        >
          <table class="min-w-full divide-y divide-default text-sm">
            <thead class="bg-muted/40">
              <tr>
                <th class="px-4 py-3 text-left font-medium text-muted">
                  Name
                </th>
                <th class="px-4 py-3 text-left font-medium text-muted">
                  Address
                </th>
                <th class="px-4 py-3 text-left font-medium text-muted">
                  Online
                </th>
                <th class="px-4 py-3 text-left font-medium text-muted">
                  Traffic
                </th>
                <th class="px-4 py-3 text-left font-medium text-muted">
                  Last handshake
                </th>
                <th
                  class="px-4 py-3 text-right font-medium text-muted"
                >
                  Actions
                </th>
              </tr>
            </thead>
            <tbody class="divide-y divide-default">
              <tr
                v-for="client in clients"
                :key="client.id"
                class="hover:bg-muted/30"
              >
                <td class="px-4 py-3">
                  <div class="min-w-52">
                    <p class="font-medium text-highlighted">
                      {{ client.name }}
                    </p>
                  </div>
                </td>
                <td class="px-4 py-3 font-mono text-sm text-default">
                  {{ client.address }}
                </td>
                <td class="px-4 py-3">
                  <UBadge
                    v-if="isOnline(client)"
                    color="success"
                    variant="subtle"
                    icon="i-lucide-wifi"
                  >
                    Online
                  </UBadge>
                  <span
                    v-else
                    class="text-sm text-muted"
                  >
                    Offline
                  </span>
                </td>
                <td class="px-4 py-3">
                  <div class="grid min-w-36 gap-1 text-xs">
                    <div class="flex items-center gap-1 font-medium text-highlighted">
                      <UIcon
                        name="i-lucide-arrow-down-to-line"
                        class="size-3.5 text-muted"
                      />
                      Total down {{ formatBytes(totalDownloadedBytes(client)) }}
                    </div>
                    <div class="flex items-center gap-1 font-medium text-highlighted">
                      <UIcon
                        name="i-lucide-arrow-up-to-line"
                        class="size-3.5 text-muted"
                      />
                      Total up {{ formatBytes(totalUploadedBytes(client)) }}
                    </div>
                    <div class="flex items-center gap-1 text-default">
                      <UIcon
                        name="i-lucide-arrow-down"
                        class="size-3.5 text-muted"
                      />
                      Down {{ formatSpeed(downloadSpeed(client)) }}
                    </div>
                    <div class="flex items-center gap-1 text-default">
                      <UIcon
                        name="i-lucide-arrow-up"
                        class="size-3.5 text-muted"
                      />
                      Up {{ formatSpeed(uploadSpeed(client)) }}
                    </div>
                  </div>
                </td>
                <td class="px-4 py-3 text-muted">
                  {{ formatRelativeTime(stats[client.id]?.latestHandshakeAt) }}
                </td>
                <td
                  class="px-4 py-3"
                >
                  <div class="flex justify-end gap-1">
                    <UButton
                      icon="i-lucide-pencil"
                      color="neutral"
                      variant="ghost"
                      :loading="actionId === client.id"
                      aria-label="Edit client name"
                      @click="openEditClient(client)"
                    />
                    <UButton
                      icon="i-lucide-download"
                      color="neutral"
                      variant="ghost"
                      :loading="actionId === client.id"
                      aria-label="Download config"
                      @click="downloadConfig(client)"
                    />
                    <UButton
                      icon="i-lucide-share-2"
                      color="neutral"
                      variant="ghost"
                      :loading="actionId === client.id"
                      aria-label="Share config"
                      @click="shareConfig(client)"
                    />
                    <UButton
                      icon="i-lucide-qr-code"
                      color="neutral"
                      variant="ghost"
                      :loading="actionId === client.id"
                      aria-label="Generate QR code"
                      @click="openQrCode(client)"
                    />
                    <UButton
                      :icon="client.enabled ? 'i-lucide-power-off' : 'i-lucide-power'"
                      :color="client.enabled ? 'warning' : 'success'"
                      variant="ghost"
                      :loading="actionId === client.id"
                      :aria-label="client.enabled ? 'Disable client' : 'Enable client'"
                      @click="toggleClient(client)"
                    />
                    <UButton
                      icon="i-lucide-trash-2"
                      color="error"
                      variant="ghost"
                      :loading="actionId === client.id"
                      aria-label="Delete client"
                      @click="deleteClient(client)"
                    />
                  </div>
                </td>
              </tr>
            </tbody>
          </table>
        </div>
      </div>
    </div>

    <div
      v-if="createOpen"
      class="fixed inset-0 z-50 flex items-center justify-center bg-black/45 p-4"
      @click.self="createOpen = false"
    >
      <div class="max-h-[90vh] w-full max-w-2xl overflow-y-auto rounded-lg border border-default bg-default shadow-xl">
        <div class="flex items-center justify-between border-b border-default px-5 py-4">
          <div>
            <h2 class="text-base font-semibold text-highlighted">
              Add client
            </h2>
            <p class="mt-1 text-sm text-muted">
              Optional obfuscation values override server defaults.
            </p>
          </div>
          <UButton
            icon="i-lucide-x"
            color="neutral"
            variant="ghost"
            aria-label="Close"
            @click="createOpen = false"
          />
        </div>

        <div class="space-y-5 px-5 py-5">
          <UFormField
            label="Name"
            required
          >
            <UInput
              v-model="form.name"
              placeholder="phone-ivan"
              autofocus
            />
          </UFormField>

          <UCheckbox
            v-model="useObfuscation"
            label="Override client obfuscation"
          />

          <div
            v-if="useObfuscation"
            class="space-y-5 rounded-lg border border-default p-4"
          >
            <div class="grid gap-4 sm:grid-cols-3">
              <UFormField label="Jc">
                <UInput
                  v-model.number="form.jc"
                  type="number"
                  min="1"
                  max="128"
                  placeholder="6"
                />
              </UFormField>
              <UFormField label="Jmin">
                <UInput
                  v-model.number="form.jmin"
                  type="number"
                  min="0"
                  max="1280"
                  placeholder="5"
                />
              </UFormField>
              <UFormField label="Jmax">
                <UInput
                  v-model.number="form.jmax"
                  type="number"
                  min="1"
                  max="1280"
                  placeholder="80"
                />
              </UFormField>
            </div>

            <div class="grid gap-4 sm:grid-cols-2">
              <UFormField label="I1">
                <UInput
                  v-model="form.i1"
                  placeholder="<r 16>"
                />
              </UFormField>
              <UFormField label="I2">
                <UInput
                  v-model="form.i2"
                  placeholder="<t>"
                />
              </UFormField>
              <UFormField label="I3">
                <UInput v-model="form.i3" />
              </UFormField>
              <UFormField label="I4">
                <UInput v-model="form.i4" />
              </UFormField>
              <UFormField
                label="I5"
                class="sm:col-span-2"
              >
                <UInput v-model="form.i5" />
              </UFormField>
            </div>
          </div>
        </div>

        <div class="flex justify-end gap-2 border-t border-default px-5 py-4">
          <UButton
            color="neutral"
            variant="subtle"
            @click="createOpen = false"
          >
            Cancel
          </UButton>
          <UButton
            icon="i-lucide-plus"
            :loading="saving"
            @click="createClient"
          >
            Create
          </UButton>
        </div>
      </div>
    </div>

    <div
      v-if="qrOpen"
      class="fixed inset-0 z-50 flex items-center justify-center bg-black/45 p-4"
      @click.self="qrOpen = false"
    >
      <div class="w-full max-w-sm rounded-lg border border-default bg-default shadow-xl">
        <div class="flex items-center justify-between border-b border-default px-5 py-4">
          <div class="min-w-0">
            <h2 class="truncate text-base font-semibold text-highlighted">
              {{ qrClient?.name }} QR
            </h2>
            <p class="mt-1 text-sm text-muted">
              Client configuration
            </p>
          </div>
          <UButton
            icon="i-lucide-x"
            color="neutral"
            variant="ghost"
            aria-label="Close"
            @click="qrOpen = false"
          />
        </div>

        <div class="flex min-h-80 items-center justify-center px-5 py-5">
          <div
            v-if="qrLoading"
            class="flex items-center gap-3 text-sm text-muted"
          >
            <UIcon
              name="i-lucide-loader-circle"
              class="size-5 animate-spin"
            />
            Generating QR code
          </div>
          <img
            v-else-if="qrDataUrl"
            :src="qrDataUrl"
            :alt="`${qrClient?.name} configuration QR code`"
            class="size-80 max-w-full rounded-md bg-white p-3"
          >
        </div>

        <div class="flex justify-end gap-2 border-t border-default px-5 py-4">
          <UButton
            color="neutral"
            variant="subtle"
            @click="qrOpen = false"
          >
            Close
          </UButton>
          <UButton
            icon="i-lucide-download"
            :disabled="!qrDataUrl"
            @click="downloadQrCode"
          >
            Download PNG
          </UButton>
        </div>
      </div>
    </div>

    <div
      v-if="editOpen"
      class="fixed inset-0 z-50 flex items-center justify-center bg-black/45 p-4"
      @click.self="editOpen = false"
    >
      <div class="w-full max-w-md rounded-lg border border-default bg-default shadow-xl">
        <div class="flex items-center justify-between border-b border-default px-5 py-4">
          <div>
            <h2 class="text-base font-semibold text-highlighted">
              Edit client
            </h2>
            <p class="mt-1 text-sm text-muted">
              Only the display name is changed.
            </p>
          </div>
          <UButton
            icon="i-lucide-x"
            color="neutral"
            variant="ghost"
            aria-label="Close"
            @click="editOpen = false"
          />
        </div>

        <div class="px-5 py-5">
          <UFormField
            label="Name"
            required
          >
            <UInput
              v-model="editName"
              autofocus
              @keyup.enter="saveClientName"
            />
          </UFormField>
        </div>

        <div class="flex justify-end gap-2 border-t border-default px-5 py-4">
          <UButton
            color="neutral"
            variant="subtle"
            @click="editOpen = false"
          >
            Cancel
          </UButton>
          <UButton
            icon="i-lucide-save"
            :loading="actionId === editingClient?.id"
            @click="saveClientName"
          >
            Save
          </UButton>
        </div>
      </div>
    </div>
  </UContainer>
</template>
