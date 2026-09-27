<script setup lang="ts">
import type { Fleet, GeneratedKey, ImportResult, ServerObfuscationProfile } from '~/types/api'

definePageMeta({ middleware: 'auth' })

const api = useControlApi()
const toast = useToast()
const { fleet, refresh } = useFleetState()

const loading = ref(true)
const saving = ref(false)
const errorMessage = ref<string | null>(null)

const generatingKey = ref(false)
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

/**
 * AmneziaWG 3.x tunables. Each takes a single number or an inclusive `lo-hi` range that the peer
 * re-rolls per use, which is the point: a fixed rekey interval is itself a fingerprint. The hints
 * carry the stock WireGuard value so it is clear what a blank field leaves in place.
 */
const tuningFields = [
  { key: 'defaultContentPaddingAddition', label: 'ContentPaddingAddition', hint: 'Extra random payload bytes. Off by default.' },
  { key: 'defaultRekeyAfterTime', label: 'RekeyAfterTime', hint: 'Seconds between re-handshakes. WireGuard uses 120.' },
  { key: 'defaultRekeyTimeout', label: 'RekeyTimeout', hint: 'Seconds between handshake retries. WireGuard uses 5.' },
  { key: 'defaultRejectAfterTime', label: 'RejectAfterTime', hint: 'Seconds before a key is abandoned. Must stay above RekeyAfterTime. WireGuard uses 180.' },
  { key: 'defaultKeepaliveTimeout', label: 'KeepaliveTimeout', hint: 'Seconds of silence before a keepalive. WireGuard uses 10.' },
  { key: 'defaultMaxHandshakeAttempts', label: 'MaxHandshakeAttempts', hint: 'Handshake attempts before giving up. WireGuard uses 18.' },
  { key: 'defaultPersistentKeepalive', label: 'PersistentKeepalive', hint: 'Seconds, written into the client [Peer]. Defaults to 25.' }
] as const

// Read once: the sheet frame polls the fleet, and a poll must not overwrite a half-edited form.
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

async function copyPublicKey() {
  if (fleet.value) {
    await navigator.clipboard.writeText(fleet.value.serverPublicKey)
    toast.add({ title: 'Key copied', color: 'success', icon: 'i-lucide-check' })
  }
}

async function generateHeaderProtectionKey() {
  generatingKey.value = true

  try {
    const generated = await api.post<GeneratedKey>('/fleet/header-protection-key')
    form.headerProtectionKey = generated.key
    toast.add({
      title: 'Key generated',
      description: 'Save the profile to roll it out. Every client config has to be handed out again.',
      color: 'success',
      icon: 'i-lucide-key'
    })
  } catch (error) {
    toast.add({
      title: 'Could not generate a key',
      description: describeError(error, ''),
      color: 'error',
      icon: 'i-lucide-circle-alert'
    })
  } finally {
    generatingKey.value = false
  }
}

