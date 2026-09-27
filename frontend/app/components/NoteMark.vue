<script setup lang="ts">
/** The circled reference number tying a note to its callout on the drawing. */
defineProps<{ n: number | string }>()
</script>

<template>
  <span
    class="inline-flex size-[18px] shrink-0 items-center justify-center rounded-full border border-accented font-mono text-[10px] leading-none text-default"
    aria-hidden="true"
  >{{ n }}</span>
</template>
