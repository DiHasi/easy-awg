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
          <NuxtLink
            to="/"
            class="flex min-w-0 items-center gap-3 rounded-md focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-primary"
            aria-label="Go to clients"
          >
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
          </NuxtLink>

          <div class="flex items-center gap-2">
            <UButton
              to="/admin"
              icon="i-lucide-settings"
              color="neutral"
              variant="subtle"
              size="sm"
            >
              Admin
            </UButton>
            <UColorModeButton />
          </div>
        </UContainer>
      </header>

      <UMain>
        <NuxtPage />
      </UMain>
    </div>
  </UApp>
</template>
