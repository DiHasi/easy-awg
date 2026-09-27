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
const expiresLocal = computed(() => share.value ? new Date(share.value.expiresAt).toLocaleString() : '—')

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
  <LooseSheet
    drawing="client configuration"
    :rows="[
      { label: 'Client', value: share?.clientName ?? '—' },
      { label: 'Valid until', value: expiresLocal }
    ]"
  >
    <p
      v-if="loading"
      class="py-16 text-center font-mono text-xs text-muted"
    >
      reading the link…
    </p>

    <div
      v-else-if="errorMessage"
      class="flex flex-col gap-2 border border-dashed border-default px-4 py-8 text-center"
    >
      <span class="caps text-error">Link unavailable</span>
      <p class="text-sm text-muted">
        {{ errorMessage }} Ask whoever sent it for a new one.
      </p>
    </div>

    <div
      v-else
      class="flex flex-col gap-5"
    >
      <figure class="m-0 flex flex-col items-center gap-2">
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
            class="size-64 bg-white p-2 sm:size-72"
          >
        </div>
        <figcaption class="caps text-muted">
          Fig. 1 — {{ share?.clientName }}.conf
        </figcaption>
      </figure>

      <UButton
        :href="configUrl"
        color="primary"
        variant="solid"
        icon="i-lucide-download"
        block
        external
      >
        Download config
      </UButton>

      <ol class="flex flex-col gap-2 border-t border-muted pt-4">
        <li class="flex gap-2.5 text-[13px] leading-snug text-toned">
          <NoteMark n="1" />
          <span>Install the AmneziaWG app on the device.</span>
        </li>
        <li class="flex gap-2.5 text-[13px] leading-snug text-toned">
          <NoteMark n="2" />
          <span>Scan figure 1 in the app, or download the file and import it.</span>
        </li>
        <li class="flex gap-2.5 text-[13px] leading-snug text-toned">
          <NoteMark n="3" />
          <span>Keep this to yourself: the link carries your private key and works until {{ expiresLocal }}.</span>
        </li>
      </ol>
    </div>
  </LooseSheet>
</template>
