<script setup lang="ts">
import type { AvailabilityCheck, AvailabilityChecks } from '~/types/api'

const api = useControlApi()
const { nodes } = useFleetState()
const nodeId = ref('all')
const kind = ref('all')
const until = ref('')
const issuesOnly = ref(false)
const pageCursors = ref<(string | null)[]>([null])
const history = shallowRef<AvailabilityChecks | null>(null)
const table = useTemplateRef<HTMLDivElement>('table')
const pending = ref(true)
const errorMessage = ref<string | null>(null)
let requestId = 0

const nodeOptions = computed(() => [
  { label: 'All nodes', value: 'all' },
  ...nodes.value.map(node => ({ label: node.name, value: node.id }))
])
const kinds = [
  { label: 'All sources', value: 'all' },
  { label: 'Panel assessments', value: 'panel' },
  { label: 'Probe checks', value: 'probe' }
]

const outcomes: Record<AvailabilityCheck['outcome'], {
  label: string
  color: 'success' | 'error' | 'warning' | 'neutral'
}> = {
  healthy: { label: 'Healthy', color: 'success' },
  blocked: { label: 'Blocked', color: 'error' },
  down: { label: 'Down', color: 'error' },
  silent: { label: 'Silent', color: 'warning' },
  unknown: { label: 'Unknown', color: 'neutral' },
  revoked: { label: 'Revoked', color: 'neutral' },
  reachable: { label: 'Reachable', color: 'success' },
  unreachable: { label: 'Unreachable', color: 'error' },
  error: { label: 'Probe error', color: 'warning' }
}

const timestampFormat = new Intl.DateTimeFormat('en-GB', {
  day: 'numeric',
  month: 'short',
  year: 'numeric',
  hour: '2-digit',
  minute: '2-digit',
  second: '2-digit',
  hour12: false
})

async function load(pages: (string | null)[]) {
  const id = ++requestId
  pending.value = true
  errorMessage.value = null
  try {
    const query = new URLSearchParams()
    if (nodeId.value !== 'all') {
      query.set('nodeId', nodeId.value)
    }
    if (kind.value !== 'all') {
      query.set('kind', kind.value)
    }
    if (until.value) {
      query.set('until', new Date(until.value).toISOString())
    }
    if (issuesOnly.value) {
      query.set('issuesOnly', 'true')
    }
    const cursor = pages.at(-1)
    if (cursor) {
      query.set('cursor', cursor)
    }
    const result = await api.get<AvailabilityChecks>(`/nodes/availability/checks?${query}`)
    if (id === requestId) {
      if (pageCursors.value.at(-1) !== pages.at(-1)) {
        table.value?.scrollTo({ top: 0 })
      }
      history.value = result
      pageCursors.value = pages
    }
  } catch (error) {
    if (id === requestId) {
      errorMessage.value = describeError(error, 'Could not load availability checks.')
    }
  } finally {
    if (id === requestId) {
      pending.value = false
    }
  }
}

async function refresh() {
  history.value = null
  pageCursors.value = [null]
  await load([null])
}

watch([nodeId, kind, until, issuesOnly], refresh)
usePolling(async () => {
  if (pageCursors.value.length === 1 && (!pending.value || !requestId)) {
    await load([null])
  }
}, 30000)

defineExpose({ refresh })
</script>

