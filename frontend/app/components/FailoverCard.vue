<script setup lang="ts">
import type { Tone } from '~/composables/useNodeState'
import type { FailoverAction, FailoverStatus } from '~/types/api'

/**
 * Whether the panel moves the record on its own, and what it last concluded. The switch itself is
 * still the node's "Make active"; this only decides whether the panel may press it unasked.
 */
const api = useControlApi()
const toast = useToast()
const confirm = useConfirm()
const { nodes, refresh } = useFleetState()

const status = ref<FailoverStatus | null>(null)
const busy = ref<'mode' | 'evaluate' | 'test' | null>(null)

async function load() {
  try {
    status.value = await api.get<FailoverStatus>('/failover')
  } catch {
    // The fleet state already reports a panel that cannot be read; one message is enough.
  }
}

usePolling(load, 10000)

const armed = computed(() => status.value?.mode === 'automatic')

// Only the verdicts an operator has to act on get colour; "nothing to do" stays quiet.
const verdicts: Record<FailoverAction, { word: string, tone: Tone, icon: string }> = {
  none: { word: 'Nothing to do', tone: 'success', icon: 'i-lucide-check' },
  wait: { word: 'Waiting out a failure', tone: 'warning', icon: 'i-lucide-hourglass' },
  hold: { word: 'Holding after a switch', tone: 'warning', icon: 'i-lucide-pause' },
  stuck: { word: 'No node can take over', tone: 'error', icon: 'i-lucide-octagon-alert' },
  switch: { word: 'Switched', tone: 'success', icon: 'i-lucide-arrow-right-left' },
  recommend: { word: 'Switch recommended', tone: 'warning', icon: 'i-lucide-arrow-right-left' },
  failed: { word: 'Switch failed', tone: 'error', icon: 'i-lucide-circle-alert' }
}

const verdict = computed(() => status.value?.action ? verdicts[status.value.action] : null)

const target = computed(() => nodes.value.find(node => node.id === status.value?.targetNodeId) ?? null)

function duration(seconds: number) {
  return seconds < 120 ? `${seconds} s` : `${Math.round(seconds / 60)} min`
}

async function setMode(automatic: boolean) {
  const current = status.value
  if (!current) {
    return
  }

  if (automatic) {
    const confirmed = await confirm({
      title: 'Arm automatic failover?',
      description: `Once the active node has been blocked, down or silent for ${duration(current.graceSeconds)}, the panel moves the record to the healthiest standby on its own. It never switches back by itself.`,
      confirmLabel: 'Arm'
    })
    if (!confirmed) {
      return
    }
  }

  busy.value = 'mode'

  try {
    status.value = await api.put<FailoverStatus>('/failover', { mode: automatic ? 'automatic' : 'manual' })
    toast.add({
      title: automatic ? 'Automatic failover armed' : 'Automatic failover off',
      icon: 'i-lucide-check',
      color: 'success'
    })
  } catch (error) {
    toast.add({ title: 'Could not change failover', description: describeError(error, ''), color: 'error', icon: 'i-lucide-circle-alert' })
  } finally {
    busy.value = null
  }
}

async function evaluate() {
  busy.value = 'evaluate'
  try {
    status.value = await api.post<FailoverStatus>('/failover/evaluate')
    await refresh()
  } catch (error) {
    toast.add({ title: 'Could not evaluate', description: describeError(error, ''), color: 'error', icon: 'i-lucide-circle-alert' })
  } finally {
    busy.value = null
  }
}

async function testNotification() {
  busy.value = 'test'
  try {
    await api.post('/failover/test-notification')
    toast.add({ title: 'Test notification sent', icon: 'i-lucide-check', color: 'success' })
  } catch (error) {
    toast.add({ title: 'Could not send', description: describeError(error, ''), color: 'error', icon: 'i-lucide-circle-alert' })
  } finally {
    busy.value = null
  }
}
</script>

<template>
  <AppCard
    title="Automatic failover"
    icon="i-lucide-route"
    description="Moves the record off a blocked or dead active node without waiting for you."
  >
    <template #actions>
      <UButton
        icon="i-lucide-refresh-cw"
        color="neutral"
        variant="outline"
        :loading="busy === 'evaluate'"
        @click="evaluate"
      >
        Check now
      </UButton>
      <USwitch
        :model-value="armed"
        :disabled="!status || busy === 'mode' || (!status.providerConfigured && !armed)"
        :loading="busy === 'mode'"
        :label="armed ? 'Armed' : 'Off'"
        @update:model-value="setMode"
      />
    </template>

    <p
      v-if="!status"
      class="text-sm text-muted"
    >
      Reading…
    </p>

    <template v-else>
      <div
        v-if="verdict"
        class="flex items-start gap-3 rounded-lg border border-default p-3"
      >
        <span
          class="mt-0.5 inline-flex shrink-0 items-center gap-1.5 whitespace-nowrap rounded-full px-2 py-0.5 text-xs font-medium"
          :class="toneBadge[verdict.tone]"
        >
          <UIcon
            :name="verdict.icon"
            class="size-3.5"
          />
          {{ verdict.word }}
        </span>
        <div class="min-w-0 text-sm">
          <p class="text-default">
            {{ status.message }}
          </p>
          <p class="mt-1 text-xs text-muted">
            Checked {{ relativeTime(status.evaluatedAt) }}<template v-if="target && status.action === 'recommend'">
              · make <span class="font-mono">{{ target.name }}</span> active on its card to follow the advice
            </template>
          </p>
        </div>
      </div>
      <p
        v-else
        class="text-sm text-muted"
      >
        The panel has not checked the fleet yet. It does every {{ duration(status.checkIntervalSeconds) }}, or press Check now.
      </p>

      <dl class="mt-4 grid grid-cols-2 gap-x-6 gap-y-4 md:grid-cols-3 xl:grid-cols-5">
        <SpecItem
          label="Grace period"
          mono
        >
          {{ duration(status.graceSeconds) }}
        </SpecItem>
        <SpecItem
          label="Cooldown"
          mono
        >
          {{ duration(status.cooldownSeconds) }}
        </SpecItem>
        <SpecItem
          label="Silent after"
          mono
        >
          {{ duration(status.nodeStaleSeconds) }}
        </SpecItem>
        <SpecItem label="DNS provider">
          <span :class="status.providerConfigured ? '' : 'text-muted'">{{ status.providerConfigured ? status.provider : 'none - manual edits' }}</span>
        </SpecItem>
        <SpecItem label="Notifications">
          <span
            v-if="status.notificationChannels.length"
            class="flex flex-wrap items-center gap-2"
          >
            <span class="font-mono text-[13px]">{{ status.notificationChannels.join(', ') }}</span>
            <UButton
              size="xs"
              color="neutral"
              variant="outline"
              :loading="busy === 'test'"
              @click="testNotification"
            >
              Test
            </UButton>
          </span>
          <span
            v-else
            class="text-muted"
          >none configured</span>
        </SpecItem>
      </dl>

      <UAlert
        v-if="!status.providerConfigured"
        class="mt-4"
        color="neutral"
        variant="subtle"
        icon="i-lucide-info"
        title="Needs a DNS provider"
      >
        <template #description>
          Automatic failover edits the record itself, so it can only be armed with
          <code class="font-mono">AWG_CLOUDFLARE_API_TOKEN</code> and
          <code class="font-mono">AWG_CLOUDFLARE_ZONE_ID</code> set. Until then the panel still
          watches and tells you when a switch is due.
        </template>
      </UAlert>
    </template>
  </AppCard>
</template>
