<script setup lang="ts">
import type { EgressLabel, Node } from '~/types/api'

/**
 * Which source addresses this node hands out, and what they are called.
 *
 * The action sits on the node because the addresses do: a provider gives one server a second
 * address, and only that server knows it has it. So the rows are what the agent reported and
 * nothing else - SNAT to an address this host does not carry leaves that peer with no traffic at
 * all, which is why there is no field to type an address into.
 *
 * A label and not a pin: a peer asks for "clean" and the node it lands on resolves that to an
 * address of its own, so the choice survives failover. The default is the other half, and stating
 * it is what makes "change the address for everybody" a thing done here rather than with `ip addr`
 * on the server.
 */
const props = defineProps<{
  node: Node
}>()

const emit = defineEmits<{ close: [boolean] }>()

const api = useControlApi()
const toast = useToast()

type Row = {
  address: string
  label: string | undefined
  observed: string | null
  kernelDefault: boolean
  /** False for an address that is in the plan but no longer in what the node reports. */
  reported: boolean
}

const labelled = new Map(props.node.egressPlan.labels.map(entry => [entry.address, entry.label]))

/**
 * Every address this node is known to have: what it reported, plus anything the plan still names.
 * A plan entry the node has stopped reporting has to stay visible - it is the one row an operator
 * needs in order to clear it.
 */
const rows = ref<Row[]>([
  ...props.node.egressAddresses.map(address => ({
    address: address.address,
    label: labelled.get(address.address),
    observed: address.observed ?? null,
    kernelDefault: address.kernelDefault,
    reported: true
  })),
  ...[...labelled.keys(), props.node.egressPlan.defaultAddress]
    .filter((address): address is string =>
      !!address && !props.node.egressAddresses.some(reported => reported.address === address))
    .map(address => ({
      address,
      label: labelled.get(address),
      observed: null,
      kernelDefault: false,
      reported: false
    }))
])

const defaultAddress = ref(props.node.egressPlan.defaultAddress ?? '')
const saving = ref(false)

// No item for "whatever the kernel picks", for the same reason as beside `egressChoices`: a
// select item with an empty value throws instead of rendering, so unset is the placeholder and
// clearing it is the button next to it.
const defaultChoices = computed(() => rows.value.map(row => ({ label: row.address, value: row.address })))

const missing = computed(() => rows.value.filter(row => !row.reported).map(row => row.address))

/** The address unpinned peers leave with today, which until now was visible nowhere. */
const kernelChoice = computed(() => props.node.egressAddresses.find(address => address.kernelDefault)?.address)

async function save() {
  saving.value = true

  try {
    const labels: EgressLabel[] = rows.value
      .filter(row => row.label?.trim())
      .map(row => ({ address: row.address, label: row.label!.trim() }))

    await api.put<Node>(`/nodes/${props.node.id}/egress`, {
      defaultAddress: defaultAddress.value || null,
      labels
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
    :title="`Egress · ${node.name}`"
    description="Which addresses this node leaves through. Name an address to let a peer ask for it."
    :ui="{ content: 'sm:max-w-2xl' }"
    @update:open="(open: boolean) => !open && emit('close', false)"
  >
    <template #body>
      <div class="flex flex-col gap-5">
        <UAlert
          v-if="rows.length === 0"
          color="neutral"
          variant="subtle"
          icon="i-lucide-info"
          title="This node has not reported its addresses"
          description="Only the node can say which addresses it carries, so there is nothing to name yet. It
            arrives with the next status report; an agent older than this panel never sends it."
        />

        <UAlert
          v-else-if="!node.supportsCurrentSchema"
          color="warning"
          variant="subtle"
          icon="i-lucide-triangle-alert"
          title="This agent is too old to apply an egress plan"
          description="The panel will keep what you save here, and this node will go on letting the kernel
            pick a source address for everybody until its agent is upgraded."
        />

        <UFormField
          v-if="rows.length > 0"
          label="Default address"
          :description="kernelChoice
            ? `What every peer that asked for nothing else leaves with. The kernel currently picks ${kernelChoice}.`
            : 'What every peer that asked for nothing else leaves with.'"
        >
          <div class="flex items-center gap-2">
            <USelect
              v-model="defaultAddress"
              :items="defaultChoices"
              placeholder="Whatever the kernel picks"
              class="w-full font-mono"
            />
            <UButton
              v-if="defaultAddress"
              color="neutral"
              variant="subtle"
              icon="i-lucide-rotate-ccw"
              @click="defaultAddress = ''"
            >
              Clear
            </UButton>
          </div>
        </UFormField>

        <div
          v-if="rows.length > 0"
          class="flex flex-col gap-2"
        >
          <p class="text-sm font-medium">
            Names
          </p>
          <p class="text-xs text-muted">
            A peer picks one of these names, not an address, so it keeps what it was given when
            failover moves it to another node. Leave a row empty and nobody can ask for it.
          </p>

          <div
            v-for="row in rows"
            :key="row.address"
            class="flex flex-wrap items-center gap-2 rounded-lg border border-default p-2"
          >
            <div class="min-w-0 flex-1">
              <div class="flex flex-wrap items-center gap-1.5">
                <span class="font-mono text-[13px]">{{ row.address }}</span>
                <UBadge
                  v-if="row.kernelDefault"
                  size="sm"
                  color="neutral"
                  variant="subtle"
                >
                  kernel picks this
                </UBadge>
                <UBadge
                  v-if="!row.reported"
                  size="sm"
                  color="warning"
                  variant="subtle"
                >
                  not reported
                </UBadge>
              </div>
              <p
                v-if="row.observed && row.observed !== row.address"
                class="mt-0.5 text-xs text-warning"
              >
                seen from outside as {{ row.observed }}
              </p>
              <p
                v-else-if="row.observed"
                class="mt-0.5 text-xs text-muted"
              >
                seen from outside as itself
              </p>
            </div>

            <UInput
              v-model="row.label"
              placeholder="unnamed"
              class="w-48"
            />
          </div>
        </div>

        <p
          v-if="rows.length > 0"
          class="text-xs text-muted"
        >
          Saving advances the fleet revision, so every node re-applies its configuration. No
          interface is taken down and no client config is reissued - a changed address takes effect
          for connections made after it.
        </p>

        <UAlert
          v-if="missing.length > 0"
          color="warning"
          variant="subtle"
          icon="i-lucide-triangle-alert"
          :title="`${missing.join(', ')} is not on this node any more`"
          description="The agent skips a rule for an address its host does not carry, so those peers leave
            through the default. Clear the name to drop it."
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
          :disabled="rows.length === 0"
          @click="save"
        >
          Save
        </UButton>
      </div>
    </template>
  </UModal>
</template>
