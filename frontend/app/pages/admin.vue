<script setup lang="ts">
import type { Admin, Fleet, FleetRestore, FleetRotation, GeneratedKey, ImportResult, ServerObfuscationProfile } from '~/types/api'

definePageMeta({ middleware: 'auth' })

const api = useControlApi()
const toast = useToast()
const confirm = useConfirm()
const { fleet, refresh } = useFleetState()

const loading = ref(true)
const saving = ref(false)
const errorMessage = ref<string | null>(null)

const savingMtu = ref(false)
const mtuForm = ref<number>(1280)

const generatingKey = ref(false)
const importing = ref(false)
const importResult = ref<ImportResult | null>(null)
const replaceExisting = ref(false)

const rotateOpen = ref(false)
const rotating = ref(false)
const reissueClientKeys = ref(false)
const rotation = ref<FleetRotation | null>(null)

/**
 * Who can sign in. Loaded alongside the fleet rather than polled: the list changes when this page
 * changes it and at no other time, and a poll would fight whatever dialog is open.
 */
const admins = ref<Admin[]>([])
const adminsError = ref<string | null>(null)
const savingAdmin = ref(false)

const createAdminOpen = ref(false)
const newAdmin = reactive({ username: '', password: '' })

const passwordOpen = ref(false)
const passwordForm = reactive({ current: '', next: '', repeat: '' })

// Which account's password is being reset by another operator, or null while nobody's is.
const resetTarget = ref<Admin | null>(null)
const resetForm = reactive({ password: '', repeat: '' })

/** The panel's own rule, repeated here only so the form can say so before a round trip. */
const minimumPasswordLength = 12

async function loadAdmins() {
  try {
    admins.value = await api.get<Admin[]>('/admins')
    adminsError.value = null
  } catch (error) {
    adminsError.value = describeError(error, 'Failed to load the operator list.')
  }
}

function failed(title: string) {
  return (error: unknown) => toast.add({
    title,
    description: describeError(error, ''),
    color: 'error',
    icon: 'i-lucide-circle-alert'
  })
}

async function createAdmin() {
  savingAdmin.value = true

  try {
    await api.post('/admins', { username: newAdmin.username.trim(), password: newAdmin.password })
    createAdminOpen.value = false
    newAdmin.username = ''
    newAdmin.password = ''
    await loadAdmins()
    toast.add({ title: 'Operator added', color: 'success', icon: 'i-lucide-user-plus' })
  } catch (error) {
    failed('Could not add the operator')(error)
  } finally {
    savingAdmin.value = false
  }
}

async function changeOwnPassword() {
  savingAdmin.value = true

  try {
    await api.post('/admins/password', { currentPassword: passwordForm.current, newPassword: passwordForm.next })
    passwordOpen.value = false
    passwordForm.current = ''
    passwordForm.next = ''
    passwordForm.repeat = ''
    toast.add({
      title: 'Password changed',
      // Said plainly rather than left to be discovered: the cookie carries a name, not anything
      // derived from the password, so sessions that were already open are still open.
      description: 'Sessions that are already signed in stay signed in, this one included.',
      color: 'success',
      icon: 'i-lucide-key'
    })
  } catch (error) {
    failed('Could not change the password')(error)
  } finally {
    savingAdmin.value = false
  }
}

async function resetPassword() {
  const target = resetTarget.value
  if (!target) {
    return
  }

  savingAdmin.value = true

  try {
    await api.put(`/admins/${encodeURIComponent(target.username)}/password`, { password: resetForm.password })
    resetTarget.value = null
    resetForm.password = ''
    resetForm.repeat = ''
    toast.add({ title: `Password reset for ${target.username}`, color: 'success', icon: 'i-lucide-key' })
  } catch (error) {
    failed('Could not reset the password')(error)
  } finally {
    savingAdmin.value = false
  }
}

async function deleteAdmin(account: Admin) {
  const ok = await confirm({
    title: `Remove ${account.username}?`,
    description: account.demo
      ? 'The read-only demo account. If AWG_DEMO_USER is still set, the panel creates it again on its next start.'
      : 'That account can no longer sign in. Nothing about the fleet changes.',
    confirmLabel: 'Remove',
    danger: true
  })

  if (!ok) {
    return
  }

  try {
    await api.del(`/admins/${encodeURIComponent(account.username)}`)
    await loadAdmins()
    toast.add({ title: `${account.username} removed`, color: 'success', icon: 'i-lucide-check' })
  } catch (error) {
    failed('Could not remove the account')(error)
  }
}

/**
 * The API models "unset" as null, but form inputs only understand undefined. Keep the boundary
 * conversion in one place rather than sprinkling `?? undefined` through the template.
 */
type ObfuscationForm = {
  [K in keyof ServerObfuscationProfile]: NonNullable<ServerObfuscationProfile[K]> | undefined
}

