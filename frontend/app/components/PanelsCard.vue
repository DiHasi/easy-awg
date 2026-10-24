<script setup lang="ts">
import type { DropdownMenuItem } from '@nuxt/ui'
import type { Panel, PanelSwitch } from '~/types/api'

/**
 * The panel pair: which of the two is in charge, how far behind the other is, and the two acts
 * that change the answer.
 *
 * It sits beside the node cards rather than in settings because it belongs to the same question
 * they do - where is this fleet being served from, and what happens when that place is gone. The
 * difference is the blast radius, which is why every button here confirms and why the term is on
 * screen: in an incident the term is the only thing that says which panel is the real one.
 */
const api = useControlApi()
const toast = useToast()
const confirm = useConfirm()
const { pair, refresh } = usePanelState()
const { refresh: refreshFleet } = useFleetState()

const enrollOpen = ref(false)
const busy = ref<'mode' | 'sync' | 'promote' | 'demote' | 'hold' | null>(null)

// No timer of its own: the shell polls the pair for every page, because the read-only banner has
// to be right wherever the operator is, not only here.

const panels = computed(() => pair.value?.panels ?? [])
const armed = computed(() => pair.value?.mode === 'automatic')

// A lone panel is not half of a broken pair: until a standby is enrolled the card says what the
// risk is and offers the one action that changes it.
const alone = computed(() => panels.value.length <= 1 && pair.value?.role === 'active')

const verdict = computed(() => pair.value?.action
  ? describeRound(pair.value.action, pair.value.role)
  : null)

// Two panels on different builds is what this column exists to reveal: a standby a release behind
// is refused the snapshot and told to upgrade, and the version is where an operator looks next.
// Revoked panels are left out - whatever they are running stopped mattering when they were cut off.
const mixedVersions = computed(() => new Set(
  panels.value.filter(panel => !panel.revoked && panel.version).map(panel => panel.version)).size > 1)

function duration(seconds: number) {
  return seconds < 120 ? `${seconds} s` : `${Math.round(seconds / 60)} min`
}

async function reload() {
  await Promise.all([refresh(), refreshFleet()])
}

async function sync() {
  busy.value = 'sync'
  try {
    pair.value = await api.post('/panel/sync')
    await refreshFleet()
  } catch (error) {
    toast.add({ title: 'Could not sync', description: describeError(error, ''), color: 'error', icon: 'i-lucide-circle-alert' })
  } finally {
    busy.value = null
  }
}

async function promote() {
  const current = pair.value
  if (!current) {
    return
  }

  const other = panels.value.find(panel => !panel.isSelf && !panel.revoked)
  const confirmed = await confirm({
    title: 'Make this panel the one in charge?',
    description: `It starts accepting changes at term ${current.term + 1}`
      + (current.recordName ? `, and ${current.recordName} is pointed at it` : '')
      + `. ${other ? `'${other.name}' steps down the next time it can see this one` : 'The other panel steps down the next time it can see this one'}`
      + ' - until then it is still accepting changes that this panel will never see.',
    confirmLabel: 'Take over'
  })

  if (!confirmed) {
    return
  }

  busy.value = 'promote'
  try {
    const result = await api.post<PanelSwitch>('/panel/promote')
    pair.value = result.pair
    toast.add({
      title: `This panel is in charge at term ${result.term}`,
      description: result.warning ?? undefined,
      icon: 'i-lucide-crown',
      color: result.warning ? 'warning' : 'success'
    })
    await reload()
  } catch (error) {
    toast.add({ title: 'Could not take over', description: describeError(error, ''), color: 'error', icon: 'i-lucide-circle-alert' })
  } finally {
    busy.value = null
  }
}

