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

// The person opening this is not an operator: their own clock is the one that matters.
const expiresLocal = computed(() => share.value ? new Date(share.value.expiresAt).toLocaleString() : '')

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

useHead({ title: 'Your VPN configuration' })
</script>

<template>
  <div class="flex min-h-screen items-center justify-center px-4 py-10">
    <section class="w-full max-w-md rounded-xl border border-default bg-default p-6 shadow-xs">
      <div class="flex items-center gap-2.5">
        <span class="flex size-9 items-center justify-center rounded-md bg-inverted text-inverted">
          <UIcon
            name="i-lucide-shield-check"
            class="size-5"
          />
        </span>
        <div>
          <h1 class="font-semibold text-highlighted">
            Your VPN configuration
          </h1>
          <p
            v-if="share"
            class="text-sm text-muted"
          >
            for {{ share.clientName }}
          </p>
        </div>
      </div>

      <div
        v-if="loading"
        class="flex items-center justify-center gap-2 py-16 text-sm text-muted"
      >
        <UIcon
          name="i-lucide-loader-circle"
          class="size-5 animate-spin"
        />
        Loading
      </div>

      <UAlert
        v-else-if="errorMessage"
        class="mt-6"
        color="error"
        variant="subtle"
        icon="i-lucide-circle-alert"
        title="Link unavailable"
        :description="`${errorMessage} Ask whoever sent it for a new one.`"
      />

      <div
        v-else
        class="mt-6 flex flex-col gap-5"
      >
        <div class="flex justify-center">
          <!-- Always dark on white: a scanner reads contrast, not the page theme. -->
          <img
            v-if="qrDataUrl"
            :src="qrDataUrl"
            alt="Configuration QR code"
            class="size-64 rounded-lg bg-white p-2 ring-1 ring-default"
          >
        </div>

        <UButton
          :href="configUrl"
          icon="i-lucide-download"
          size="lg"
          block
          external
        >
          Download config
        </UButton>

        <ol class="flex flex-col gap-2.5 border-t border-default pt-4 text-sm text-toned">
          <li class="flex gap-3">
            <span class="flex size-6 shrink-0 items-center justify-center rounded-full bg-elevated text-xs font-semibold text-highlighted">1</span>
            Install the AmneziaWG app on your device.
          </li>
          <li class="flex gap-3">
            <span class="flex size-6 shrink-0 items-center justify-center rounded-full bg-elevated text-xs font-semibold text-highlighted">2</span>
            Scan the code in the app, or download the file and import it.
          </li>
          <li class="flex gap-3">
            <span class="flex size-6 shrink-0 items-center justify-center rounded-full bg-elevated text-xs font-semibold text-highlighted">3</span>
            Keep this link to yourself: it carries your private key and works until {{ expiresLocal }}.
          </li>
        </ol>
      </div>
    </section>
  </div>
</template>
