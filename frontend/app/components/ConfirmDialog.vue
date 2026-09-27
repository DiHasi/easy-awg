<script setup lang="ts">
/**
 * Asks before a change that cannot be taken back. Opened through `useConfirm`, which resolves
 * to true only when the operator presses the confirming button.
 */
withDefaults(defineProps<{
  title: string
  description?: string
  confirmLabel?: string
  danger?: boolean
}>(), {
  description: undefined,
  confirmLabel: 'Confirm',
  danger: false
})

const emit = defineEmits<{ close: [boolean] }>()
</script>

<template>
  <UModal
    :title="title"
    :close="false"
    @update:open="(open: boolean) => !open && emit('close', false)"
  >
    <template #body>
      <p class="text-sm leading-relaxed text-toned">
        {{ description }}
      </p>
    </template>

    <template #footer>
      <div class="flex w-full justify-end gap-2">
        <UButton
          variant="ghost"
          @click="emit('close', false)"
        >
          Cancel
        </UButton>
        <UButton
          :color="danger ? 'error' : 'primary'"
          variant="solid"
          @click="emit('close', true)"
        >
          {{ confirmLabel }}
        </UButton>
      </div>
    </template>
  </UModal>
</template>
