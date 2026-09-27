<script setup lang="ts">
import type { DnsStatus, Node } from '~/types/api'

/**
 * The fleet as a signal-flow drawing: client configs → the failover record → the nodes.
 *
 * This is the product's one mechanism made visible. Every config names the record and pins the
 * fleet key, so the only thing that decides where traffic goes is which node the record
 * answers with — drawn as the one solid line. Everything else is a standby branch.
 *
 * Two drawings are rendered and CSS picks one: horizontal on wide screens, vertical on narrow
 * ones. Measuring the container in script would work too, but costs a resize observer to
 * reach the same place.
 */
const props = withDefaults(defineProps<{
  nodes: Node[]
  dns: DnsStatus | null
  clientsTotal: number
  clientsOnline: number
  subnet?: string | null
  listenPort?: number | null
  detail?: boolean
  callouts?: boolean
}>(), {
  subnet: null,
  listenPort: null,
  detail: false,
  callouts: false
})

function clip(text: string, max: number) {
  return text.length > max ? `${text.slice(0, max - 1)}…` : text
}

function endpoint(node: Node) {
  if (!node.publicIp) {
    return 'address unknown'
  }
  return props.listenPort ? `${node.publicIp}:${props.listenPort}` : node.publicIp
}

function revision(node: Node) {
  return node.inSync
    ? `r${node.appliedRevision} applied`
    : `r${node.appliedRevision} → r${node.fleetRevision}`
}

function backend(node: Node) {
  return [node.backend, node.agentVersion ? `agent ${node.agentVersion}` : null].filter(Boolean).join(' · ') || '—'
}

const described = computed(() => props.nodes.map((node, index) => ({
  node,
  designator: `N${index + 1}`,
  state: describeNode(node)
})))

const liveIndex = computed(() => props.nodes.findIndex(node => node.isActive))
const firstBehind = computed(() => props.nodes.findIndex(node => !node.inSync))

const recordName = computed(() => props.dns?.recordName ?? '—')
const recordType = computed(() => props.dns?.recordType ?? 'A')
const pointsAt = computed(() => props.dns?.targetAddress ?? 'unset')
const resolvingElsewhere = computed(() => Boolean(props.dns?.targetAddress && !props.dns.matches))

/* ---------- horizontal drawing ---------- */

const W = 1100
const boxH = computed(() => props.detail ? 124 : 76)
const gap = 14

const stackH = computed(() => {
  const n = Math.max(props.nodes.length, 1)
  return n * boxH.value + (n - 1) * gap
})

const H = computed(() => Math.max(310, stackH.value + 24))
const cy = computed(() => H.value / 2)
const viewH = computed(() => H.value + (props.dns ? 36 : 6))

const nodeX = 660
const nodeW = W - nodeX - 8
const junctionX = 500

const boxes = computed(() => {
  const top = (H.value - stackH.value) / 2
  return described.value.map((item, index) => {
    const y = top + index * (boxH.value + gap)
    return { ...item, y, cy: y + boxH.value / 2 }
  })
})

const tickSpan = computed(() => Math.min(220, H.value - 100))
const ticks = computed(() => {
  const count = Math.min(Math.max(props.clientsTotal, 0), 30)
  if (count === 0) {
    return []
  }
  if (count === 1) {
    return [cy.value]
  }
  const start = cy.value - tickSpan.value / 2
  const step = tickSpan.value / (count - 1)
  return Array.from({ length: count }, (_, index) => start + index * step)
})

function branchPath(boxCy: number) {
  return `M${junctionX} ${cy.value}V${boxCy}H${nodeX}`
}

/* ---------- vertical drawing ---------- */

const VW = 360
const vBoxH = 64
const vGap = 16
const vTop = 172

const vBoxes = computed(() => described.value.map((item, index) => {
  const y = vTop + index * (vBoxH + vGap)
  return { ...item, y, cy: y + vBoxH / 2 }
}))

const vStackEnd = computed(() => vTop + Math.max(props.nodes.length, 1) * (vBoxH + vGap) - vGap)
const vViewH = computed(() => vStackEnd.value + (props.dns ? 40 : 10))

