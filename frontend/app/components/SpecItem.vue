<script setup lang="ts">
/** A label over a value. */
defineProps<{
  label: string
  mono?: boolean
}>()
</script>

<template>
  <div class="flex min-w-0 flex-col gap-0.5">
    <dt class="text-xs text-muted">
      {{ label }}
    </dt>
    <dd
      class="min-w-0 break-words text-sm text-highlighted"
      :class="mono ? 'font-mono text-[13px]' : ''"
    >
      <slot />
    </dd>
  </div>
</template>
