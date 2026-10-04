<script setup lang="ts">
import type { DropdownMenuItem } from '@nuxt/ui'
import type { Client } from '~/types/api'

/**
 * One peer: one client config. The numbers are passed in already formatted, because what a
 * handshake or a transfer reads as is the page's decision - a row the stats poll has not reached
 * yet must not round a silence down to zero here.
 */
withDefaults(defineProps<{
  client: Client
  state: 'up' | 'idle' | 'off' | 'unknown'
  handshake: string
  down: string
  up: string
  menu: DropdownMenuItem[][]
  busy?: boolean
  arrangeable?: boolean
  /** This row is the one being carried right now. */
  carried?: boolean
  /** The drop would land immediately above this row. */
  dropBefore?: boolean
}>(), {
  busy: false,
  arrangeable: false,
  carried: false,
  dropBefore: false
})

const emit = defineEmits<{
  config: []
  stats: []
  grab: [PointerEvent]
}>()
</script>

<template>
  <li
    :data-sort-id="client.id"
    data-sort-kind="peer"
    class="relative grid grid-cols-[minmax(0,1fr)_auto] items-center gap-x-4 gap-y-1 px-4 py-3 transition-colors hover:bg-elevated/40 sm:px-5 lg:grid-cols-[minmax(0,1.6fr)_7rem_7.5rem_minmax(0,1.1fr)_9rem] lg:py-2.5"
    :class="carried ? 'opacity-40' : ''"
  >
    <!-- Where the row would land. A line between rows rather than a moving preview: the list is
         also being polled, and a preview that reshuffles under a live update reads as a glitch. -->
    <span
      v-if="dropBefore"
      class="pointer-events-none absolute inset-x-3 -top-px h-0.5 rounded-full bg-primary"
      aria-hidden="true"
    />

    <div class="flex min-w-0 items-center gap-1.5">
      <DragHandle
        v-if="arrangeable"
        :label="`Move ${client.name}`"
        @grab="emit('grab', $event)"
      />
      <div
        class="min-w-0"
        :class="state === 'off' ? 'opacity-60' : ''"
      >
        <p class="flex min-w-0 items-center gap-2">
          <span class="truncate font-medium text-highlighted">{{ client.name }}</span>
          <UBadge
            v-if="client.obfuscation"
            size="sm"
            color="neutral"
            variant="subtle"
          >
            custom timings
          </UBadge>
        </p>
        <p class="font-mono text-xs text-muted">
          {{ client.address }}<span class="lg:hidden"> · {{ handshake }}</span>
        </p>
        <!-- The traffic columns do not fit on a phone, so the numbers move here and open the
             full picture on a tap. -->
        <button
          type="button"
          class="tabular mt-1 flex items-center gap-2.5 font-mono text-xs text-toned lg:hidden"
          :aria-label="`Traffic for ${client.name}`"
          @click="emit('stats')"
        >
          <span>↓ {{ down }}</span>
          <span>↑ {{ up }}</span>
          <UIcon
            name="i-lucide-activity"
            class="size-3.5 text-muted"
          />
        </button>
      </div>
    </div>

    <div class="hidden lg:block">
      <StateMark :state="state" />
    </div>
    <span class="hidden text-sm text-toned lg:block">{{ handshake }}</span>
    <span class="tabular hidden font-mono text-xs text-toned lg:block">
      ↓ {{ down }} &nbsp;↑ {{ up }}
    </span>

    <div class="flex items-center justify-end gap-1.5">
      <StateMark
        class="lg:hidden"
        :state="state"
      />
      <UButton
        size="sm"
        color="neutral"
        variant="outline"
        icon="i-lucide-qr-code"
        :aria-label="`Config for ${client.name}`"
        @click="emit('config')"
      >
        <span class="hidden sm:inline">Config</span>
      </UButton>
      <UDropdownMenu
        :items="menu"
        :content="{ align: 'end' }"
      >
        <UButton
          size="sm"
          color="neutral"
          variant="ghost"
          icon="i-lucide-ellipsis-vertical"
          :loading="busy"
          :aria-label="`More actions for ${client.name}`"
        />
      </UDropdownMenu>
    </div>
  </li>
</template>
