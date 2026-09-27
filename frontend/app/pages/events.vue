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
    return 'text-error'
  }
  if (kind.startsWith('node.')) {
    return 'text-info'
  }
  if (kind.startsWith('fleet.')) {
    return 'text-warning'
  }
  return 'text-toned'
}

usePolling(loadEvents, 15000)
</script>

<template>
  <div class="flex flex-col">
    <SheetSection
      title="Revision history"
      flush
    >
      <template #meta>
        {{ visible.length }} of {{ events.length }} entries · newest first · utc
      </template>

      <template #actions>
        <!-- Areas come from the data, so there may be more than a phone is wide. -->
        <div
          v-if="areas.length > 1"
          class="max-w-full overflow-x-auto"
        >
          <UFieldGroup>
            <UButton
              size="sm"
              :color="area === 'all' ? 'primary' : 'neutral'"
              :variant="area === 'all' ? 'solid' : 'outline'"
              :aria-pressed="area === 'all'"
              @click="area = 'all'"
            >
              All
            </UButton>
            <UButton
              v-for="[name, count] in areas"
              :key="name"
              size="sm"
              :color="area === name ? 'primary' : 'neutral'"
              :variant="area === name ? 'solid' : 'outline'"
              :aria-pressed="area === name"
              @click="area = name"
            >
              {{ name }}&nbsp;<span class="font-mono opacity-70">{{ count }}</span>
            </UButton>
          </UFieldGroup>
        </div>
        <UButton
          icon="i-lucide-refresh-cw"
          size="sm"
          :loading="loading"
          @click="loadEvents"
        >
          Refresh
        </UButton>
      </template>

      <UAlert
        v-if="errorMessage"
        class="m-3 w-auto sm:m-4"
        color="error"
        variant="subtle"
        icon="i-lucide-circle-alert"
        title="Could not read the log"
        :description="errorMessage"
      />

      <div
        class="hidden grid-cols-[4.5rem_10.5rem_12rem_7rem_minmax(0,1fr)] gap-x-4 border-b border-accented px-4 py-1.5 lg:grid"
        aria-hidden="true"
      >
        <span class="caps text-muted">No.</span>
        <span class="caps text-muted">Date</span>
        <span class="caps text-muted">Change</span>
        <span class="caps text-muted">By</span>
        <span class="caps text-muted">Description</span>
      </div>

      <p
        v-if="loading && events.length === 0"
        class="px-4 py-10 text-center font-mono text-xs text-muted"
      >
        reading the log…
      </p>

      <p
        v-else-if="visible.length === 0"
        class="px-4 py-10 text-center font-mono text-xs text-muted"
      >
        nothing recorded yet
      </p>

      <ol v-else>
        <li
          v-for="event in visible"
          :key="event.id"
          class="grid grid-cols-[minmax(0,1fr)_auto] gap-x-4 gap-y-1 border-b border-muted px-3 py-2 last:border-b-0 sm:px-4 lg:grid-cols-[4.5rem_10.5rem_12rem_7rem_minmax(0,1fr)] lg:items-baseline"
        >
          <span class="hidden font-mono text-[11px] text-muted lg:block">{{ event.id }}</span>
          <span class="order-2 font-mono text-[11px] text-muted lg:order-none">{{ formatUtc(event.at).replace(' UTC', '') }}</span>
          <span
            class="order-1 break-all font-mono text-xs lg:order-none"
            :class="toneFor(event.kind)"
          >{{ event.kind }}</span>
          <span class="order-3 hidden font-mono text-xs text-toned lg:order-none lg:block">{{ event.actor ?? '—' }}</span>
          <span class="order-4 col-span-2 text-[13px] leading-snug text-default lg:order-none lg:col-span-1">
            {{ event.message }}
            <span
              v-if="event.actor"
              class="font-mono text-[11px] text-muted lg:hidden"
            > · {{ event.actor }}</span>
          </span>
        </li>
      </ol>
    </SheetSection>
  </div>
</template>
