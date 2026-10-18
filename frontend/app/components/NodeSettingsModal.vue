<script setup lang="ts">
import type { Node } from '~/types/api'

/**
 * The per-node half of a node's configuration.
 *
 * Two kinds of field in one dialog, and the difference is worth stating rather than hiding: the
 * MTU and the egress interface are handed to this node in its bundle, so saving one moves the
 * fleet revision and that node takes its interface down once to apply it. The name and the
 * endpoint host are notes in the panel.
 *
 * The API models "no override" as null while an input needs undefined, so the conversion happens
 * here, at the boundary, in both directions.
 */
const props = defineProps<{
  node: Node
}>()

const emit = defineEmits<{ close: [boolean] }>()

const api = useControlApi()
const toast = useToast()
const { fleet } = useFleetState()

const name = ref(props.node.name)
const endpointHost = ref(props.node.endpointHost ?? undefined)
const egressInterface = ref(props.node.egressInterface ?? undefined)
const mtu = ref<number | undefined>(props.node.mtu ?? undefined)
const saving = ref(false)

/** Whether this save is one the fleet has to be told about, which is what the warning turns on. */
const changesBundle = computed(() =>
  (egressInterface.value ?? null) !== (props.node.egressInterface ?? null)
  || (mtu.value ?? null) !== (props.node.mtu ?? null))

async function save() {
  const trimmed = name.value.trim()
  if (!trimmed) {
    toast.add({ title: 'Name is required', color: 'error', icon: 'i-lucide-circle-alert' })
    return
  }

  saving.value = true

  try {
    await api.put<Node>(`/nodes/${props.node.id}`, {
      name: trimmed,
      endpointHost: endpointHost.value?.trim() || null,
      egressInterface: egressInterface.value?.trim() || null,
      mtu: mtu.value ?? null
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
    :title="`Settings · ${node.name}`"
    description="What this node runs that the rest of the fleet does not. Leave a field empty to follow the fleet."
    @update:open="(open: boolean) => !open && emit('close', false)"
  >
    <template #body>
      <div class="flex flex-col gap-5">
        <UFormField
          label="Name"
          description="The panel's own label for this server. Changing it reissues nothing."
        >
          <UInput
            v-model="name"
            class="w-full"
            autofocus
          />
        </UFormField>

        <UFormField
          label="Endpoint host"
          description="A note for a node you give a record of its own. It is not written into any client config - the fleet endpoint is."
        >
          <UInput
            v-model="endpointHost"
            placeholder="ams.vpn.example.com"
            class="w-full"
          />
        </UFormField>

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

        <UAlert
          v-if="changesBundle"
          color="warning"
          variant="subtle"
          icon="i-lucide-triangle-alert"
          title="This node will restart its interface"
          description="The MTU and the egress interface travel in the bundle, so saving advances the fleet
            revision. Every node re-applies its configuration, and this one brings its interface down and
            back up once - awg syncconf cannot change an interface-level setting. Tunnels reconnect
            on their own; no config is reissued."
        />
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
