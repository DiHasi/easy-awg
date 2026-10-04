<script setup lang="ts">
import type { ClientGroup, ClientUsage, ClientUsageSeries, UsageSummary, UsageWindowKey } from '~/types/api'

/**
 * Who moved what, over a window.
 *
 * The peer list answers what a config has moved in its lifetime; this answers when, which is the
 * only form in which a question about a person has an answer - who is costing the most this
 * month, who has stopped using the config they were given, which evening the active node was
 * saturated.
 *
 * A group is one person, so the list is people first and devices inside them: a household with
 * four devices is one line here, not four. Ordered by traffic rather than by the arrangement the
 * peer list follows, because a ranking is the whole point of the page.
 */
definePageMeta({ middleware: 'auth' })

const api = useControlApi()

const windows: { value: UsageWindowKey, label: string }[] = [
  { value: '24h', label: '24 hours' },
  { value: '7d', label: '7 days' },
  { value: '30d', label: '30 days' },
  { value: '90d', label: '90 days' }
]

const span = ref<UsageWindowKey>('7d')
const usage = ref<UsageSummary | null>(null)
const groups = ref<ClientGroup[]>([])
const loading = ref(true)
const errorMessage = ref<string | null>(null)

/** Which rows are open, and the series fetched for the peers among them. */
const opened = ref<Set<string>>(new Set())
const series = ref<Record<string, ClientUsageSeries>>({})
const fetching = ref<Set<string>>(new Set())

async function load() {
  try {
    const [summary, people] = await Promise.all([
      api.get<UsageSummary>(`/usage?window=${span.value}`),
      api.get<ClientGroup[]>('/groups')
    ])

    usage.value = summary
    groups.value = people
    errorMessage.value = null
  } catch (error) {
    errorMessage.value = describeError(error, 'Failed to read the traffic history.')
  } finally {
    loading.value = false
  }
}

const recording = computed(() => (usage.value?.retentionDays ?? 0) > 0)

const total = computed(() => (usage.value?.receivedBytes ?? 0) + (usage.value?.transmittedBytes ?? 0))

const peers = computed(() => usage.value?.clients ?? [])

const movedBy = (client: ClientUsage) => client.receivedBytes + client.transmittedBytes

/** Peers that moved anything in the window, which is what "active" can honestly mean here. */
const activePeers = computed(() => peers.value.filter(client => movedBy(client) > 0).length)

/** The fullest bucket, or nothing at all: with no traffic anywhere, every bucket ties at zero
 *  and naming the first one would read as a claim about it. */
const busiest = computed(() => {
  const points = usage.value?.series ?? []
  if (points.length === 0 || total.value === 0) {
    return null
  }

  return points.reduce((peak, point) =>
    point.receivedBytes + point.transmittedBytes > peak.receivedBytes + peak.transmittedBytes ? point : peak)
})

type Row = {
  key: string
  name: string
  /** Null for a peer filed under nobody: the row is the device itself. */
  group: ClientGroup | null
  devices: ClientUsage[]
  down: number
  up: number
  total: number
}

/**
 * One line per person, plus a line for each peer filed under nobody. A group with no traffic
 * still shows, as a zero: "nothing this month" is an answer, and a missing row is not.
 */
const rows = computed<Row[]>(() => {
  const byGroup = new Map<string, ClientUsage[]>()
  const lone: ClientUsage[] = []

  for (const client of peers.value) {
    if (client.groupId) {
      byGroup.set(client.groupId, [...(byGroup.get(client.groupId) ?? []), client])
    } else {
      lone.push(client)
    }
  }

  const build = (key: string, name: string, group: ClientGroup | null, devices: ClientUsage[]): Row => ({
    key,
    name,
    group,
    devices: [...devices].sort((a, b) => movedBy(b) - movedBy(a)),
    down: devices.reduce((sum, client) => sum + client.transmittedBytes, 0),
    up: devices.reduce((sum, client) => sum + client.receivedBytes, 0),
    total: devices.reduce((sum, client) => sum + movedBy(client), 0)
  })

  return [
    ...groups.value.map(group => build(group.id, group.name, group, byGroup.get(group.id) ?? [])),
    ...lone.map(client => build(client.id, client.name, null, [client]))
  ].sort((a, b) => b.total - a.total || a.name.localeCompare(b.name))
})

