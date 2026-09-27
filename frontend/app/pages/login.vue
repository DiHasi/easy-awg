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

useHead({ title: 'Sign in · AWG Easy' })
</script>

<template>
  <div class="flex min-h-screen items-center justify-center px-4 py-10">
    <div class="w-full max-w-sm">
      <div class="mb-6 flex items-center justify-center gap-2.5">
        <span class="flex size-9 items-center justify-center rounded-md bg-inverted text-inverted">
          <UIcon
            name="i-lucide-shield-check"
            class="size-5"
          />
        </span>
        <span class="text-lg font-semibold text-highlighted">AWG Easy</span>
      </div>

      <section class="rounded-xl border border-default bg-default p-6 shadow-xs">
        <h1 class="text-lg font-semibold text-highlighted">
          Sign in
        </h1>
        <p class="mt-1 text-sm text-muted">
          To manage the fleet.
        </p>

        <form
          class="mt-6 flex flex-col gap-4"
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
      </section>
    </div>
  </div>
</template>
