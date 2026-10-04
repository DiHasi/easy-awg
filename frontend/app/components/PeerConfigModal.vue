<script setup lang="ts">
import type { Client, ClientShare } from '~/types/api'

/**
 * Everything needed to hand a config to a person, in one place: the QR code to scan, the file to
 * import, and a share link for someone who is not in the room.
 */
const props = defineProps<{ client: Client | null }>()
const open = defineModel<boolean>('open', { default: false })

const api = useControlApi()
const toast = useToast()

const configText = ref<string | null>(null)
const downloading = ref(false)
const sharing = ref(false)
const share = ref<ClientShare | null>(null)

function fail(title: string, error: unknown) {
  toast.add({ title, description: describeError(error, ''), color: 'error', icon: 'i-lucide-circle-alert' })
}

async function fetchConfig(client: Client) {
  const response = await fetch(api.url(`/clients/${client.id}/config`), { credentials: 'include' })
  if (!response.ok) {
    throw new Error(`HTTP ${response.status}`)
  }
  return response
}

watch(open, async (isOpen) => {
  if (!isOpen || !props.client) {
    return
  }

  configText.value = null
  share.value = null

  try {
    configText.value = await (await fetchConfig(props.client)).text()
  } catch (error) {
    fail('Could not load the config', error)
  }
})

async function download() {
  if (!props.client) {
    return
  }

  downloading.value = true

  try {
    const blob = await (await fetchConfig(props.client)).blob()
    const url = URL.createObjectURL(blob)
    const link = document.createElement('a')
    link.href = url
    link.download = `${props.client.name}.conf`
    link.click()
    URL.revokeObjectURL(url)
  } catch (error) {
    fail('Could not download the config', error)
  } finally {
    downloading.value = false
  }
}

async function createShare() {
  if (!props.client) {
    return
  }

  sharing.value = true

  try {
    share.value = await api.post<ClientShare>(`/clients/${props.client.id}/share`)
  } catch (error) {
    fail('Could not create a share link', error)
  } finally {
    sharing.value = false
  }
}

async function copyShare() {
  if (share.value) {
    await navigator.clipboard.writeText(share.value.url)
    toast.add({ title: 'Link copied', icon: 'i-lucide-check', color: 'success' })
  }
}
</script>

<template>
  <UModal
    v-model:open="open"
    :title="client ? `Config for ${client.name}` : 'Config'"
    :description="client ? `${client.address} · contains this peer's private key` : undefined"
    :ui="{ content: 'sm:max-w-2xl' }"
  >
    <template #body>
      <div class="grid gap-5 sm:grid-cols-[auto_minmax(0,1fr)]">
        <div class="w-full sm:w-80">
          <ConfigQrCode :config="configText" />
        </div>

        <div class="flex flex-col gap-4">
          <div class="flex flex-col gap-2">
            <h3 class="text-sm font-semibold text-highlighted">
              On this device
            </h3>
            <UButton
              icon="i-lucide-download"
              block
              :loading="downloading"
              @click="download"
            >
              Download .conf
            </UButton>
          </div>

          <div class="flex flex-col gap-2 border-t border-default pt-4">
            <h3 class="text-sm font-semibold text-highlighted">
              For someone else
            </h3>
            <p class="text-sm text-muted">
              A link that opens this config without signing in. Anyone holding it gets the config
              until it expires.
            </p>
            <UButton
              v-if="!share"
              icon="i-lucide-link"
              color="neutral"
              variant="outline"
              block
              :loading="sharing"
              @click="createShare"
            >
              Create share link
            </UButton>
            <template v-else>
              <div class="rounded-md border border-default bg-elevated p-2.5">
                <code class="block break-all font-mono text-xs text-default">{{ share.url }}</code>
              </div>
              <div class="flex items-center gap-2">
                <UButton
                  icon="i-lucide-copy"
                  color="neutral"
                  variant="outline"
                  @click="copyShare"
                >
                  Copy link
                </UButton>
                <span class="text-xs text-muted">valid until {{ formatUtc(share.expiresAt) }}</span>
              </div>
            </template>
          </div>
        </div>
      </div>
    </template>
  </UModal>
</template>
