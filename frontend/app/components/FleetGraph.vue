<script setup lang="ts">
import type { DnsStatus, Fleet, Node } from '~/types/api'

/**
 * Client configs → the failover record → the nodes, as real cards joined by lines.
 *
 * Every config names the record and pins the fleet key, so the only thing deciding where traffic
 * goes is which node the record answers with. That path is drawn live; the others are standby.
 * The cards carry their own actions, so switching a node happens on the node, where you are
 * already looking, rather than on another page.
 *
 * The lines are measured from the rendered cards, which keeps them right whatever the cards'
 * heights. Below lg the cards stack and the lines give way to arrows between them.
 */
const props = defineProps<{
  nodes: Node[]
  dns: DnsStatus | null
  fleet: Fleet | null
  clients: { total: number, online: number, idle: number, off: number }
  busyId?: string | null
}>()

const emit = defineEmits<{
  activate: [node: Node]
  revoke: [node: Node]
  remove: [node: Node]
  failover: [node: Node]
  newPeer: []
  enroll: []
}>()

const toast = useToast()
const markerId = useId()

const root = ref<HTMLElement | null>(null)
const clientsEl = ref<HTMLElement | null>(null)
const recordEl = ref<HTMLElement | null>(null)
const nodeEls = new Map<string, HTMLElement>()

function setNodeEl(id: string, el: unknown) {
  if (el instanceof HTMLElement) {
    nodeEls.set(id, el)
  } else {
    nodeEls.delete(id)
  }
}

const paths = ref<{ d: string, live: boolean }[]>([])

function measure() {
  const base = root.value?.getBoundingClientRect()
  if (!base || !clientsEl.value || !recordEl.value || window.innerWidth < 1024) {
    paths.value = []
    return
  }

  const box = (el: HTMLElement) => {
    const rect = el.getBoundingClientRect()
    return { left: rect.left - base.left, right: rect.right - base.left, cy: rect.top - base.top + rect.height / 2 }
  }

  const clients = box(clientsEl.value)
  const record = box(recordEl.value)
  const hasLive = props.nodes.some(node => node.isActive && !node.revoked)
  const next = [{
    d: `M${clients.right} ${clients.cy}H${(clients.right + record.left) / 2}V${record.cy}H${record.left - 3}`,
    live: hasLive
  }]

  for (const node of props.nodes) {
    const el = nodeEls.get(node.id)
    if (!el) {
      continue
    }
    const target = box(el)
    const junction = record.right + (target.left - record.right) / 2
    next.push({
      d: `M${record.right} ${record.cy}H${junction}V${target.cy}H${target.left - 3}`,
      live: node.isActive && !node.revoked
    })
  }

  // The live line is drawn last so it runs over the shared trunk.
  paths.value = next.sort((a, b) => Number(a.live) - Number(b.live))
}

let observer: ResizeObserver | null = null

onMounted(() => {
  observer = new ResizeObserver(() => measure())
  if (root.value) {
    observer.observe(root.value)
  }
  window.addEventListener('resize', measure)
  measure()
})

onBeforeUnmount(() => {
  observer?.disconnect()
  window.removeEventListener('resize', measure)
})

watch(() => [props.nodes, props.dns], () => nextTick(measure), { deep: true })

const provider = computed(() => props.dns?.providerConfigured ? props.dns.provider : 'manual')

async function copyRecord() {
  const dns = props.dns
  if (!dns?.targetAddress) {
    return
  }
  await navigator.clipboard.writeText(`${dns.recordName} ${dns.ttl} IN ${dns.recordType} ${dns.targetAddress}`)
  toast.add({ title: 'Record copied', icon: 'i-lucide-check', color: 'success' })
}
</script>

