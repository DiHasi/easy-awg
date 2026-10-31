<script setup lang="ts">
import type { Client, Fleet } from '~/types/api'

/**
 * What one peer routes into the tunnel, and which resolver its config names.
 *
 * Both are written into the config and nowhere else, so this is the one dialog in the panel that
 * changes a file somebody is already holding without changing anything about the fleet: no node is
 * told, nothing is reissued, and the person has to be handed their config again. The dialog says
 * so rather than leaving it to be discovered.
 */
const props = defineProps<{ client: Client | null, fleet: Fleet | null }>()
const open = defineModel<boolean>('open', { default: false })

const emit = defineEmits<{ save: [{ allowedIps: string | null, dns: string | null }] }>()

const saving = ref(false)

// The API models "unset" as null and the inputs want a string; converted at the boundary, like
// everywhere else the two meet.
const allowedIps = ref('')
const dns = ref('')

watch(open, (isOpen) => {
  const client = props.client
  if (!isOpen || !client) {
    return
  }

  saving.value = false
  allowedIps.value = client.allowedIps ?? ''
  dns.value = client.dns ?? ''
})

/** What the fleet would write, which is what a blank field means here. */
const fleetDefaults = computed(() => ({
  allowedIps: props.fleet?.clientAllowedIps ?? '0.0.0.0/0, ::/0',
  dns: props.fleet?.clientDns ?? 'none'
}))

const changed = computed(() =>
  (allowedIps.value.trim() || null) !== (props.client?.allowedIps ?? null)
  || (dns.value.trim() || null) !== (props.client?.dns ?? null))

function save() {
  saving.value = true
  emit('save', {
    allowedIps: allowedIps.value.trim() || null,
    dns: dns.value.trim() || null
  })
}
</script>

<template>
  <UModal
    v-model:open="open"
    :title="client ? `Routing for ${client.name}` : 'Routing'"
    description="What this config sends through the tunnel, and which resolver it uses. Written into the config and nowhere else."
  >
    <template #body>
      <div class="flex flex-col gap-5">
        <UFormField
          label="AllowedIPs"
          :help="`Comma-separated CIDR ranges. Empty uses the fleet value: ${fleetDefaults.allowedIps}`"
        >
          <UInput
            v-model="allowedIps"
            class="w-full font-mono"
            placeholder="0.0.0.0/0, ::/0"
          />
        </UFormField>

        <UFormField
          label="DNS"
          :help="`Resolver addresses, and search domains if you use them. Empty uses the fleet value: ${fleetDefaults.dns}`"
        >
          <UInput
            v-model="dns"
            class="w-full font-mono"
            placeholder="1.1.1.1, 1.0.0.1"
          />
        </UFormField>

        <!-- The part that is easy to miss, and the only consequence of pressing Save: the file in
             somebody's hands does not change because the panel's copy did. -->
        <UAlert
          color="warning"
          variant="subtle"
          icon="i-lucide-info"
          title="The person has to be handed the config again"
          description="This changes what the panel renders, not what any node runs. Nothing is reissued and no tunnel drops — and the config already installed keeps the routes it was imported with until it is replaced."
        />
      </div>
    </template>

    <template #footer>
      <div class="flex w-full justify-end gap-2">
        <UButton
          color="neutral"
          variant="ghost"
          @click="open = false"
        >
          Cancel
        </UButton>
        <UButton
          :loading="saving"
          :disabled="!changed"
          @click="save"
        >
          Save
        </UButton>
      </div>
    </template>
  </UModal>
</template>