const form = reactive<ObfuscationForm>({})

function intoForm(profile: ServerObfuscationProfile | null | undefined) {
  for (const [key, value] of Object.entries(profile ?? {})) {
    form[key as keyof ObfuscationForm] = (value ?? undefined) as never
  }
}

const numericFields = [
  { key: 's1', label: 'S1' },
  { key: 's2', label: 'S2' },
  { key: 's3', label: 'S3' },
  { key: 's4', label: 'S4' }
] as const

const headerFields = [
  { key: 'h1', label: 'H1' },
  { key: 'h2', label: 'H2' },
  { key: 'h3', label: 'H3' },
  { key: 'h4', label: 'H4' }
] as const

const clientDefaultFields = [
  { key: 'defaultI1', label: 'I1' },
  { key: 'defaultI2', label: 'I2' },
  { key: 'defaultI3', label: 'I3' },
  { key: 'defaultI4', label: 'I4' },
  { key: 'defaultI5', label: 'I5' }
] as const

/**
 * AmneziaWG 3.x tunables. Each takes a single number or an inclusive `lo-hi` range that the peer
 * re-rolls per use, which is the point: a fixed rekey interval is itself a fingerprint. The hints
 * carry the stock WireGuard value so it is clear what a blank field leaves in place.
 */
const tuningFields = [
  { key: 'defaultContentPaddingAddition', label: 'ContentPaddingAddition', hint: 'Extra random payload bytes. Off by default.' },
  { key: 'defaultRekeyAfterTime', label: 'RekeyAfterTime', hint: 'Seconds between re-handshakes. WireGuard uses 120.' },
  { key: 'defaultRekeyTimeout', label: 'RekeyTimeout', hint: 'Seconds between handshake retries. WireGuard uses 5.' },
  { key: 'defaultRejectAfterTime', label: 'RejectAfterTime', hint: 'Seconds before a key is abandoned. Must stay above RekeyAfterTime. WireGuard uses 180.' },
  { key: 'defaultKeepaliveTimeout', label: 'KeepaliveTimeout', hint: 'Seconds of silence before a keepalive. WireGuard uses 10.' },
  { key: 'defaultMaxHandshakeAttempts', label: 'MaxHandshakeAttempts', hint: 'Handshake attempts before giving up. WireGuard uses 18.' },
  { key: 'defaultPersistentKeepalive', label: 'PersistentKeepalive', hint: 'Seconds, written into the client [Peer]. Defaults to 25.' }
] as const

// Read once: the sheet frame polls the fleet, and a poll must not overwrite a half-edited form.
async function loadFleet() {
  try {
    fleet.value = await api.get<Fleet>('/fleet')
    intoForm(fleet.value.obfuscation)
    mtuForm.value = fleet.value.tunnelMtu
    errorMessage.value = null
  } catch (error) {
    errorMessage.value = describeError(error, 'Failed to load fleet settings.')
  } finally {
    loading.value = false
  }
}

async function saveMtu() {
  savingMtu.value = true

  try {
    fleet.value = await api.put<Fleet>('/fleet/mtu', { mtu: mtuForm.value })
    mtuForm.value = fleet.value.tunnelMtu
    void refresh()
    toast.add({
      title: 'Tunnel MTU saved',
      description: `Fleet is now at revision ${fleet.value.revision}. Each node restarts its interface once as it picks this up.`,
      color: 'success',
      icon: 'i-lucide-check'
    })
  } catch (error) {
    toast.add({
      title: 'Could not save the MTU',
      description: describeError(error, ''),
      color: 'error',
      icon: 'i-lucide-circle-alert'
    })
  } finally {
    savingMtu.value = false
  }
}

async function copyPublicKey() {
  if (fleet.value) {
    await navigator.clipboard.writeText(fleet.value.serverPublicKey)
    toast.add({ title: 'Key copied', color: 'success', icon: 'i-lucide-check' })
  }
}

/**
 * Replaces the fleet identity. The one action in the panel that disconnects every client at once,
 * so it is behind its own dialog that says so, and the result is kept on the page afterwards: it
 * is the list of people who have to be handed a new config before they are back online.
 */
async function rotateIdentity() {
  rotating.value = true

  try {
    rotation.value = await api.post<FleetRotation>('/fleet/rotate', { reissueClientKeys: reissueClientKeys.value })
    rotateOpen.value = false
    await loadFleet()
    void refresh()
    toast.add({
      title: `Fleet is on generation ${rotation.value.generation}`,
      description: `${rotation.value.clients.length} config(s) have to be handed out again. Nodes converge on their next poll.`,
      color: 'success',
      icon: 'i-lucide-refresh-cw'
    })
  } catch (error) {
    toast.add({
      title: 'Could not rotate the identity',
      description: describeError(error, ''),
      color: 'error',
      icon: 'i-lucide-circle-alert'
    })
  } finally {
    rotating.value = false
  }
}

