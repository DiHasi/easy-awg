<script setup lang="ts">
import QRCode from 'qrcode'

// Deliberately no auth middleware: a share link must work for someone with no account.
definePageMeta({ layout: false })

type PublicShare = {
  expiresAt: string
}

/**
 * Where to get the client app. AmneziaWG ships per platform, and someone who was handed a link
 * has no idea which build is theirs - so the page picks, rather than listing five options.
 */
const AMNEZIAWG_APPS = {
  android: { label: 'Get AmneziaWG for Android', url: 'https://play.google.com/store/apps/details?id=org.amnezia.awg' },
  ios: { label: 'Get AmneziaWG for iPhone', url: 'https://apps.apple.com/app/amneziawg/id6478942365' },
  macos: { label: 'Get AmneziaWG for macOS', url: 'https://apps.apple.com/app/amneziawg/id6478942365' },
  linux: { label: 'Get AmneziaWG for Linux', url: 'https://github.com/amnezia-vpn/amneziawg-linux-kernel-module' },
  windows: { label: 'Get AmneziaWG for Windows', url: 'https://github.com/amnezia-vpn/amneziawg-windows-client/releases/latest' }
} as const

const route = useRoute()
const api = useControlApi()

const token = computed(() => String(route.params.token ?? ''))
const share = ref<PublicShare | null>(null)
const loading = ref(true)
const errorMessage = ref<string | null>(null)
const qrDataUrl = ref<string | null>(null)

const configUrl = computed(() => api.url(`/shares/${token.value}/config`))

// Resolved after mount, never during prerender: the static build has no user agent to read, and
// a Windows link baked into the page would be wrong for most of the people opening it.
const platform = ref<keyof typeof AMNEZIAWG_APPS>('windows')
const app = computed(() => AMNEZIAWG_APPS[platform.value])

function detectPlatform(): keyof typeof AMNEZIAWG_APPS {
  const agent = navigator.userAgent.toLowerCase()
  if (agent.includes('android')) {
    return 'android'
  }
  if (agent.includes('iphone') || agent.includes('ipad') || agent.includes('ipod')) {
    return 'ios'
  }
  // An iPad on recent iPadOS claims to be a Mac; the App Store listing covers both anyway.
  if (agent.includes('mac os')) {
    return 'macos'
  }
  if (agent.includes('linux')) {
    return 'linux'
  }
  return 'windows'
}

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

onMounted(() => {
  platform.value = detectPlatform()
  load()
})

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
            Scan it or download it below
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

        <div class="grid gap-2">
          <!-- Named here as well as in Content-Disposition: a plain navigation lets the browser
               pick, and some of them pick an extension of their own. Not the client name. -->
          <UButton
            :href="configUrl"
            download="amneziawg.conf"
            icon="i-lucide-download"
            size="lg"
            block
            external
          >
            Download config
          </UButton>
          <UButton
            :href="app.url"
            icon="i-lucide-external-link"
            color="neutral"
            variant="subtle"
            size="lg"
            block
            external
            target="_blank"
            rel="noopener"
          >
            {{ app.label }}
          </UButton>
        </div>

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
