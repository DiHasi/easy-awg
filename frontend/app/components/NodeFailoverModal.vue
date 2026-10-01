<script setup lang="ts">
import type { Node } from '~/types/api'

/**
 * Where a node stands in automatic failover's order. Only the automatic path reads this: the
 * "Make active" button still sends traffic anywhere the operator chooses. Opened through
 * `useNodeActions`, and closes with true once the change is saved.
 */
const props = defineProps<{
  node: Node
}>()

const emit = defineEmits<{ close: [boolean] }>()

const api = useControlApi()
const toast = useToast()

const priority = ref(props.node.failoverPriority)
const automatic = ref(props.node.autoFailover)
const saving = ref(false)

async function save() {
  saving.value = true

  try {
    await api.put<Node>(`/nodes/${props.node.id}/failover`, {
      priority: priority.value,
      autoFailover: automatic.value
    })
    emit('close', true)
  } catch (error) {
    toast.add({ title: 'Could not save', description: describeError(error, ''), color: 'error', icon: 'i-lucide-circle-alert' })
  } finally {
    saving.value = false
  }
}
</script>

<template>
  <UModal
    :title="`Failover · ${node.name}`"
    description="How automatic failover treats this node when the active one fails. Switching by hand ignores both."
    @update:open="(open: boolean) => !open && emit('close', false)"
  >
    <template #body>
      <div class="flex flex-col gap-5">
        <USwitch
          v-model="automatic"
          label="Automatic failover may send traffic here"
          description="Turn off for a node kept in reserve, or one under maintenance."
        />
        <UFormField
          label="Priority"
          description="Lower is tried first among healthy nodes. A node that is in sync, and one probes can reach, still wins over priority."
        >
          <UInputNumber
            v-model="priority"
            :min="0"
            :max="1000"
            :disabled="!automatic"
            class="w-40"
          />
        </UFormField>
      </div>
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
          :loading="saving"
          @click="save"
        >
          Save
        </UButton>
      </div>
    </template>
  </UModal>
</template>