async function demote() {
  const confirmed = await confirm({
    title: 'Step down to standby?',
    description: 'This panel stops accepting changes and starts replicating from the other one. Make sure the other '
      + 'panel has actually taken over first, or the fleet has no panel that can change anything.',
    confirmLabel: 'Step down',
    danger: true
  })

  if (!confirmed) {
    return
  }

  busy.value = 'demote'
  try {
    pair.value = await api.post('/panel/demote')
    toast.add({ title: 'This panel is a standby', icon: 'i-lucide-check', color: 'success' })
    await reload()
  } catch (error) {
    toast.add({ title: 'Could not step down', description: describeError(error, ''), color: 'error', icon: 'i-lucide-circle-alert' })
  } finally {
    busy.value = null
  }
}

async function setMode(automatic: boolean) {
  const current = pair.value
  if (!current) {
    return
  }

  if (automatic) {
    const confirmed = await confirm({
      title: 'Let a standby take over on its own?',
      description: `After ${duration(current.graceSeconds)} without contact, a standby that can still reach the `
        + 'internet takes the fleet over by itself. Unlike a node, a panel has no probe to ask - so if the two '
        + 'panels are only cut off from each other, both end up accepting changes until they can see each other again.',
      confirmLabel: 'Arm'
    })
    if (!confirmed) {
      return
    }
  }

  busy.value = 'mode'
  try {
    pair.value = await api.put('/panel/failover', { mode: automatic ? 'automatic' : 'manual' })
    toast.add({
      title: automatic ? 'Automatic promotion armed' : 'Automatic promotion off',
      icon: 'i-lucide-check',
      color: 'success'
    })
  } catch (error) {
    toast.add({ title: 'Could not change this', description: describeError(error, ''), color: 'error', icon: 'i-lucide-circle-alert' })
  } finally {
    busy.value = null
  }
}

async function setHold(held: boolean) {
  busy.value = 'hold'
  try {
    pair.value = await api.put('/panel/hold', { held })
    toast.add({
      title: held ? 'This panel is held' : 'Hold released',
      description: held ? 'It will not promote itself, whatever the pair says.' : undefined,
      icon: 'i-lucide-check',
      color: 'success'
    })
  } catch (error) {
    toast.add({ title: 'Could not change the hold', description: describeError(error, ''), color: 'error', icon: 'i-lucide-circle-alert' })
  } finally {
    busy.value = null
  }
}

async function revoke(id: string, name: string) {
  const confirmed = await confirm({
    title: `Revoke '${name}'?`,
    description: 'It stops receiving the fleet on its next poll and can no longer take over. What it already holds '
      + 'it keeps - a revocation is not a remote wipe.',
    confirmLabel: 'Revoke'
  })

  if (confirmed) {
    await act(() => api.post(`/panels/${id}/revoke`), `'${name}' revoked`)
  }
}

async function remove(id: string, name: string) {
  const confirmed = await confirm({
    title: `Remove '${name}' from the pair?`,
    description: 'It is forgotten here and stops being able to authenticate. The copy of the fleet it already holds '
      + 'stays on that machine until you take it down.',
    confirmLabel: 'Remove',
    danger: true
  })

  if (confirmed) {
    await act(() => api.del(`/panels/${id}`), `'${name}' removed`)
  }
}

/**
 * What can be done to one panel. Nothing that would leave the fleet without a writable panel is
 * offered: stepping down is only there while a peer exists, and a panel never acts on itself
 * beyond that.
 */
function menu(panel: Panel): DropdownMenuItem[][] {
  const items: DropdownMenuItem[] = []

  if (panel.isSelf && pair.value?.role === 'active' && panels.value.length > 1) {
    items.push({ label: 'Step down to standby', icon: 'i-lucide-arrow-down', onSelect: demote })
  }

  if (!panel.isSelf) {
    if (!panel.revoked) {
      items.push({ label: 'Revoke', icon: 'i-lucide-ban', onSelect: () => revoke(panel.id, panel.name) })
    }

    items.push({ label: 'Remove from pair', icon: 'i-lucide-trash-2', color: 'error', onSelect: () => remove(panel.id, panel.name) })
  }

  return items.length > 0 ? [items] : []
}

