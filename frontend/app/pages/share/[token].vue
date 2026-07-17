<script setup lang="ts">
import QRCode from 'qrcode'

type ClientShare = {
  token: string
  clientId: string
  clientName: string
  expiresAt: string
}

type ApiError = {
  code: string
  message: string
}

const AMNEZIAWG_LINKS = {
  android: 'https://play.google.com/store/apps/details?id=org.amnezia.awg',
  windows: 'https://github.com/amnezia-vpn/amneziawg-windows-client/releases/latest',
  apple: 'https://apps.apple.com/app/amneziawg/id6478942365'
}

const route = useRoute()
const runtimeConfig = useRuntimeConfig()

const loading = ref(true)
const errorMessage = ref<string | null>(null)
const share = ref<ClientShare | null>(null)
const qrDataUrl = ref<string | null>(null)
const origin = ref('')

const token = computed(() => String(route.params.token || ''))
const apiBase = computed(() => {
  const value = String(runtimeConfig.public.apiBase || '').replace(/\/$/, '')
  return value || ''
})

const configUrl = computed(() => `${origin.value}${apiBase.value}/api/shares/${token.value}/config`)
const appLink = computed(() => getAmneziaWgLink())

function apiUrl(path: string) {
  return `${apiBase.value}/api${path}`
}

async function apiFetch<T>(path: string): Promise<T> {
  const response = await fetch(apiUrl(path))
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

function getAmneziaWgLink() {
  if (!import.meta.client) {
    return AMNEZIAWG_LINKS.windows
  }

  const userAgent = navigator.userAgent.toLowerCase()
  if (userAgent.includes('android')) {
    return AMNEZIAWG_LINKS.android
  }

  if (userAgent.includes('iphone') || userAgent.includes('ipad') || userAgent.includes('mac os')) {
    return AMNEZIAWG_LINKS.apple
  }

  return AMNEZIAWG_LINKS.windows
}

function formatExpiresAt(value?: string) {
  if (!value) {
    return ''
  }

  return new Intl.DateTimeFormat(undefined, {
    dateStyle: 'medium',
    timeStyle: 'short'
  }).format(new Date(value))
}

async function loadShare() {
  loading.value = true
  errorMessage.value = null

  try {
    share.value = await apiFetch<ClientShare>(`/shares/${token.value}`)
    qrDataUrl.value = await QRCode.toDataURL(configUrl.value, {
      errorCorrectionLevel: 'M',
      margin: 2,
      width: 320
    })
  } catch (error) {
    errorMessage.value = error instanceof Error ? error.message : 'Share link is unavailable.'
  } finally {
    loading.value = false
  }
}

onMounted(() => {
  origin.value = window.location.origin
  loadShare()
})
</script>

<template>
  <UContainer class="py-6">
    <div class="mx-auto flex max-w-md flex-col gap-6">
      <section class="min-w-0">
        <h1 class="text-2xl font-semibold tracking-tight text-highlighted">
          AmneziaWG config
        </h1>
        <p
          v-if="share"
          class="mt-1 break-words text-sm text-muted"
        >
          {{ share.clientName }} - valid until {{ formatExpiresAt(share.expiresAt) }}
        </p>
      </section>

      <UAlert
        v-if="errorMessage"
        color="error"
        variant="subtle"
        icon="i-lucide-circle-alert"
        title="Share link unavailable"
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
          Loading share
        </div>
      </div>

      <template v-else-if="share">
        <section class="rounded-lg border border-default bg-default p-5">
          <div class="flex justify-center">
            <img
              v-if="qrDataUrl"
              :src="qrDataUrl"
              :alt="`${share.clientName} config download QR code`"
              class="size-80 max-w-full rounded-md bg-white p-3"
            >
          </div>

          <div class="mt-5 grid gap-2">
            <UButton
              :to="configUrl"
              icon="i-lucide-download"
              block
              external
            >
              Download config
            </UButton>
            <UButton
              :to="appLink"
              icon="i-lucide-external-link"
              color="neutral"
              variant="subtle"
              block
              external
            >
              Download AmneziaWG
            </UButton>
          </div>
        </section>
      </template>
    </div>
  </UContainer>
</template>
