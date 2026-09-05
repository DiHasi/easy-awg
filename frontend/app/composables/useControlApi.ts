import type { ApiError } from '~/types/api'

/**
 * Thin wrapper over fetch for the control plane API.
 *
 * The session is a HttpOnly cookie, so credentials are included and never touched by script.
 * A 401 anywhere means the session lapsed: rather than let every caller handle that, bounce to
 * the login page once and preserve where the user was going.
 */
export function useControlApi() {
  const config = useRuntimeConfig()
  const route = useRoute()

  const base = computed(() => String(config.public.apiBase || '').replace(/\/$/, ''))

  function url(path: string) {
    return `${base.value}/api${path}`
  }

  async function request<T>(path: string, options: RequestInit = {}): Promise<T> {
    const response = await fetch(url(path), {
      credentials: 'include',
      ...options,
      headers: {
        ...(options.body ? { 'Content-Type': 'application/json' } : {}),
        ...options.headers
      }
    })

    if (response.status === 401 && !path.startsWith('/auth/')) {
      await navigateTo({ path: '/login', query: { redirect: route.fullPath } })
      throw new Error('Session expired')
    }

    if (!response.ok) {
      throw new Error(await describeFailure(response))
    }

    if (response.status === 204) {
      return undefined as T
    }

    return await response.json() as T
  }

  async function describeFailure(response: Response) {
    try {
      const error = await response.json() as ApiError
      return error.message || error.code || `HTTP ${response.status}`
    } catch {
      return `HTTP ${response.status}`
    }
  }

  return {
    url,
    get: <T>(path: string) => request<T>(path),
    post: <T>(path: string, body?: unknown) => request<T>(path, {
      method: 'POST',
      body: body === undefined ? undefined : JSON.stringify(body)
    }),
    postRaw: <T>(path: string, body: string) => request<T>(path, { method: 'POST', body }),
    put: <T>(path: string, body: unknown) => request<T>(path, { method: 'PUT', body: JSON.stringify(body) }),
    del: <T>(path: string) => request<T>(path, { method: 'DELETE' })
  }
}

/** Turns any thrown value into something worth showing a person. */
export function describeError(error: unknown, fallback: string) {
  return error instanceof Error && error.message ? error.message : fallback
}
