<script setup lang="ts">
/**
 * One section of a page. Sections are separate cards on a darker canvas so each reads as its own
 * block, and a section's actions sit in its own header rather than somewhere else on the page.
 */
defineProps<{
  title: string
  description?: string
  icon?: string
  flush?: boolean
  stickyFooter?: boolean
}>()
</script>

<template>
  <section class="rounded-xl border border-default bg-default shadow-xs">
    <header class="flex flex-wrap items-center gap-x-4 gap-y-3 border-b border-default px-4 py-3 sm:px-5">
      <div class="flex min-w-0 items-center gap-3">
        <span
          v-if="icon"
          class="flex size-9 shrink-0 items-center justify-center rounded-lg bg-elevated text-highlighted"
        >
          <UIcon
            :name="icon"
            class="size-5"
          />
        </span>
        <div class="min-w-0">
          <h2 class="text-base font-semibold text-highlighted">
            {{ title }}
          </h2>
          <p
            v-if="description || $slots.description"
            class="text-sm text-muted"
          >
            <slot name="description">
              {{ description }}
            </slot>
          </p>
        </div>
      </div>
      <div
        v-if="$slots.actions"
        class="ms-auto flex min-w-0 max-w-full flex-wrap items-center gap-2"
      >
        <slot name="actions" />
      </div>
    </header>

    <div :class="flush ? '' : 'p-4 sm:p-5'">
      <slot />
    </div>

    <footer
      v-if="$slots.footer"
      class="rounded-b-xl border-t border-default bg-default px-4 py-3 sm:px-5"
      :class="stickyFooter ? 'sticky bottom-16 z-10 md:bottom-0' : ''"
    >
      <slot name="footer" />
    </footer>
  </section>
</template>
