<script setup lang="ts">
import type { DropdownMenuItem } from '@nuxt/ui'
import type { Node } from '~/types/api'

/**
 * A node with its one frequent action in plain sight. Switching traffic to a standby node is the
 * operation that matters under pressure, so it is a button on the card; revoking and removing are
 * rare and destructive, so they sit behind the menu.
 */
const props = withDefaults(defineProps<{
  node: Node
  listenPort?: number | null
  busy?: boolean
  detail?: boolean
}>(), {
  listenPort: null,
  busy: false,
  detail: false
})

const emit = defineEmits<{
  activate: []
  revoke: []
  remove: []
  failover: []
}>()

const state = computed(() => describeNode(props.node))

const endpoint = computed(() => {
  if (!props.node.publicIp) {
    return 'address unknown'
  }
  return props.listenPort ? `${props.node.publicIp}:${props.listenPort}` : props.node.publicIp
})

// Worth a line of its own only when something is wrong: a healthy node's reason is noise.
const failing = computed(() => ['blocked', 'down', 'silent'].includes(props.node.health))

const reachability = computed(() => {
  const node = props.node
  if (node.healthSource === 'probes') {
    return `${node.probesReachable}/${node.probesReporting} probes reach it`
  }
  return 'agent only, no probe'
})

const menu = computed(() => {
  const groups: DropdownMenuItem[][] = []
  if (!props.node.revoked) {
    groups.push([{ label: 'Failover settings', icon: 'i-lucide-route', onSelect: () => emit('failover') }])
    groups.push([{ label: 'Revoke access', icon: 'i-lucide-ban', onSelect: () => emit('revoke') }])
  }
  groups.push([{ label: 'Remove from panel', icon: 'i-lucide-trash-2', color: 'error', onSelect: () => emit('remove') }])
  return groups
})
</script>

<template>
  <!--
    `min-w-0` because the endpoint line is `truncate`: nowrap makes this card's min-content the
    whole address, and a grid or flex item cannot shrink below its min-content. Without it the
    card pushes the page sideways on a phone rather than ellipsising the address, as intended.
  -->
  <article
    class="min-w-0 rounded-lg border bg-default p-3.5 transition-colors"
    :class="node.isActive && !node.revoked ? 'border-live/60 ring-1 ring-live/25' : 'border-default'"
  >
    <header class="flex items-start gap-2">
      <div class="min-w-0 flex-1">
        <div class="flex flex-wrap items-center gap-2">
          <h3 class="font-mono text-sm font-semibold text-highlighted">
            {{ node.name }}
          </h3>
          <span
            class="rounded-full px-2 py-0.5 text-xs font-medium"
            :class="toneBadge[state.tone]"
          >{{ state.word }}</span>
        </div>
        <p class="mt-0.5 truncate font-mono text-xs text-muted">
          {{ endpoint }} · seen {{ relativeTime(node.lastSeenAt) }}
        </p>
      </div>
      <UDropdownMenu
        :items="menu"
        :content="{ align: 'end' }"
      >
        <UButton
          icon="i-lucide-ellipsis-vertical"
          color="neutral"
          variant="ghost"
          size="sm"
          :aria-label="`More actions for ${node.name}`"
        />
      </UDropdownMenu>
    </header>

    <p
      v-if="node.revoked"
      class="mt-2 flex items-center gap-1.5 text-xs text-muted"
    >
      <UIcon
        name="i-lucide-ban"
        class="size-3.5 shrink-0"
      />
      No longer receives configuration; last applied r{{ node.appliedRevision }}
    </p>
    <p
      v-else
      class="mt-2 flex items-center gap-1.5 text-xs"
      :class="node.inSync ? 'text-muted' : 'text-warning'"
    >
      <UIcon
        :name="node.inSync ? 'i-lucide-check' : 'i-lucide-refresh-cw'"
        class="size-3.5 shrink-0"
      />
      <span v-if="node.inSync">Config r{{ node.appliedRevision }} applied</span>
      <span v-else>Updating r{{ node.appliedRevision }} → r{{ node.fleetRevision }}</span>
    </p>
    <!--
      A node behind the bundle schema is served the older obfuscation profile, so it quietly speaks
      a different wire format from the rest of the fleet. Worth saying rather than looking healthy.
    -->
    <p
      v-if="!node.supportsCurrentSchema && !node.revoked"
      class="mt-1 flex items-center gap-1.5 text-xs text-warning"
    >
      <UIcon
        name="i-lucide-triangle-alert"
        class="size-3.5 shrink-0"
      />
      Agent predates AmneziaWG 3.x and speaks an older wire format
    </p>
    <p
      v-if="failing && !node.revoked"
      class="mt-2 flex items-start gap-1.5 text-xs text-error"
    >
      <UIcon
        name="i-lucide-shield-alert"
        class="mt-px size-3.5 shrink-0"
      />
      <span>
        {{ node.healthReason }}
        <template v-if="node.failingSince"> Started {{ relativeTime(node.failingSince) }}.</template>
      </span>
    </p>
    <p
      v-if="node.lastError"
      class="mt-2 rounded-md bg-error/10 px-2 py-1 font-mono text-xs text-error"
    >
      {{ node.lastError }}
    </p>

    <dl
      v-if="detail"
      class="mt-3 grid grid-cols-2 gap-3 border-t border-default pt-3 sm:grid-cols-3"
    >
      <SpecItem
        label="Backend"
        mono
      >
        {{ node.backend ?? '—' }}
      </SpecItem>
      <SpecItem
        label="Agent"
        mono
      >
        {{ node.agentVersion ?? '—' }}
      </SpecItem>
      <SpecItem
        label="Bundle schema"
        mono
      >
        v{{ node.bundleSchemaVersion }}
      </SpecItem>
      <SpecItem
        label="Interface"
        mono
      >
        <span :class="node.interfaceUp ? '' : 'text-error'">awg0 {{ node.interfaceUp ? 'up' : 'down' }}</span>
      </SpecItem>
      <SpecItem
        label="Hostname"
        mono
      >
        {{ node.hostname ?? '—' }}
      </SpecItem>
      <SpecItem
        label="Enrolled"
        mono
      >
        {{ formatUtc(node.enrolledAt).slice(0, 10) }}
      </SpecItem>
      <SpecItem label="Reachability">
        {{ reachability }}
      </SpecItem>
      <SpecItem label="Auto failover">
        <span
          v-if="node.autoFailover"
          class="font-mono text-[13px]"
        >priority {{ node.failoverPriority }}</span>
        <span
          v-else
          class="text-muted"
        >excluded</span>
      </SpecItem>
    </dl>

    <div
      v-if="!node.revoked"
      class="mt-3 flex flex-wrap items-center gap-2"
    >
      <p
        v-if="node.isActive"
        class="flex items-center gap-1.5 text-xs font-medium text-live"
      >
        <UIcon
          name="i-lucide-radio-tower"
          class="size-3.5"
        />
        Clients connect here
      </p>
      <template v-else>
        <UButton
          size="sm"
          icon="i-lucide-arrow-right-left"
          :disabled="!node.publicIp"
          :loading="busy"
          @click="emit('activate')"
        >
          Make active
        </UButton>
        <span
          v-if="!node.publicIp"
          class="text-xs text-muted"
        >waiting for the node to report its address</span>
      </template>
    </div>
  </article>
</template>
