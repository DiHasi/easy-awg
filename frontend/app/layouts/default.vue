<script setup lang="ts">
/**
 * The drawing sheet every admin page is drawn on: a double frame with zone rulers, the sheet
 * index along the top and the title block in the corner. The title block quotes the fleet
 * revision and how many nodes run it, so it is refreshed here for every sheet at once.
 */
const route = useRoute()
const colorMode = useColorMode()
const { user, logout } = useSession()
const { fleet, serving, appliedCount, refreshedAt, errorMessage, refresh } = useFleetState()

usePolling(refresh, 10000)

const sheets = [
  { to: '/', label: 'Peers' },
  { to: '/nodes', label: 'Nodes' },
  { to: '/admin', label: 'Wire format' },
  { to: '/events', label: 'Log' }
]

const sheetIndex = computed(() => Math.max(sheets.findIndex(sheet => sheet.to === route.path), 0))
const sheet = computed(() => sheets[sheetIndex.value]!)

const zones = ['A', 'B', 'C', 'D', 'E', 'F']
const rows = ['1', '2', '3', '4']

const isCyanotype = computed(() => colorMode.value === 'dark')

function toggleMedium() {
  colorMode.preference = isCyanotype.value ? 'light' : 'dark'
}

useHead({ title: computed(() => `${sheet.value.label} · AWG Easy`) })
</script>

