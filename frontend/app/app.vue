<script setup lang="ts">
useHead({
  meta: [{ name: 'viewport', content: 'width=device-width, initial-scale=1' }],
  link: [{ rel: 'icon', href: '/favicon.ico' }],
  htmlAttrs: { lang: 'en' }
})

useSeoMeta({
  title: 'AWG Easy',
  description: 'Control plane for an AmneziaWG fleet'
})
</script>

<template>
  <UApp :toaster="{ position: 'bottom-right' }">
    <!-- Login and share links opt out with `layout: false` and draw their own sheet. -->
    <NuxtLayout>
      <NuxtPage />
    </NuxtLayout>
  </UApp>
</template>