<template>
  <div
    ref="root"
    class="relative"
  >
    <svg
      v-if="paths.length"
      class="pointer-events-none absolute inset-0 hidden size-full overflow-visible lg:block"
      aria-hidden="true"
    >
      <defs>
        <marker
          :id="`${markerId}-live`"
          viewBox="0 0 10 10"
          refX="7"
          refY="5"
          markerWidth="7"
          markerHeight="7"
          orient="auto"
        >
          <path
            d="M0 0L10 5L0 10z"
            class="fill-live"
          />
        </marker>
        <marker
          :id="`${markerId}-standby`"
          viewBox="0 0 10 10"
          refX="7"
          refY="5"
          markerWidth="6"
          markerHeight="6"
          orient="auto"
        >
          <path
            d="M0 0L10 5L0 10z"
            class="fill-(--ui-border-accented)"
          />
        </marker>
      </defs>
      <path
        v-for="(path, index) in paths"
        :key="index"
        :d="path.d"
        fill="none"
        :class="path.live ? 'live-flow stroke-live' : 'stroke-(--ui-border-accented)'"
        :stroke-width="path.live ? 2.5 : 1.5"
        :stroke-dasharray="path.live ? undefined : '4 5'"
        :marker-end="`url(#${markerId}-${path.live ? 'live' : 'standby'})`"
      />
    </svg>

    <div class="relative grid gap-3 lg:grid-cols-[minmax(0,15rem)_minmax(0,17rem)_minmax(0,1fr)] lg:items-center lg:gap-x-20">
      <!-- clients -->
      <div
        ref="clientsEl"
        class="rounded-lg border border-default bg-default p-4"
      >
        <p class="flex items-center gap-2 text-sm font-medium text-toned">
          <UIcon
            name="i-lucide-users"
            class="size-4"
          />
          Clients
        </p>
        <p class="tabular mt-2 text-3xl font-semibold text-highlighted">
          {{ clients.online }}<span class="text-base font-normal text-muted"> / {{ clients.total }} online</span>
        </p>
        <p class="mt-1 text-xs text-muted">
          {{ clients.idle }} idle · {{ clients.off }} disabled
        </p>
        <div class="mt-3 flex flex-wrap gap-2">
          <UButton
            size="sm"
            icon="i-lucide-plus"
            @click="emit('newPeer')"
          >
            New peer
          </UButton>
          <UButton
            size="sm"
            color="neutral"
            variant="ghost"
            to="#peers"
          >
            View list
          </UButton>
        </div>
      </div>

      <div
        class="flex justify-center lg:hidden"
        aria-hidden="true"
      >
        <UIcon
          name="i-lucide-arrow-down"
          class="size-5 text-live"
        />
      </div>

      <!-- record -->
      <div
        ref="recordEl"
        class="rounded-lg border border-default bg-default p-4"
      >
        <p class="flex items-center gap-2 text-sm font-medium text-toned">
          <UIcon
            name="i-lucide-globe"
            class="size-4"
          />
          DNS record
          <span class="ms-auto rounded bg-elevated px-1.5 py-0.5 text-xs font-normal text-muted">{{ provider }}</span>
        </p>
        <template v-if="dns">
          <p class="mt-2 break-all font-mono text-sm font-semibold text-highlighted">
            {{ dns.recordName }}
          </p>
          <p class="mt-2 text-xs text-muted">
            points to
          </p>
          <p class="break-all font-mono text-sm">
            <span :class="dns.targetAddress ? 'text-live' : 'text-dimmed'">{{ dns.targetAddress ?? 'not set' }}</span>
            <span
              v-if="dns.activeNodeName"
              class="text-muted"
            > · {{ dns.activeNodeName }}</span>
          </p>
          <div
            v-if="dns.targetAddress"
            class="mt-2.5"
          >
            <StateMark
              :state="dns.matches ? 'up' : 'idle'"
              :label="dns.matches ? 'Resolving correctly' : 'Not resolving here yet'"
            />
          </div>
          <p class="mt-2 text-xs text-muted">
            TTL {{ dns.ttl }} s — clients follow a switch within that
          </p>
          <UButton
            v-if="!dns.providerConfigured && dns.targetAddress"
            size="sm"
            color="neutral"
            variant="outline"
            icon="i-lucide-copy"
            class="mt-3"
            @click="copyRecord"
          >
            Copy record
          </UButton>
        </template>
        <p
          v-else
          class="mt-2 text-sm text-muted"
        >
          Reading…
        </p>
      </div>

      <div
        class="flex justify-center lg:hidden"
        aria-hidden="true"
      >
        <UIcon
          name="i-lucide-arrow-down"
          class="size-5 text-live"
        />
      </div>

      <!-- nodes -->
      <!-- The grid item here is this column, not the card, so the card's own min-w-0 cannot help. -->
      <div class="flex min-w-0 flex-col gap-3">
        <div
          v-for="node in nodes"
          :key="node.id"
          :ref="(el) => setNodeEl(node.id, el)"
        >
          <NodeCard
            :node="node"
            :listen-port="fleet?.listenPort"
            :busy="busyId === node.id"
            @activate="emit('activate', node)"
            @revoke="emit('revoke', node)"
            @remove="emit('remove', node)"
            @failover="emit('failover', node)"
          />
        </div>

        <div
          v-if="nodes.length === 0"
          class="flex flex-col items-start gap-2 rounded-lg border border-dashed border-accented p-4"
        >
          <p class="text-sm font-medium text-highlighted">
            No node enrolled yet
          </p>
          <p class="text-sm text-muted">
            Enroll a VPN server to carry client traffic.
          </p>
          <UButton
            size="sm"
            icon="i-lucide-plus"
            @click="emit('enroll')"
          >
            Enroll node
          </UButton>
        </div>
        <UButton
          v-else
          size="sm"
          color="neutral"
          variant="ghost"
          icon="i-lucide-plus"
          class="self-start"
          @click="emit('enroll')"
        >
          Enroll another node
        </UButton>
      </div>
    </div>
  </div>
</template>
