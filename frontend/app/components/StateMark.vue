<script setup lang="ts">
/**
 * A peer's state as a mark and a word. The marks differ in shape, not only in colour, so the
 * list stays readable in print, in forced-colours mode and for colour-blind operators.
 */
const props = defineProps<{
  state: 'up' | 'idle' | 'off'
  label?: string
}>()

const marks = {
  up: { symbol: '●', word: 'up', tone: 'text-success' },
  idle: { symbol: '○', word: 'idle', tone: 'text-warning' },
  off: { symbol: '–', word: 'off', tone: 'text-dimmed' }
} as const

const mark = computed(() => marks[props.state])
</script>

<template>
  <span
    class="inline-flex items-baseline gap-1.5 font-mono text-xs"
    :class="mark.tone"
  >
    <span aria-hidden="true">{{ mark.symbol }}</span>
    <span>{{ label ?? mark.word }}</span>
  </span>
</template>
