/**
 * Who is signed in. Kept in shared state so the header and the route guard agree without
 * each re-asking the server.
 */
export function useSession() {
  const user = useState<string | null>('awg-session-user', () => null)
  const checked = useState<boolean>('awg-session-checked', () => false)
  const api = useControlApi()

  async function refresh() {
    try {
      const me = await api.get<{ status: string }>('/auth/me')
      user.value = me.status
    } catch {
      user.value = null
    } finally {
      checked.value = true
    }

    return user.value
  }

  async function ensure() {
    return checked.value ? user.value : await refresh()
  }

  async function login(username: string, password: string) {
    await api.post('/auth/login', { username, password })
    await refresh()
  }

  async function logout() {
    try {
      await api.post('/auth/logout')
    } finally {
      user.value = null
      checked.value = true
      await navigateTo('/login')
    }
  }

  return { user, checked, refresh, ensure, login, logout }
}
