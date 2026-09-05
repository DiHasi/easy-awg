<script setup lang="ts">
import type { AuditEvent } from '~/types/api'

definePageMeta({ middleware: 'auth' })

const api = useControlApi()

const events = ref<AuditEvent[]>([])
const loading = ref(true)
const errorMessage = ref<string | null>(null)

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

function toneFor(kind: string) {
  if (kind.includes('failed') || kind.includes('revoked') || kind.includes('deleted')) {
    return 'error'
  }
  if (kind.startsWith('node.')) {
    return 'info'
  }
  if (kind.startsWith('fleet.')) {
    return 'warning'
  }
  return 'neutral'
}

function formatTime(value: string) {
  return new Date(value).toLocaleString()
}

onMounted(loadEvents)
</script>

<template>
  <UContainer class="py-6">
    <div class="flex flex-col gap-6">
      <section class="flex items-end justify-between gap-4">
        <div>
          <h1 class="text-2xl font-semibold tracking-tight text-highlighted">
            Events
          </h1>
          <p class="mt-1 text-sm text-muted">
            Who changed what, and which nodes pulled configuration.
          </p>
        </div>

        <UButton
          icon="i-lucide-refresh-cw"
          color="neutral"
          variant="subtle"
          :loading="loading"
          @click="loadEvents"
        >
          Refresh
        </UButton>
      </section>

      <UAlert
        v-if="errorMessage"
        color="error"
        variant="subtle"
        icon="i-lucide-circle-alert"
        title="Could not load events"
        :description="errorMessage"
      />

      <div class="overflow-hidden rounded-lg border border-default bg-default">
        <div
          v-if="loading"
          class="flex min-h-48 items-center justify-center text-sm text-muted"
        >
          <UIcon
            name="i-lucide-loader-circle"
            class="mr-2 size-5 animate-spin"
          />
          Loading events
        </div>

        <p
          v-else-if="events.length === 0"
          class="p-6 text-center text-sm text-muted"
        >
          Nothing recorded yet.
        </p>

        <ul
          v-else
          class="divide-y divide-default"
        >
          <li
            v-for="event in events"
            :key="event.id"
            class="flex flex-col gap-1 p-3 sm:flex-row sm:items-center sm:gap-3"
          >
            <UBadge
              :color="toneFor(event.kind)"
              variant="subtle"
              class="shrink-0 self-start font-mono"
            >
              {{ event.kind }}
            </UBadge>
            <span class="min-w-0 flex-1 text-sm text-default">{{ event.message }}</span>
            <span class="shrink-0 text-xs text-muted">
              <span v-if="event.actor">{{ event.actor }} · </span>{{ formatTime(event.at) }}
            </span>
          </li>
        </ul>
      </div>
    </div>
  </UContainer>
</template>
