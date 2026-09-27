<script setup lang="ts">
import type { ClientStats, DnsStatus, EnrollmentToken, Node } from '~/types/api'

definePageMeta({ middleware: 'auth' })

const api = useControlApi()
const toast = useToast()
const confirm = useConfirm()
const { fleet, nodes, dns, loaded, serving, refresh } = useFleetState()

const actionId = ref<string | null>(null)
const clientsOnline = ref(0)

const addOpen = ref(false)
const newNodeName = ref('')
const creating = ref(false)
const issuedToken = ref<EnrollmentToken | null>(null)

// The drawing numbers nodes in the order it draws them; the schedule below uses the same numbers.
const designators = computed(() => Object.fromEntries(serving.value.map((node, index) => [node.id, `N${index + 1}`])))

const behind = computed(() => serving.value.find(node => !node.inSync) ?? null)

async function loadOnline() {
  try {
    const items = await api.get<ClientStats[]>('/clients/stats')
    clientsOnline.value = items.filter(item => item.online).length
  } catch {
    // Only feeds a label on the drawing; the next tick will try again.
  }
}

usePolling(loadOnline, 10000)

async function activateNode(node: Node) {
  if (!node.publicIp) {
    toast.add({
      title: 'No address reported yet',
      description: `${node.name} has not told the panel where it is reachable, so there is nothing to point DNS at.`,
      color: 'warning',
      icon: 'i-lucide-circle-alert'
    })
    return
  }

  const record = dns.value ? `${dns.value.recordName} ${dns.value.recordType}` : 'the failover record'
  const ttl = dns.value ? ` Clients move over as resolvers expire the old answer, which takes up to ${dns.value.ttl} s.` : ''
  const confirmed = await confirm({
    title: `Move the record to ${node.name}`,
    description: `Point ${record} at ${node.publicIp}.${ttl} No config is reissued and no node re-applies anything.`,
    confirmLabel: 'Move record'
  })
  if (!confirmed) {
    return
  }

  actionId.value = node.id

  try {
    const status = await api.post<DnsStatus>(`/nodes/${node.id}/activate`)
    await refresh()

    toast.add({
      title: `${node.name} is now active`,
      description: status?.providerConfigured
        ? undefined
        : `Set ${status?.recordName} ${status?.recordType} to ${node.publicIp} at your DNS provider.`,
      icon: 'i-lucide-check',
      color: 'success'
    })
  } catch (error) {
    toast.add({ title: 'Could not switch over', description: describeError(error, ''), color: 'error', icon: 'i-lucide-circle-alert' })
  } finally {
    actionId.value = null
  }
}

async function copyRecord() {
  if (!dns.value?.targetAddress) {
    return
  }

  await navigator.clipboard.writeText(`${dns.value.recordName} ${dns.value.ttl} IN ${dns.value.recordType} ${dns.value.targetAddress}`)
  toast.add({ title: 'Record copied', icon: 'i-lucide-check', color: 'success' })
}

async function issueToken() {
  const name = newNodeName.value.trim()
  if (!name) {
    toast.add({ title: 'Name is required', color: 'error', icon: 'i-lucide-circle-alert' })
    return
  }

  creating.value = true

  try {
    issuedToken.value = await api.post<EnrollmentToken>('/nodes/tokens', {
      name,
      endpointHost: null,
      egressInterface: null,
      mtu: null
    })
    newNodeName.value = ''
  } catch (error) {
    toast.add({
      title: 'Could not issue a token',
      description: describeError(error, ''),
      color: 'error',
      icon: 'i-lucide-circle-alert'
    })
  } finally {
    creating.value = false
  }
}

async function copyInstallCommand() {
  if (!issuedToken.value) {
    return
  }

  await navigator.clipboard.writeText(issuedToken.value.installCommand)
  toast.add({ title: 'Command copied', icon: 'i-lucide-check', color: 'success' })
}

function closeAddDialog() {
  addOpen.value = false
  // The token is shown once and never retrievable, so clear it rather than leave it on screen.
  issuedToken.value = null
  newNodeName.value = ''
  void refresh()
}