async function generateHeaderProtectionKey() {
  generatingKey.value = true

  try {
    const generated = await api.post<GeneratedKey>('/fleet/header-protection-key')
    form.headerProtectionKey = generated.key
    toast.add({
      title: 'Key generated',
      description: 'Save the profile to roll it out. Every client config has to be handed out again.',
      color: 'success',
      icon: 'i-lucide-key'
    })
  } catch (error) {
    toast.add({
      title: 'Could not generate a key',
      description: describeError(error, ''),
      color: 'error',
      icon: 'i-lucide-circle-alert'
    })
  } finally {
    generatingKey.value = false
  }
}

async function saveObfuscation() {
  saving.value = true

  try {
    fleet.value = await api.put<Fleet>('/fleet/obfuscation', form)
    void refresh()
    toast.add({
      title: 'Obfuscation saved',
      description: `Fleet is now at revision ${fleet.value.revision}. Nodes pick it up on their next poll.`,
      color: 'success',
      icon: 'i-lucide-check'
    })
  } catch (error) {
    toast.add({
      title: 'Could not save',
      description: describeError(error, ''),
      color: 'error',
      icon: 'i-lucide-circle-alert'
    })
  } finally {
    saving.value = false
  }
}

const exporting = ref(false)
const restoring = ref(false)
const restoreResult = ref<FleetRestore | null>(null)
const replaceOnRestore = ref(false)

/**
 * The panel handing over a copy of itself. Downloaded through fetch rather than a plain link so
 * the session cookie goes with it and a failure can be shown as a failure, and the filename the
 * panel chose is kept: it names the fleet and the moment, which is the only way a drawer full of
 * these can be told apart.
 */
async function exportPanel() {
  exporting.value = true

  try {
    const response = await fetch(api.url('/fleet/export'), { credentials: 'include' })
    if (!response.ok) {
      throw new Error(`HTTP ${response.status}`)
    }

    const suggested = /filename="?([^";]+)"?/.exec(response.headers.get('content-disposition') ?? '')?.[1]
    const url = URL.createObjectURL(await response.blob())
    const link = document.createElement('a')
    link.href = url
    link.download = suggested ?? 'awg-panel-export.json'
    link.click()
    URL.revokeObjectURL(url)

    toast.add({
      title: 'Export downloaded',
      description: 'It carries the fleet private key and every client key in plaintext. Keep it where you would keep the panel’s own disk.',
      color: 'success',
      icon: 'i-lucide-download'
    })
  } catch (error) {
    failed('Could not export the panel')(error)
  } finally {
    exporting.value = false
  }
}

/**
 * And the way back. Behind a confirmation because it is wholesale: nothing is merged, so running
 * one on a panel that is already serving replaces that fleet and every config it ever issued.
 */
async function restorePanel(event: Event) {
  const input = event.target as HTMLInputElement
  const file = input.files?.[0]
  if (!file) {
    return
  }

  input.value = ''

  const peers = fleet.value?.clientsCount ?? 0
  const ok = await confirm({
    title: 'Restore this panel from the export?',
    description: peers > 0
      ? `This panel holds ${peers} peer(s) right now. A restore replaces them with whatever is in the file, and every config this panel issued stops working at once.`
      : 'The fleet identity, the peers and their keys, the nodes and the operators all come from the file. The agents reconnect on their next poll.',
    confirmLabel: 'Restore',
    danger: true
  })

  if (!ok) {
    return
  }

  restoring.value = true
  restoreResult.value = null

  try {
    const text = await file.text()
    restoreResult.value = await api.postRaw<FleetRestore>(
      `/fleet/restore?replace=${replaceOnRestore.value || peers > 0}`,
      text
    )

    await loadFleet()
    await loadAdmins()
    void refresh()
    toast.add({
      title: `Restored generation ${restoreResult.value.generation}`,
      description: `${restoreResult.value.peersRestored} peer(s) and ${restoreResult.value.nodesRestored} node(s) are back.`,
      color: 'success',
      icon: 'i-lucide-archive-restore'
    })
  } catch (error) {
    failed('Could not restore the panel')(error)
  } finally {
    restoring.value = false
  }
}

async function importLegacyState(event: Event) {
  const input = event.target as HTMLInputElement
  const file = input.files?.[0]
  if (!file) {
    return
  }

  importing.value = true
  importResult.value = null

  try {
    const text = await file.text()
    importResult.value = await api.postRaw<ImportResult>(
      `/fleet/import?replace=${replaceExisting.value}`,
      text
    )

    await loadFleet()
    void refresh()
    toast.add({
      title: `Imported ${importResult.value.clientsImported} client(s)`,
      color: 'success',
      icon: 'i-lucide-check'
    })
  } catch (error) {
    toast.add({
      title: 'Import failed',
      description: describeError(error, ''),
      color: 'error',
      icon: 'i-lucide-circle-alert'
    })
  } finally {
    importing.value = false
    input.value = ''
  }
}

