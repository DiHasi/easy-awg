import { defineVitestConfig } from '@nuxt/test-utils/config'

/**
 * The frontend had no automated tests at all, and lint plus typecheck cannot see the things that
 * actually went wrong here: a share that reads 0% where there is no evidence, a null the API sends
 * where a form holds undefined, an arrangement that reorders itself.
 *
 * `environment: 'nuxt'` because the composables under test are written the way the rest of the app
 * is - auto-imported `ref` and `computed`, `~/` aliases - and a plain Node environment would mean
 * testing a rewritten copy of them rather than the code that ships.
 */
export default defineVitestConfig({
  test: {
    environment: 'nuxt',
    include: ['test/**/*.spec.ts']
  }
})
