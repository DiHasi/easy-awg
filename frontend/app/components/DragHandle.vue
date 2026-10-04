<script setup lang="ts">
/**
 * Where a drag starts. A handle rather than the whole row: a row is also a link to a config and a
 * menu, and a list whose every row moves when a finger brushes it is worse than one that does not
 * move at all. `touch-none` is what stops the page scrolling under the finger instead.
 */
defineProps<{ label: string }>()

const emit = defineEmits<{ grab: [PointerEvent] }>()
</script>

<template>
  <button
    type="button"
    class="flex size-7 shrink-0 cursor-grab touch-none items-center justify-center rounded-md text-dimmed transition-colors hover:bg-elevated hover:text-toned active:cursor-grabbing"
    :aria-label="label"
    :title="label"
    @pointerdown="emit('grab', $event)"
  >
    <UIcon
      name="i-lucide-grip-vertical"
      class="size-4"
    />
  </button>
</template>
