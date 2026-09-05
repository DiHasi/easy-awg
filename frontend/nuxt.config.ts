// https://nuxt.com/docs/api/configuration/nuxt-config
export default defineNuxtConfig({
  // The generated frontend is hosted by ASP.NET as a client-side application.
  // Keep the HTML shell route-agnostic so dynamic URLs such as /share/:token
  // can be served through MapFallbackToFile without hydrating the home page.

  modules: [
    '@nuxt/eslint',
    '@nuxt/ui'
  ],
  ssr: false,

  devtools: {
    enabled: true
  },

  css: ['~/assets/css/main.css'],

  runtimeConfig: {
    public: {
      apiBase: ''
    }
  },

  routeRules: {
    '/': { prerender: true }
  },

  compatibilityDate: '2025-01-15',

  eslint: {
    config: {
      stylistic: {
        commaDangle: 'never',
        braceStyle: '1tbs'
      }
    }
  }
})
