<script setup lang="ts">
import type { Node, NodePeers } from '~/types/api'

/**
 * Who one node is carrying, which is the question an operator asks with their hand on the power
 * switch.
 *
 * The two groups are not the same kind of fact and the dialog says so. The peers connected now
 * reconnect to whatever the record answers within a handshake retry, and they will notice a blip.
 * The silent ones are the ones that break quietly: a running tunnel keeps the address it resolved
 * when it came up, so a phone in a pocket is still pointed here and will find nothing when it
 * wakes.
 */
const props = defineProps<{
  node: Node
}>()

const emit = defineEmits<{ close: [boolean] }>()

const api = useControlApi()

const peers = ref<NodePeers | null>(null)
const loading = ref(true)
const errorMessage = ref('')

const online = computed(() => peers.value?.peers.filter(peer => peer.online) ?? [])
const silent = computed(() => peers.value?.peers.filter(peer => !peer.online) ?? [])

onMounted(async () => {
  try {
    peers.value = await api.get<NodePeers>(`/nodes/${props.node.id}/peers`)
  } catch (error) {
    errorMessage.value = describeError(error, 'Could not read who is on this node.')
  } finally {
    loading.value = false
  }
})
</script>

<template>
  <UModal
    :title="`Peers on ${node.name}`"
    description="Read from the handshakes this node reported. A handshake is all the evidence there is, so silence is not the same as nobody."
    :ui="{ content: 'sm:max-w-2xl' }"
    @update:open="(open: boolean) => !open && emit('close', false)"
  >
    <template #body>
      <div class="flex flex-col gap-4">
        <UAlert
          v-if="errorMessage"
          color="error"
          variant="subtle"
          icon="i-lucide-circle-alert"
          :description="errorMessage"
        />

        <p
          v-else-if="loading"
          class="text-sm text-muted"
        >
          Reading the last report…
        </p>

        <template v-else>
          <p class="text-xs text-muted">
            As of this node's last report
            <span class="text-toned">{{ relativeTime(peers?.evidenceAt, 'never') }}</span>.
            <template v-if="!node.isActive">
              Clients are not being sent here any more, and a tunnel that is already up keeps the
              address it resolved - so these are the people repointing the record did not move.
            </template>
          </p>

          <section
            v-if="online.length > 0"
            class="flex flex-col gap-1"
          >
            <h4 class="text-sm font-medium text-highlighted">
              Connected here · {{ online.length }}
            </h4>
            <p class="text-xs text-muted">
              Powering this node off drops these sessions. They reconnect to whatever the record
              answers, within a handshake retry.
            </p>
            <ul class="divide-y divide-default rounded-lg border border-default">
              <li
                v-for="peer in online"
                :key="peer.id"
                class="flex items-center justify-between gap-3 px-3 py-2"
              >
                <span class="min-w-0">
                  <span class="block truncate text-sm text-highlighted">{{ peer.name }}</span>
                  <span class="block truncate font-mono text-xs text-muted">{{ peer.address }}</span>
                </span>
                <span class="shrink-0 text-xs text-toned">{{ relativeTime(peer.latestHandshakeAt) }}</span>
              </li>
            </ul>
          </section>

          <section
            v-if="silent.length > 0"
            class="flex flex-col gap-1"
          >
            <h4 class="text-sm font-medium text-highlighted">
              Pointed here, silent · {{ silent.length }}
            </h4>
            <p class="text-xs text-muted">
              Their last handshake in the fleet was here, and they are saying nothing now. Nobody
              can tell a device that has gone away from one that is asleep, so this is a ceiling on
              who would notice rather than a count of them.
            </p>
            <ul class="divide-y divide-default rounded-lg border border-default">
              <li
                v-for="peer in silent"
                :key="peer.id"
                class="flex items-center justify-between gap-3 px-3 py-2"
              >
                <span class="min-w-0">
                  <span class="block truncate text-sm text-toned">{{ peer.name }}</span>
                  <span class="block truncate font-mono text-xs text-muted">{{ peer.address }}</span>
                </span>
                <span class="shrink-0 text-xs text-muted">{{ relativeTime(peer.latestHandshakeAt) }}</span>
              </li>
            </ul>
          </section>

          <p
            v-if="online.length === 0 && silent.length === 0"
            class="text-sm text-muted"
          >
            No peer's last handshake was through this node. Nothing here is carrying anybody, as far
            as its last report can say.
          </p>
        </template>
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