<template>
  <div class="min-h-screen bg-desk p-1.5 sm:p-3 lg:p-4">
    <div class="mx-auto max-w-[1680px] border border-(--ui-text-dimmed) bg-default p-[3px]">
      <div class="flex min-h-[calc(100vh-1.5rem)] flex-col border border-default sm:min-h-[calc(100vh-2rem)]">
        <!-- zone ruler -->
        <div
          class="hidden h-4 border-b border-muted md:flex"
          aria-hidden="true"
        >
          <span
            v-for="zone in zones"
            :key="zone"
            class="flex flex-1 items-center justify-center border-r border-muted font-mono text-[9px] text-dimmed last:border-r-0"
          >{{ zone }}</span>
        </div>

        <div class="flex min-h-0 flex-1">
          <div
            class="hidden w-4 flex-col border-r border-muted md:flex"
            aria-hidden="true"
          >
            <span
              v-for="row in rows"
              :key="row"
              class="flex flex-1 items-center justify-center border-b border-muted font-mono text-[9px] text-dimmed last:border-b-0"
            >{{ row }}</span>
          </div>

          <div class="flex min-w-0 flex-1 flex-col">
            <!-- sheet index -->
            <header class="flex flex-wrap items-stretch border-b border-default">
              <NuxtLink
                to="/"
                class="flex items-center gap-2.5 border-e border-default px-3 py-2.5 sm:px-4"
              >
                <span class="caps text-[11px] tracking-[0.2em] text-highlighted">AWG&nbsp;Easy</span>
                <span
                  v-if="fleet"
                  class="hidden font-mono text-[11px] text-muted sm:inline"
                >{{ fleet.endpointHost }}</span>
              </NuxtLink>

              <nav
                aria-label="Sheets"
                class="order-last flex w-full overflow-x-auto border-t border-default md:order-none md:w-auto md:border-t-0"
              >
                <NuxtLink
                  v-for="(item, index) in sheets"
                  :key="item.to"
                  :to="item.to"
                  class="caps flex min-h-11 shrink-0 items-center gap-1.5 border-e border-default px-2.5 transition-colors sm:gap-2 sm:px-3.5 md:min-h-0"
                  :class="index === sheetIndex ? 'bg-inverted text-inverted' : 'text-toned hover:bg-muted'"
                >
                  <span class="font-mono text-[10px] opacity-70">{{ index + 1 }}</span>
                  <span>{{ item.label }}</span>
                </NuxtLink>
              </nav>

              <div class="ms-auto flex items-center gap-1 px-2">
                <UButton
                  variant="ghost"
                  size="sm"
                  :icon="isCyanotype ? 'i-lucide-sun' : 'i-lucide-moon'"
                  :aria-label="isCyanotype ? 'Draw on paper' : 'Draw as cyanotype'"
                  @click="toggleMedium"
                >
                  <span class="hidden sm:inline">{{ isCyanotype ? 'Paper' : 'Cyanotype' }}</span>
                </UButton>
                <UButton
                  v-if="user"
                  variant="ghost"
                  size="sm"
                  icon="i-lucide-log-out"
                  :aria-label="`Sign out ${user}`"
                  @click="logout"
                >
                  <span class="hidden sm:inline">Sign out</span>
                </UButton>
              </div>
            </header>

            <p
              v-if="errorMessage"
              class="flex items-center gap-2 border-b border-default bg-muted px-3 py-2 font-mono text-xs text-error sm:px-4"
              role="alert"
            >
              <UIcon
                name="i-lucide-triangle-alert"
                class="size-4 shrink-0"
              />
              {{ errorMessage }} The sheet shows the last state that was read.
            </p>

            <main class="flex min-w-0 flex-1 flex-col">
              <slot />
            </main>

            <!-- legend and title block -->
            <footer class="flex flex-col border-t border-accented lg:flex-row">
              <div class="flex flex-1 flex-wrap items-center gap-x-6 gap-y-2 px-3 py-2.5 sm:px-4">
                <span class="caps text-muted">Legend</span>
                <span class="inline-flex items-center gap-2 font-mono text-[11px] text-toned">
                  <svg
                    width="28"
                    height="6"
                    aria-hidden="true"
                  ><path
                    d="M0 3h28"
                    class="stroke-live"
                    stroke-width="2.2"
                  /></svg>
                  live path
                </span>
                <span class="inline-flex items-center gap-2 font-mono text-[11px] text-toned">
                  <svg
                    width="28"
                    height="6"
                    aria-hidden="true"
                  ><path
                    d="M0 3h28"
                    class="stroke-standby"
                    stroke-width="1.2"
                    stroke-dasharray="6 4"
                  /></svg>
                  standby
                </span>
                <span class="font-mono text-[11px] text-toned">● up&nbsp;&nbsp;○ idle&nbsp;&nbsp;– off</span>
                <span
                  v-if="refreshedAt"
                  class="font-mono text-[11px] text-muted"
                >drawn {{ formatUtc(refreshedAt) }}</span>
              </div>

              <div class="grid grid-cols-[auto_1fr] border-t border-default text-xs lg:w-[440px] lg:border-t-0 lg:border-s">
                <span class="caps border-e border-b border-muted px-2.5 py-1.5 text-muted">Project</span>
                <span class="truncate border-b border-muted px-2.5 py-1.5 font-mono">awg-easy · {{ fleet?.endpointHost ?? '—' }}</span>
                <span class="caps border-e border-b border-muted px-2.5 py-1.5 text-muted">Sheet</span>
                <span class="border-b border-muted px-2.5 py-1.5 font-mono">{{ sheetIndex + 1 }} of {{ sheets.length }} · {{ sheet.label.toLowerCase() }}</span>
                <span class="caps border-e border-b border-muted px-2.5 py-1.5 text-muted">Identity</span>
                <span class="truncate border-b border-muted px-2.5 py-1.5 font-mono">{{ shortKey(fleet?.serverPublicKey) }} · one key, every node</span>
                <span class="caps border-e border-muted px-2.5 py-1.5 text-muted">Subnet</span>
                <span class="px-2.5 py-1.5 font-mono">{{ fleet ? `${fleet.subnet} · udp ${fleet.listenPort}` : '—' }}</span>
                <div class="col-span-2 flex items-center gap-3 border-t border-accented px-2.5 py-2">
                  <span class="caps text-muted">Revision</span>
                  <span class="tabular font-mono text-2xl leading-none text-highlighted">{{ fleet?.revision ?? '—' }}</span>
                  <span class="ms-auto flex flex-col text-right">
                    <span class="caps text-muted">Applied</span>
                    <span
                      class="font-mono"
                      :class="appliedCount < serving.length ? 'text-warning' : 'text-default'"
                    >{{ appliedCount }} of {{ serving.length }} nodes</span>
                  </span>
                </div>
              </div>
            </footer>
          </div>
        </div>
      </div>
    </div>
  </div>
</template>
