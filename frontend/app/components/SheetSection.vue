<script setup lang="ts">
/**
 * One labelled region of a sheet. Regions are separated by rules rather than boxed as cards:
 * on a drawing the frame is shared and only the lines between areas are drawn.
 */
defineProps<{
  title: string
  meta?: string
  flush?: boolean
}>()
</script>

<template>
  <section class="border-b border-default last:border-b-0">
    <header class="flex min-h-10 flex-wrap items-center gap-x-3 gap-y-2 border-b border-muted px-3 py-1.5 sm:px-4">
      <h2 class="caps text-highlighted">
        {{ title }}
      </h2>
      <span
        v-if="meta || $slots.meta"
        class="font-mono text-[11px] text-muted"
      >
        <slot name="meta">{{ meta }}</slot>
      </span>
      <div
        v-if="$slots.actions"
        class="ms-auto flex min-w-0 max-w-full flex-wrap items-center gap-2"
      >
        <slot name="actions" />
      </div>
    </header>

    <div :class="flush ? '' : 'p-3 sm:p-4'">
      <slot />
    </div>
  </section>
</template>
