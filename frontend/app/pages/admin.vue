<script setup lang="ts">
type ServerObfuscationProfile = {
  s1?: number | null
  s2?: number | null
  s3?: number | null
  s4?: number | null
  h1?: string | null
  h2?: string | null
  h3?: string | null
  h4?: string | null
  defaultJc?: number | null
  defaultJmin?: number | null
  defaultJmax?: number | null
  defaultI1?: string | null
  defaultI2?: string | null
  defaultI3?: string | null
  defaultI4?: string | null
  defaultI5?: string | null
}

type Server = {
  interfaceName: string
  awgPort: number
  tunnelSubnet: string
  clientAllowedIps: string
  endpointHost: string
  serverPublicKey: string
  serverObfuscation?: ServerObfuscationProfile | null
  clientsCount: number
}

type RuntimeStatus = {
  interfaceName: string
  isRunning: boolean
  backend?: string | null
  tools: {
    awg?: string | null
    awgQuick?: string | null
    amneziawgGo?: string | null
    userspaceImplementation?: string | null
  }
  awgShow: {
    command: string
    exitCode: number
    output: string
    error: string
  }
  ipAddress: {
    command: string
    exitCode: number
    output: string
    error: string
  }
}

type ApiError = {
  code: string
  message: string
}

type AwgEasyOptions = {
  configPath: string
  interfaceConfigPath: string
  interfaceName: string
  awgPort: number
  tunnelSubnet: string
  clientAllowedIps: string
  endpointHost: string
  clientDns?: string | null
}

type BackupImportResponse = {
  server: Server
  backupOptions: AwgEasyOptions
  currentOptions: AwgEasyOptions
  warnings: string[]
}

const toast = useToast()
const config = useRuntimeConfig()

const loading = ref(true)
const saving = ref(false)
const exportingBackup = ref(false)
const importingBackup = ref(false)
const backupInput = ref<HTMLInputElement | null>(null)
const backupWarnings = ref<string[]>([])
const server = ref<Server | null>(null)
const runtime = ref<RuntimeStatus | null>(null)
const errorMessage = ref<string | null>(null)

const form = reactive({
  s1: undefined as number | undefined,
  s2: undefined as number | undefined,
  s3: undefined as number | undefined,
  s4: undefined as number | undefined,
  h1: '',
  h2: '',
  h3: '',
  h4: '',
  defaultJc: undefined as number | undefined,
  defaultJmin: undefined as number | undefined,
  defaultJmax: undefined as number | undefined,
  defaultI1: '',
  defaultI2: '',
  defaultI3: '',
  defaultI4: '',
  defaultI5: ''
})

const apiBase = computed(() => {
  const value = String(config.public.apiBase || '').replace(/\/$/, '')
  return value || ''
})

const toolRows = computed<Array<[string, string | null | undefined]>>(() => {
  const tools = runtime.value?.tools
  return [
    ['backend', runtime.value?.backend],
    ['awg', tools?.awg],
    ['awg-quick', tools?.awgQuick],
    ['amneziawg-go', tools?.amneziawgGo],
    ['userspace', tools?.userspaceImplementation]
  ]
})

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

  return await response.json() as T
}

function cleanText(value: string) {
  const trimmed = value.trim()
  return trimmed.length > 0 ? trimmed : undefined
}

function assignForm(profile?: ServerObfuscationProfile | null) {
  form.s1 = profile?.s1 ?? undefined
  form.s2 = profile?.s2 ?? undefined
  form.s3 = profile?.s3 ?? undefined
  form.s4 = profile?.s4 ?? undefined
  form.h1 = profile?.h1 ?? ''
  form.h2 = profile?.h2 ?? ''
  form.h3 = profile?.h3 ?? ''
  form.h4 = profile?.h4 ?? ''
  form.defaultJc = profile?.defaultJc ?? undefined
  form.defaultJmin = profile?.defaultJmin ?? undefined
  form.defaultJmax = profile?.defaultJmax ?? undefined
  form.defaultI1 = profile?.defaultI1 ?? ''
  form.defaultI2 = profile?.defaultI2 ?? ''
  form.defaultI3 = profile?.defaultI3 ?? ''
  form.defaultI4 = profile?.defaultI4 ?? ''
  form.defaultI5 = profile?.defaultI5 ?? ''
}

