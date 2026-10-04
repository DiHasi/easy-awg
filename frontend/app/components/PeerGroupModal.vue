<script setup lang="ts">
/**
 * Names a group. One person is one group, so the name asked for is a person's, not a label for a
 * device. Opened through `usePeerArrangement`, and closes with the trimmed name, or false when
 * the operator backed out.
 */
const props = withDefaults(defineProps<{
  name?: string
  title?: string
}>(), {
  name: '',
  title: 'New group'
})

const emit = defineEmits<{ close: [string | false] }>()

const value = ref(props.name)

function save() {
  const name = value.value.trim()
  if (name) {
    emit('close', name)
  }
}
</script>

<template>
  <UModal
    :title="title"
    description="A group is one person. The peers inside it are the devices they hold, and moving a peer between groups reissues nothing."
    @update:open="(open: boolean) => !open && emit('close', false)"
  >
    <template #body>
      <UFormField label="Name">
        <UInput
          v-model="value"
          class="w-full"
          placeholder="Alice"
          autofocus
          @keyup.enter="save"
        />
      </UFormField>
    </template>

    <template #footer>
      <div class="flex w-full justify-end gap-2">
        <UButton
          color="neutral"
          variant="ghost"
          @click="emit('close', false)"
        >
          Cancel
        </UButton>
        <UButton
          :disabled="!value.trim()"
          @click="save"
        >
          Save
        </UButton>
      </div>
    </template>
  </UModal>
</template>
