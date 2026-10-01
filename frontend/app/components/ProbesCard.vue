<script setup lang="ts">
import type { DropdownMenuItem } from '@nuxt/ui'
import type { Probe, ProbeResult } from '~/types/api'

/**
 * Probes: the panel's eyes where clients are. A node can only say that it runs; a probe says
 * whether a handshake with it completes from out there, which is what tells a blocked node from
 * a healthy one.
 */
const api = useControlApi()
const toast = useToast()
const confirm = useConfirm()
const { nodes, refresh } = useFleetState()

const probes = ref<Probe[]>([])
const loaded = ref(false)
const enrollOpen = ref(false)
const busyId = ref<string | null>(null)

async function load() {
  try {
    probes.value = await api.get<Probe[]>('/probes')
  } catch {
    // Reported once by the fleet state.
  } finally {
    loaded.value = true
  }
}

usePolling(load, 10000)

function nodeName(id: string) {
  return nodes.value.find(node => node.id === id)?.name ?? 'removed node'
}

const outcomes = {
  reachable: { word: 'traffic flows', tone: 'success' },
  unreachable: { word: 'no handshake', tone: 'error' },
  error: { word: 'probe error', tone: 'muted' }
} as const

function describe(result: ProbeResult) {
  if (result.outcome === 'reachable' && result.latencyMs != null) {
    return `${outcomes.reachable.word} · ${result.latencyMs} ms`
  }
  // Worth its own words: it is the block that lets the handshake through and drops the rest, and
  // it calls for a different fix than an address nobody can reach at all.
  if (result.outcome === 'unreachable' && result.handshake) {
    return 'handshake, no traffic'
  }
  return outcomes[result.outcome].word
}

function menu(probe: Probe) {
  const groups: DropdownMenuItem[][] = []
  if (!probe.revoked) {
    groups.push([{ label: 'Revoke', icon: 'i-lucide-ban', onSelect: () => revoke(probe) }])
  }
  groups.push([{ label: 'Remove from panel', icon: 'i-lucide-trash-2', color: 'error', onSelect: () => remove(probe) }])
  return groups
}

async function act(probe: Probe, run: () => Promise<unknown>, done: string, failed: string) {
  busyId.value = probe.id
  try {
    await run()
    await Promise.all([load(), refresh()])
    toast.add({ title: done, icon: 'i-lucide-check', color: 'success' })
  } catch (error) {
    toast.add({ title: failed, description: describeError(error, ''), color: 'error', icon: 'i-lucide-circle-alert' })
  } finally {
    busyId.value = null
  }
}

async function revoke(probe: Probe) {
  const confirmed = await confirm({
    title: `Revoke ${probe.name}?`,
    description: 'Its results stop counting at once and its key is taken off every node. Stop the container on that host as well.',
    confirmLabel: 'Revoke',
    danger: true
  })
  if (confirmed) {
    await act(probe, () => api.post(`/probes/${probe.id}/revoke`), `${probe.name} revoked`, 'Could not revoke')
  }
}

async function remove(probe: Probe) {
  const confirmed = await confirm({
    title: `Remove ${probe.name}?`,
    description: 'Forgets the probe and everything it reported. Stop the container on that host as well.',
    confirmLabel: 'Remove',
    danger: true
  })
  if (confirmed) {
    await act(probe, () => api.del(`/probes/${probe.id}`), `${probe.name} removed`, 'Could not remove')
  }
}
</script>

<template>
  <AppCard
    title="Probes"
    icon="i-lucide-radar"
    :description="`${probes.filter(probe => !probe.revoked).length} watching · a real connection to every node, from where clients are`"
  >
    <template #actions>
      <UButton
        icon="i-lucide-plus"
        @click="enrollOpen = true"
      >
        Add probe
      </UButton>
    </template>

    <p
      v-if="!loaded"
      class="text-sm text-muted"
    >
      Reading…
    </p>

    <p
      v-else-if="probes.length === 0"
      class="max-w-2xl text-sm text-muted"
    >
      No probes yet, so health comes from the nodes' own reports - and a node blocked from your
      clients' side still reports itself healthy. Run a probe on a host inside the network your
      clients use: it connects to every node and fetches a page through the tunnel, so a node that
      completes the handshake and then passes no traffic shows up as blocked.
    </p>

    <div
      v-else
      class="grid gap-3 lg:grid-cols-2"
    >
      <article
        v-for="probe in probes"
        :key="probe.id"
        class="min-w-0 rounded-lg border border-default bg-default p-3.5"
        :class="probe.revoked ? 'opacity-70' : ''"
      >
        <header class="flex items-start gap-2">
          <div class="min-w-0 flex-1">
            <h3 class="font-mono text-sm font-semibold text-highlighted">
              {{ probe.name }}
            </h3>
            <p class="mt-0.5 truncate font-mono text-xs text-muted">
              {{ probe.revoked ? 'revoked' : `reported ${relativeTime(probe.lastSeenAt)}` }}
              <template v-if="probe.hostname">
                · {{ probe.hostname }}
              </template>
            </p>
          </div>
          <UDropdownMenu
            :items="menu(probe)"
            :content="{ align: 'end' }"
          >
            <UButton
              icon="i-lucide-ellipsis-vertical"
              color="neutral"
              variant="ghost"
              size="sm"
              :loading="busyId === probe.id"
              :aria-label="`More actions for ${probe.name}`"
            />
          </UDropdownMenu>
        </header>

        <ul
          v-if="probe.results.length"
          class="mt-3 flex flex-col gap-1.5 border-t border-default pt-3"
        >
          <li
            v-for="result in probe.results"
            :key="result.nodeId"
            class="flex flex-wrap items-center gap-x-2 gap-y-1 text-xs"
            :title="result.detail ?? undefined"
          >
            <span class="font-mono text-highlighted">{{ nodeName(result.nodeId) }}</span>
            <span class="font-mono text-muted">{{ result.address }}</span>
            <span
              class="ms-auto rounded-full px-2 py-0.5 font-medium"
              :class="toneBadge[outcomes[result.outcome].tone]"
            >{{ describe(result) }}</span>
          </li>
        </ul>
        <p
          v-else
          class="mt-3 border-t border-default pt-3 text-xs text-muted"
        >
          Nothing reported yet.
        </p>
      </article>
    </div>

    <EnrollNodeModal
      v-model:open="enrollOpen"
      kind="probe"
      @issued="load"
    />
  </AppCard>
</template>