/**
 * A group row opens its devices; a lone peer opens its own chart. Both are the same gesture, so
 * the chart for a device is fetched the moment that device's own row is opened - not before,
 * because a fleet of fifty peers is fifty requests nobody asked for.
 */
async function toggle(row: Row) {
  const open = new Set(opened.value)
  if (open.has(row.key)) {
    open.delete(row.key)
  } else {
    open.add(row.key)
    if (!row.group && row.devices[0]) {
      void loadSeries(row.devices[0].id)
    }
  }

  opened.value = open
}

async function toggleDevice(client: ClientUsage) {
  const key = `device:${client.id}`
  const open = new Set(opened.value)
  if (open.has(key)) {
    open.delete(key)
  } else {
    open.add(key)
    void loadSeries(client.id)
  }

  opened.value = open
}

async function loadSeries(clientId: string) {
  if (series.value[clientId]?.window === span.value || fetching.value.has(clientId)) {
    return
  }

  fetching.value = new Set(fetching.value).add(clientId)

  try {
    series.value = {
      ...series.value,
      [clientId]: await api.get<ClientUsageSeries>(`/clients/${clientId}/usage?window=${span.value}`)
    }
  } catch {
    // A row that will not open its chart is not worth a banner over the whole page.
  } finally {
    const pending = new Set(fetching.value)
    pending.delete(clientId)
    fetching.value = pending
  }
}

/** Changing the window invalidates every chart already drawn, including the open ones. */
watch(span, async () => {
  loading.value = true
  series.value = {}
  await load()
  for (const key of opened.value) {
    if (key.startsWith('device:')) {
      void loadSeries(key.slice('device:'.length))
    }
  }

  for (const row of rows.value) {
    if (opened.value.has(row.key) && !row.group && row.devices[0]) {
      void loadSeries(row.devices[0].id)
    }
  }
})

/**
 * The same table, for a spreadsheet. One line per device with its person beside it, which is the
 * shape anything downstream wants - billing a household, or charting a quarter the panel does not
 * keep. Built here from what is already on screen rather than as an endpoint: the numbers are the
 * ones being looked at, and no request can disagree with them.
 */
function exportCsv() {
  const quote = (value: string) => `"${value.replace(/"/g, '""')}"`
  const header = ['person', 'device', 'downloaded_bytes', 'uploaded_bytes', 'total_bytes', 'active_days', 'last_active_at', 'enabled']

  const lines = [header.join(',')]
  for (const row of rows.value) {
    for (const device of row.devices) {
      lines.push([
        quote(row.group ? row.name : ''),
        quote(device.name),
        String(device.transmittedBytes),
        String(device.receivedBytes),
        String(movedBy(device)),
        String(device.activeDays),
        quote(device.lastActiveAt ?? ''),
        device.enabled ? 'true' : 'false'
      ].join(','))
    }
  }

  const url = URL.createObjectURL(new Blob([lines.join('\n')], { type: 'text/csv;charset=utf-8' }))
  const link = document.createElement('a')
  link.href = url
  link.download = `awg-traffic-${span.value}-${new Date().toISOString().slice(0, 10)}.csv`
  link.click()
  URL.revokeObjectURL(url)
}

// Slower than the ten seconds the peer list polls at: this is a history, and the newest bucket
// only changes as fast as the nodes report into it.
usePolling(load, 60000)
</script>

