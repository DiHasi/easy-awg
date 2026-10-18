<script setup lang="ts">
import type { AvailabilitySummary, UsageWindowKey } from '~/types/api'

/**
 * How the fleet has stood, rather than how it stands.
 *
 * The node cards above answer what is wrong right now, which is what matters while something is
 * burning. This answers the slower questions: whether the node that just recovered has been
 * flapping all week, and - from the probe rows - which network a block is actually in. That second
 * one is the only place in the panel where "blocked" gets an answer naming a vantage point, and it
 * is what decides whether an operator needs a new address or a node somewhere else entirely.
 *
 * Polled slowly on purpose: this is a history, and its newest bucket only moves as fast as the
 * failover rounds that write it.
 */
const api = useControlApi()
const { nodes } = useFleetState()

const windows: { value: UsageWindowKey, label: string }[] = [
  { value: '24h', label: '24 hours' },
  { value: '7d', label: '7 days' },
  { value: '30d', label: '30 days' },
  { value: '90d', label: '90 days' }
]

const span = ref<UsageWindowKey>('24h')
const history = ref<AvailabilitySummary | null>(null)
const loading = ref(true)
const errorMessage = ref<string | null>(null)

async function load() {
  try {
    history.value = await api.get<AvailabilitySummary>(`/nodes/availability?window=${span.value}&offset=${utcOffsetMinutes()}`)
    errorMessage.value = null
  } catch (error) {
    errorMessage.value = describeError(error, 'Failed to read the availability history.')
  } finally {
    loading.value = false
  }
}

watch(span, async () => {
  loading.value = true
  await load()
})

const recording = computed(() => (history.value?.retentionDays ?? 0) > 0)

/** Nodes in the order the cards above read them, so the two lists line up. */
const rows = computed(() => {
  const order = new Map(nodes.value.map((node, index) => [node.id, index]))
  return [...(history.value?.nodes ?? [])]
    .sort((a, b) => (order.get(a.nodeId) ?? Number.MAX_SAFE_INTEGER) - (order.get(b.nodeId) ?? Number.MAX_SAFE_INTEGER))
})

/** Probes that reported about at least one node in the window; a silent one has nothing to show. */
const vantagePoints = computed(() => (history.value?.probes ?? []).filter(probe => probe.nodes.length > 0))

const nodeNames = computed(() => new Map((history.value?.nodes ?? []).map(node => [node.nodeId, node.name])))

function uptimeTone(uptime?: number | null) {
  if (uptime === null || uptime === undefined) {
    return 'text-dimmed'
  }
  if (uptime >= 0.99) {
    return 'text-success'
  }
  return uptime >= 0.9 ? 'text-warning' : 'text-error'
}

/** A share as the page shows it: "—" for no evidence, never 0%, which would be a claim. */
function share(value?: number | null) {
  return value === null || value === undefined ? '—' : formatShare(value, 1)
}

// Slower than the node cards poll at: a bucket only moves as fast as the failover rounds that
// write it, and the oldest of them is an hour wide.
usePolling(load, 60000)
</script>