const vTicks = computed(() => {
  const count = Math.min(Math.max(props.clientsTotal, 0), 24)
  if (count === 0) {
    return []
  }
  if (count === 1) {
    return [VW / 2]
  }
  const step = (VW - 24) / (count - 1)
  return Array.from({ length: count }, (_, index) => 12 + index * step)
})

function railPath(boxCy: number) {
  return `M${VW / 2} 156H22V${boxCy}H40`
}

const summary = computed(() => {
  const live = liveIndex.value >= 0 ? props.nodes[liveIndex.value]!.name : 'no node'
  return `${props.clientsTotal} client configurations reach the record ${recordName.value}, `
    + `which points at ${live}. ${props.nodes.length} node(s) in the fleet.`
})
</script>

<template>
  <figure class="m-0 text-default">
    <!-- horizontal: lg and up -->
    <svg
      class="hidden w-full lg:block"
      :viewBox="`0 0 ${W} ${viewH}`"
      role="img"
      :aria-label="summary"
    >
      <!-- clients -->
      <text
        x="8"
        :y="cy - tickSpan / 2 - 16"
        class="caps fill-(--ui-text-highlighted)"
      >Clients</text>
      <g class="stroke-(--ui-border-accented)">
        <path
          v-for="(y, index) in ticks"
          :key="index"
          :d="`M8 ${y}h16`"
          stroke-width="1"
        />
      </g>
      <path
        :d="`M30 ${cy - tickSpan / 2 - 4}h6v${tickSpan + 8}h-6`"
        fill="none"
        class="stroke-(--ui-text)"
        stroke-width="1.2"
      />
      <text
        x="8"
        :y="cy + tickSpan / 2 + 22"
        class="fill-(--ui-text) font-mono text-[12px]"
      >{{ clientsTotal }} issued · {{ clientsOnline }} up</text>
      <template v-if="subnet">
        <path
          :d="`M8 ${cy + tickSpan / 2 + 34}v8M78 ${cy + tickSpan / 2 + 34}v8M8 ${cy + tickSpan / 2 + 38}h70`"
          fill="none"
          class="stroke-(--ui-text-dimmed)"
        />
        <text
          x="86"
          :y="cy + tickSpan / 2 + 42"
          class="fill-(--ui-text-muted) font-mono text-[11px]"
        >{{ subnet }}</text>
      </template>

      <!-- bus to the record -->
      <path
        :d="`M36 ${cy}H190`"
        fill="none"
        class="stroke-(--ui-text)"
        stroke-width="1.2"
      />
      <g v-if="callouts">
        <circle
          cx="113"
          :cy="cy - 28"
          r="9.5"
          fill="none"
          class="stroke-(--ui-text)"
        />
        <text
          x="113"
          :y="cy - 24"
          text-anchor="middle"
          class="fill-(--ui-text) font-mono text-[11px]"
        >1</text>
        <path
          :d="`M113 ${cy - 18}V${cy - 2}`"
          class="stroke-(--ui-text)"
          stroke-width="0.8"
        />
      </g>

      <!-- R1: the record -->
      <text
        x="190"
        :y="cy - 66"
        class="caps fill-(--ui-text-muted)"
      >{{ dns?.providerConfigured ? `via ${dns.provider}` : 'edited by hand' }}</text>
      <rect
        x="190"
        :y="cy - 57"
        width="230"
        height="114"
        class="fill-(--ui-bg-elevated) stroke-(--ui-text)"
        stroke-width="1.2"
      />
      <rect
        x="190"
        :y="cy - 57"
        width="230"
        height="24"
        class="fill-title stroke-(--ui-text)"
        stroke-width="1.2"
      />
      <text
        x="200"
        :y="cy - 41"
        class="caps fill-(--ui-text-highlighted)"
      >R1</text>
      <text
        x="226"
        :y="cy - 41"
        class="caps fill-(--ui-text-toned)"
      >DNS {{ recordType }} record</text>
      <path
        :d="`M190 ${cy + 3}h230M190 ${cy + 30}h230`"
        class="stroke-(--ui-border-muted)"
      />
      <text
        x="200"
        :y="cy - 11"
        class="caps fill-(--ui-text-muted)"
      >Name</text>
      <text
        x="410"
        :y="cy - 11"
        text-anchor="end"
        class="fill-(--ui-text) font-mono text-[12px]"
      >{{ clip(recordName, 24) }}</text>
      <text
        x="200"
        :y="cy + 20"
        class="caps fill-(--ui-text-muted)"
      >TTL</text>
      <text
        x="410"
        :y="cy + 20"
        text-anchor="end"
        class="fill-(--ui-text) font-mono text-[12px]"
      >{{ dns ? `${dns.ttl} s` : '—' }}</text>
      <text
        x="200"
        :y="cy + 47"
        class="caps fill-(--ui-text-muted)"
      >Points at</text>
      <text
        x="410"
        :y="cy + 47"
        text-anchor="end"
        class="font-mono text-[12px]"
        :class="resolvingElsewhere ? 'fill-warning' : 'fill-live'"
      >{{ pointsAt }}</text>
      <text
        v-if="resolvingElsewhere"
        x="190"
        :y="cy + 74"
        class="fill-warning font-mono text-[11px]"
      >resolves to {{ clip(dns?.resolvedAddresses.join(', ') || 'nothing', 30) }}</text>

      <g v-if="callouts && dns">
        <circle
          cx="444"
          :cy="cy - 76"
          r="9.5"
          fill="none"
          class="stroke-(--ui-text)"
        />
        <text
          x="444"
          :y="cy - 72"
          text-anchor="middle"
          class="fill-(--ui-text) font-mono text-[11px]"
        >2</text>
        <path
          :d="`M437 ${cy - 69}L421 ${cy - 58}`"
          class="stroke-(--ui-text)"
          stroke-width="0.8"
        />
      </g>

      <!-- record to junction -->
      <path
        :d="`M420 ${cy}H${junctionX}`"
        fill="none"
        class="stroke-(--ui-text)"
        stroke-width="1.2"
      />
      <circle
        :cx="junctionX"
        :cy="cy"
        r="3.6"
        class="fill-(--ui-text)"
      />

      <!-- standby branches first, so the live one is drawn over the shared trunk -->
      <g
        v-for="(box, index) in boxes"
        :key="`branch-${box.node.id}`"
      >
        <template v-if="index !== liveIndex">
          <path
            :d="branchPath(box.cy)"
            fill="none"
            class="stroke-standby"
            stroke-width="1.2"
            stroke-dasharray="6 4"
          />
          <path
            :d="`M${nodeX - 8} ${box.cy - 5}l8 5-8 5`"
            fill="none"
            class="stroke-standby"
            stroke-width="1.2"
          />
          <text
            :x="junctionX + 12"
            :y="box.cy - 7"
            class="caps"
            :class="toneFill[box.state.tone]"
          >{{ box.state.word }}</text>
        </template>
      </g>
      <g v-if="liveIndex >= 0 && boxes[liveIndex]">
        <path
          :d="branchPath(boxes[liveIndex]!.cy)"
          fill="none"
          class="stroke-live"
          stroke-width="2.2"
        />
        <path
          :d="`M${nodeX - 10} ${boxes[liveIndex]!.cy - 6}l10 6-10 6`"
          fill="none"
          class="stroke-live"
          stroke-width="2.2"
        />
        <text
          :x="junctionX + 12"
          :y="boxes[liveIndex]!.cy - 8"
          class="caps fill-live"
        >Live path · {{ clientsOnline }} up</text>
      </g>

      <!-- nodes -->
      <g
        v-for="(box, index) in boxes"
        :key="box.node.id"
      >
        <rect
          :x="nodeX"
          :y="box.y"
          :width="nodeW"
          :height="boxH"
          class="fill-(--ui-bg-elevated)"
          :class="index === liveIndex ? 'stroke-live' : 'stroke-(--ui-border-accented)'"
          :stroke-width="index === liveIndex ? 2 : 1.2"
        />
        <rect
          :x="nodeX"
          :y="box.y"
          :width="nodeW"
          height="24"
          :class="index === liveIndex ? 'fill-live-tint stroke-live' : 'fill-title stroke-(--ui-border-accented)'"
          :stroke-width="index === liveIndex ? 2 : 1.2"
        />
        <text
          :x="nodeX + 10"
          :y="box.y + 16"
          class="caps fill-(--ui-text-highlighted)"
        >{{ box.designator }}</text>
        <text
          :x="nodeX + 38"
          :y="box.y + 17"
          class="fill-(--ui-text-highlighted) font-mono text-[13px] font-medium"
        >{{ clip(box.node.name, 22) }}</text>
        <text
          v-if="detail && box.node.hostname"
          :x="nodeX + 38 + Math.min(box.node.name.length, 22) * 8 + 12"
          :y="box.y + 16"
          class="caps fill-(--ui-text-muted)"
        >{{ clip(box.node.hostname, 22) }}</text>
        <text
          :x="nodeX + nodeW - 10"
          :y="box.y + 16"
          text-anchor="end"
          class="caps"
          :class="toneFill[box.state.tone]"
        >{{ box.state.word }}</text>

        <template v-if="detail">
          <path
            :d="`M${nodeX} ${box.y + 49}h${nodeW}M${nodeX} ${box.y + 74}h${nodeW}M${nodeX} ${box.y + 99}h${nodeW}`"
            class="stroke-(--ui-border-muted)"
          />
          <text
            :x="nodeX + 10"
            :y="box.y + 41"
            class="caps fill-(--ui-text-muted)"
          >Endpoint</text>
          <text
            :x="nodeX + nodeW - 10"
            :y="box.y + 41"
            text-anchor="end"
            class="font-mono text-[12px]"
            :class="box.node.publicIp ? 'fill-(--ui-text)' : 'fill-(--ui-text-dimmed)'"
          >{{ endpoint(box.node) }}</text>
          <text
            :x="nodeX + 10"
            :y="box.y + 66"
            class="caps fill-(--ui-text-muted)"
          >Backend</text>
          <text
            :x="nodeX + nodeW - 10"
            :y="box.y + 66"
            text-anchor="end"
            class="fill-(--ui-text) font-mono text-[12px]"
          >{{ clip(backend(box.node), 44) }}</text>
          <text
            :x="nodeX + 10"
            :y="box.y + 91"
            class="caps fill-(--ui-text-muted)"
          >Revision</text>
          <text
            :x="nodeX + nodeW - 10"
            :y="box.y + 91"
            text-anchor="end"
            class="font-mono text-[12px]"
            :class="box.node.inSync ? 'fill-(--ui-text)' : 'fill-warning'"
          >{{ revision(box.node) }}</text>
          <text
            :x="nodeX + 10"
            :y="box.y + 116"
            class="caps fill-(--ui-text-muted)"
          >Seen</text>
          <text
            :x="nodeX + nodeW - 10"
            :y="box.y + 116"
            text-anchor="end"
            class="fill-(--ui-text) font-mono text-[12px]"
          >{{ relativeTime(box.node.lastSeenAt) }}</text>
        </template>

        <template v-else>
          <path
            :d="`M${nodeX} ${box.y + 50}h${nodeW}`"
            class="stroke-(--ui-border-muted)"
          />
          <text
            :x="nodeX + 10"
            :y="box.y + 42"
            class="caps fill-(--ui-text-muted)"
          >Endpoint</text>
          <text
            :x="nodeX + nodeW - 10"
            :y="box.y + 42"
            text-anchor="end"
            class="font-mono text-[12px]"
            :class="box.node.publicIp ? 'fill-(--ui-text)' : 'fill-(--ui-text-dimmed)'"
          >{{ endpoint(box.node) }}</text>
          <text
            :x="nodeX + 10"
            :y="box.y + 67"
            class="caps fill-(--ui-text-muted)"
          >Revision</text>
          <text
            :x="nodeX + nodeW - 10"
            :y="box.y + 67"
            text-anchor="end"
            class="font-mono text-[12px]"
            :class="box.node.inSync ? 'fill-(--ui-text)' : 'fill-warning'"
          >{{ revision(box.node) }}</text>
        </template>
      </g>

      <g v-if="callouts && firstBehind >= 0 && boxes[firstBehind]">
        <circle
          :cx="nodeX - 22"
          :cy="boxes[firstBehind]!.y + 12"
          r="9.5"
          fill="none"
          class="stroke-(--ui-text)"
        />
        <text
          :x="nodeX - 22"
          :y="boxes[firstBehind]!.y + 16"
          text-anchor="middle"
          class="fill-(--ui-text) font-mono text-[11px]"
        >3</text>
      </g>

      <!-- no nodes yet -->
      <g v-if="nodes.length === 0">
        <path
          :d="`M${junctionX} ${cy}H${nodeX}`"
          fill="none"
          class="stroke-standby"
          stroke-width="1.2"
          stroke-dasharray="6 4"
        />
        <rect
          :x="nodeX"
          :y="cy - boxH / 2"
          :width="nodeW"
          :height="boxH"
          fill="none"
          class="stroke-(--ui-border)"
          stroke-dasharray="4 4"
        />
        <text
          :x="nodeX + nodeW / 2"
          :y="cy + 4"
          text-anchor="middle"
          class="caps fill-(--ui-text-muted)"
        >No node enrolled yet</text>
      </g>

      <!-- how long a switch takes to land -->
      <g v-if="dns">
        <path
          :d="`M190 ${H + 12}v12M${nodeX} ${H + 12}v12M190 ${H + 18}H${nodeX}`"
          fill="none"
          class="stroke-(--ui-text-dimmed)"
        />
        <path
          :d="`M196 ${H + 14}l-6 4 6 4M${nodeX - 6} ${H + 14}l6 4-6 4`"
          fill="none"
          class="stroke-(--ui-text-dimmed)"
        />
        <rect
          :x="(190 + nodeX) / 2 - 104"
          :y="H + 8"
          width="208"
          height="20"
          class="fill-(--ui-bg)"
        />
        <text
          :x="(190 + nodeX) / 2"
          :y="H + 22"
          text-anchor="middle"
          class="caps fill-(--ui-text-toned)"
        >Convergence ≤ TTL {{ dns.ttl }} s</text>
      </g>
    </svg>

    <!-- vertical: below lg -->
    <svg
      class="mx-auto block w-full max-w-[440px] lg:hidden"
      :viewBox="`0 0 ${VW} ${vViewH}`"
      role="img"
      :aria-label="summary"
    >
      <text
        x="12"
        y="12"
        class="caps fill-(--ui-text-highlighted)"
      >Clients</text>
      <text
        :x="VW - 12"
        y="12"
        text-anchor="end"
        class="fill-(--ui-text-muted) font-mono text-[11px]"
      >{{ clientsTotal }} issued · {{ clientsOnline }} up</text>
      <g class="stroke-(--ui-border-accented)">
        <path
          v-for="(x, index) in vTicks"
          :key="index"
          :d="`M${x} 20v11`"
        />
      </g>
      <path
        :d="`M12 31v6H${VW - 12}v-6M${VW / 2} 37v21`"
        fill="none"
        class="stroke-(--ui-text)"
        stroke-width="1.2"
      />

      <rect
        x="50"
        y="58"
        width="260"
        height="72"
        class="fill-(--ui-bg-elevated) stroke-(--ui-text)"
        stroke-width="1.2"
      />
      <rect
        x="50"
        y="58"
        width="260"
        height="22"
        class="fill-title stroke-(--ui-text)"
        stroke-width="1.2"
      />
      <text
        x="60"
        y="73"
        class="caps fill-(--ui-text-highlighted)"
      >R1 · DNS {{ recordType }}</text>
      <text
        x="300"
        y="73"
        text-anchor="end"
        class="caps fill-(--ui-text-muted)"
      >{{ dns?.providerConfigured ? dns.provider : 'by hand' }}</text>
      <text
        x="60"
        y="99"
        class="fill-(--ui-text) font-mono text-[12px]"
      >{{ clip(recordName, 28) }}</text>
      <path
        d="M50 106h260"
        class="stroke-(--ui-border-muted)"
      />
      <text
        x="60"
        y="123"
        class="fill-(--ui-text-muted) font-mono text-[11px]"
      >ttl {{ dns?.ttl ?? '—' }} s →</text>
      <text
        x="300"
        y="123"
        text-anchor="end"
        class="font-mono text-[12px]"
        :class="resolvingElsewhere ? 'fill-warning' : 'fill-live'"
      >{{ pointsAt }}</text>

      <path
        :d="`M${VW / 2} 130v26`"
        class="stroke-(--ui-text)"
        stroke-width="1.2"
      />
      <circle
        :cx="VW / 2"
        cy="156"
        r="3.4"
        class="fill-(--ui-text)"
      />

      <g
        v-for="(box, index) in vBoxes"
        :key="`vrail-${box.node.id}`"
      >
        <template v-if="index !== liveIndex">
          <path
            :d="railPath(box.cy)"
            fill="none"
            class="stroke-standby"
            stroke-width="1.2"
            stroke-dasharray="6 4"
          />
          <path
            :d="`M32 ${box.cy - 5}l8 5-8 5`"
            fill="none"
            class="stroke-standby"
            stroke-width="1.2"
          />
        </template>
      </g>
      <g v-if="liveIndex >= 0 && vBoxes[liveIndex]">
        <path
          :d="railPath(vBoxes[liveIndex]!.cy)"
          fill="none"
          class="stroke-live"
          stroke-width="2.2"
        />
        <path
          :d="`M31 ${vBoxes[liveIndex]!.cy - 6}l9 6-9 6`"
          fill="none"
          class="stroke-live"
          stroke-width="2.2"
        />
      </g>

      <g
        v-for="(box, index) in vBoxes"
        :key="`vbox-${box.node.id}`"
      >
        <rect
          x="40"
          :y="box.y"
          :width="VW - 48"
          :height="vBoxH"
          class="fill-(--ui-bg-elevated)"
          :class="index === liveIndex ? 'stroke-live' : 'stroke-(--ui-border-accented)'"
          :stroke-width="index === liveIndex ? 2 : 1.2"
        />
        <rect
          x="40"
          :y="box.y"
          :width="VW - 48"
          height="22"
          :class="index === liveIndex ? 'fill-live-tint stroke-live' : 'fill-title stroke-(--ui-border-accented)'"
          :stroke-width="index === liveIndex ? 2 : 1.2"
        />
        <text
          x="50"
          :y="box.y + 15"
          class="caps fill-(--ui-text-highlighted)"
        >{{ box.designator }}</text>
        <text
          x="76"
          :y="box.y + 16"
          class="fill-(--ui-text-highlighted) font-mono text-[12px] font-medium"
        >{{ clip(box.node.name, 20) }}</text>
        <text
          :x="VW - 18"
          :y="box.y + 15"
          text-anchor="end"
          class="caps"
          :class="toneFill[box.state.tone]"
        >{{ box.state.word }}</text>
        <text
          x="50"
          :y="box.y + 40"
          class="font-mono text-[11.5px]"
          :class="box.node.publicIp ? 'fill-(--ui-text)' : 'fill-(--ui-text-dimmed)'"
        >{{ endpoint(box.node) }}</text>
        <text
          x="50"
          :y="box.y + 56"
          class="font-mono text-[11px]"
          :class="box.node.inSync ? 'fill-(--ui-text-toned)' : 'fill-warning'"
        >{{ clip(`${revision(box.node)} · seen ${relativeTime(box.node.lastSeenAt)}`, 44) }}</text>
      </g>

      <g v-if="nodes.length === 0">
        <path
          :d="`M${VW / 2} 156V${vTop}`"
          class="stroke-standby"
          stroke-dasharray="6 4"
        />
        <rect
          x="40"
          :y="vTop"
          :width="VW - 48"
          :height="vBoxH"
          fill="none"
          class="stroke-(--ui-border)"
          stroke-dasharray="4 4"
        />
        <text
          :x="VW / 2 + 16"
          :y="vTop + vBoxH / 2 + 4"
          text-anchor="middle"
          class="caps fill-(--ui-text-muted)"
        >No node enrolled yet</text>
      </g>

      <g v-if="dns">
        <path
          :d="`M22 ${vStackEnd + 16}v10M${VW - 8} ${vStackEnd + 16}v10M22 ${vStackEnd + 21}H${VW - 8}`"
          fill="none"
          class="stroke-(--ui-text-dimmed)"
        />
        <rect
          :x="VW / 2 - 92"
          :y="vStackEnd + 12"
          width="184"
          height="18"
          class="fill-(--ui-bg)"
        />
        <text
          :x="VW / 2"
          :y="vStackEnd + 25"
          text-anchor="middle"
          class="caps fill-(--ui-text-toned)"
        >Convergence ≤ TTL {{ dns.ttl }} s</text>
      </g>
    </svg>
  </figure>
</template>
