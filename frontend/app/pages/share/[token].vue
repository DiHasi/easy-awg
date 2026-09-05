<script setup lang="ts">
import QRCode from 'qrcode'

// Deliberately no auth middleware: a share link must work for someone with no account.
definePageMeta({ layout: false })

type PublicShare = {
  clientName: string
  expiresAt: string
}

const route = useRoute()
const api = useControlApi()

const token = computed(() => String(route.params.token ?? ''))
const share = ref<PublicShare | null>(null)
const loading = ref(true)
const errorMessage = ref<string | null>(null)
const qrDataUrl = ref<string | null>(null)

const configUrl = computed(() => api.url(`/shares/${token.value}/config`))

async function load() {
  try {
    share.value = await api.get<PublicShare>(`/shares/${token.value}`)

    const response = await fetch(configUrl.value)
    if (response.ok) {
      qrDataUrl.value = await QRCode.toDataURL(await response.text(), { width: 300, margin: 1 })
    }
  } catch (error) {
    errorMessage.value = describeError(error, 'This link is not valid or has expired.')
  } finally {
    loading.value = false
  }
}

onMounted(load)
</script>

<template>
  <div class="flex min-h-screen items-center justify-center bg-muted/30 px-4 py-10">
    <UCard class="w-full max-w-md">
      <template #header>
        <div class="flex items-center gap-3">
          <UIcon
            name="i-lucide-shield"
            class="size-6 text-primary"
          />
          <h1 class="text-base font-semibold text-highlighted">
            Your VPN configuration
          </h1>
        </div>
      </template>

      <div
        v-if="loading"
        class="flex min-h-48 items-center justify-center text-sm text-muted"
      >
        <UIcon
          name="i-lucide-loader-circle"
          class="mr-2 size-5 animate-spin"
        />
        Loading
      </div>

      <UAlert
        v-else-if="errorMessage"
        color="error"
        variant="subtle"
        icon="i-lucide-circle-alert"
        title="Link unavailable"
        :description="errorMessage"
      />

      <div
        v-else
        class="flex flex-col items-center gap-4"
      >
        <p class="text-sm text-muted">
          Configuration for <span class="font-medium text-highlighted">{{ share?.clientName }}</span>
        </p>

        <img
          v-if="qrDataUrl"
          :src="qrDataUrl"
          alt="Configuration QR code"
          class="rounded-md bg-white p-2"
        >

        <p class="text-center text-xs text-muted">
          Scan this in the AmneziaWG app, or download the file and import it.
        </p>

        <UButton
          :href="configUrl"
          icon="i-lucide-download"
          block
          external
        >
          Download config
        </UButton>
      </div>
    </UCard>
  </div>
</template>
