<script setup lang="ts">
import QRCode from 'qrcode'

/**
 * A config QR code a camera can actually read.
 *
 * A full AmneziaWG config is around 750 bytes - a version 19-22 symbol, roughly a hundred modules
 * across, where a plain WireGuard config is half that. Three things that are harmless on a small
 * symbol make one that size unreadable, so all three are handled here:
 *
 * - a raster rendered at a fractional scale samples modules at uneven widths, and CSS shrinking
 *   the bitmap afterwards blurs what is left. This renders SVG and lets the layout size it, so
 *   every module stays a crisp whole number of pixels at whatever size it ends up.
 * - error correction level L. A screen has no smudges or creases to recover from, and the spare
 *   codewords of level M cost three versions - twelve more modules across the same box.
 * - a hundred modules inside a 20rem card is about three pixels each, which is the edge of what
 *   a phone resolves. Tapping fills the screen with it, where each module is three times that.
 */
const props = defineProps<{ config: string | null }>()

const source = ref<string | null>(null)
const tooLong = ref(false)
const zoomed = ref(false)

watch(() => props.config, async (config) => {
  source.value = null
  tooLong.value = false

  if (!config) {
    return
  }

  try {
    // margin 2 rather than the spec's 4: the white card around the code is quiet zone too, and
    // widening the symbol's own border shrinks the modules inside a fixed box.
    const svg = await QRCode.toString(config, { type: 'svg', errorCorrectionLevel: 'L', margin: 2 })
    source.value = `data:image/svg+xml;charset=utf-8,${encodeURIComponent(svg)}`
  } catch {
    // Long I1-I5 obfuscation packets can outgrow the largest symbol that exists.
    tooLong.value = true
  }
}, { immediate: true })
</script>

<template>
  <div class="flex w-full flex-col items-center gap-1.5">
    <!-- Always dark on white: a scanner reads contrast, not the page theme. -->
    <button
      v-if="source"
      type="button"
      aria-label="Enlarge the QR code"
      class="w-full cursor-zoom-in rounded-lg bg-white p-3 ring-1 ring-default transition hover:ring-accented"
      @click="zoomed = true"
    >
      <img
        :src="source"
        alt="Configuration QR code"
        class="aspect-square w-full"
      >
    </button>
    <div
      v-else-if="tooLong"
      class="flex aspect-square w-full items-center justify-center rounded-lg bg-elevated p-6 text-center text-sm text-muted"
    >
      This config is too long to fit in a QR code. Use the file instead.
    </div>
    <div
      v-else
      class="flex aspect-square w-full items-center justify-center rounded-lg bg-elevated"
    >
      <UIcon
        name="i-lucide-loader-circle"
        class="size-5 animate-spin"
      />
    </div>

    <span
      v-if="source"
      class="text-xs text-muted"
    >
      Scan in the AmneziaWG app &middot; tap to enlarge
    </span>

    <UModal
      v-model:open="zoomed"
      fullscreen
      title="Configuration QR code"
      description="Scan it in the AmneziaWG app"
    >
      <template #body>
        <div class="flex h-full items-center justify-center">
          <!-- Square and as large as the shorter side of the viewport allows: the point of this
               view is module size, so it tracks the height as well as the width. -->
          <div class="w-full max-w-[min(100%,calc(100vh-12rem))] rounded-xl bg-white p-4">
            <img
              :src="source!"
              alt="Configuration QR code"
              class="aspect-square w-full"
            >
          </div>
        </div>
      </template>
    </UModal>
  </div>
</template>
