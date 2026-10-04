<script setup lang="ts">
import type { Client, ClientStats, Node } from '~/types/api'

/**
 * What one peer is actually doing: how much it has moved, when it last handshook and which node
 * it is on. The list can only afford a couple of numbers per row and drops even those on a narrow
 * screen, so this is where the whole picture lives.
 *
 * Resetting the counter is here rather than in the peer's menu because this is where the number
 * being reset is on screen.
 */
const props = defineProps<{
  client: Client | null
  stats?: ClientStats | null
  node?: Node | null
}>()

const open = defineModel<boolean>('open', { default: false })

const emit = defineEmits<{ reset: [ClientStats] }>()

const api = useControlApi()
const toast = useToast()
const confirm = useConfirm()

const resetting = ref(false)

const state = computed<'up' | 'idle' | 'off' | 'unknown'>(() => {
  if (!props.client?.enabled) {
    return 'off'
  }
  // No stats row yet means the nodes have not reported this peer to us, not that it is idle.
  if (!props.stats) {
    return 'unknown'
  }
  return props.stats.online ? 'up' : 'idle'
})

const known = computed(() => Boolean(props.stats))

/** Reported numbers only; an em dash where there is nothing to report. */
function bytes(value: number) {
  return known.value ? formatBytes(value) : '—'
}

// The server's "received" is what the client sent, so the arrows are flipped for the person
// reading them: they think in what their own device downloaded.
const down = computed(() => props.stats?.transmittedBytes ?? 0)
const up = computed(() => props.stats?.receivedBytes ?? 0)

const since = computed(() => props.stats?.statsResetAt
  ? `since the counter was reset on ${formatUtc(props.stats.statsResetAt)}`
  : 'since the peer was created')

async function reset() {
  const client = props.client
  if (!client) {
    return
  }

  const confirmed = await confirm({
    title: `Reset the counter for ${client.name}?`,
    description: `Traffic counted so far (↓ ${formatBytes(down.value)} · ↑ ${formatBytes(up.value)}) stops being shown and counting starts from zero. The tunnel is not touched and nothing is reissued.`,
    confirmLabel: 'Reset counter'
  })
  if (!confirmed) {
    return
  }

  resetting.value = true

  try {
    emit('reset', await api.post<ClientStats>(`/clients/${client.id}/stats/reset`))
    toast.add({ title: 'Counter reset', icon: 'i-lucide-check', color: 'success' })
  } catch (error) {
    toast.add({
      title: 'Could not reset the counter',
      description: describeError(error, ''),
      color: 'error',
      icon: 'i-lucide-circle-alert'
    })
  } finally {
    resetting.value = false
  }
}
</script>

<template>
  <UModal
    v-model:open="open"
    :title="client ? `Traffic for ${client.name}` : 'Traffic'"
    :description="client ? `${client.address} · counted across every node` : undefined"
  >
    <template #body>
      <div class="flex flex-col gap-4">
        <div class="grid grid-cols-2 gap-3 rounded-lg border border-default bg-elevated/40 p-3">
          <div class="flex min-w-0 flex-col gap-0.5">
            <span class="flex items-center gap-1 text-xs text-muted">
              <UIcon
                name="i-lucide-arrow-down"
                class="size-3.5"
              />
              Downloaded
            </span>
            <span class="tabular font-mono text-lg font-semibold text-highlighted">{{ bytes(down) }}</span>
          </div>
          <div class="flex min-w-0 flex-col gap-0.5">
            <span class="flex items-center gap-1 text-xs text-muted">
              <UIcon
                name="i-lucide-arrow-up"
                class="size-3.5"
              />
              Uploaded
            </span>
            <span class="tabular font-mono text-lg font-semibold text-highlighted">{{ bytes(up) }}</span>
          </div>
        </div>

        <dl class="grid grid-cols-2 gap-3">
          <SpecItem label="Status">
            <StateMark :state="state" />
          </SpecItem>
          <SpecItem label="Last handshake">
            {{ relativeTime(stats?.latestHandshakeAt, known ? 'never' : '—') }}
          </SpecItem>
          <SpecItem
            label="Connected through"
            mono
          >
            {{ node?.name ?? '—' }}
          </SpecItem>
          <SpecItem
            label="Total"
            mono
          >
            {{ bytes(down + up) }}
          </SpecItem>
          <SpecItem
            label="Public key"
            mono
          >
            {{ shortKey(client?.publicKey) }}
          </SpecItem>
        </dl>

        <p class="text-xs text-muted">
          Counted {{ since }}. The nodes count, not the panel, so a node that reboots starts its
          own tally again.
        </p>
      </div>
    </template>

    <template #footer>
      <div class="flex w-full flex-wrap items-center gap-2">
        <UButton
          icon="i-lucide-rotate-ccw"
          color="neutral"
          variant="outline"
          :loading="resetting"
          @click="reset"
        >
          Reset counter
        </UButton>
        <UButton
          class="ms-auto"
          color="neutral"
          variant="ghost"
          @click="open = false"
        >
          Close
        </UButton>
      </div>
    </template>
  </UModal>
</template>
