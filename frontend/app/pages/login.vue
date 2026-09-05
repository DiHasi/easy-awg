<script setup lang="ts">
definePageMeta({ layout: false })

const route = useRoute()
const { login } = useSession()

const username = ref('')
const password = ref('')
const submitting = ref(false)
const errorMessage = ref<string | null>(null)

async function submit() {
  if (!username.value || !password.value) {
    errorMessage.value = 'Enter your username and password.'
    return
  }

  submitting.value = true
  errorMessage.value = null

  try {
    await login(username.value, password.value)
    const redirect = typeof route.query.redirect === 'string' ? route.query.redirect : '/'
    await navigateTo(redirect)
  } catch {
    // Deliberately vague: distinguishing "no such user" from "wrong password" only helps
    // someone probing for valid usernames.
    errorMessage.value = 'Those credentials were not accepted.'
  } finally {
    submitting.value = false
  }
}
</script>

<template>
  <div class="flex min-h-screen items-center justify-center bg-muted/30 px-4">
    <UCard class="w-full max-w-sm">
      <template #header>
        <div class="flex items-center gap-3">
          <UIcon
            name="i-lucide-shield"
            class="size-6 text-primary"
          />
          <div>
            <h1 class="text-base font-semibold text-highlighted">
              AWG Easy
            </h1>
            <p class="text-xs text-muted">
              Sign in to manage the fleet
            </p>
          </div>
        </div>
      </template>

      <form
        class="flex flex-col gap-4"
        @submit.prevent="submit"
      >
        <UFormField label="Username">
          <UInput
            v-model="username"
            autocomplete="username"
            autofocus
            class="w-full"
          />
        </UFormField>

        <UFormField label="Password">
          <UInput
            v-model="password"
            type="password"
            autocomplete="current-password"
            class="w-full"
          />
        </UFormField>

        <UAlert
          v-if="errorMessage"
          color="error"
          variant="subtle"
          icon="i-lucide-circle-alert"
          :description="errorMessage"
        />

        <UButton
          type="submit"
          block
          :loading="submitting"
        >
          Sign in
        </UButton>
      </form>
    </UCard>
  </div>
</template>
