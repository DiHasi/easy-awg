<script setup lang="ts">
useHead({
  meta: [
    { name: 'viewport', content: 'width=device-width, initial-scale=1' }
  ],
  link: [
    { rel: 'icon', href: '/favicon.ico' }
  ],
  htmlAttrs: {
    lang: 'ru'
  }
})

useSeoMeta({
  title: 'AWG Easy',
  description: 'Web UI for AmneziaWG clients'
})
</script>

<template>
  <UApp>
    <div class="min-h-screen bg-default">
      <header class="border-b border-default bg-muted/30">
        <UContainer class="flex h-14 items-center justify-between gap-4">
          <div class="flex min-w-0 items-center gap-3">
            <UIcon
              name="i-lucide-shield"
              class="size-6 text-primary"
            />
            <div class="min-w-0">
              <p class="truncate text-sm font-semibold text-highlighted">
                AWG Easy
              </p>
              <p class="truncate text-xs text-muted">
                AmneziaWG clients
              </p>
            </div>
          </div>

          <UColorModeButton />
        </UContainer>
      </header>

      <UMain>
        <NuxtPage />
      </UMain>
    </div>
  </UApp>
</template>