<template>
  <div class="flex flex-col gap-4 lg:gap-6">
    <h1 class="sr-only">
      Traffic
    </h1>

    <AppCard
      title="Traffic"
      icon="i-lucide-chart-column"
      :description="usage
        ? `${formatBytes(total)} moved by ${activePeers} of ${peers.length} peers`
        : 'Reading what the fleet has moved'"
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
        title="Could not read the traffic history"
        :description="errorMessage"
      />

      <UAlert
        v-else-if="usage && !recording"
        class="mb-4"
        color="neutral"
        variant="subtle"
        icon="i-lucide-eye-off"
        title="The panel is not recording a history"
        description="AWG_USAGE_RETENTION_DAYS is 0, so nothing per hour is kept. The live counters on
          the peer list keep working - they come from the nodes, not from here. Set it to a number
          of days and restart the panel to record one."
      />

      <div class="flex flex-col gap-5">
        <dl class="grid grid-cols-2 gap-3 rounded-lg border border-default bg-elevated/40 p-3 sm:grid-cols-4">
          <SpecItem label="Downloaded">
            <span class="tabular font-mono text-base font-semibold">{{ formatBytes(usage?.transmittedBytes ?? 0) }}</span>
          </SpecItem>
          <SpecItem label="Uploaded">
            <span class="tabular font-mono text-base font-semibold">{{ formatBytes(usage?.receivedBytes ?? 0) }}</span>
          </SpecItem>
          <SpecItem label="Total">
            <span class="tabular font-mono text-base font-semibold">{{ formatBytes(total) }}</span>
          </SpecItem>
          <SpecItem :label="usage?.bucket === 'day' ? 'Busiest day' : 'Busiest hour'">
            <span class="tabular font-mono text-sm">
              {{ busiest && usage ? formatBucketLong(busiest.at, usage.bucket) : '—' }}
            </span>
          </SpecItem>
        </dl>

        <UsageChart
          :points="usage?.series ?? []"
          :bucket="usage?.bucket ?? 'hour'"
          :loading="loading && !usage"
        />

        <p class="text-xs text-muted">
          <template v-if="recording">
            Counted from what the nodes report, every bucket in UTC. Kept for
            {{ usage?.retentionDays }} days;
            <template v-if="usage?.recordingSince">
              the oldest hour on record is {{ formatUtc(usage.recordingSince) }}.
            </template>
            <template v-else>
              nothing has been recorded yet - a peer's traffic appears once its node has reported
              twice.
            </template>
          </template>
          <template v-else>
            Nothing is being recorded, so this window is empty by design rather than by quiet.
          </template>
        </p>
      </div>
    </AppCard>

    <AppCard
      title="Per person"
      icon="i-lucide-users"
      description="One line per group, holding that person's devices. Peers filed under nobody stand on their own."
      flush
    >
      <template #actions>
        <UButton
          icon="i-lucide-download"
          color="neutral"
          variant="outline"
          :disabled="rows.length === 0"
          @click="exportCsv"
        >
          Export CSV
        </UButton>
      </template>

      <div
        class="hidden grid-cols-[2.5rem_minmax(0,1.4fr)_minmax(0,1fr)_7rem_7rem_7rem_8rem] items-center gap-x-4 border-b border-default px-5 py-2 text-xs font-medium text-muted lg:grid"
        aria-hidden="true"
      >
        <span />
        <span>Person</span>
        <span>Share</span>
        <span class="text-end">Downloaded</span>
        <span class="text-end">Uploaded</span>
        <span class="text-end">Total</span>
        <span class="text-end">Last active</span>
      </div>

      <div
        v-if="loading && !usage"
        class="flex items-center justify-center gap-2 px-4 py-12 text-sm text-muted"
      >
        <UIcon
          name="i-lucide-loader-circle"
          class="size-5 animate-spin"
        />
        Reading the history
      </div>

      <p
        v-else-if="rows.length === 0"
        class="px-4 py-12 text-center text-sm text-muted"
      >
        No peers yet.
      </p>

      <ul
        v-else
        class="divide-y divide-default"
      >
        <li
          v-for="(row, index) in rows"
          :key="row.key"
        >
          <button
            type="button"
            class="grid w-full grid-cols-[2.5rem_minmax(0,1fr)] items-center gap-x-4 gap-y-1 px-4 py-3 text-start transition-colors hover:bg-elevated/40 sm:px-5 lg:grid-cols-[2.5rem_minmax(0,1.4fr)_minmax(0,1fr)_7rem_7rem_7rem_8rem]"
            :aria-expanded="opened.has(row.key)"
            @click="toggle(row)"
          >
            <span class="flex items-center gap-1.5">
              <UIcon
                :name="opened.has(row.key) ? 'i-lucide-chevron-down' : 'i-lucide-chevron-right'"
                class="size-4 shrink-0 text-dimmed"
              />
              <span class="tabular font-mono text-xs text-dimmed">{{ itemNumber(index) }}</span>
            </span>

            <span class="flex min-w-0 flex-col">
              <span class="flex min-w-0 items-center gap-2">
                <UIcon
                  :name="row.group ? 'i-lucide-user' : 'i-lucide-smartphone'"
                  class="size-4 shrink-0 text-muted"
                />
                <span class="truncate font-medium text-highlighted">{{ row.name }}</span>
              </span>
              <span class="ps-6 text-xs text-muted">
                {{ row.group
                  ? `${row.devices.length} ${row.devices.length === 1 ? 'device' : 'devices'}`
                  : 'ungrouped' }}
                <span class="tabular lg:hidden">· ↓ {{ formatBytes(row.down) }} ↑ {{ formatBytes(row.up) }}</span>
              </span>
            </span>

            <!-- The bar is the only thing on the row that compares people to each other; the
                 numbers beside it are what each one actually moved. -->
            <span class="hidden items-center gap-2 lg:flex">
              <span class="h-1.5 min-w-0 flex-1 overflow-hidden rounded-full bg-elevated">
                <span
                  class="block h-full rounded-full bg-primary"
                  :style="{ width: total ? `${(row.total / total) * 100}%` : '0' }"
                />
              </span>
              <span class="tabular w-9 text-end font-mono text-xs text-muted">{{ formatShare(row.total, total) }}</span>
            </span>

            <span class="tabular hidden text-end font-mono text-xs text-toned lg:block">{{ formatBytes(row.down) }}</span>
            <span class="tabular hidden text-end font-mono text-xs text-toned lg:block">{{ formatBytes(row.up) }}</span>
            <span class="tabular hidden text-end font-mono text-sm text-highlighted lg:block">{{ formatBytes(row.total) }}</span>
            <span class="hidden text-end text-xs text-muted lg:block">
              {{ relativeTime(row.devices.map(device => device.lastActiveAt).filter(Boolean).sort().at(-1), 'never') }}
            </span>
          </button>

          <!-- A person opens into their devices; a device opens into its own history. -->
          <div
            v-if="opened.has(row.key)"
            class="border-t border-default bg-elevated/30 px-4 py-3 sm:px-5"
          >
            <UsageChart
              v-if="!row.group && row.devices[0]"
              :points="series[row.devices[0].id]?.series ?? []"
              :bucket="series[row.devices[0].id]?.bucket ?? usage?.bucket ?? 'hour'"
              :loading="!series[row.devices[0].id]"
              compact
            />

            <div
              v-else-if="row.devices.length === 0"
              class="text-sm text-muted"
            >
              No devices filed under {{ row.name }} yet.
            </div>

            <ul
              v-else
              class="flex flex-col gap-2"
            >
              <li
                v-for="device in row.devices"
                :key="device.id"
                class="rounded-lg border border-default bg-default"
              >
                <button
                  type="button"
                  class="grid w-full grid-cols-[minmax(0,1fr)_auto] items-center gap-x-4 px-3 py-2 text-start"
                  :aria-expanded="opened.has(`device:${device.id}`)"
                  @click="toggleDevice(device)"
                >
                  <span class="flex min-w-0 items-center gap-2">
                    <UIcon
                      :name="opened.has(`device:${device.id}`) ? 'i-lucide-chevron-down' : 'i-lucide-chevron-right'"
                      class="size-3.5 shrink-0 text-dimmed"
                    />
                    <span class="truncate text-sm font-medium text-highlighted">{{ device.name }}</span>
                    <UBadge
                      v-if="!device.enabled"
                      size="sm"
                      color="neutral"
                      variant="subtle"
                    >
                      disabled
                    </UBadge>
                  </span>
                  <span class="tabular flex items-center gap-3 font-mono text-xs text-toned">
                    <span>↓ {{ formatBytes(device.transmittedBytes) }}</span>
                    <span>↑ {{ formatBytes(device.receivedBytes) }}</span>
                    <span class="hidden text-muted sm:inline">{{ relativeTime(device.lastActiveAt, 'never') }}</span>
                  </span>
                </button>

                <div
                  v-if="opened.has(`device:${device.id}`)"
                  class="border-t border-default px-3 py-3"
                >
                  <UsageChart
                    :points="series[device.id]?.series ?? []"
                    :bucket="series[device.id]?.bucket ?? usage?.bucket ?? 'hour'"
                    :loading="!series[device.id]"
                    compact
                  />
                </div>
              </li>
            </ul>
          </div>
        </li>
      </ul>
    </AppCard>
  </div>
</template>
