<script setup lang="ts">
import type { DropdownMenuItem } from '@nuxt/ui'
import type { PeerBucket } from '~/composables/usePeerArrangement'

/**
 * One section of the peer list: a person and the devices they hold, or the ungrouped peers that
 * are filed under nobody.
 *
 * It is both a row - a section can be dragged among the other sections - and a bucket, which is
 * why the element carries `data-sort-kind="group"` and `data-sort-bucket` at once. A fleet with
 * no groups renders the list bare, so grouping costs nothing to anyone not using it.
 */
const props = withDefaults(defineProps<{
  bucket: PeerBucket
  /** Peers filed here, before the search narrowed anything down. */
  count: number
  online: number
  bare?: boolean
  arrangeable?: boolean
  carried?: boolean
  /** A section being carried would land immediately above this one. */
  dropBefore?: boolean
  /** A peer being carried would land at the end of this bucket. */
  dropAtEnd?: boolean
}>(), {
  bare: false,
  arrangeable: false,
  carried: false,
  dropBefore: false,
  dropAtEnd: false
})

const emit = defineEmits<{
  rename: []
  remove: []
  newPeer: []
  grab: [PointerEvent]
}>()

const menu = computed<DropdownMenuItem[][]>(() => [
  [{ label: 'Rename', icon: 'i-lucide-pencil', onSelect: () => emit('rename') }],
  // Deleting the person is not deleting their configs; the dialog says so before it happens.
  [{ label: 'Delete group', icon: 'i-lucide-trash-2', color: 'error', onSelect: () => emit('remove') }]
])

const summary = computed(() => {
  if (props.count === 0) {
    return 'no peers'
  }
  const devices = props.count === 1 ? '1 device' : `${props.count} devices`
  return props.online > 0 ? `${devices} · ${props.online} online` : devices
})
</script>

<template>
  <section
    :data-sort-bucket="bucket.key"
    :data-sort-kind="bucket.group ? 'group' : undefined"
    :data-sort-id="bucket.group?.id"
    class="relative"
    :class="carried ? 'opacity-40' : ''"
  >
    <span
      v-if="dropBefore"
      class="pointer-events-none absolute inset-x-3 -top-px h-0.5 rounded-full bg-primary"
      aria-hidden="true"
    />

    <header
      v-if="!bare"
      class="flex items-center gap-2 border-b border-default bg-elevated/30 px-4 py-2 sm:px-5"
    >
      <DragHandle
        v-if="arrangeable && bucket.group"
        :label="`Move ${bucket.group.name}`"
        @grab="emit('grab', $event)"
      />
      <UIcon
        :name="bucket.group ? 'i-lucide-user' : 'i-lucide-inbox'"
        class="size-4 shrink-0 text-dimmed"
        :class="arrangeable && bucket.group ? '' : 'ms-1'"
      />
      <h3 class="min-w-0 truncate text-sm font-semibold text-highlighted">
        {{ bucket.group?.name ?? 'Ungrouped' }}
      </h3>
      <span class="shrink-0 text-xs text-muted">{{ summary }}</span>

      <div class="ms-auto flex shrink-0 items-center gap-1">
        <UButton
          v-if="bucket.group"
          size="xs"
          color="neutral"
          variant="ghost"
          icon="i-lucide-plus"
          :aria-label="`New peer for ${bucket.group.name}`"
          @click="emit('newPeer')"
        >
          <span class="hidden sm:inline">New peer</span>
        </UButton>
        <UDropdownMenu
          v-if="bucket.group"
          :items="menu"
          :content="{ align: 'end' }"
        >
          <UButton
            size="xs"
            color="neutral"
            variant="ghost"
            icon="i-lucide-ellipsis-vertical"
            :aria-label="`More actions for ${bucket.group.name}`"
          />
        </UDropdownMenu>
      </div>
    </header>

    <ul class="relative divide-y divide-default">
      <slot />

      <!-- The end of the bucket, and the only place an empty one can be dropped into. It keeps a
           height of its own so a group with nobody in it is still a target a finger can hit. -->
      <li
        class="relative"
        :class="count === 0 ? 'px-4 py-3 sm:px-5' : 'h-2'"
      >
        <span
          v-if="dropAtEnd"
          class="pointer-events-none absolute inset-x-3 top-0 h-0.5 rounded-full bg-primary"
          aria-hidden="true"
        />
        <p
          v-if="count === 0 && !bare"
          class="text-xs text-dimmed"
        >
          {{ bucket.group ? 'Drag a peer here to file it under this person.' : 'Peers filed under nobody show up here.' }}
        </p>
      </li>
    </ul>
  </section>
</template>
