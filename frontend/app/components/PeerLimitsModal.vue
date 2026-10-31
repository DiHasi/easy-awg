<script setup lang="ts">
import type { Client } from '~/types/api'

/**
 * When this config stops working on its own, and how much it may carry before it does.
 *
 * Both halves in one dialog because they answer the same question - how long does this person
 * have - and an operator handing out a trial sets them together. Saving changes nothing a node
 * runs: what the nodes eventually see is the peer disappearing from the bundle when a limit is
 * reached, which is an ordinary disable.
 */
const props = defineProps<{ client: Client | null }>()
const open = defineModel<boolean>('open', { default: false })

const emit = defineEmits<{
  save: [{ expiresAt: string | null, quotaBytes: number | null, quotaWindow: string }]
  reset: []
}>()

const saving = ref(false)

/**
 * The API models "unset" as null and speaks ISO; the inputs want a local datetime string and a
 * number of gigabytes. Converted here rather than in the page, so the page never holds a half of
 * either representation.
 */
const expiry = ref('')
const allowance = ref<number | undefined>(undefined)
const window = ref<'total' | 'monthly'>('total')

const GIGABYTE = 1024 ** 3

const windows = [
  { label: 'In total', value: 'total', description: 'Counted from now and never reset by the panel. A trial.' },
  { label: 'Every month', value: 'monthly', description: 'Counted per calendar month in UTC, and the peer comes back when the next one starts.' }
]

watch(open, (isOpen) => {
  const client = props.client
  if (!isOpen || !client) {
    return
  }

  saving.value = false
  // datetime-local wants the viewer's own clock, with no zone and no seconds.
  expiry.value = client.expiresAt ? toLocalInput(client.expiresAt) : ''
  allowance.value = client.quotaBytes ? Number((client.quotaBytes / GIGABYTE).toFixed(2)) : undefined
  window.value = client.quotaWindow === 'monthly' ? 'monthly' : 'total'
})

function toLocalInput(iso: string) {
  const at = new Date(iso)
  const pad = (value: number) => String(value).padStart(2, '0')
  return `${at.getFullYear()}-${pad(at.getMonth() + 1)}-${pad(at.getDate())}T${pad(at.getHours())}:${pad(at.getMinutes())}`
}

/** What has been used, as a share of the allowance. Null when there is no allowance to divide by. */
const used = computed(() => {
  const client = props.client
  if (!client?.quotaBytes) {
    return null
  }

  return {
    moved: formatBytes(client.quotaUsedBytes),
    left: formatBytes(client.quotaRemaining ?? 0),
    share: Math.min(100, Math.round((client.quotaUsedBytes / client.quotaBytes) * 100)),
    since: client.quotaStartedAt
  }
})

/** Why the panel switched this peer off, in the words of the thing that would put it back. */
const switchedOff = computed(() => {
  const reason = props.client?.disabledBy
  if (reason === 'expiry') {
    return 'This peer is switched off because it passed its expiry. Move the date and it comes straight back on.'
  }

  if (reason === 'quota') {
    return 'This peer is switched off because it used its whole allowance. Raise it, or start it again, and the peer comes back on.'
  }

  return null
})

function save() {
  saving.value = true
  emit('save', {
    // The input is in the viewer's own zone; the panel stores UTC.
    expiresAt: expiry.value ? new Date(expiry.value).toISOString() : null,
    quotaBytes: allowance.value && allowance.value > 0 ? Math.round(allowance.value * GIGABYTE) : null,
    quotaWindow: window.value
  })
}

function clear() {
  expiry.value = ''
  allowance.value = undefined
}
</script>

<template>
  <UModal
    v-model:open="open"
    :title="client ? `Limits for ${client.name}` : 'Limits'"
    description="An expiry and an allowance. Reaching either disables the peer, which reissues nothing: its keys and its address stay exactly as they are."
  >
    <template #body>
      <div class="flex flex-col gap-5">
        <UAlert
          v-if="switchedOff"
          color="warning"
          variant="subtle"
          icon="i-lucide-power-off"
          title="Switched off by its limits"
          :description="switchedOff"
        />

        <UFormField
          label="Expires"
          help="Leave empty for a config that never expires. The panel checks once a minute."
        >
          <div class="flex items-center gap-2">
            <UInput
              v-model="expiry"
              type="datetime-local"
              class="w-full"
            />
            <UButton
              v-if="expiry"
              icon="i-lucide-x"
              color="neutral"
              variant="ghost"
              aria-label="Clear the expiry"
              @click="expiry = ''"
            />
          </div>
        </UFormField>

        <UFormField
          label="Allowance (GB)"
          help="Received and sent together. Leave empty for no allowance."
        >
          <div class="flex items-center gap-2">
            <UInput
              v-model.number="allowance"
              type="number"
              min="0"
              step="0.5"
              class="w-full"
            />
            <UButton
              v-if="allowance"
              icon="i-lucide-x"
              color="neutral"
              variant="ghost"
              aria-label="Clear the allowance"
              @click="allowance = undefined"
            />
          </div>
        </UFormField>

        <URadioGroup
          v-model="window"
          :items="windows"
          :disabled="!allowance"
          legend="Counted"
        />

        <!-- What the allowance has actually been used for, which is the number an operator opens
             this dialog to read as often as to change. -->
        <div
          v-if="used"
          class="flex flex-col gap-2 rounded-lg border border-default p-3"
        >
          <div class="flex items-center justify-between text-sm">
            <span class="text-toned">{{ used.moved }} used · {{ used.left }} left</span>
            <UButton
              size="xs"
              color="neutral"
              variant="outline"
              icon="i-lucide-rotate-ccw"
              @click="emit('reset')"
            >
              Start again
            </UButton>
          </div>
          <UProgress
            :model-value="used.share"
            :color="used.share >= 100 ? 'error' : used.share >= 80 ? 'warning' : 'primary'"
          />
          <p
            v-if="used.since"
            class="text-xs text-muted"
          >
            Counted since {{ formatUtc(used.since) }}.
          </p>
        </div>
      </div>
    </template>

    <template #footer>
      <div class="flex w-full items-center justify-end gap-2">
        <UButton
          v-if="client?.expiresAt || client?.quotaBytes"
          color="neutral"
          variant="ghost"
          @click="clear"
        >
          No limits
        </UButton>
        <UButton
          color="neutral"
          variant="ghost"
          @click="open = false"
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