function buildProfile(): ServerObfuscationProfile {
  return {
    s1: form.s1 ?? undefined,
    s2: form.s2 ?? undefined,
    s3: form.s3 ?? undefined,
    s4: form.s4 ?? undefined,
    h1: cleanText(form.h1),
    h2: cleanText(form.h2),
    h3: cleanText(form.h3),
    h4: cleanText(form.h4),
    defaultJc: form.defaultJc ?? undefined,
    defaultJmin: form.defaultJmin ?? undefined,
    defaultJmax: form.defaultJmax ?? undefined,
    defaultI1: cleanText(form.defaultI1),
    defaultI2: cleanText(form.defaultI2),
    defaultI3: cleanText(form.defaultI3),
    defaultI4: cleanText(form.defaultI4),
    defaultI5: cleanText(form.defaultI5)
  }
}

async function loadAdmin() {
  loading.value = true
  errorMessage.value = null

  try {
    const [serverResult, runtimeResult] = await Promise.all([
      apiFetch<Server>('/server'),
      apiFetch<RuntimeStatus>('/server/runtime')
    ])
    server.value = serverResult
    runtime.value = runtimeResult
    assignForm(serverResult.serverObfuscation)
  } catch (error) {
    errorMessage.value = error instanceof Error ? error.message : 'Failed to load server settings.'
  } finally {
    loading.value = false
  }
}

async function saveObfuscation() {
  saving.value = true

  try {
    const next = await apiFetch<Server>('/server/obfuscation', {
      method: 'PUT',
      body: JSON.stringify(buildProfile())
    })
    server.value = next
    assignForm(next.serverObfuscation)
    toast.add({ title: 'Obfuscation saved', color: 'success', icon: 'i-lucide-check' })
  } catch (error) {
    toast.add({
      title: 'Could not save obfuscation',
      description: error instanceof Error ? error.message : undefined,
      color: 'error',
      icon: 'i-lucide-circle-alert'
    })
  } finally {
    saving.value = false
  }
}

async function downloadBackup() {
  exportingBackup.value = true

  try {
    const response = await fetch(apiUrl('/backups/export'))
    if (!response.ok) {
      throw new Error(`HTTP ${response.status}`)
    }

    const blob = await response.blob()
    const disposition = response.headers.get('content-disposition') || ''
    const match = disposition.match(/filename="?([^"]+)"?/i)
    const fileName = match?.[1] || `awg-easy-backup-${new Date().toISOString().slice(0, 19).replace(/[:T]/g, '-')}.json`
    const url = URL.createObjectURL(blob)
    const link = document.createElement('a')
    link.href = url
    link.download = fileName
    document.body.appendChild(link)
    link.click()
    link.remove()
    URL.revokeObjectURL(url)

    toast.add({ title: 'Backup downloaded', color: 'success', icon: 'i-lucide-download' })
  } catch (error) {
    toast.add({
      title: 'Could not download backup',
      description: error instanceof Error ? error.message : undefined,
      color: 'error',
      icon: 'i-lucide-circle-alert'
    })
  } finally {
    exportingBackup.value = false
  }
}

function selectBackupFile() {
  backupInput.value?.click()
}

async function importBackup(event: Event) {
  const input = event.target as HTMLInputElement
  const file = input.files?.[0]
  input.value = ''

  if (!file) {
    return
  }

  if (!confirm('Restore this backup? Current clients and server keys will be replaced.')) {
    return
  }

  importingBackup.value = true
  backupWarnings.value = []

  try {
    const text = await file.text()
    JSON.parse(text)

    const result = await apiFetch<BackupImportResponse>('/backups/import', {
      method: 'POST',
      body: text
    })

    server.value = result.server
    backupWarnings.value = result.warnings
    await loadAdmin()
    toast.add({ title: 'Backup restored', color: 'success', icon: 'i-lucide-rotate-ccw' })
  } catch (error) {
    toast.add({
      title: 'Could not restore backup',
      description: error instanceof Error ? error.message : undefined,
      color: 'error',
      icon: 'i-lucide-circle-alert'
    })
  } finally {
    importingBackup.value = false
  }
}

