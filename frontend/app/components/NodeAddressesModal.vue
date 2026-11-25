<script setup lang="ts">
import type { AddressStatus, Node, NodeAddresses } from '~/types/api'

const props = defineProps<{ node: Node }>()
const emit = defineEmits<{ close: [boolean] }>()
const api = useControlApi()
const checks = ref<NodeAddresses | null>(null)
const loading = ref(true)
const errorMessage = ref('')

async function load() {
  try {
    checks.value = await api.get<NodeAddresses>(`/nodes/${props.node.id}/addresses`)
    errorMessage.value = ''
  } catch (error) {
    errorMessage.value = describeError(error, 'Could not read address checks.')
  } finally {
    loading.value = false
  }
}

usePolling(load, 10000)

const sections = computed(() => checks.value
  ? [
      { title: 'Entry addresses', direction: 'ingress', addresses: checks.value.ingress, description: `AWG handshake and traffic on port ${checks.value.port}. The primary address still decides node failover.` },
      { title: 'Exit addresses', direction: 'egress', addresses: checks.value.egress, description: 'Traffic through a dedicated tunnel peer pinned to each source address, checked against an IP echo service. The outside IP may differ because of routing or NAT.' }
    ]
  : [])

function status(address: AddressStatus) {
  if (address.outcome === 'reachable') return { word: 'traffic flows', tone: 'success' } as const
  if (address.outcome === 'unreachable') return { word: 'check failed', tone: 'error' } as const
  return { word: 'no current evidence', tone: 'muted' } as const
}
</script>

<template>
  <UModal
    :title="`Address checks · ${node.name}`"
    description="Every discovered IPv4 address, checked from the networks where probes run. Checks never change an address or move a peer."
    :ui="{ content: 'sm:max-w-3xl' }"
    @update:open="(open: boolean) => !open && emit('close', false)"
  >
    <template #body>
      <div class="flex flex-col gap-5">
        <UAlert
          v-if="errorMessage"
          color="error"
          variant="subtle"
          icon="i-lucide-circle-alert"
          :description="errorMessage"
        />
        <p
          v-if="loading"
          class="text-sm text-muted"
        >
          Reading checks…
        </p>
        <section
          v-for="section in sections"
          :key="section.direction"
          class="flex flex-col gap-2"
        >
          <h3 class="text-sm font-semibold text-highlighted">
            {{ section.title }}
          </h3>
          <p class="text-xs text-muted">
            {{ section.description }}
          </p>
          <p
            v-if="section.addresses.length === 0"
            class="text-sm text-muted"
          >
            No addresses reported yet. Update the node agent to discover all addresses.
          </p>
          <article
            v-for="address in section.addresses"
            :key="address.address"
            class="min-w-0 rounded-lg border border-default p-3"
          >
            <header class="flex flex-wrap items-center gap-2 text-xs">
              <span class="font-mono text-highlighted">{{ address.address }}</span>
              <span
                v-if="section.direction === 'ingress' && address.address === node.publicIp"
                class="text-muted"
              >primary</span>
              <span
                class="ms-auto rounded-full px-2 py-0.5 font-medium"
                :class="toneBadge[status(address).tone]"
              >
                {{ status(address).word }}
              </span>
            </header>
            <p
              v-if="address.probes.length === 0"
              class="mt-2 text-xs text-muted"
            >
              Waiting for a probe check. Exit checks need an updated probe and a node that has applied its dedicated peer. A free tunnel address is required for each probe and exit.
            </p>
            <ul
              v-else
              class="mt-2 flex flex-col gap-2"
            >
              <li
                v-for="probe in address.probes"
                :key="probe.probeId"
                class="text-xs"
              >
                <div class="flex flex-wrap items-center gap-x-2 gap-y-1">
                  <span class="font-medium">{{ probe.probeName }}</span>
                  <span :class="probe.fresh && probe.outcome === 'unreachable' ? 'text-error' : 'text-muted'">
                    {{ !probe.fresh ? 'stale' : probe.outcome === 'error' ? 'check could not run' : probe.outcome === 'reachable' ? 'traffic flows' : probe.handshake ? 'handshake completed, traffic check failed' : 'no handshake' }}
                  </span>
                  <span
                    v-if="probe.latencyMs != null"
                    class="font-mono text-muted"
                  >{{ probe.latencyMs }} ms</span>
                  <span class="ms-auto text-muted">{{ relativeTime(probe.checkedAt) }}</span>
                </div>
                <p
                  v-if="section.direction === 'egress'"
                  class="mt-0.5 font-mono text-muted"
                >
                  via {{ probe.ingressAddress }}<template v-if="probe.observedAddress">
                    · seen as {{ probe.observedAddress }}
                  </template>
                </p>
                <p
                  v-if="probe.detail"
                  class="mt-0.5 break-words text-muted"
                >
                  {{ probe.detail }}
                </p>
              </li>
            </ul>
          </article>
        </section>
        <p
          v-if="checks"
          class="text-xs text-muted"
        >
          A working entry says nothing about the other entries. An exit whose entry could not complete a handshake stays unknown. Old results and probe errors do not count as failures.
        </p>
      </div>
    </template>
    <template #footer>
      <div class="flex w-full justify-end">
        <UButton
          color="neutral"
          variant="ghost"
          @click="emit('close', false)"
        >
          Close
        </UButton>
      </div>
    </template>
  </UModal>
</template>
