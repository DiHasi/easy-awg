<script setup lang="ts">
import type { EnrollmentToken, Node } from '~/types/api'

definePageMeta({ middleware: 'auth' })

const api = useControlApi()
const toast = useToast()

const nodes = ref<Node[]>([])
const loading = ref(true)
const errorMessage = ref<string | null>(null)
const actionId = ref<string | null>(null)

const addOpen = ref(false)
const newNodeName = ref('')
const creating = ref(false)
const issuedToken = ref<EnrollmentToken | null>(null)

let refreshTimer: ReturnType<typeof setInterval> | null = null

const outOfSyncCount = computed(() => nodes.value.filter(node => !node.inSync && !node.revoked).length)
const healthyCount = computed(() => nodes.value.filter(node => node.status === 'healthy').length)

async function loadNodes() {
  try {
    nodes.value = await api.get<Node[]>('/nodes')
    errorMessage.value = null
  } catch (error) {
    errorMessage.value = describeError(error, 'Failed to load nodes.')
  } finally {
    loading.value = false
  }
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
      description: describeError(error, undefined as unknown as string),
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
  toast.add({ title: 'Copied', icon: 'i-lucide-check', color: 'success' })
}

function closeAddDialog() {
  addOpen.value = false
  // The token is shown once and never retrievable, so clear it rather than leave it on screen.
  issuedToken.value = null
  newNodeName.value = ''
}

async function revokeNode(node: Node) {
  if (!confirm(`Revoke access for "${node.name}"? It stops receiving configuration immediately, but keeps serving traffic until you remove it.`)) {
    return
  }

  actionId.value = node.id

  try {
    await api.post(`/nodes/${node.id}/revoke`)
    await loadNodes()
    toast.add({ title: 'Node revoked', icon: 'i-lucide-check', color: 'success' })
  } catch (error) {
    toast.add({ title: 'Could not revoke', description: describeError(error, ''), color: 'error', icon: 'i-lucide-circle-alert' })
  } finally {
    actionId.value = null
  }
}

async function deleteNode(node: Node) {
  if (!confirm(`Remove "${node.name}" from the fleet? This only forgets it here - stop the agent on the server itself as well.`)) {
    return
  }

  actionId.value = node.id

  try {
    await api.del(`/nodes/${node.id}`)
    await loadNodes()
    toast.add({ title: 'Node removed', icon: 'i-lucide-check', color: 'success' })
  } catch (error) {
    toast.add({ title: 'Could not remove', description: describeError(error, ''), color: 'error', icon: 'i-lucide-circle-alert' })
  } finally {
    actionId.value = null
  }
}

function statusColor(node: Node) {
  if (node.revoked) {
    return 'neutral'
  }

  return ({
    healthy: 'success',
    degraded: 'warning',
    down: 'error',
    provisioning: 'info',
    retired: 'neutral'
  } as const)[node.status] ?? 'neutral'
}

function relativeTime(value?: string | null) {
  if (!value) {
    return 'never'
  }

  const seconds = Math.max(0, Math.round((Date.now() - new Date(value).getTime()) / 1000))
  if (seconds < 60) {
    return `${seconds}s ago`
  }
  if (seconds < 3600) {
    return `${Math.round(seconds / 60)}m ago`
  }
  if (seconds < 86400) {
    return `${Math.round(seconds / 3600)}h ago`
  }
  return `${Math.round(seconds / 86400)}d ago`
}

onMounted(async () => {
  await loadNodes()
  refreshTimer = setInterval(loadNodes, 10000)
})

onBeforeUnmount(() => {
  if (refreshTimer) {
    clearInterval(refreshTimer)
  }
})
</script>