async function revokeNode(node: Node) {
  const confirmed = await confirm({
    title: `Revoke ${node.name}`,
    description: 'It stops receiving configuration immediately, but keeps serving traffic on its last bundle until you stop the agent on the server.',
    confirmLabel: 'Revoke',
    danger: true
  })
  if (!confirmed) {
    return
  }

  actionId.value = node.id

  try {
    await api.post(`/nodes/${node.id}/revoke`)
    await refresh()
    toast.add({ title: 'Node revoked', icon: 'i-lucide-check', color: 'success' })
  } catch (error) {
    toast.add({ title: 'Could not revoke', description: describeError(error, ''), color: 'error', icon: 'i-lucide-circle-alert' })
  } finally {
    actionId.value = null
  }
}

async function deleteNode(node: Node) {
  const confirmed = await confirm({
    title: `Remove ${node.name}`,
    description: 'This only forgets the node here. Stop the agent on the server itself as well, or it keeps serving its last configuration.',
    confirmLabel: 'Remove',
    danger: true
  })
  if (!confirmed) {
    return
  }

  actionId.value = node.id

  try {
    await api.del(`/nodes/${node.id}`)
    await refresh()
    toast.add({ title: 'Node removed', icon: 'i-lucide-check', color: 'success' })
  } catch (error) {
    toast.add({ title: 'Could not remove', description: describeError(error, ''), color: 'error', icon: 'i-lucide-circle-alert' })
  } finally {
    actionId.value = null
  }
}
</script>

