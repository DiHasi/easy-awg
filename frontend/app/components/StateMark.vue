<script setup lang="ts">
/** A peer's state as a dot and a word, so it never depends on colour alone. */
const props = defineProps<{
  state: 'up' | 'idle' | 'off'
  label?: string
}>()

const marks = {
  up: { word: 'Online', tone: 'bg-success/10 text-success', dot: 'bg-success' },
  idle: { word: 'Idle', tone: 'bg-warning/10 text-warning', dot: 'bg-warning' },
  off: { word: 'Disabled', tone: 'bg-elevated text-muted', dot: 'bg-(--ui-text-dimmed)' }
} as const

const mark = computed(() => marks[props.state])
</script>

<template>
  <span
    class="inline-flex items-center gap-1.5 whitespace-nowrap rounded-full px-2 py-0.5 text-xs font-medium"
    :class="mark.tone"
  >
    <span
      class="size-1.5 rounded-full"
      :class="mark.dot"
      aria-hidden="true"
    />
    {{ label ?? mark.word }}
  </span>
</template>