onMounted(loadAdmin)
</script>

<template>
  <UContainer class="py-6">
    <div class="flex flex-col gap-6">
      <section class="flex flex-col gap-4 md:flex-row md:items-end md:justify-between">
        <div class="min-w-0">
          <h1 class="text-2xl font-semibold tracking-tight text-highlighted">
            Admin
          </h1>
          <p class="mt-1 text-sm text-muted">
            Server runtime and obfuscation settings.
          </p>
        </div>

        <UButton
          icon="i-lucide-refresh-cw"
          color="neutral"
          variant="subtle"
          :loading="loading"
          @click="loadAdmin"
        >
          Refresh
        </UButton>
      </section>

      <UAlert
        v-if="errorMessage"
        color="error"
        variant="subtle"
        icon="i-lucide-circle-alert"
        title="Could not load admin page"
        :description="errorMessage"
      />

      <div
        v-if="loading"
        class="flex min-h-64 items-center justify-center rounded-lg border border-default bg-default"
      >
        <div class="flex items-center gap-3 text-sm text-muted">
          <UIcon
            name="i-lucide-loader-circle"
            class="size-5 animate-spin"
          />
          Loading server data
        </div>
      </div>

      <template v-else>
        <section class="grid gap-4 md:grid-cols-4">
          <div class="rounded-lg border border-default bg-default p-4">
            <p class="text-xs text-muted">
              Interface
            </p>
            <p class="mt-1 font-mono text-sm font-medium text-highlighted">
              {{ server?.interfaceName || runtime?.interfaceName }}
            </p>
          </div>
          <div class="rounded-lg border border-default bg-default p-4">
            <p class="text-xs text-muted">
              Runtime
            </p>
            <UBadge
              class="mt-2"
              :color="runtime?.isRunning ? 'success' : 'warning'"
              variant="subtle"
              :icon="runtime?.isRunning ? 'i-lucide-circle-check' : 'i-lucide-circle-alert'"
            >
              {{ runtime?.isRunning ? 'Running' : 'Stopped' }}
            </UBadge>
          </div>
          <div class="rounded-lg border border-default bg-default p-4">
            <p class="text-xs text-muted">
              Endpoint
            </p>
            <p class="mt-1 font-mono text-sm font-medium text-highlighted">
              {{ server?.endpointHost }}:{{ server?.awgPort }}
            </p>
          </div>
          <div class="rounded-lg border border-default bg-default p-4">
            <p class="text-xs text-muted">
              Clients
            </p>
            <p class="mt-1 text-sm font-medium text-highlighted">
              {{ server?.clientsCount ?? 0 }}
            </p>
          </div>
        </section>

        <section class="grid gap-6 lg:grid-cols-[minmax(0,1fr)_22rem]">
          <div class="rounded-lg border border-default bg-default">
            <div class="border-b border-default px-5 py-4">
              <h2 class="text-base font-semibold text-highlighted">
                Obfuscation
              </h2>
            </div>

            <div class="space-y-6 px-5 py-5">
              <div>
                <h3 class="text-sm font-medium text-highlighted">
                  Server
                </h3>
                <div class="mt-3 grid gap-4 sm:grid-cols-4">
                  <UFormField label="S1">
                    <UInput
                      v-model.number="form.s1"
                      type="number"
                    />
                  </UFormField>
                  <UFormField label="S2">
                    <UInput
                      v-model.number="form.s2"
                      type="number"
                    />
                  </UFormField>
                  <UFormField label="S3">
                    <UInput
                      v-model.number="form.s3"
                      type="number"
                    />
                  </UFormField>
                  <UFormField label="S4">
                    <UInput
                      v-model.number="form.s4"
                      type="number"
                    />
                  </UFormField>
                </div>
                <div class="mt-4 grid gap-4 sm:grid-cols-2">
                  <UFormField label="H1">
                    <UInput v-model="form.h1" />
                  </UFormField>
                  <UFormField label="H2">
                    <UInput v-model="form.h2" />
                  </UFormField>
                  <UFormField label="H3">
                    <UInput v-model="form.h3" />
                  </UFormField>
                  <UFormField label="H4">
                    <UInput v-model="form.h4" />
                  </UFormField>
                </div>
              </div>

              <div>
                <h3 class="text-sm font-medium text-highlighted">
                  Client defaults
                </h3>
                <div class="mt-3 grid gap-4 sm:grid-cols-3">
                  <UFormField label="Jc">
                    <UInput
                      v-model.number="form.defaultJc"
                      type="number"
                      min="1"
                      max="128"
                    />
                  </UFormField>
                  <UFormField label="Jmin">
                    <UInput
                      v-model.number="form.defaultJmin"
                      type="number"
                      min="0"
                      max="1280"
                    />
                  </UFormField>
                  <UFormField label="Jmax">
                    <UInput
                      v-model.number="form.defaultJmax"
                      type="number"
                      min="1"
                      max="1280"
                    />
                  </UFormField>
                </div>
                <div class="mt-4 grid gap-4 sm:grid-cols-2">
                  <UFormField label="I1">
                    <UInput v-model="form.defaultI1" />
                  </UFormField>
                  <UFormField label="I2">
                    <UInput v-model="form.defaultI2" />
                  </UFormField>
                  <UFormField label="I3">
                    <UInput v-model="form.defaultI3" />
                  </UFormField>
                  <UFormField label="I4">
                    <UInput v-model="form.defaultI4" />
                  </UFormField>
                  <UFormField
                    label="I5"
                    class="sm:col-span-2"
                  >
                    <UInput v-model="form.defaultI5" />
                  </UFormField>
                </div>
              </div>
            </div>

            <div class="flex justify-end border-t border-default px-5 py-4">
              <UButton
                icon="i-lucide-save"
                :loading="saving"
                @click="saveObfuscation"
              >
                Save
              </UButton>
            </div>
          </div>

          <div class="space-y-6">
            <aside class="rounded-lg border border-default bg-default">
              <div class="border-b border-default px-5 py-4">
                <h2 class="text-base font-semibold text-highlighted">
                  Backup
                </h2>
              </div>
              <div class="space-y-4 px-5 py-5">
                <input
                  ref="backupInput"
                  class="hidden"
                  type="file"
                  accept="application/json,.json"
                  @change="importBackup"
                >
                <div class="grid gap-2">
                  <UButton
                    icon="i-lucide-download"
                    block
                    :loading="exportingBackup"
                    @click="downloadBackup"
                  >
                    Export backup
                  </UButton>
                  <UButton
                    icon="i-lucide-upload"
                    color="neutral"
                    variant="subtle"
                    block
                    :loading="importingBackup"
                    @click="selectBackupFile"
                  >
                    Import backup
                  </UButton>
                </div>

                <UAlert
                  v-if="backupWarnings.length > 0"
                  color="warning"
                  variant="subtle"
                  icon="i-lucide-triangle-alert"
                  title="Backup restored with differences"
                >
                  <template #description>
                    <ul class="mt-2 space-y-1">
                      <li
                        v-for="warning in backupWarnings"
                        :key="warning"
                        class="break-words"
                      >
                        {{ warning }}
                      </li>
                    </ul>
                  </template>
                </UAlert>
              </div>
            </aside>

            <aside class="rounded-lg border border-default bg-default">
              <div class="border-b border-default px-5 py-4">
                <h2 class="text-base font-semibold text-highlighted">
                  Runtime tools
                </h2>
              </div>
              <div class="divide-y divide-default">
                <div
                  v-for="[name, value] in toolRows"
                  :key="name"
                  class="px-5 py-3"
                >
                  <p class="text-xs text-muted">
                    {{ name }}
                  </p>
                  <p class="mt-1 break-all font-mono text-xs text-highlighted">
                    {{ value || 'Not found' }}
                  </p>
                </div>
              </div>
            </aside>
          </div>
        </section>
      </template>
    </div>
  </UContainer>
</template>
