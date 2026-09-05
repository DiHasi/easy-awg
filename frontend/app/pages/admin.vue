<script setup lang="ts">
import type { Fleet, ImportResult, ServerObfuscationProfile } from '~/types/api'

definePageMeta({ middleware: 'auth' })

const api = useControlApi()
const toast = useToast()

const fleet = ref<Fleet | null>(null)
const loading = ref(true)
const saving = ref(false)
const errorMessage = ref<string | null>(null)

const importing = ref(false)
const importResult = ref<ImportResult | null>(null)
const replaceExisting = ref(false)

/**
 * The API models "unset" as null, but form inputs only understand undefined. Keep the boundary
 * conversion in one place rather than sprinkling `?? undefined` through the template.
 */
type ObfuscationForm = {
  [K in keyof ServerObfuscationProfile]: NonNullable<ServerObfuscationProfile[K]> | undefined
}

const form = reactive<ObfuscationForm>({})

function intoForm(profile: ServerObfuscationProfile | null | undefined) {
  for (const [key, value] of Object.entries(profile ?? {})) {
    form[key as keyof ObfuscationForm] = (value ?? undefined) as never
  }
}

const numericFields = [
  { key: 's1', label: 'S1' },
  { key: 's2', label: 'S2' },
  { key: 's3', label: 'S3' },
  { key: 's4', label: 'S4' }
] as const

const headerFields = [
  { key: 'h1', label: 'H1' },
  { key: 'h2', label: 'H2' },
  { key: 'h3', label: 'H3' },
  { key: 'h4', label: 'H4' }
] as const

const clientDefaultFields = [
  { key: 'defaultI1', label: 'I1' },
  { key: 'defaultI2', label: 'I2' },
  { key: 'defaultI3', label: 'I3' },
  { key: 'defaultI4', label: 'I4' },
  { key: 'defaultI5', label: 'I5' }
] as const

async function loadFleet() {
  try {
    fleet.value = await api.get<Fleet>('/fleet')
    intoForm(fleet.value.obfuscation)
    errorMessage.value = null
  } catch (error) {
    errorMessage.value = describeError(error, 'Failed to load fleet settings.')
  } finally {
    loading.value = false
  }
}

async function saveObfuscation() {
  saving.value = true

  try {
    fleet.value = await api.put<Fleet>('/fleet/obfuscation', form)
    toast.add({
      title: 'Obfuscation saved',
      description: `Fleet is now at revision ${fleet.value.revision}. Nodes pick it up on their next poll.`,
      color: 'success',
      icon: 'i-lucide-check'
    })
  } catch (error) {
    toast.add({
      title: 'Could not save',
      description: describeError(error, ''),
      color: 'error',
      icon: 'i-lucide-circle-alert'
    })
  } finally {
    saving.value = false
  }
}

async function importLegacyState(event: Event) {
  const input = event.target as HTMLInputElement
  const file = input.files?.[0]
  if (!file) {
    return
  }

  importing.value = true
  importResult.value = null

  try {
    const text = await file.text()
    importResult.value = await api.postRaw<ImportResult>(
      `/fleet/import?replace=${replaceExisting.value}`,
      text
    )

    await loadFleet()
    toast.add({
      title: `Imported ${importResult.value.clientsImported} client(s)`,
      color: 'success',
      icon: 'i-lucide-check'
    })
  } catch (error) {
    toast.add({
      title: 'Import failed',
      description: describeError(error, ''),
      color: 'error',
      icon: 'i-lucide-circle-alert'
    })
  } finally {
    importing.value = false
    input.value = ''
  }
}

onMounted(loadFleet)
</script>

