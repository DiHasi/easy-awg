<script setup lang="ts">
import type { EnrollmentToken } from '~/types/api'

/**
 * Issues a one-time enrollment token and shows what to run for a new node, probe or standby
 * panel. The three tokens are not interchangeable - the panel refuses one used for another - so
 * the kind is fixed by where the dialog was opened, and for the panel kind that is the point:
 * what it hands over is the whole control plane, not a share of it.
 */
const open = defineModel<boolean>('open', { default: false })

const props = withDefaults(defineProps<{
  kind?: 'node' | 'probe' | 'panel'
}>(), {
  kind: 'node'
})

const emit = defineEmits<{
  issued: []
}>()

const wording = computed(() => {
  if (props.kind === 'probe') {
    return {
      title: 'Add a probe',
      description: 'Run it where your clients are - ideally inside the network that does the blocking. It checks each node with a real handshake and reports back.',
      label: 'Probe name',
      placeholder: 'moscow-home',
      run: 'Run this on a host where clients are (needs Docker)'
    }
  }

  if (props.kind === 'panel') {
    return {
      title: 'Enroll a standby panel',
      description: 'A second copy of this panel. It replicates everything, refuses every change, and can take the fleet over when this one is gone.',
      label: 'Panel name',
      placeholder: 'panel-frankfurt',
      run: 'Put these in the standby panel environment, then start it'
    }
  }

  return {
    title: 'Enroll a node',
    description: 'Name the node, then run the generated command on that server. The agent enrolls itself and pulls the fleet configuration.',
    label: 'Node name',
    placeholder: 'helsinki-1',
    run: 'Run this on the new server'
  }
})

const api = useControlApi()
const toast = useToast()
const { fleet, refresh } = useFleetState()

const name = ref('')
const creating = ref(false)
const token = ref<EnrollmentToken | null>(null)

/**
 * The per-node settings, folded away because almost no node needs them. They travel on the token
 * and are written to the node row when the agent enrolls, so a node that does need a narrower MTU
 * has it in the very first bundle it fetches rather than being corrected afterwards - by which
 * time it has already brought an interface up at the fleet value.
 *
 * Inputs hold undefined where the API wants null, so the conversion happens at the request.
 */
const advanced = ref(false)
const endpointHost = ref<string | undefined>()
const egressInterface = ref<string | undefined>()
const mtu = ref<number | undefined>()

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
      : props.kind === 'panel'
        ? await api.post<EnrollmentToken>('/panels/tokens', { name: trimmed })
        : await api.post<EnrollmentToken>('/nodes/tokens', {
            name: trimmed,
            endpointHost: endpointHost.value?.trim() || null,
            egressInterface: egressInterface.value?.trim() || null,
            mtu: mtu.value ?? null
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
  advanced.value = false
  endpointHost.value = undefined
  egressInterface.value = undefined
  mtu.value = undefined
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
      <div
        v-if="!token"
        class="flex flex-col gap-4"
      >
        <UAlert
          v-if="kind === 'panel'"
          color="warning"
          variant="subtle"
          icon="i-lucide-shield-alert"
          title="This hands over the whole panel"
        >
          <template #description>
            A standby holds the fleet private key, the bundle signing key, every client private key
            and every admin credential - that is what it takes to keep the fleet converging when
            this panel is gone. Put it somewhere you trust as much as this machine.
          </template>
        </UAlert>

        <UFormField :label="wording.label">
          <UInput
            v-model="name"
            :placeholder="wording.placeholder"
            class="w-full"
            autofocus
            @keyup.enter="issue"
          />
        </UFormField>

        <template v-if="kind === 'node'">
          <UButton
            :icon="advanced ? 'i-lucide-chevron-down' : 'i-lucide-chevron-right'"
            color="neutral"
            variant="link"
            class="-mx-2.5 self-start"
            @click="advanced = !advanced"
          >
            Per-node settings
          </UButton>

          <div
            v-if="advanced"
            class="flex flex-col gap-4 border-s border-default ps-4"
          >
            <UFormField
              label="Egress interface"
              :ui="paramField"
              description="What this node NATs through. Empty lets the agent detect the default-route interface, which is right on almost every host."
            >
              <UInput
                v-model="egressInterface"
                placeholder="detected"
                class="w-full font-mono"
              />
            </UFormField>

            <UFormField
              label="Tunnel MTU"
              :description="`Empty runs the fleet value${fleet ? ` (${fleet.tunnelMtu})` : ''}. Lower it only for a node whose own path to the internet is narrower.`"
            >
              <UInputNumber
                v-model="mtu"
                :min="1280"
                :max="1420"
                placeholder="fleet value"
                class="w-40"
              />
            </UFormField>

            <UFormField
              label="Endpoint host"
              description="A note for a node you give a record of its own. It is not written into any client config."
            >
              <UInput
                v-model="endpointHost"
                placeholder="ams.vpn.example.com"
                class="w-full"
              />
            </UFormField>
          </div>
        </template>
      </div>

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
        <UAlert
          v-if="kind === 'panel'"
          color="neutral"
          variant="subtle"
          icon="i-lucide-info"
          title="Set the two addresses to real ones"
        >
          <template #description>
            <code class="font-mono">AWG_PANEL_URL</code> is how this panel reaches the standby - without
            it the pair cannot tell which of the two is in charge.
            <code class="font-mono">AWG_PANEL_PUBLIC_IP</code> is what the panel record is pointed at when
            the standby takes over.
          </template>
        </UAlert>
        <UFormField :label="wording.run">
          <div class="rounded-md border border-default bg-elevated p-3">
            <code class="block whitespace-pre-wrap break-all font-mono text-xs text-default">{{ token.installCommand }}</code>
          </div>
        </UFormField>
        <UButton
          icon="i-lucide-copy"
          color="neutral"
          variant="outline"
          block
          @click="copy"
        >
          {{ kind === 'panel' ? 'Copy settings' : 'Copy command' }}
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
