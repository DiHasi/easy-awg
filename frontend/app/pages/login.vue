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
  <LooseSheet
    drawing="fleet control"
    :rows="[
      { label: 'Drawing', value: 'access' },
      { label: 'Sheet', value: '0 of 4' }
    ]"
  >
    <h1 class="caps text-sm text-highlighted">
      Sign in
    </h1>
    <p class="mt-1 text-[13px] text-muted">
      To read and change the fleet.
    </p>

    <form
      class="mt-5 flex flex-col gap-4"
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
        color="primary"
        variant="solid"
        block
        :loading="submitting"
      >
        Sign in
      </UButton>
    </form>
  </LooseSheet>
</template>
