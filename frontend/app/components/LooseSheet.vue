<script setup lang="ts">
/**
 * A single sheet for pages outside the admin frame: signing in, and the share link a client
 * opens without an account. Same frame and title block, no sheet index.
 */
defineProps<{
  drawing: string
  rows: { label: string, value: string }[]
}>()
</script>

<template>
  <div class="flex min-h-screen items-center justify-center bg-desk p-3 sm:p-6">
    <div class="w-full max-w-md border border-(--ui-text-dimmed) bg-default p-[3px]">
      <div class="flex flex-col border border-default">
        <header class="flex items-center gap-3 border-b border-default px-4 py-2.5">
          <span class="caps text-[11px] tracking-[0.2em] text-highlighted">AWG&nbsp;Easy</span>
          <span class="font-mono text-[11px] text-muted">{{ drawing }}</span>
        </header>

        <div class="p-4 sm:p-5">
          <slot />
        </div>

        <footer class="grid grid-cols-[auto_1fr] border-t border-accented text-xs">
          <template
            v-for="(row, index) in rows"
            :key="row.label"
          >
            <span
              class="caps border-e border-muted px-2.5 py-1.5 text-muted"
              :class="index < rows.length - 1 ? 'border-b' : ''"
            >{{ row.label }}</span>
            <span
              class="break-all border-muted px-2.5 py-1.5 font-mono"
              :class="index < rows.length - 1 ? 'border-b' : ''"
            >{{ row.value }}</span>
          </template>
        </footer>
      </div>
    </div>
  </div>
</template>
