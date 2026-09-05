<script setup lang="ts">
const route = useRoute()
const { user, logout } = useSession()

useHead({
  meta: [{ name: 'viewport', content: 'width=device-width, initial-scale=1' }],
  link: [{ rel: 'icon', href: '/favicon.ico' }],
  htmlAttrs: { lang: 'en' }
})

useSeoMeta({
  title: 'AWG Easy',
  description: 'Control plane for an AmneziaWG fleet'
})

const navigation = [
  { to: '/', label: 'Clients', icon: 'i-lucide-users' },
  { to: '/nodes', label: 'Nodes', icon: 'i-lucide-server' },
  { to: '/admin', label: 'Fleet', icon: 'i-lucide-settings' },
  { to: '/events', label: 'Events', icon: 'i-lucide-scroll-text' }
]

// Login and share pages render standalone, without the admin chrome.
const showChrome = computed(() => route.path !== '/login' && !route.path.startsWith('/share/'))
</script>

<template>
  <UApp>
    <div class="min-h-screen bg-default">
      <header
        v-if="showChrome"
        class="border-b border-default bg-muted/30"
      >
        <UContainer class="flex h-14 items-center justify-between gap-4">
          <div class="flex min-w-0 items-center gap-6">
            <NuxtLink
              to="/"
              class="flex shrink-0 items-center gap-2"
              aria-label="AWG Easy"
            >
              <UIcon
                name="i-lucide-shield"
                class="size-6 text-primary"
              />
              <span class="text-sm font-semibold text-highlighted">AWG Easy</span>
            </NuxtLink>

            <nav class="flex items-center gap-1 overflow-x-auto">
              <UButton
                v-for="item in navigation"
                :key="item.to"
                :to="item.to"
                :icon="item.icon"
                :color="route.path === item.to ? 'primary' : 'neutral'"
                :variant="route.path === item.to ? 'subtle' : 'ghost'"
                size="sm"
              >
                {{ item.label }}
              </UButton>
            </nav>
          </div>

          <div class="flex shrink-0 items-center gap-2">
            <UColorModeButton />
            <UButton
              v-if="user"
              icon="i-lucide-log-out"
              color="neutral"
              variant="ghost"
              size="sm"
              :aria-label="`Sign out ${user}`"
              @click="logout"
            />
          </div>
        </UContainer>
      </header>

      <UMain>
        <NuxtPage />
      </UMain>
    </div>
  </UApp>
</template>