async function saveObfuscation() {
  saving.value = true

  try {
    fleet.value = await api.put<Fleet>('/fleet/obfuscation', form)
    void refresh()
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
    void refresh()
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
  <div class="flex flex-col gap-4 lg:gap-6">
    <h1 class="sr-only">
      Settings
    </h1>

    <UAlert
      v-if="errorMessage"
      color="error"
      variant="subtle"
      icon="i-lucide-circle-alert"
      title="Could not load the fleet"
      :description="errorMessage"
    />

    <AppCard
      title="Fleet identity"
      icon="i-lucide-fingerprint"
      description="Shared by every node and pinned by every client config. It is never regenerated."
    >
      <template #actions>
        <UButton
          v-if="fleet"
          icon="i-lucide-copy"
          color="neutral"
          variant="outline"
          @click="copyPublicKey"
        >
          Copy public key
        </UButton>
      </template>

      <div
        v-if="loading"
        class="flex items-center gap-2 text-sm text-muted"
      >
        <UIcon
          name="i-lucide-loader-circle"
          class="size-5 animate-spin"
        />
        Loading
      </div>

      <dl
        v-else-if="fleet"
        class="grid grid-cols-2 gap-x-6 gap-y-4 md:grid-cols-4"
      >
        <SpecItem
          label="Server public key"
          mono
          class="col-span-2"
        >
          <span class="break-all">{{ fleet.serverPublicKey }}</span>
        </SpecItem>
        <SpecItem
          label="Endpoint"
          mono
        >
          {{ fleet.endpointHost }}:{{ fleet.listenPort }}
        </SpecItem>
        <SpecItem
          label="Subnet"
          mono
        >
          {{ fleet.subnet }}
        </SpecItem>
        <SpecItem
          label="Client allowed IPs"
          mono
        >
          {{ fleet.clientAllowedIps }}
        </SpecItem>
        <SpecItem
          label="Client DNS"
          mono
        >
          {{ fleet.clientDns ?? '—' }}
        </SpecItem>
        <SpecItem
          label="Revision"
          mono
        >
          r{{ fleet.revision }} · generation {{ fleet.generation }}
        </SpecItem>
        <SpecItem label="Issued">
          {{ fleet.clientsCount }} peers · {{ fleet.nodesCount }} nodes
        </SpecItem>
      </dl>
    </AppCard>

    <AppCard
      title="Obfuscation"
      icon="i-lucide-shield-ellipsis"
      description="Saving bumps the fleet revision; nodes pick it up on their next poll."
      sticky-footer
    >
      <div class="grid gap-4 lg:grid-cols-2">
        <section class="flex flex-col gap-4 rounded-lg border border-default p-4">
          <header class="flex items-start gap-3">
            <span class="flex size-8 shrink-0 items-center justify-center rounded-md bg-error/10 text-error">
              <UIcon
                name="i-lucide-lock"
                class="size-4"
              />
            </span>
            <div>
              <h3 class="font-semibold text-highlighted">
                Wire format — must match on both ends
              </h3>
              <p class="text-sm text-muted">
                Applied to every node and copied into every client config. A mismatch is a
                handshake that never happens, so changing these means handing out every config again.
              </p>
            </div>
          </header>

          <div class="grid grid-cols-2 gap-3 sm:grid-cols-4">
            <UFormField
              v-for="field in numericFields"
              :key="field.key"
              :ui="paramField"
              :label="field.label"
            >
              <UInput
                v-model.number="form[field.key]"
                type="number"
                class="w-full"
              />
            </UFormField>
          </div>

          <div class="grid grid-cols-1 gap-3 sm:grid-cols-2">
            <UFormField
              v-for="field in headerFields"
              :key="field.key"
              :ui="paramField"
              :label="field.label"
              help="Number or range"
            >
              <UInput
                v-model="form[field.key]"
                class="w-full"
                placeholder="1000000-1000500"
              />
            </UFormField>
          </div>

          <UFormField
            :ui="paramField"
            label="HeaderProtectionKey"
            help="AmneziaWG 3.x. Encrypts the packet header. Needs S1–S4 of at least 12."
          >
            <UFieldGroup class="w-full">
              <UInput
                v-model="form.headerProtectionKey"
                class="w-full"
                placeholder="base64, 32 bytes"
              />
              <UButton
                color="neutral"
                variant="outline"
                icon="i-lucide-key-round"
                :loading="generatingKey"
                @click="generateHeaderProtectionKey"
              >
                Generate
              </UButton>
            </UFieldGroup>
          </UFormField>

          <USwitch
            v-model="form.randomTrailers"
            label="RandomTrailers"
            description="AmneziaWG 3.x. Appends random trailing bytes to every packet."
          />
        </section>

        <section class="flex flex-col gap-4 rounded-lg border border-default p-4">
          <header class="flex items-start gap-3">
            <span class="flex size-8 shrink-0 items-center justify-center rounded-md bg-primary/10 text-primary">
              <UIcon
                name="i-lucide-shuffle"
                class="size-4"
              />
            </span>
            <div>
              <h3 class="font-semibold text-highlighted">
                Client defaults — may differ per client
              </h3>
              <p class="text-sm text-muted">
                Seeds for new configs; any peer can override them. Differing is the point: two
                clients that rekey on the same schedule are a correlatable pair.
              </p>
            </div>
          </header>

          <div class="grid grid-cols-3 gap-3">
            <UFormField
              :ui="paramField"
              label="Jc"
            >
              <UInput
                v-model.number="form.defaultJc"
                type="number"
                class="w-full"
              />
            </UFormField>
            <UFormField
              :ui="paramField"
              label="Jmin"
            >
              <UInput
                v-model.number="form.defaultJmin"
                type="number"
                class="w-full"
              />
            </UFormField>
            <UFormField
              :ui="paramField"
              label="Jmax"
            >
              <UInput
                v-model.number="form.defaultJmax"
                type="number"
                class="w-full"
              />
            </UFormField>
          </div>

          <div class="grid grid-cols-1 gap-3 sm:grid-cols-2">
            <UFormField
              v-for="field in clientDefaultFields"
              :key="field.key"
              :ui="paramField"
              :label="field.label"
            >
              <UInput
                v-model="form[field.key]"
                class="w-full"
                placeholder="<b 0x...>"
              />
            </UFormField>
          </div>

          <div class="grid grid-cols-1 gap-3 sm:grid-cols-2">
            <UFormField
              v-for="field in tuningFields"
              :key="field.key"
              :ui="paramField"
              :label="field.label"
              :help="field.hint"
            >
              <UInput
                v-model="form[field.key]"
                class="w-full"
                placeholder="140 or 120-160"
              />
            </UFormField>
          </div>

          <USwitch
            v-model="form.defaultDisableCookies"
            label="DisableCookies"
            description="AmneziaWG 3.x. Stops the peer answering with a cookie reply under load."
          />
        </section>
      </div>

      <template #footer>
        <div class="flex flex-wrap items-center justify-end gap-3">
          <span class="text-sm text-muted">Rolls out to every node on their next poll.</span>
          <UButton
            icon="i-lucide-save"
            :loading="saving"
            :disabled="loading"
            @click="saveObfuscation"
          >
            Save obfuscation
          </UButton>
        </div>
      </template>
    </AppCard>

    <AppCard
      title="Import"
      icon="i-lucide-upload"
      description="Adopt a single-server deployment. Its key pair is preserved, so configs already handed out keep working."
    >
      <div class="flex flex-col gap-4 sm:flex-row sm:items-center sm:gap-8">
        <USwitch
          v-model="replaceExisting"
          label="Replace existing clients"
          description="Required if this panel already has clients."
        />

        <label
          class="inline-flex w-fit cursor-pointer items-center gap-2 rounded-md border border-accented px-3 py-2 text-sm font-medium text-default transition-colors hover:bg-elevated focus-within:outline-2 focus-within:outline-offset-2 focus-within:outline-primary"
          :class="importing ? 'pointer-events-none opacity-60' : ''"
        >
          <UIcon
            :name="importing ? 'i-lucide-loader-circle' : 'i-lucide-file-up'"
            class="size-4"
            :class="importing ? 'animate-spin' : ''"
          />
          {{ importing ? 'Importing…' : 'Choose state.json' }}
          <input
            type="file"
            accept="application/json,.json"
            class="sr-only"
            :disabled="importing"
            @change="importLegacyState"
          >
        </label>
      </div>

      <UAlert
        v-if="importResult"
        class="mt-4"
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
    </AppCard>
  </div>
</template>