<template>
  <UContainer class="py-6">
    <div class="flex flex-col gap-6">
      <section>
        <h1 class="text-2xl font-semibold tracking-tight text-highlighted">
          Fleet
        </h1>
        <p class="mt-1 text-sm text-muted">
          Settings shared by every node.
        </p>
      </section>

      <UAlert
        v-if="errorMessage"
        color="error"
        variant="subtle"
        icon="i-lucide-circle-alert"
        title="Could not load settings"
        :description="errorMessage"
      />

      <UCard v-if="fleet">
        <template #header>
          <h2 class="text-base font-medium text-highlighted">
            Identity
          </h2>
        </template>

        <dl class="grid grid-cols-1 gap-4 text-sm sm:grid-cols-2">
          <div>
            <dt class="text-xs text-muted">
              Server public key
            </dt>
            <dd class="break-all font-mono text-default">
              {{ fleet.serverPublicKey }}
            </dd>
          </div>
          <div>
            <dt class="text-xs text-muted">
              Endpoint
            </dt>
            <dd class="font-mono text-default">
              {{ fleet.endpointHost }}:{{ fleet.listenPort }}
            </dd>
          </div>
          <div>
            <dt class="text-xs text-muted">
              Subnet
            </dt>
            <dd class="font-mono text-default">
              {{ fleet.subnet }}
            </dd>
          </div>
          <div>
            <dt class="text-xs text-muted">
              Revision
            </dt>
            <dd class="text-default">
              {{ fleet.revision }} · generation {{ fleet.generation }} · {{ fleet.nodesCount }} node(s)
            </dd>
          </div>
        </dl>
      </UCard>

      <UCard>
        <template #header>
          <div>
            <h2 class="text-base font-medium text-highlighted">
              Obfuscation
            </h2>
            <p class="mt-1 text-sm text-muted">
              S1-S4 and H1-H4 are applied to every node interface. The client defaults are written
              into new client configs when the client does not override them.
            </p>
          </div>
        </template>

        <div class="flex flex-col gap-6">
          <div>
            <h3 class="mb-2 text-sm font-medium text-highlighted">
              Interface
            </h3>
            <div class="grid grid-cols-2 gap-3 sm:grid-cols-4">
              <UFormField
                v-for="field in numericFields"
                :key="field.key"
                :label="field.label"
              >
                <UInput
                  v-model.number="form[field.key]"
                  type="number"
                  class="w-full"
                />
              </UFormField>
            </div>
            <div class="mt-3 grid grid-cols-2 gap-3 sm:grid-cols-4">
              <UFormField
                v-for="field in headerFields"
                :key="field.key"
                :label="field.label"
              >
                <UInput
                  v-model="form[field.key]"
                  class="w-full"
                />
              </UFormField>
            </div>
          </div>

          <div>
            <h3 class="mb-2 text-sm font-medium text-highlighted">
              Client defaults
            </h3>
            <div class="grid grid-cols-3 gap-3">
              <UFormField label="Jc">
                <UInput
                  v-model.number="form.defaultJc"
                  type="number"
                  class="w-full"
                />
              </UFormField>
              <UFormField label="Jmin">
                <UInput
                  v-model.number="form.defaultJmin"
                  type="number"
                  class="w-full"
                />
              </UFormField>
              <UFormField label="Jmax">
                <UInput
                  v-model.number="form.defaultJmax"
                  type="number"
                  class="w-full"
                />
              </UFormField>
            </div>
            <div class="mt-3 grid grid-cols-1 gap-3 sm:grid-cols-2">
              <UFormField
                v-for="field in clientDefaultFields"
                :key="field.key"
                :label="field.label"
              >
                <UInput
                  v-model="form[field.key]"
                  class="w-full"
                  placeholder="<b 0x...>"
                />
              </UFormField>
            </div>
          </div>
        </div>

        <template #footer>
          <div class="flex justify-end">
            <UButton
              :loading="saving"
              @click="saveObfuscation"
            >
              Save obfuscation
            </UButton>
          </div>
        </template>
      </UCard>

      <UCard>
        <template #header>
          <div>
            <h2 class="text-base font-medium text-highlighted">
              Import from a single-server deployment
            </h2>
            <p class="mt-1 text-sm text-muted">
              Upload the old <code class="font-mono">state.json</code> or a backup export. The
              original server key pair is preserved, so configs already handed out keep working.
            </p>
          </div>
        </template>

        <div class="flex flex-col gap-4">
          <USwitch
            v-model="replaceExisting"
            label="Replace existing clients"
            description="Required if this panel already has clients."
          />

          <input
            type="file"
            accept="application/json,.json"
            class="block w-full text-sm text-muted file:mr-3 file:rounded-md file:border-0 file:bg-primary file:px-3 file:py-2 file:text-sm file:text-inverted"
            :disabled="importing"
            @change="importLegacyState"
          >

          <UAlert
            v-if="importResult"
            :color="importResult.warnings.length ? 'warning' : 'success'"
            variant="subtle"
            :icon="importResult.warnings.length ? 'i-lucide-triangle-alert' : 'i-lucide-check'"
            :title="`Imported ${importResult.clientsImported} client(s) at revision ${importResult.revision}`"
          >
            <template
              v-if="importResult.warnings.length"
              #description
            >
              <ul class="list-inside list-disc">
                <li
                  v-for="warning in importResult.warnings"
                  :key="warning"
                >
                  {{ warning }}
                </li>
              </ul>
            </template>
          </UAlert>
        </div>
      </UCard>
    </div>
  </UContainer>
</template>