<template>
  <div class="space-y-4">
    <div class="flex flex-wrap items-end gap-3">
      <UFormField label="Node">
        <USelect
          v-model="nodeId"
          :items="nodeOptions"
          class="min-w-36"
        />
      </UFormField>
      <UFormField label="Source">
        <USelect
          v-model="kind"
          :items="kinds"
          class="min-w-40"
        />
      </UFormField>
      <UFormField label="Before (local time)">
        <UInput
          v-model="until"
          type="datetime-local"
        />
      </UFormField>
      <UCheckbox
        v-model="issuesOnly"
        label="Issues only"
        class="self-end py-1.5"
      />
    </div>

    <UAlert
      v-if="errorMessage"
      color="error"
      variant="subtle"
      icon="i-lucide-circle-alert"
      :title="errorMessage"
    />
    <p
      v-if="pending && !history"
      role="status"
      class="py-3 text-sm text-muted"
    >
      Reading checks…
    </p>
    <p
      v-else-if="history && !history.retentionDays"
      class="py-3 text-sm text-muted"
    >
      Availability recording is disabled.
    </p>
    <p
      v-else-if="history && !history.items.length"
      class="py-3 text-sm text-muted"
    >
      No individual checks match. Earlier hourly records are available in Summary.
    </p>
    <div
      v-else-if="history"
      ref="table"
      role="region"
      aria-label="Availability checks"
      tabindex="0"
      class="max-h-[min(34rem,65vh)] overflow-auto rounded-md border border-default"
    >
      <table class="w-full min-w-3xl text-left text-sm">
        <caption class="sr-only">
          Individual availability checks in local time
        </caption>
        <thead class="sticky top-0 z-10 bg-elevated text-xs text-muted shadow-[0_1px_var(--ui-border)]">
          <tr>
            <th
              scope="col"
              class="px-3 py-2"
            >
              Recorded at
            </th>
            <th
              scope="col"
              class="px-3 py-2"
            >
              Node
            </th>
            <th
              scope="col"
              class="px-3 py-2"
            >
              Source
            </th>
            <th
              scope="col"
              class="px-3 py-2"
            >
              Result
            </th>
            <th
              scope="col"
              class="px-3 py-2"
            >
              Handshake
            </th>
            <th
              scope="col"
              class="px-3 py-2"
            >
              Details
            </th>
          </tr>
        </thead>
        <tbody class="divide-y divide-default">
          <tr
            v-for="check in history.items"
            :key="check.id"
            class="align-top"
          >
            <td class="px-3 py-2.5 whitespace-nowrap font-mono text-xs text-muted">
              <time :datetime="check.checkedAt">{{ timestampFormat.format(new Date(check.checkedAt)) }}</time>
            </td>
            <td class="px-3 py-2.5 font-mono text-xs font-medium wrap-anywhere text-highlighted">
              {{ check.nodeName }}
            </td>
            <td class="px-3 py-2.5 text-xs wrap-anywhere">
              {{ check.probeName ?? 'Panel' }}
              <span class="block text-muted">
                {{ check.probeId ? 'Probe' : check.healthSource === 'probes' ? 'From probes' : 'From agent status' }}
              </span>
            </td>
            <td class="px-3 py-2">
              <UBadge
                :color="outcomes[check.outcome].color"
                :label="check.outcome === 'unreachable' && check.handshake ? 'No tunnel traffic' : outcomes[check.outcome].label"
                variant="subtle"
                class="whitespace-nowrap"
              />
            </td>
            <td class="px-3 py-2.5 whitespace-nowrap text-xs text-muted">
              {{ check.handshake == null ? '—' : check.handshake ? 'Completed' : 'Failed' }}
            </td>
            <td class="min-w-52 px-3 py-2.5 text-xs wrap-anywhere text-muted">
              <div v-if="check.detail">
                {{ check.detail }}
              </div>
              <div
                v-if="check.address || check.latencyMs != null"
                class="font-mono"
              >
                {{ [check.address, check.latencyMs == null ? null : `${check.latencyMs} ms`].filter(Boolean).join(' · ') }}
              </div>
              <span v-if="!check.detail && !check.address && check.latencyMs == null">—</span>
            </td>
          </tr>
        </tbody>
      </table>
    </div>

    <div
      v-if="history && (history.nextCursor || pageCursors.length > 1)"
      class="flex items-center justify-between gap-2"
      aria-label="Check history pages"
    >
      <UButton
        color="neutral"
        variant="outline"
        icon="i-lucide-chevron-left"
        :disabled="pending || pageCursors.length === 1"
        @click="load(pageCursors.slice(0, -1))"
      >
        Newer
      </UButton>
      <UButton
        color="neutral"
        variant="outline"
        trailing-icon="i-lucide-chevron-right"
        :disabled="pending || !history.nextCursor"
        @click="load([...pageCursors, history.nextCursor ?? null])"
      >
        Older
      </UButton>
    </div>
  </div>
</template>
