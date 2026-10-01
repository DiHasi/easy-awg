<script setup lang="ts">
import type { EnrollmentToken } from '~/types/api'

/**
 * Issues a one-time enrollment token and shows the install command for a new node or probe. The
 * two tokens are not interchangeable - the panel refuses one used for the other - so the kind is
 * fixed by where the dialog was opened.
 */
const open = defineModel<boolean>('open', { default: false })

const props = withDefaults(defineProps<{
  kind?: 'node' | 'probe'
}>(), {
  kind: 'node'
})

const emit = defineEmits<{
  issued: []
}>()

const wording = computed(() => props.kind === 'probe'
  ? {
      title: 'Add a probe',
      description: 'Run it where your clients are - ideally inside the network that does the blocking. It checks each node with a real handshake and reports back.',
      label: 'Probe name',
      placeholder: 'moscow-home',
      run: 'Run this on a host where clients are (needs Docker)'
    }
  : {
      title: 'Enroll a node',
      description: 'Name the node, then run the generated command on that server. The agent enrolls itself and pulls the fleet configuration.',
      label: 'Node name',
      placeholder: 'helsinki-1',
      run: 'Run this on the new server'
    })

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
    token.value = props.kind === 'probe'
      ? await api.post<EnrollmentToken>('/probes/tokens', { name: trimmed })
      : await api.post<EnrollmentToken>('/nodes/tokens', {
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
  emit('issued')
}
</script>

<template>
  <UModal
    v-model:open="open"
    :title="wording.title"
    :description="wording.description"
    @after:leave="reset"
  >
    <template #body>
      <UFormField
        v-if="!token"
        :label="wording.label"
      >
        <UInput
          v-model="name"
          :placeholder="wording.placeholder"
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
        <UFormField :label="wording.run">
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