<template>
  <UContainer class="py-6">
    <div class="flex flex-col gap-6">
      <section class="flex flex-col gap-4 md:flex-row md:items-end md:justify-between">
        <div class="min-w-0">
          <h1 class="text-2xl font-semibold tracking-tight text-highlighted">
            Nodes
          </h1>
          <p class="mt-1 text-sm text-muted">
            {{ nodes.length }} total · {{ healthyCount }} healthy
            <span v-if="outOfSyncCount"> · {{ outOfSyncCount }} out of sync</span>
          </p>
        </div>

        <div class="flex flex-wrap gap-2">
          <UButton
            icon="i-lucide-refresh-cw"
            color="neutral"
            variant="subtle"
            :loading="loading"
            @click="loadNodes"
          >
            Refresh
          </UButton>
          <UButton
            icon="i-lucide-plus"
            @click="addOpen = true"
          >
            Add node
          </UButton>
        </div>
      </section>

      <UAlert
        v-if="errorMessage"
        color="error"
        variant="subtle"
        icon="i-lucide-circle-alert"
        title="Could not load nodes"
        :description="errorMessage"
      />

      <div class="overflow-hidden rounded-lg border border-default bg-default">
        <div
          v-if="loading"
          class="flex min-h-48 items-center justify-center text-sm text-muted"
        >
          <UIcon
            name="i-lucide-loader-circle"
            class="mr-2 size-5 animate-spin"
          />
          Loading nodes
        </div>

        <div
          v-else-if="nodes.length === 0"
          class="flex min-h-48 flex-col items-center justify-center gap-4 px-6 py-10 text-center"
        >
          <div class="flex size-12 items-center justify-center rounded-full bg-muted">
            <UIcon
              name="i-lucide-server"
              class="size-6 text-muted"
            />
          </div>
          <div>
            <h2 class="text-base font-medium text-highlighted">
              No nodes yet
            </h2>
            <p class="mt-1 max-w-md text-sm text-muted">
              Add a node to get an install command. Every node runs the same fleet identity, so
              clients keep working when you switch between them.
            </p>
          </div>
          <UButton
            icon="i-lucide-plus"
            @click="addOpen = true"
          >
            Add node
          </UButton>
        </div>

        <div
          v-else
          class="divide-y divide-default"
        >
          <article
            v-for="node in nodes"
            :key="node.id"
            class="flex flex-col gap-3 p-4 md:flex-row md:items-center md:justify-between"
          >
            <div class="min-w-0">
              <div class="flex flex-wrap items-center gap-2">
                <h2 class="text-base font-medium text-highlighted">
                  {{ node.name }}
                </h2>
                <UBadge
                  :color="statusColor(node)"
                  variant="subtle"
                >
                  {{ node.revoked ? 'revoked' : node.status }}
                </UBadge>
                <UBadge
                  v-if="!node.inSync && !node.revoked"
                  color="warning"
                  variant="subtle"
                  icon="i-lucide-refresh-cw"
                >
                  rev {{ node.appliedRevision }} of {{ node.fleetRevision }}
                </UBadge>
              </div>

              <p class="mt-1 text-sm text-muted">
                <span v-if="node.hostname">{{ node.hostname }} · </span>
                seen {{ relativeTime(node.lastSeenAt) }}
                <span v-if="node.backend"> · {{ node.backend }}</span>
                <span v-if="node.agentVersion"> · agent {{ node.agentVersion }}</span>
              </p>

              <p
                v-if="node.lastError"
                class="mt-1 text-sm text-error"
              >
                {{ node.lastError }}
              </p>
            </div>

            <div class="flex shrink-0 gap-2">
              <UButton
                v-if="!node.revoked"
                icon="i-lucide-ban"
                color="warning"
                variant="subtle"
                :loading="actionId === node.id"
                @click="revokeNode(node)"
              >
                Revoke
              </UButton>
              <UButton
                icon="i-lucide-trash-2"
                color="error"
                variant="subtle"
                :loading="actionId === node.id"
                @click="deleteNode(node)"
              >
                Remove
              </UButton>
            </div>
          </article>
        </div>
      </div>
    </div>

    <UModal
      v-model:open="addOpen"
      title="Add a node"
      @close="closeAddDialog"
    >
      <template #body>
        <div
          v-if="!issuedToken"
          class="flex flex-col gap-4"
        >
          <p class="text-sm text-muted">
            Name the node, then run the generated command on that server. The agent enrolls
            itself and pulls the fleet configuration.
          </p>

          <UFormField label="Node name">
            <UInput
              v-model="newNodeName"
              placeholder="helsinki-1"
              class="w-full"
              @keyup.enter="issueToken"
            />
          </UFormField>
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
            description="This token is not stored in readable form and cannot be shown again. Copy it now; it expires in 30 minutes."
          />

          <UFormField label="Run this on the new server">
            <div class="rounded-md border border-default bg-muted/40 p-3">
              <code class="block break-all font-mono text-xs text-default">{{ issuedToken.installCommand }}</code>
            </div>
          </UFormField>

          <UButton
            icon="i-lucide-copy"
            color="neutral"
            variant="subtle"
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
            color="neutral"
            variant="ghost"
            @click="closeAddDialog"
          >
            {{ issuedToken ? 'Done' : 'Cancel' }}
          </UButton>
          <UButton
            v-if="!issuedToken"
            :loading="creating"
            @click="issueToken"
          >
            Generate command
          </UButton>
        </div>
      </template>
    </UModal>
  </UContainer>
</template>
