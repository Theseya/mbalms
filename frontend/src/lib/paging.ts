import { useEffect, useState } from 'react'

export const PAGE_SIZE = 25

/** Every word of the search must occur in one of the values (case-insensitive), like the server search. */
export function matchesSearch(search: string, ...values: (string | null | undefined)[]): boolean {
  const terms = search.toLocaleLowerCase().split(/\s+/).filter(Boolean)
  if (!terms.length) return true
  const text = values.filter(Boolean).join(' ').toLocaleLowerCase()
  return terms.every((term) => text.includes(term))
}

/** Client-side page of a short list; an out-of-range page (e.g. after a deletion) falls back to the last one. */
export function pageOf<T>(items: T[], page: number, size = PAGE_SIZE): { items: T[]; page: number } {
  const last = Math.max(1, Math.ceil(items.length / size))
  const current = Math.min(Math.max(1, page), last)
  return { items: items.slice((current - 1) * size, current * size), page: current }
}

export function useDebounced<T>(value: T, delay = 300): T {
  const [debounced, setDebounced] = useState(value)
  useEffect(() => {
    const id = setTimeout(() => setDebounced(value), delay)
    return () => clearTimeout(id)
  }, [value, delay])
  return debounced
}
