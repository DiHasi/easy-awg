import type { Session } from '~/types/api'

/**
 * Who is signed in. Kept in shared state so the header and the route guard agree without
 * each re-asking the server.
 */
export function useSession() {
  const session = useState<Session | null>('awg-session', () => null)
  const checked = useState<boolean>('awg-session-checked', () => false)
  const api = useControlApi()

  async function refresh() {
    try {
      session.value = await api.get<Session>('/auth/me')
    } catch {
      session.value = null
    } finally {
      checked.value = true
    }

    return session.value
  }

  async function ensure() {
    return checked.value ? session.value : await refresh()
  }

  async function login(username: string, password: string) {
    await api.post('/auth/login', { username, password })
    await refresh()
  }

  async function logout() {
    try {
      await api.post('/auth/logout')
    } finally {
      session.value = null
      checked.value = true
      await navigateTo('/login')
    }
  }

  const user = computed(() => session.value?.username ?? null)

  /** The read-only account. Every name, address and key it is shown is a stand-in. */
  const isDemo = computed(() => session.value?.demo === true)

  return { session, user, isDemo, checked, refresh, ensure, login, logout }
}
