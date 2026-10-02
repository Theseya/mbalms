import { useCallback, useEffect, useRef, useState } from 'react'
import { ApiError } from '../api/client'

export interface LoadState<T> {
  data: T | undefined
  error: ApiError | null
  loading: boolean
  reload: () => void
}

/** Loads data with the given function and reloads whenever `key` changes. */
export function useLoad<T>(load: () => Promise<T>, key: string): LoadState<T> {
  const [data, setData] = useState<T>()
  const [error, setError] = useState<ApiError | null>(null)
  const [loading, setLoading] = useState(true)
  const [version, setVersion] = useState(0)
  const loadRef = useRef(load)
  useEffect(() => {
    loadRef.current = load
  })

  useEffect(() => {
    let cancelled = false
    setLoading(true)
    loadRef.current()
      .then((result) => {
        if (!cancelled) {
          setData(result)
          setError(null)
        }
      })
      .catch((e: unknown) => {
        if (!cancelled) setError(e instanceof ApiError ? e : new ApiError(0, 'server_error'))
      })
      .finally(() => {
        if (!cancelled) setLoading(false)
      })
    return () => {
      cancelled = true
    }
  }, [key, version])

  const reload = useCallback(() => setVersion((v) => v + 1), [])
  return { data, error, loading, reload }
}

export function toApiError(e: unknown): ApiError {
  return e instanceof ApiError ? e : new ApiError(0, 'server_error')
}
