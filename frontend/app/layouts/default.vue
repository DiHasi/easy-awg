<script setup lang="ts">
/**
 * The admin shell. The revision chip is refreshed here for every page at once: it is the one
 * fleet-wide fact worth seeing wherever you are, because a node that has not applied the latest
 * revision is still running the previous configuration.
 */
const route = useRoute()
const { user, logout } = useSession()
const { fleet, serving, appliedCount, errorMessage, refresh } = useFleetState()
const { pair, isStandby, blank, refresh: refreshPanel } = usePanelState()

// One timer for both. Which panel this is belongs in the frame rather than on a page: an operator
// who does not know which of two identical panels they are looking at will try to make the change
// on the wrong one, and a standby refuses it with a message they will read as a bug.
usePolling(() => Promise.all([refresh(), refreshPanel()]), 10000)

const links = [
  { to: '/', label: 'Overview', icon: 'i-lucide-network' },
  { to: '/nodes', label: 'Nodes', icon: 'i-lucide-server' },
  { to: '/usage', label: 'Traffic', icon: 'i-lucide-chart-column' },
  { to: '/admin', label: 'Settings', icon: 'i-lucide-sliders-horizontal' },
  { to: '/events', label: 'Log', icon: 'i-lucide-scroll-text' }
]

const current = computed(() => links.find(link => link.to === route.path) ?? links[0]!)
const behind = computed(() => appliedCount.value < serving.value.length)

useHead({ title: computed(() => `${current.value.label} · AWG Easy`) })
</script>

<template>
  <div class="min-h-screen pb-20 md:pb-0">
    <header class="sticky top-0 z-30 border-b border-default bg-default/90 backdrop-blur">
      <div class="mx-auto flex h-14 max-w-[1400px] items-center gap-3 px-4 lg:px-6">
        <NuxtLink
          to="/"
          class="flex shrink-0 items-center gap-2.5"
        >
          <span class="flex size-8 items-center justify-center rounded-md bg-inverted text-inverted">
            <UIcon
              name="i-lucide-shield-check"
              class="size-[18px]"
            />
          </span>
          <span class="font-semibold text-highlighted">AWG Easy</span>
          <span
            v-if="fleet"
            class="hidden font-mono text-xs text-muted xl:inline"
          >{{ fleet.endpointHost }}</span>
        </NuxtLink>

        <nav
          class="ms-3 hidden items-center gap-1 md:flex"
          aria-label="Main"
        >
          <NuxtLink
            v-for="link in links"
            :key="link.to"
            :to="link.to"
            class="flex h-9 items-center gap-2 rounded-md px-3 text-sm font-medium transition-colors"
            :class="link.to === current.to ? 'bg-elevated text-highlighted' : 'text-muted hover:bg-elevated/60 hover:text-default'"
          >
            <UIcon
              :name="link.icon"
              class="size-4"
            />
            {{ link.label }}
          </NuxtLink>
        </nav>

        <div class="ms-auto flex items-center gap-1.5">
          <NuxtLink
            v-if="fleet"
            to="/nodes"
            class="hidden h-8 items-center gap-1.5 rounded-md border px-2.5 text-xs font-medium sm:flex"
            :class="behind ? 'border-warning/40 bg-warning/10 text-warning' : 'border-default text-muted hover:text-default'"
            :title="behind ? 'Some nodes still run an older configuration' : 'Every node runs the latest configuration'"
          >
            <UIcon
              :name="behind ? 'i-lucide-refresh-cw' : 'i-lucide-check'"
              class="size-3.5"
            />
            <span class="font-mono">r{{ fleet.revision }}</span>
            <span>· {{ appliedCount }}/{{ serving.length }} nodes updated</span>
          </NuxtLink>
          <UColorModeButton />
          <UButton
            v-if="user"
            icon="i-lucide-log-out"
            color="neutral"
            variant="ghost"
            :aria-label="`Sign out ${user}`"
            @click="logout"
          />
        </div>
      </div>
    </header>

    <div
      v-if="isStandby"
      role="status"
      class="border-b border-warning/30 bg-warning/10"
    >
      <p class="mx-auto flex max-w-[1400px] flex-wrap items-center gap-x-2 gap-y-1 px-4 py-2 text-sm text-warning lg:px-6">
        <UIcon
          name="i-lucide-layers"
          class="size-4 shrink-0"
        />
        <span class="font-medium">Standby panel</span>
        <span class="text-warning/90">
          <template v-if="blank">
            It has not replicated the fleet yet, so there is nothing to show here.
          </template>
          <template v-else>
            Read-only - it replicates the panel in charge, last {{ relativeTime(pair?.syncedAt) }}. Make changes there.
          </template>
        </span>
        <NuxtLink
          to="/nodes"
          class="ms-auto shrink-0 font-medium underline decoration-warning/40 hover:decoration-warning"
        >
          Panel pair
        </NuxtLink>
      </p>
    </div>

    <div
      v-if="errorMessage && !blank"
      role="alert"
      class="border-b border-error/30 bg-error/10"
    >
      <p class="mx-auto flex max-w-[1400px] items-center gap-2 px-4 py-2 text-sm text-error lg:px-6">
        <UIcon
          name="i-lucide-triangle-alert"
          class="size-4 shrink-0"
        />
        {{ errorMessage }} Showing the last state that was read.
      </p>
    </div>

    <main class="mx-auto max-w-[1400px] px-3 py-4 sm:px-4 lg:px-6 lg:py-6">
      <slot />
    </main>

    <nav
      class="fixed inset-x-0 bottom-0 z-30 grid grid-cols-5 border-t border-default bg-default md:hidden"
      aria-label="Main"
    >
      <NuxtLink
        v-for="link in links"
        :key="link.to"
        :to="link.to"
        class="flex h-16 flex-col items-center justify-center gap-1 text-[11px] font-medium"
        :class="link.to === current.to ? 'text-primary' : 'text-muted'"
      >
        <UIcon
          :name="link.icon"
          class="size-5"
        />
        {{ link.label }}
      </NuxtLink>
    </nav>
  </div>
</template>
