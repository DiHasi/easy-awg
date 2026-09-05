/**
 * Guards the admin pages. Public routes (login, share links) opt out by not using this
 * middleware, so someone holding a share link never sees a login wall.
 */
export default defineNuxtRouteMiddleware(async (to) => {
  const { ensure } = useSession()

  if (await ensure()) {
    return
  }

  return navigateTo({ path: '/login', query: { redirect: to.fullPath } })
})
