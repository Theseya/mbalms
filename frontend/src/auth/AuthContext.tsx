import { createContext, useCallback, useContext, useEffect, useMemo, useState, type ReactNode } from 'react'
import { api, refreshCsrf, setUnauthorizedHandler } from '../api/client'
import type { Me } from '../api/types'
import { setAppTimeZone } from '../lib/format'

interface AuthState {
  /** undefined while the session is being checked. */
  me: Me | null | undefined
  login: (email: string, password: string) => Promise<Me>
  logout: () => Promise<void>
}

const AuthContext = createContext<AuthState | null>(null)

export function AuthProvider({ children }: { children: ReactNode }) {
  const [me, setMe] = useState<Me | null | undefined>(undefined)

  const apply = useCallback((user: Me | null) => {
    if (user) setAppTimeZone(user.timeZone)
    setMe(user)
  }, [])

  useEffect(() => {
    setUnauthorizedHandler(() => apply(null))
    api
      .get<Me>('/api/auth/me')
      .then(apply)
      .catch(() => apply(null))
    return () => setUnauthorizedHandler(null)
  }, [apply])

  const login = useCallback(
    async (email: string, password: string) => {
      await refreshCsrf()
      const user = await api.post<Me>('/api/auth/login', { email, password })
      await refreshCsrf()
      apply(user)
      return user
    },
    [apply],
  )

  const logout = useCallback(async () => {
    try {
      await api.post('/api/auth/logout')
    } finally {
      await refreshCsrf().catch(() => undefined)
      apply(null)
    }
  }, [apply])

  const value = useMemo(() => ({ me, login, logout }), [me, login, logout])
  return <AuthContext.Provider value={value}>{children}</AuthContext.Provider>
}

export function useAuth(): AuthState {
  const ctx = useContext(AuthContext)
  if (!ctx) throw new Error('useAuth must be used inside AuthProvider')
  return ctx
}
