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
  },

  // Every icon ships inside the bundle. Left alone, the client fetches missing icons from
  // /api/_nuxt_icon, which a static build does not have, and then from api.iconify.design: a
  // self-hosted VPN panel should not call a third party on every page load, and on the networks
  // AmneziaWG exists for that call is exactly the kind that gets blocked.
  icon: {
    provider: 'none',
    fallbackToApi: false,
    clientBundle: {
      scan: true,
      // Nuxt UI's own components use these; the scan only sees this app's source.
      icons: [
        'arrow-down', 'arrow-left', 'arrow-right', 'arrow-up', 'arrow-up-right', 'check',
        'chevron-down', 'chevron-left', 'chevron-right', 'chevron-up', 'chevrons-left',
        'chevrons-right', 'circle-alert', 'circle-check', 'circle-x', 'copy', 'copy-check',
        'ellipsis', 'eye', 'eye-off', 'info', 'loader-circle', 'minus', 'monitor', 'moon',
        'plus', 'search', 'sun', 'triangle-alert', 'upload', 'x'
      ].map(name => `lucide:${name}`)
    }
  }
})