onMounted(() => {
  void loadFleet()
  void loadAdmins()
})
</script>

<template>
  <div class="flex flex-col gap-4 lg:gap-6">
    <h1 class="sr-only">
      Settings
    </h1>

    <UAlert
      v-if="errorMessage"
      color="error"
      variant="subtle"
      icon="i-lucide-circle-alert"
      title="Could not load the fleet"
      :description="errorMessage"
    />

    <AppCard
      title="Fleet identity"
      icon="i-lucide-fingerprint"
      description="Shared by every node and pinned by every client config. Rotated only when a node has been compromised."
    >
      <template #actions>
        <UButton
          v-if="fleet"
          icon="i-lucide-copy"
          color="neutral"
          variant="outline"
          @click="copyPublicKey"
        >
          Copy public key
        </UButton>
        <UButton
          v-if="fleet"
          icon="i-lucide-refresh-cw"
          color="error"
          variant="outline"
          @click="rotateOpen = true"
        >
          Rotate identity
        </UButton>
      </template>

      <div
        v-if="loading"
        class="flex items-center gap-2 text-sm text-muted"
      >
        <UIcon
          name="i-lucide-loader-circle"
          class="size-5 animate-spin"
        />
        Loading
      </div>

      <dl
        v-else-if="fleet"
        class="grid grid-cols-2 gap-x-6 gap-y-4 md:grid-cols-4"
      >
        <SpecItem
          label="Server public key"
          mono
          class="col-span-2"
        >
          <span class="break-all">{{ fleet.serverPublicKey }}</span>
        </SpecItem>
        <SpecItem
          label="Endpoint"
          mono
        >
          {{ fleet.endpointHost }}:{{ fleet.listenPort }}
        </SpecItem>
        <SpecItem
          label="Subnet"
          mono
        >
          {{ fleet.subnet }}
        </SpecItem>
        <SpecItem
          label="Client allowed IPs"
          mono
        >
          {{ fleet.clientAllowedIps }}
        </SpecItem>
        <SpecItem
          label="Client DNS"
          mono
        >
          {{ fleet.clientDns ?? '—' }}
        </SpecItem>
        <SpecItem
          label="Revision"
          mono
        >
          r{{ fleet.revision }} · generation {{ fleet.generation }}
        </SpecItem>
        <SpecItem label="Issued">
          {{ fleet.clientsCount }} peers · {{ fleet.nodesCount }} nodes
        </SpecItem>
      </dl>

      <!-- Left on the page after a rotation rather than only in a toast: this is the work that
           remains, and the panel is where it gets ticked off peer by peer. -->
      <UAlert
        v-if="rotation"
        class="mt-4"
        color="warning"
        variant="subtle"
        icon="i-lucide-triangle-alert"
        :title="`Rotated from generation ${rotation.previousGeneration} to ${rotation.generation} at revision ${rotation.revision}`"
      >
        <template #description>
          <p>
            Every node applies the new identity on its next poll. Nothing a client already holds
            works until it is handed out again: {{ rotation.clients.length }} config(s),
            {{ rotation.shareLinksRevoked }} share link(s) revoked,
            {{ rotation.probesReissued }} probe(s) re-keyed on their own.
            {{ rotation.clientKeysReissued
              ? 'Client key pairs were reissued as well.'
              : 'Client key pairs were left alone.' }}
          </p>
          <p class="mt-2">
            {{ rotation.warning }}
          </p>
          <p class="mt-2">
            <ULink to="/#peers">
              Go to the peers
            </ULink>
            — the ones still on an older generation are marked there.
          </p>
        </template>
      </UAlert>

      <UAlert
        v-else-if="fleet && fleet.staleConfigsCount > 0"
        class="mt-4"
        color="warning"
        variant="subtle"
        icon="i-lucide-triangle-alert"
        :title="`${fleet.staleConfigsCount} peer(s) are not on generation ${fleet.generation}`"
        description="Their configs were issued against an identity this fleet no longer runs, so they connect to nothing. They are marked on the peer list."
      />
    </AppCard>

    <UModal
      v-model:open="rotateOpen"
      title="Rotate the fleet identity"
      description="For a node you have to assume was read. Every client is disconnected until they are handed a new config."
    >
      <template #body>
        <div class="flex flex-col gap-5">
          <p class="text-sm text-muted">
            Every node runs the same key pair, the same preshared keys and the same wire format —
            which is what makes failover invisible, and what makes one node's disk the whole
            fleet's secrets. Rotating draws a new server key pair, a new preshared key for every
            peer and a fresh wire format (S1–S4, H1–H4 and the header protection key), and moves
            the fleet to the next generation in one step.
          </p>

          <ul class="flex flex-col gap-1.5 text-sm text-toned">
            <li class="flex gap-2">
              <UIcon
                name="i-lucide-check"
                class="mt-0.5 size-4 shrink-0 text-success"
              />
              Nodes converge on their next poll, without restarting their interfaces.
            </li>
            <li class="flex gap-2">
              <UIcon
                name="i-lucide-x"
                class="mt-0.5 size-4 shrink-0 text-error"
              />
              Every config in the field stops working at once, and share links stop resolving.
            </li>
            <li class="flex gap-2">
              <UIcon
                name="i-lucide-minus"
                class="mt-0.5 size-4 shrink-0 text-muted"
              />
              The bundle signing key is left alone, so no node has to be re-enrolled.
            </li>
            <li class="flex gap-2">
              <UIcon
                name="i-lucide-x"
                class="mt-0.5 size-4 shrink-0 text-error"
              />
              It does not take the compromised server off the air. Nodes are fail-static: one this
              panel stops configuring re-applies its cached bundle and keeps serving the old
              configs. Power that host down as well.
            </li>
          </ul>

          <USwitch
            v-model="reissueClientKeys"
            label="Also reissue every client key pair"
            description="A node never held one, so a read node does not expose them. Turn this on if the panel's own database may have been read too."
          />
        </div>
      </template>

      <template #footer>
        <div class="flex w-full justify-end gap-2">
          <UButton
            color="neutral"
            variant="ghost"
            @click="rotateOpen = false"
          >
            Cancel
          </UButton>
          <UButton
            color="error"
            icon="i-lucide-refresh-cw"
            :loading="rotating"
            @click="rotateIdentity"
          >
            Rotate identity
          </UButton>
        </div>
      </template>
    </UModal>

    <AppCard
      title="Operators"
      icon="i-lucide-users"
      description="Who can sign in to this panel. The password here is what stands between the open internet and the fleet private key."
    >
      <template #actions>
        <UButton
          icon="i-lucide-key"
          color="neutral"
          variant="outline"
          @click="passwordOpen = true"
        >
          Change my password
        </UButton>
        <UButton
          icon="i-lucide-user-plus"
          @click="createAdminOpen = true"
        >
          Add operator
        </UButton>
      </template>

      <UAlert
        v-if="adminsError"
        class="mb-4"
        color="error"
        variant="subtle"
        icon="i-lucide-circle-alert"
        title="Could not load the operators"
        :description="adminsError"
      />

      <ul class="divide-y divide-default">
        <li
          v-for="account in admins"
          :key="account.username"
          class="flex flex-wrap items-center gap-x-3 gap-y-2 py-3 first:pt-0 last:pb-0"
        >
          <UIcon
            :name="account.demo ? 'i-lucide-eye' : 'i-lucide-user'"
            class="size-4 shrink-0 text-muted"
          />
          <span class="font-medium text-highlighted">{{ account.username }}</span>
          <UBadge
            v-if="account.self"
            color="primary"
            variant="subtle"
            size="sm"
          >
            You
          </UBadge>
          <UBadge
            v-if="account.demo"
            color="neutral"
            variant="subtle"
            size="sm"
          >
            Read-only
          </UBadge>
          <span class="text-sm text-muted">added {{ formatUtc(account.createdAt) }}</span>

          <div class="ms-auto flex items-center gap-1">
            <UButton
              v-if="!account.self"
              icon="i-lucide-key-round"
              color="neutral"
              variant="ghost"
              size="sm"
              :title="`Reset the password for ${account.username}`"
              @click="resetTarget = account"
            />
            <UButton
              v-if="!account.self"
              icon="i-lucide-trash-2"
              color="error"
              variant="ghost"
              size="sm"
              :title="`Remove ${account.username}`"
              @click="deleteAdmin(account)"
            />
          </div>
        </li>
      </ul>

      <!-- The one thing an operator cannot discover from the list itself, and the reason a second
           account is worth creating before it is needed. -->
      <p class="mt-4 text-sm text-muted">
        A forgotten password can only be reset by another operator: the bootstrap variables create
        an account on an empty database and are ignored afterwards. Sign-in is refused after ten
        wrong passwords for one account within fifteen minutes, counted in memory — a restart
        clears them.
      </p>
    </AppCard>

    <UModal
      v-model:open="createAdminOpen"
      title="Add an operator"
      description="A full account: it can change everything about the fleet, including removing this one."
    >
      <template #body>
        <div class="flex flex-col gap-4">
          <UFormField label="Username">
            <UInput
              v-model="newAdmin.username"
              class="w-full"
              autocomplete="off"
            />
          </UFormField>
          <UFormField
            label="Password"
            :help="`At least ${minimumPasswordLength} characters.`"
          >
            <UInput
              v-model="newAdmin.password"
              class="w-full"
              type="password"
              autocomplete="new-password"
            />
          </UFormField>
        </div>
      </template>

      <template #footer>
        <div class="flex w-full justify-end gap-2">
          <UButton
            variant="ghost"
            @click="createAdminOpen = false"
          >
            Cancel
          </UButton>
          <UButton
            :loading="savingAdmin"
            :disabled="!newAdmin.username.trim() || newAdmin.password.length < minimumPasswordLength"
            @click="createAdmin"
          >
            Add operator
          </UButton>
        </div>
      </template>
    </UModal>

    <UModal
      v-model:open="passwordOpen"
      title="Change my password"
      description="The current password is required: a session on its own is not proof of knowing it."
    >
      <template #body>
        <div class="flex flex-col gap-4">
          <UFormField label="Current password">
            <UInput
              v-model="passwordForm.current"
              class="w-full"
              type="password"
              autocomplete="current-password"
            />
          </UFormField>
          <UFormField
            label="New password"
            :help="`At least ${minimumPasswordLength} characters.`"
          >
            <UInput
              v-model="passwordForm.next"
              class="w-full"
              type="password"
              autocomplete="new-password"
            />
          </UFormField>
          <UFormField label="Repeat the new password">
            <UInput
              v-model="passwordForm.repeat"
              class="w-full"
              type="password"
              autocomplete="new-password"
            />
          </UFormField>
        </div>
      </template>

      <template #footer>
        <div class="flex w-full justify-end gap-2">
          <UButton
            variant="ghost"
            @click="passwordOpen = false"
          >
            Cancel
          </UButton>
          <UButton
            :loading="savingAdmin"
            :disabled="passwordForm.next.length < minimumPasswordLength || passwordForm.next !== passwordForm.repeat"
            @click="changeOwnPassword"
          >
            Change password
          </UButton>
        </div>
      </template>
    </UModal>

    <UModal
      :open="resetTarget !== null"
      :title="`Reset the password for ${resetTarget?.username ?? ''}`"
      description="Set a new one and hand it over. Their old password stops working at once; a session they already have stays open until it expires."
      @update:open="(open: boolean) => !open && (resetTarget = null)"
    >
      <template #body>
        <div class="flex flex-col gap-4">
          <UFormField
            label="New password"
            :help="`At least ${minimumPasswordLength} characters.`"
          >
            <UInput
              v-model="resetForm.password"
              class="w-full"
              type="password"
              autocomplete="new-password"
            />
          </UFormField>
          <UFormField label="Repeat it">
            <UInput
              v-model="resetForm.repeat"
              class="w-full"
              type="password"
              autocomplete="new-password"
            />
          </UFormField>
        </div>
      </template>

      <template #footer>
        <div class="flex w-full justify-end gap-2">
          <UButton
            variant="ghost"
            @click="resetTarget = null"
          >
            Cancel
          </UButton>
          <UButton
            :loading="savingAdmin"
            :disabled="resetForm.password.length < minimumPasswordLength || resetForm.password !== resetForm.repeat"
            @click="resetPassword"
          >
            Reset password
          </UButton>
        </div>
      </template>
    </UModal>

    <AppCard
      title="Tunnel MTU"
      icon="i-lucide-ruler"
      description="Written into every client config and applied by every node. Saving bumps the fleet revision."
    >
      <div class="flex flex-col gap-4 sm:flex-row sm:items-start sm:gap-8">
        <UFormField
          label="MTU"
          :ui="paramField"
          help="1280 is AmneziaWG's recommendation for the 3.x line, and the IPv6 minimum."
        >
          <UInput
            v-model.number="mtuForm"
            type="number"
            :min="1280"
            :max="1420"
            class="w-32"
          />
        </UFormField>
        <p class="max-w-prose text-sm text-muted">
          AmneziaWG 3.x adds bytes to every transport packet — S4, ContentPaddingAddition and
          RandomTrailers, which 3.1 turns on by default. The 1420 awg-quick works out on its own
          does not account for them, so the outer packet crosses 1500 and fragments, and plenty of
          mobile carriers and home routers drop fragments. Configs already handed out keep their
          current MTU until they are downloaded again.
        </p>
      </div>

      <template #footer>
        <div class="flex flex-wrap items-center justify-end gap-3">
          <span class="text-sm text-muted">Each node restarts its interface once.</span>
          <UButton
            icon="i-lucide-save"
            :loading="savingMtu"
            :disabled="loading"
            @click="saveMtu"
          >
            Save MTU
          </UButton>
        </div>
      </template>
    </AppCard>

    <AppCard
      title="Obfuscation"
      icon="i-lucide-shield-ellipsis"
      description="Saving bumps the fleet revision; nodes pick it up on their next poll."
      sticky-footer
    >
      <div class="grid gap-4 lg:grid-cols-2">
        <section class="flex flex-col gap-4 rounded-lg border border-default p-4">
          <header class="flex items-start gap-3">
            <span class="flex size-8 shrink-0 items-center justify-center rounded-md bg-error/10 text-error">
              <UIcon
                name="i-lucide-lock"
                class="size-4"
              />
            </span>
            <div>
              <h3 class="font-semibold text-highlighted">
                Wire format — must match on both ends
              </h3>
              <p class="text-sm text-muted">
                Applied to every node and copied into every client config. A mismatch is a
                handshake that never happens, so changing these means handing out every config again.
              </p>
            </div>
          </header>

          <div class="grid grid-cols-2 gap-3 sm:grid-cols-4">
            <UFormField
              v-for="field in numericFields"
              :key="field.key"
              :ui="paramField"
              :label="field.label"
            >
              <UInput
                v-model.number="form[field.key]"
                type="number"
                class="w-full"
              />
            </UFormField>
          </div>

          <div class="grid grid-cols-1 gap-3 sm:grid-cols-2">
            <UFormField
              v-for="field in headerFields"
              :key="field.key"
              :ui="paramField"
              :label="field.label"
              help="Number or range"
            >
              <UInput
                v-model="form[field.key]"
                class="w-full"
                placeholder="1000000-1000500"
              />
            </UFormField>
          </div>

          <UFormField
            :ui="paramField"
            label="HeaderProtectionKey"
            help="AmneziaWG 3.x. Encrypts the packet header. Needs S1–S4 of at least 12."
          >
            <UFieldGroup class="w-full">
              <UInput
                v-model="form.headerProtectionKey"
                class="w-full"
                placeholder="base64, 32 bytes"
              />
              <UButton
                color="neutral"
                variant="outline"
                icon="i-lucide-key-round"
                :loading="generatingKey"
                @click="generateHeaderProtectionKey"
              >
                Generate
              </UButton>
            </UFieldGroup>
          </UFormField>

          <USwitch
            v-model="form.randomTrailers"
            label="RandomTrailers"
            description="AmneziaWG 3.x. Appends random trailing bytes to every packet."
          />
        </section>

        <section class="flex flex-col gap-4 rounded-lg border border-default p-4">
          <header class="flex items-start gap-3">
            <span class="flex size-8 shrink-0 items-center justify-center rounded-md bg-primary/10 text-primary">
              <UIcon
                name="i-lucide-shuffle"
                class="size-4"
              />
            </span>
            <div>
              <h3 class="font-semibold text-highlighted">
                Client defaults — may differ per client
              </h3>
              <p class="text-sm text-muted">
                Seeds for new configs; any peer can override them. Differing is the point: two
                clients that rekey on the same schedule are a correlatable pair.
              </p>
            </div>
          </header>

          <div class="grid grid-cols-3 gap-3">
            <UFormField
              :ui="paramField"
              label="Jc"
            >
              <UInput
                v-model.number="form.defaultJc"
                type="number"
                class="w-full"
              />
            </UFormField>
            <UFormField
              :ui="paramField"
              label="Jmin"
            >
              <UInput
                v-model.number="form.defaultJmin"
                type="number"
                class="w-full"
              />
            </UFormField>
            <UFormField
              :ui="paramField"
              label="Jmax"
            >
              <UInput
                v-model.number="form.defaultJmax"
                type="number"
                class="w-full"
              />
            </UFormField>
          </div>

          <div class="grid grid-cols-1 gap-3 sm:grid-cols-2">
            <UFormField
              v-for="field in clientDefaultFields"
              :key="field.key"
              :ui="paramField"
              :label="field.label"
            >
              <UInput
                v-model="form[field.key]"
                class="w-full"
                placeholder="<b 0x...>"
              />
            </UFormField>
          </div>

          <div class="grid grid-cols-1 gap-3 sm:grid-cols-2">
            <UFormField
              v-for="field in tuningFields"
              :key="field.key"
              :ui="paramField"
              :label="field.label"
              :help="field.hint"
            >
              <UInput
                v-model="form[field.key]"
                class="w-full"
                placeholder="140 or 120-160"
              />
            </UFormField>
          </div>

          <USwitch
            v-model="form.defaultDisableCookies"
            label="DisableCookies"
            description="AmneziaWG 3.x. Stops the peer answering with a cookie reply under load."
          />
        </section>
      </div>

      <template #footer>
        <div class="flex flex-wrap items-center justify-end gap-3">
          <span class="text-sm text-muted">Rolls out to every node on their next poll.</span>
          <UButton
            icon="i-lucide-save"
            :loading="saving"
            :disabled="loading"
            @click="saveObfuscation"
          >
            Save obfuscation
          </UButton>
        </div>
      </template>
    </AppCard>

    <AppCard
      title="Export and restore"
      icon="i-lucide-archive"
      description="A copy of the panel to keep somewhere else. The one thing in this deployment that cannot be rebuilt is in it."
    >
      <template #actions>
        <UButton
          icon="i-lucide-download"
          color="neutral"
          variant="outline"
          :loading="exporting"
          @click="exportPanel"
        >
          Download export
        </UButton>
      </template>

      <p class="text-sm text-muted">
        The export holds the fleet identity, every peer with its own key pair, the groups and their
        arrangement, each node’s own settings, the probes and the operators — everything a panel
        publishes, and nothing it merely observed. A
        <ULink to="/#panels">
          standby
        </ULink>
        covers losing this host, but it follows a mistake as faithfully as anything else: a
        rotation nobody meant to run reaches it within one poll. This is the copy that does not.
      </p>

      <UAlert
        class="mt-4"
        color="warning"
        variant="subtle"
        icon="i-lucide-triangle-alert"
        title="It is the database, in plaintext"
        description="The fleet private key and every client private key are in the file. Keep it exactly where you would keep this panel’s own disk — anyone holding it can stand up a server every config in the field already trusts."
      />

      <div class="mt-4 flex flex-col gap-4 border-t border-default pt-4 sm:flex-row sm:items-center sm:gap-8">
        <USwitch
          v-model="replaceOnRestore"
          label="Replace what is here"
          description="Required when this panel already has peers of its own."
        />

        <label
          class="inline-flex w-fit cursor-pointer items-center gap-2 rounded-md border border-accented px-3 py-2 text-sm font-medium text-default transition-colors hover:bg-elevated focus-within:outline-2 focus-within:outline-offset-2 focus-within:outline-primary"
          :class="restoring ? 'pointer-events-none opacity-60' : ''"
        >
          <UIcon
            :name="restoring ? 'i-lucide-loader-circle' : 'i-lucide-archive-restore'"
            class="size-4"
            :class="restoring ? 'animate-spin' : ''"
          />
          {{ restoring ? 'Restoring…' : 'Restore from an export' }}
          <input
            type="file"
            accept="application/json,.json"
            class="sr-only"
            :disabled="restoring"
            @change="restorePanel"
          >
        </label>
      </div>

      <!-- Kept on the page rather than only in a toast, like a rotation: what remains to be done
           is the part an operator reads after the panel says it worked. -->
      <UAlert
        v-if="restoreResult"
        class="mt-4"
        color="warning"
        variant="subtle"
        icon="i-lucide-triangle-alert"
        :title="`Restored generation ${restoreResult.generation} at revision ${restoreResult.revision}, in charge at term ${restoreResult.panelTerm}`"
      >
        <template #description>
          <p>
            {{ restoreResult.peersRestored }} peer(s), {{ restoreResult.nodesRestored }} node(s),
            {{ restoreResult.probesRestored }} probe(s) and {{ restoreResult.operatorsRestored }}
            operator account(s) are back.
            {{ restoreResult.peersReplaced > 0
              ? `${restoreResult.peersReplaced} peer(s) this panel held before are gone.`
              : '' }}
          </p>
          <p class="mt-2">
            {{ restoreResult.warning }}
          </p>
        </template>
      </UAlert>
    </AppCard>

    <AppCard
      title="Import"
      icon="i-lucide-upload"
      description="Adopt a single-server deployment. Its key pair is preserved, so configs already handed out keep working."
    >
      <div class="flex flex-col gap-4 sm:flex-row sm:items-center sm:gap-8">
        <USwitch
          v-model="replaceExisting"
          label="Replace existing clients"
          description="Required if this panel already has clients."
        />

        <label
          class="inline-flex w-fit cursor-pointer items-center gap-2 rounded-md border border-accented px-3 py-2 text-sm font-medium text-default transition-colors hover:bg-elevated focus-within:outline-2 focus-within:outline-offset-2 focus-within:outline-primary"
          :class="importing ? 'pointer-events-none opacity-60' : ''"
        >
          <UIcon
            :name="importing ? 'i-lucide-loader-circle' : 'i-lucide-file-up'"
            class="size-4"
            :class="importing ? 'animate-spin' : ''"
          />
          {{ importing ? 'Importing…' : 'Choose state.json' }}
          <input
            type="file"
            accept="application/json,.json"
            class="sr-only"
            :disabled="importing"
            @change="importLegacyState"
          >
        </label>
      </div>

      <UAlert
        v-if="importResult"
        class="mt-4"
        :color="importResult.warnings.length ? 'warning' : 'success'"
        variant="subtle"
        :icon="importResult.warnings.length ? 'i-lucide-triangle-alert' : 'i-lucide-check'"
        :title="`Imported ${importResult.clientsImported} client(s) at revision ${importResult.revision}`"
      >
        <template
          v-if="importResult.warnings.length"
          #description
        >
          <ul class="list-inside list-disc">
            <li
              v-for="warning in importResult.warnings"
              :key="warning"
            >
              {{ warning }}
            </li>
          </ul>
        </template>
      </UAlert>
    </AppCard>
  </div>
</template>
