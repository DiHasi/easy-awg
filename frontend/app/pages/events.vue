<script setup lang="ts">
import type { AuditEvent } from '~/types/api'

definePageMeta({ middleware: 'auth' })

const api = useControlApi()

const events = ref<AuditEvent[]>([])
const loading = ref(true)
const errorMessage = ref<string | null>(null)
const area = ref<string>('all')

async function loadEvents() {
  try {
    events.value = await api.get<AuditEvent[]>('/events')
    errorMessage.value = null
  } catch (error) {
    errorMessage.value = describeError(error, 'Failed to load events.')
  } finally {
    loading.value = false
  }
}

/** The part of the system an event belongs to: `node.enrolled` → `node`. */
function areaOf(kind: string) {
  return kind.split('.')[0] || kind
}

const areas = computed(() => {
  const found = new Map<string, number>()
  for (const event of events.value) {
    found.set(areaOf(event.kind), (found.get(areaOf(event.kind)) ?? 0) + 1)
  }
  return [...found.entries()].sort((a, b) => b[1] - a[1])
})

const visible = computed(() => area.value === 'all'
  ? events.value
  : events.value.filter(event => areaOf(event.kind) === area.value))

function toneFor(kind: string) {
  if (kind.includes('failed') || kind.includes('revoked') || kind.includes('deleted')) {
    return 'bg-error/10 text-error'
  }
  if (kind.startsWith('node.')) {
    return 'bg-info/10 text-info'
  }
  if (kind.startsWith('fleet.')) {
    return 'bg-warning/10 text-warning'
  }
  return 'bg-elevated text-toned'
}

usePolling(loadEvents, 15000)
</script>

<template>
  <div class="flex flex-col gap-4 lg:gap-6">
    <h1 class="sr-only">
      Log
    </h1>

    <AppCard
      title="Activity log"
      icon="i-lucide-scroll-text"
      description="Who changed what, and which nodes pulled configuration. Newest first, times in UTC."
      flush
    >
      <template #actions>
        <UButton
          icon="i-lucide-refresh-cw"
          color="neutral"
          variant="outline"
          :loading="loading"
          @click="loadEvents"
        >
          Refresh
        </UButton>
      </template>

      <!-- Areas come from the data, so there may be more than a phone is wide. -->
      <div
        v-if="areas.length > 1"
        class="max-w-full overflow-x-auto border-b border-default px-4 py-3 sm:px-5"
      >
        <UFieldGroup>
          <UButton
            size="sm"
            color="neutral"
            :variant="area === 'all' ? 'solid' : 'outline'"
            :aria-pressed="area === 'all'"
            @click="area = 'all'"
          >
            All <span class="tabular opacity-70">{{ events.length }}</span>
          </UButton>
          <UButton
            v-for="[name, count] in areas"
            :key="name"
            size="sm"
            color="neutral"
            :variant="area === name ? 'solid' : 'outline'"
            :aria-pressed="area === name"
            @click="area = name"
          >
            {{ name }} <span class="tabular opacity-70">{{ count }}</span>
          </UButton>
        </UFieldGroup>
      </div>

      <UAlert
        v-if="errorMessage"
        class="m-4 w-auto"
        color="error"
        variant="subtle"
        icon="i-lucide-circle-alert"
        title="Could not load the log"
        :description="errorMessage"
      />

      <div
        v-if="loading && events.length === 0"
        class="flex items-center justify-center gap-2 px-4 py-12 text-sm text-muted"
      >
        <UIcon
          name="i-lucide-loader-circle"
          class="size-5 animate-spin"
        />
        Loading
      </div>

      <p
        v-else-if="visible.length === 0"
        class="px-4 py-12 text-center text-sm text-muted"
      >
        Nothing recorded yet.
      </p>

      <ol
        v-else
        class="divide-y divide-default"
      >
        <li
          v-for="event in visible"
          :key="event.id"
          class="grid gap-x-4 gap-y-1 px-4 py-3 sm:px-5 lg:grid-cols-[9.5rem_13rem_minmax(0,1fr)_7rem] lg:items-center"
        >
          <span class="tabular font-mono text-xs text-muted">{{ formatUtc(event.at).replace(' UTC', '') }}</span>
          <span>
            <span
              class="inline-block max-w-full truncate rounded-md px-2 py-0.5 font-mono text-xs"
              :class="toneFor(event.kind)"
            >{{ event.kind }}</span>
          </span>
          <span class="text-sm text-default">{{ event.message }}</span>
          <span class="text-xs text-muted lg:text-end">{{ event.actor ?? 'system' }}</span>
        </li>
      </ol>
    </AppCard>
  </div>
</template>