<template>
  <AppCard
    title="Availability"
    icon="i-lucide-activity"
    :description="history
      ? `How each node has stood over the last ${windows.find(option => option.value === span)?.label}`
      : 'Reading how the fleet has stood'"
  >
    <template #actions>
      <div class="max-w-full overflow-x-auto">
        <UFieldGroup>
          <UButton
            v-for="option in windows"
            :key="option.value"
            size="sm"
            color="neutral"
            :variant="span === option.value ? 'solid' : 'outline'"
            :aria-pressed="span === option.value"
            @click="span = option.value"
          >
            {{ option.label }}
          </UButton>
        </UFieldGroup>
      </div>
    </template>

    <UAlert
      v-if="errorMessage"
      class="mb-4"
      color="error"
      variant="subtle"
      icon="i-lucide-circle-alert"
      title="Could not read the availability history"
      :description="errorMessage"
    />

    <UAlert
      v-else-if="history && !recording"
      class="mb-4"
      color="neutral"
      variant="subtle"
      icon="i-lucide-eye-off"
      title="The panel is not recording a history"
      description="AWG_AVAILABILITY_RETENTION_DAYS is 0, so no record is kept of how nodes stood.
        The states on the cards above are judged live and keep working. Set it to a number of days
        and restart the panel to record one."
    />

    <div
      v-if="loading"
      class="flex items-center justify-center gap-2 py-10 text-sm text-muted"
    >
      <UIcon
        name="i-lucide-loader-circle"
        class="size-5 animate-spin"
      />
      Reading the history
    </div>

    <p
      v-else-if="rows.length === 0"
      class="py-6 text-center text-sm text-muted"
    >
      No nodes to show a history for yet.
    </p>

    <div
      v-else
      class="flex flex-col gap-5"
    >
      <div class="flex flex-col divide-y divide-default">
        <div
          v-for="row in rows"
          :key="row.nodeId"
          class="flex flex-col gap-2 py-3 first:pt-0 last:pb-0 sm:flex-row sm:items-center sm:gap-4"
        >
          <div class="flex min-w-0 items-baseline gap-2 sm:w-48">
            <span class="truncate font-mono text-sm text-highlighted">{{ row.name }}</span>
            <span
              v-if="row.revoked"
              class="shrink-0 text-xs text-dimmed"
            >revoked</span>
          </div>

          <div class="min-w-0 flex-1">
            <AvailabilityRibbon
              :points="row.series"
              :bucket="history!.bucket"
            />
          </div>

          <dl class="flex shrink-0 items-baseline gap-4 sm:w-44 sm:justify-end">
            <div>
              <dt class="sr-only">
                Healthy
              </dt>
              <dd
                class="tabular font-mono text-sm font-semibold"
                :class="uptimeTone(row.uptime)"
              >
                {{ share(row.uptime) }}
              </dd>
            </div>
            <div>
              <dt class="sr-only">
                Samples
              </dt>
              <dd class="tabular font-mono text-[11px] text-dimmed">
                {{ row.samples }} checks
              </dd>
            </div>
          </dl>
        </div>
      </div>

      <!-- The per-probe breakdown: the only place the panel answers "blocked where". A node that
           every probe reaches and one that only the office reaches are different problems. -->
      <div
        v-if="vantagePoints.length > 0"
        class="flex flex-col gap-3 border-t border-default pt-4"
      >
        <div>
          <h3 class="text-sm font-semibold text-highlighted">
            From each probe
          </h3>
          <p class="text-sm text-muted">
            What every vantage point got through to, over the same window. A handshake that
            completed with no traffic behind it is the DPI pattern, not a blocked address.
          </p>
        </div>

        <div class="overflow-x-auto">
          <table class="w-full min-w-md text-sm">
            <thead>
              <tr class="border-b border-default text-left text-xs uppercase tracking-wide text-dimmed">
                <th class="py-2 pe-3 font-medium">
                  Probe
                </th>
                <th class="py-2 pe-3 font-medium">
                  Node
                </th>
                <th class="py-2 pe-3 text-right font-medium">
                  Got through
                </th>
                <th class="py-2 pe-3 text-right font-medium">
                  Handshakes
                </th>
                <th class="py-2 text-right font-medium">
                  Probe errors
                </th>
              </tr>
            </thead>
            <tbody class="divide-y divide-default">
              <template
                v-for="probe in vantagePoints"
                :key="probe.probeId"
              >
                <tr
                  v-for="(about, index) in probe.nodes"
                  :key="`${probe.probeId}:${about.nodeId}`"
                >
                  <td class="py-2 pe-3 font-mono text-xs">
                    <span v-if="index === 0">{{ probe.name }}</span>
                    <span
                      v-if="index === 0 && probe.revoked"
                      class="text-dimmed"
                    > · revoked</span>
                  </td>
                  <td class="py-2 pe-3 font-mono text-xs text-muted">
                    {{ nodeNames.get(about.nodeId) ?? 'gone' }}
                  </td>
                  <td
                    class="tabular py-2 pe-3 text-right font-mono"
                    :class="uptimeTone(about.reachability)"
                  >
                    {{ share(about.reachability) }}
                  </td>
                  <td class="tabular py-2 pe-3 text-right font-mono text-muted">
                    {{ about.handshakes }}/{{ about.reachable + about.unreachable }}
                  </td>
                  <td class="tabular py-2 text-right font-mono text-muted">
                    {{ about.errors }}
                  </td>
                </tr>
              </template>
            </tbody>
          </table>
        </div>
      </div>

      <p
        v-if="history?.recordingSince"
        class="text-xs text-dimmed"
      >
        Recorded since {{ formatLocal(history.recordingSince) }}; kept for {{ history.retentionDays }} days.
      </p>
    </div>
  </AppCard>
</template>