async function act(call: () => Promise<unknown>, title: string) {
  try {
    await call()
    toast.add({ title, icon: 'i-lucide-check', color: 'success' })
    await refresh()
  } catch (error) {
    toast.add({ title: 'That did not work', description: describeError(error, ''), color: 'error', icon: 'i-lucide-circle-alert' })
  }
}
</script>

<template>
  <AppCard
    title="Panel pair"
    icon="i-lucide-layers"
    description="A second copy of this panel, ready to take the fleet over. One panel is in charge; the other replicates it."
  >
    <template #actions>
      <UButton
        icon="i-lucide-refresh-cw"
        color="neutral"
        variant="outline"
        :loading="busy === 'sync'"
        @click="sync"
      >
        {{ pair?.role === 'standby' ? 'Sync now' : 'Check now' }}
      </UButton>
      <UButton
        v-if="pair?.role === 'active'"
        icon="i-lucide-plus"
        @click="enrollOpen = true"
      >
        Enroll standby
      </UButton>
      <UButton
        v-else-if="pair"
        icon="i-lucide-crown"
        :disabled="!pair.hasFleetState"
        :loading="busy === 'promote'"
        @click="promote"
      >
        Take over
      </UButton>
      <USwitch
        v-if="pair && !alone"
        :model-value="armed"
        :disabled="busy === 'mode' || pair.role === 'standby' || (!pair.witnessConfigured && !armed)"
        :loading="busy === 'mode'"
        :label="armed ? 'Automatic' : 'Manual'"
        @update:model-value="setMode"
      />
    </template>

    <p
      v-if="!pair"
      class="text-sm text-muted"
    >
      Reading…
    </p>

    <template v-else>
      <div
        v-if="verdict && !alone"
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
            {{ pair.message }}
          </p>
          <p class="mt-1 text-xs text-muted">
            Checked {{ relativeTime(pair.evaluatedAt) }}
          </p>
        </div>
      </div>

      <dl class="mt-4 grid grid-cols-2 gap-x-6 gap-y-4 md:grid-cols-3 xl:grid-cols-5">
        <SpecItem label="This panel">
          <StateMark
            :state="pair.role === 'active' ? 'up' : 'idle'"
            :label="pair.role === 'active' ? 'In charge' : 'Standby'"
          />
        </SpecItem>
        <SpecItem
          label="Term"
          mono
        >
          {{ pair.term }}
          <span class="text-muted">· {{ pair.promotedAt ? relativeTime(pair.promotedAt) : 'never switched' }}</span>
        </SpecItem>
        <SpecItem
          v-if="pair.role === 'standby'"
          label="Configuration"
        >
          <span :class="pair.hasFleetState ? '' : 'text-error'">
            {{ pair.snapshotAt ? `${relativeTime(pair.snapshotAt)} behind` : 'nothing replicated' }}
          </span>
        </SpecItem>
        <SpecItem
          v-if="pair.role === 'standby'"
          label="Last pull"
        >
          <span :class="pair.syncError ? 'text-error' : ''">{{ relativeTime(pair.syncedAt) }}</span>
        </SpecItem>
        <SpecItem
          label="Panel record"
          mono
        >
          <span :class="pair.recordName ? '' : 'text-dimmed'">{{ pair.recordName ?? 'none - pointed by hand' }}</span>
        </SpecItem>
        <SpecItem
          label="Grace period"
          mono
        >
          {{ duration(pair.graceSeconds) }}
        </SpecItem>
        <SpecItem label="Witness">
          <span
            v-if="!pair.witnessConfigured"
            class="text-muted"
          >none - automatic promotion refused</span>
          <span
            v-else-if="pair.witnessOnline === false"
            class="text-error"
          >no uplink from here</span>
          <StateMark
            v-else
            state="up"
            label="configured"
          />
        </SpecItem>
        <SpecItem
          label="This panel is at"
          mono
        >
          <span :class="pair.url ? '' : 'text-error'">{{ pair.url ?? 'AWG_PANEL_URL not set' }}</span>
        </SpecItem>
      </dl>

      <UAlert
        v-if="pair.syncError"
        class="mt-4"
        color="error"
        variant="subtle"
        icon="i-lucide-circle-alert"
        title="Last pull failed"
        :description="pair.syncError"
      />

      <UAlert
        v-if="pair.warning"
        class="mt-4"
        :color="alone ? 'neutral' : 'warning'"
        variant="subtle"
        :icon="alone ? 'i-lucide-info' : 'i-lucide-triangle-alert'"
        :description="pair.warning"
      />

      <div
        v-if="panels.length > 1"
        class="mt-4 flex flex-col gap-2"
      >
        <div
          v-for="panel in panels"
          :key="panel.id"
          class="flex flex-wrap items-center gap-x-3 gap-y-2 rounded-lg border border-default p-3"
          :class="panel.revoked ? 'opacity-70' : ''"
        >
          <span
            class="inline-flex shrink-0 items-center gap-1.5 whitespace-nowrap rounded-full px-2 py-0.5 text-xs font-medium"
            :class="toneBadge[describePanel(panel.state).tone]"
          >
            <UIcon
              :name="describePanel(panel.state).icon"
              class="size-3.5"
            />
            {{ describePanel(panel.state).word }}
          </span>

          <div class="min-w-0 flex-1">
            <p class="truncate text-sm font-medium text-highlighted">
              {{ panel.name }}
              <span
                v-if="panel.isSelf"
                class="font-normal text-muted"
              >· this one</span>
            </p>
            <p class="truncate text-xs text-muted">
              {{ panel.stateReason }}
            </p>
          </div>

          <dl class="flex shrink-0 flex-wrap items-center gap-x-5 gap-y-1 text-xs">
            <div>
              <dt class="text-dimmed">
                Term
              </dt>
              <dd class="font-mono text-default">
                {{ panel.term }}
              </dd>
            </div>
            <div>
              <dt class="text-dimmed">
                Behind
              </dt>
              <dd class="font-mono text-default">
                {{ panel.lagSeconds === null || panel.lagSeconds === undefined ? '—' : duration(panel.lagSeconds) }}
              </dd>
            </div>
            <div class="hidden lg:block">
              <dt class="text-dimmed">
                Version
              </dt>
              <dd :class="mixedVersions ? 'font-mono text-warning' : 'font-mono text-default'">
                {{ panel.version ?? '—' }}
              </dd>
            </div>
            <div class="hidden sm:block">
              <dt class="text-dimmed">
                Reached at
              </dt>
              <dd class="font-mono text-default">
                {{ panel.url ?? 'unknown' }}
              </dd>
            </div>
          </dl>

          <UDropdownMenu
            v-if="menu(panel).length > 0"
            :items="menu(panel)"
            :content="{ align: 'end' }"
          >
            <UButton
              icon="i-lucide-ellipsis-vertical"
              color="neutral"
              variant="ghost"
              :loading="busy === 'demote'"
              aria-label="Panel actions"
            />
          </UDropdownMenu>
        </div>
      </div>

      <div
        v-if="pair.role === 'standby'"
        class="mt-4 flex flex-wrap items-center gap-3 rounded-lg border border-default p-3"
      >
        <div class="min-w-0 flex-1">
          <p class="text-sm font-medium text-highlighted">
            Hold this panel
          </p>
          <p class="text-xs text-muted">
            Stops it promoting itself, whatever the pair says. The one switch that works without the other panel
            being reachable.
          </p>
        </div>
        <USwitch
          :model-value="pair.held"
          :loading="busy === 'hold'"
          :label="pair.held ? 'Held' : 'Free'"
          @update:model-value="setHold"
        />
      </div>
    </template>

    <EnrollNodeModal
      v-model:open="enrollOpen"
      kind="panel"
      @issued="refresh"
    />
  </AppCard>
</template>
