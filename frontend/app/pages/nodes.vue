<script setup lang="ts">
definePageMeta({ middleware: 'auth' })

const toast = useToast()
const { fleet, nodes, dns, loaded } = useFleetState()
const nodeActions = useNodeActions()

const enrollOpen = ref(false)

// Active first, then the rest as they came; revoked nodes sink to the bottom.
const ordered = computed(() => [...nodes.value].sort((a, b) =>
  Number(b.isActive) - Number(a.isActive) || Number(a.revoked) - Number(b.revoked)))

async function copyRecord() {
  if (!dns.value?.targetAddress) {
    return
  }
  await navigator.clipboard.writeText(`${dns.value.recordName} ${dns.value.ttl} IN ${dns.value.recordType} ${dns.value.targetAddress}`)
  toast.add({ title: 'Record copied', icon: 'i-lucide-check', color: 'success' })
}
</script>

<template>
  <div class="flex flex-col gap-4 lg:gap-6">
    <h1 class="sr-only">
      Nodes
    </h1>

    <AppCard
      title="DNS record"
      icon="i-lucide-globe"
      description="Every client config names this record. Pointing it at another node moves the traffic."
    >
      <template #actions>
        <UButton
          v-if="dns && !dns.providerConfigured && dns.targetAddress"
          icon="i-lucide-copy"
          color="neutral"
          variant="outline"
          @click="copyRecord"
        >
          Copy record
        </UButton>
      </template>

      <p
        v-if="!dns"
        class="text-sm text-muted"
      >
        Reading…
      </p>

      <template v-else>
        <dl class="grid grid-cols-2 gap-x-6 gap-y-4 md:grid-cols-3 xl:grid-cols-6">
          <SpecItem
            label="Record"
            mono
          >
            {{ dns.recordName }} {{ dns.recordType }}
          </SpecItem>
          <SpecItem
            label="Points to"
            mono
          >
            <span :class="dns.targetAddress ? 'text-live' : 'text-dimmed'">{{ dns.targetAddress ?? 'not set' }}</span>
            <span
              v-if="dns.activeNodeName"
              class="text-muted"
            > · {{ dns.activeNodeName }}</span>
          </SpecItem>
          <SpecItem label="Resolution check">
            <StateMark
              v-if="dns.targetAddress"
              :state="dns.matches ? 'up' : 'idle'"
              :label="dns.matches ? 'Resolving correctly' : 'Not resolving here yet'"
            />
            <span
              v-else
              class="text-muted"
            >no target yet</span>
          </SpecItem>
          <SpecItem
            label="Resolves to"
            mono
          >
            {{ dns.resolvedAddresses.join(', ') || 'nothing yet' }}
          </SpecItem>
          <SpecItem
            label="TTL"
            mono
          >
            {{ dns.ttl }} s
          </SpecItem>
          <SpecItem label="Last switch">
            {{ dns.activatedAt ? `${relativeTime(dns.activatedAt)} · ${formatUtc(dns.activatedAt)}` : 'never' }}
          </SpecItem>
        </dl>

        <UAlert
          v-if="dns.warning"
          class="mt-4"
          color="warning"
          variant="subtle"
          icon="i-lucide-triangle-alert"
          :description="dns.warning"
        />

        <UAlert
          v-if="!dns.providerConfigured"
          class="mt-4"
          color="neutral"
          variant="subtle"
          icon="i-lucide-info"
          title="Edited by hand"
        >
          <template #description>
            No DNS provider is configured, so making a node active records it here and leaves the
            record to you. Set <code class="font-mono">AWG_CLOUDFLARE_API_TOKEN</code> and
            <code class="font-mono">AWG_CLOUDFLARE_ZONE_ID</code> on the panel to have it edited
            automatically.
          </template>
        </UAlert>
      </template>
    </AppCard>

    <AppCard
      title="Nodes"
      icon="i-lucide-server"
      :description="`${nodes.length} enrolled · all run the same key, subnet and wire format`"
    >
      <template #actions>
        <UButton
          icon="i-lucide-plus"
          @click="enrollOpen = true"
        >
          Enroll node
        </UButton>
      </template>

      <div
        v-if="!loaded"
        class="flex items-center justify-center gap-2 py-10 text-sm text-muted"
      >
        <UIcon
          name="i-lucide-loader-circle"
          class="size-5 animate-spin"
        />
        Loading nodes
      </div>

      <div
        v-else-if="nodes.length === 0"
        class="flex flex-col items-center gap-3 py-10 text-center"
      >
        <span class="flex size-12 items-center justify-center rounded-full bg-elevated">
          <UIcon
            name="i-lucide-server"
            class="size-6 text-muted"
          />
        </span>
        <div>
          <p class="font-medium text-highlighted">
            No nodes yet
          </p>
          <p class="mt-1 max-w-md text-sm text-muted">
            Enrolling gives you a one-line install command. Every node runs the same fleet
            identity, so clients keep working when you switch between them.
          </p>
        </div>
        <UButton
          icon="i-lucide-plus"
          @click="enrollOpen = true"
        >
          Enroll node
        </UButton>
      </div>

      <div
        v-else
        class="grid gap-3 lg:grid-cols-2"
      >
        <NodeCard
          v-for="node in ordered"
          :key="node.id"
          :node="node"
          :listen-port="fleet?.listenPort"
          :busy="nodeActions.busyId.value === node.id"
          detail
          :class="node.revoked ? 'opacity-70' : ''"
          @activate="nodeActions.activate(node)"
          @revoke="nodeActions.revoke(node)"
          @remove="nodeActions.remove(node)"
        />
      </div>
    </AppCard>

    <EnrollNodeModal v-model:open="enrollOpen" />
  </div>
</template>