<template>
  <div class="flex flex-col">
    <SheetSection
      title="Topology"
      :meta="`${serving.length} node(s) · one identity`"
    >
      <template #actions>
        <UButton
          color="primary"
          variant="solid"
          size="sm"
          icon="i-lucide-plus"
          @click="addOpen = true"
        >
          Enroll node
        </UButton>
      </template>

      <FleetSchematic
        :nodes="serving"
        :dns="dns"
        :clients-total="fleet?.clientsCount ?? 0"
        :clients-online="clientsOnline"
        :subnet="fleet?.subnet"
        :listen-port="fleet?.listenPort"
        detail
        callouts
      />
    </SheetSection>

    <!-- notes keyed to the callouts on the drawing -->
    <section
      class="grid gap-x-8 gap-y-3 border-b border-default px-3 py-3 sm:px-4 lg:grid-cols-[auto_1fr_1fr_1fr]"
      aria-label="Notes"
    >
      <h2 class="caps text-highlighted">
        Notes
      </h2>
      <p class="flex gap-2.5 text-[13px] leading-snug text-toned">
        <NoteMark n="1" />
        <span>Every config pins one fleet public key and names <span class="font-mono">{{ dns?.recordName ?? 'the record' }}</span>. Moving the record changes the address a client dials, nothing it can verify.</span>
      </p>
      <p class="flex gap-2.5 text-[13px] leading-snug text-toned">
        <NoteMark n="2" />
        <span v-if="dns?.providerConfigured">The record is edited through {{ dns.provider }} and marked active here only once the provider confirms. A refused edit leaves the current node active and fails loudly.</span>
        <span v-else>No DNS provider is configured: switching records the active node here and leaves the record to you. Set <span class="font-mono">AWG_CLOUDFLARE_API_TOKEN</span> and <span class="font-mono">AWG_CLOUDFLARE_ZONE_ID</span> to have it edited for you.</span>
      </p>
      <p class="flex gap-2.5 text-[13px] leading-snug text-toned">
        <NoteMark n="3" />
        <span v-if="behind"><span class="font-mono">{{ behind.name }}</span> still serves on r{{ behind.appliedRevision }}. A node keeps its cached bundle while it cannot reach the panel, so being behind never takes a tunnel down.</span>
        <span v-else>Every node runs r{{ fleet?.revision ?? '—' }}. A node that loses the panel keeps serving its cached configuration indefinitely.</span>
      </p>
    </section>

    <SheetSection
      v-if="dns"
      title="R1 · Failover record"
      :meta="dns.providerConfigured ? `edited through ${dns.provider}` : 'edited by hand'"
    >
      <template #actions>
        <UButton
          v-if="!dns.providerConfigured && dns.targetAddress"
          icon="i-lucide-copy"
          size="sm"
          @click="copyRecord"
        >
          Copy record
        </UButton>
      </template>

      <dl class="grid grid-cols-2 gap-x-6 gap-y-3 sm:grid-cols-3 xl:grid-cols-6">
        <SpecItem
          label="Record"
          mono
        >
          {{ dns.recordName }} {{ dns.recordType }}
        </SpecItem>
        <SpecItem
          label="TTL"
          mono
        >
          {{ dns.ttl }} s
        </SpecItem>
        <SpecItem
          label="Points at"
          mono
        >
          <span :class="dns.targetAddress ? 'text-live' : 'text-dimmed'">{{ dns.targetAddress ?? 'unset' }}</span>
          <span
            v-if="dns.activeNodeName"
            class="text-muted"
          > · {{ dns.activeNodeName }}</span>
        </SpecItem>
        <SpecItem
          label="Resolves to"
          mono
        >
          {{ dns.resolvedAddresses.join(', ') || 'nothing yet' }}
        </SpecItem>
        <SpecItem label="Check">
          <StateMark
            v-if="dns.targetAddress"
            :state="dns.matches ? 'up' : 'idle'"
            :label="dns.matches ? 'resolving here' : 'not resolving here yet'"
          />
          <span
            v-else
            class="font-mono text-xs text-dimmed"
          >no target</span>
        </SpecItem>
        <SpecItem
          label="Switched"
          mono
        >
          {{ dns.activatedAt ? `${relativeTime(dns.activatedAt)} · ${formatUtc(dns.activatedAt)}` : 'never' }}
        </SpecItem>
      </dl>

      <UAlert
        v-if="dns.warning"
        class="mt-4"
        color="warning"
        variant="subtle"
        icon="i-lucide-triangle-alert"
        :description="dns.warning"
      />
    </SheetSection>

    <SheetSection
      title="Node schedule"
      :meta="`${nodes.length} enrolled`"
      flush
    >
      <p
        v-if="!loaded"
        class="px-4 py-10 text-center font-mono text-xs text-muted"
      >
        reading the fleet…
      </p>

      <div
        v-else-if="nodes.length === 0"
        class="m-3 flex flex-col items-center gap-3 border border-dashed border-default px-6 py-10 text-center sm:m-4"
      >
        <span class="caps text-highlighted">No node enrolled</span>
        <p class="max-w-md text-sm text-muted">
          Enrolling a node gives you a one-line install command. Every node runs the same fleet
          identity, so clients keep working when you switch between them.
        </p>
        <UButton
          color="primary"
          variant="solid"
          icon="i-lucide-plus"
          @click="addOpen = true"
        >
          Enroll node
        </UButton>
      </div>

      <template v-else>
        <article
          v-for="node in nodes"
          :key="node.id"
          class="border-b border-muted px-3 py-3 last:border-b-0 sm:px-4"
          :class="node.revoked ? 'opacity-70' : ''"
        >
          <header class="flex flex-wrap items-baseline gap-x-3 gap-y-1">
            <span class="caps text-highlighted">{{ designators[node.id] ?? '—' }}</span>
            <h3 class="font-mono text-[15px] text-highlighted">
              {{ node.name }}
            </h3>
            <span
              v-if="node.hostname"
              class="font-mono text-xs text-muted"
            >{{ node.hostname }}</span>
            <span
              class="caps"
              :class="toneText[describeNode(node).tone]"
            >{{ describeNode(node).word }}</span>
            <!--
            A node behind the bundle schema is served the older obfuscation profile, so it
            quietly speaks a different wire format from the rest of the fleet. That is worth
            saying out loud rather than leaving it to look healthy and in sync.
          -->
            <span
              v-if="!node.supportsCurrentSchema && !node.revoked"
              class="caps text-warning"
            >agent predates AmneziaWG 3.x</span>
          </header>

          <dl class="mt-2.5 grid grid-cols-2 gap-x-6 gap-y-2.5 sm:grid-cols-4 xl:grid-cols-8">
            <SpecItem
              label="Public IP"
              mono
            >
              <span :class="node.publicIp ? '' : 'text-dimmed'">{{ node.publicIp ?? 'unknown' }}</span>
            </SpecItem>
            <SpecItem
              label="Backend"
              mono
            >
              {{ node.backend ?? '—' }}
            </SpecItem>
            <SpecItem
              label="Agent"
              mono
            >
              {{ node.agentVersion ?? '—' }}
            </SpecItem>
            <SpecItem
              label="Schema"
              mono
            >
              v{{ node.bundleSchemaVersion }}
            </SpecItem>
            <SpecItem
              label="Revision"
              mono
            >
              <span :class="node.inSync ? '' : 'text-warning'">r{{ node.appliedRevision }} of r{{ node.fleetRevision }}</span>
            </SpecItem>
            <SpecItem
              label="Interface"
              mono
            >
              <span :class="node.interfaceUp ? '' : 'text-error'">awg0 {{ node.interfaceUp ? 'up' : 'down' }}</span>
            </SpecItem>
            <SpecItem
              label="Seen"
              mono
            >
              {{ relativeTime(node.lastSeenAt) }}
            </SpecItem>
            <SpecItem
              label="Enrolled"
              mono
            >
              {{ formatUtc(node.enrolledAt).slice(0, 10) }}
            </SpecItem>
          </dl>

          <p
            v-if="node.lastError"
            class="mt-2.5 border-s-2 border-error ps-2.5 font-mono text-xs text-error"
          >
            {{ node.lastError }}
          </p>

          <div class="mt-3 flex flex-wrap gap-2">
            <UButton
              v-if="!node.isActive && !node.revoked"
              color="primary"
              variant="solid"
              size="sm"
              icon="i-lucide-git-branch"
              :disabled="!node.publicIp"
              :loading="actionId === node.id"
              @click="activateNode(node)"
            >
              Make active
            </UButton>
            <UButton
              v-if="!node.revoked"
              color="warning"
              size="sm"
              icon="i-lucide-ban"
              :loading="actionId === node.id"
              @click="revokeNode(node)"
            >
              Revoke
            </UButton>
            <UButton
              color="error"
              variant="ghost"
              size="sm"
              icon="i-lucide-trash-2"
              :loading="actionId === node.id"
              @click="deleteNode(node)"
            >
              Remove
            </UButton>
          </div>
        </article>
      </template>
    </SheetSection>

    <UModal
      v-model:open="addOpen"
      title="Enroll node"
      description="Name the node, then run the generated command on that server. The agent enrolls itself and pulls the fleet configuration."
      @after:leave="issuedToken = null"
    >
      <template #body>
        <UFormField
          v-if="!issuedToken"
          label="Node name"
        >
          <UInput
            v-model="newNodeName"
            placeholder="helsinki-1"
            class="w-full"
            autofocus
            @keyup.enter="issueToken"
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
            :description="`The token is not stored in readable form and cannot be shown again. It expires ${formatUtc(issuedToken.expiresAt)}.`"
          />

          <UFormField label="Run this on the new server">
            <div class="border border-default bg-muted p-3">
              <code class="block break-all font-mono text-xs text-default">{{ issuedToken.installCommand }}</code>
            </div>
          </UFormField>

          <UButton
            icon="i-lucide-copy"
            block
            @click="copyInstallCommand"
          >
            Copy command
          </UButton>
        </div>
      </template>

      <template #footer>
        <div class="flex w-full justify-end gap-2">
          <UButton
            variant="ghost"
            @click="closeAddDialog"
          >
            {{ issuedToken ? 'Done' : 'Cancel' }}
          </UButton>
          <UButton
            v-if="!issuedToken"
            color="primary"
            variant="solid"
            :loading="creating"
            @click="issueToken"
          >
            Generate command
          </UButton>
        </div>
      </template>
    </UModal>
  </div>
</template>
