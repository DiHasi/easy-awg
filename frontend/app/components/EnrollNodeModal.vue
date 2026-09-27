<script setup lang="ts">
import type { EnrollmentToken } from '~/types/api'

/** Issues a one-time enrollment token and shows the install command for a new node. */
const open = defineModel<boolean>('open', { default: false })

const api = useControlApi()
const toast = useToast()
const { refresh } = useFleetState()

const name = ref('')
const creating = ref(false)
const token = ref<EnrollmentToken | null>(null)

async function issue() {
  const trimmed = name.value.trim()
  if (!trimmed) {
    toast.add({ title: 'Name is required', color: 'error', icon: 'i-lucide-circle-alert' })
    return
  }

  creating.value = true

  try {
    token.value = await api.post<EnrollmentToken>('/nodes/tokens', {
      name: trimmed,
      endpointHost: null,
      egressInterface: null,
      mtu: null
    })
    name.value = ''
  } catch (error) {
    toast.add({ title: 'Could not issue a token', description: describeError(error, ''), color: 'error', icon: 'i-lucide-circle-alert' })
  } finally {
    creating.value = false
  }
}

async function copy() {
  if (token.value) {
    await navigator.clipboard.writeText(token.value.installCommand)
    toast.add({ title: 'Command copied', icon: 'i-lucide-check', color: 'success' })
  }
}

// The token is shown once and never retrievable, so it is cleared rather than left on screen.
function reset() {
  token.value = null
  name.value = ''
  void refresh()
}
</script>

<template>
  <UModal
    v-model:open="open"
    title="Enroll a node"
    description="Name the node, then run the generated command on that server. The agent enrolls itself and pulls the fleet configuration."
    @after:leave="reset"
  >
    <template #body>
      <UFormField
        v-if="!token"
        label="Node name"
      >
        <UInput
          v-model="name"
          placeholder="helsinki-1"
          class="w-full"
          autofocus
          @keyup.enter="issue"
        />
      </UFormField>

      <div
        v-else
        class="flex flex-col gap-4"
      >
        <UAlert
          color="warning"
          variant="subtle"
          icon="i-lucide-triangle-alert"
          title="Shown once"
          :description="`The token is not stored in readable form. It expires ${formatUtc(token.expiresAt)}.`"
        />
        <UFormField label="Run this on the new server">
          <div class="rounded-md border border-default bg-elevated p-3">
            <code class="block break-all font-mono text-xs text-default">{{ token.installCommand }}</code>
          </div>
        </UFormField>
        <UButton
          icon="i-lucide-copy"
          color="neutral"
          variant="outline"
          block
          @click="copy"
        >
          Copy command
        </UButton>
      </div>
    </template>

    <template #footer>
      <div class="flex w-full justify-end gap-2">
        <UButton
          color="neutral"
          variant="ghost"
          @click="open = false"
        >
          {{ token ? 'Done' : 'Cancel' }}
        </UButton>
        <UButton
          v-if="!token"
          :loading="creating"
          @click="issue"
        >
          Generate command
        </UButton>
      </div>
    </template>
  </UModal>
</template>
