<script setup lang="ts">
/** A label over a value, as in the parts table of a drawing. */
defineProps<{
  label: string
  mono?: boolean
}>()
</script>

<template>
  <div class="flex min-w-0 flex-col gap-0.5">
    <dt class="caps text-muted">
      {{ label }}
    </dt>
    <dd
      class="min-w-0 break-words text-sm text-default"
      :class="mono ? 'font-mono text-[13px]' : ''"
    >
      <slot />
    </dd>
  </div>
</template>
