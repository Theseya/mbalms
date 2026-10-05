export type FieldErrors = Record<string, string[]>

export class ApiError extends Error {
  readonly status: number
  readonly code: string
  readonly fieldErrors: FieldErrors

  constructor(status: number, code: string, fieldErrors: FieldErrors = {}) {
    super(code)
    this.status = status
    this.code = code
    this.fieldErrors = fieldErrors
  }
}

/** Must not exceed Paging.MaxPageSize on the server. */
const MAX_PAGE_SIZE = 200

let csrfToken: string | null = null
let onUnauthorized: (() => void) | null = null

export function setUnauthorizedHandler(handler: (() => void) | null) {
  onUnauthorized = handler
}

/** The antiforgery token is bound to the user, so it must be refreshed after login/logout. */
export async function refreshCsrf(): Promise<void> {
  const res = await fetch('/api/auth/csrf', { credentials: 'same-origin' })
  if (!res.ok) throw new ApiError(res.status, 'server_error')
  csrfToken = ((await res.json()) as { token: string }).token
}

async function toError(res: Response): Promise<ApiError> {
  try {
    const body = (await res.json()) as { code?: string; errors?: FieldErrors }
    return new ApiError(res.status, body.code ?? statusCode(res.status), body.errors ?? {})
  } catch {
    return new ApiError(res.status, statusCode(res.status))
  }
}

function statusCode(status: number): string {
  if (status === 401) return 'unauthorized'
  if (status === 403) return 'forbidden'
  if (status === 404) return 'not_found'
  if (status === 429) return 'too_many_requests'
  return 'server_error'
}

async function request(method: string, url: string, body?: unknown): Promise<Response> {
  const unsafe = method !== 'GET'
  if (unsafe && !csrfToken) await refreshCsrf()

  const send = () => {
    const headers: Record<string, string> = { Accept: 'application/json' }
    if (body !== undefined) headers['Content-Type'] = 'application/json'
    if (unsafe && csrfToken) headers['X-XSRF-TOKEN'] = csrfToken
    return fetch(url, {
      method,
      headers,
      credentials: 'same-origin',
      body: body === undefined ? undefined : JSON.stringify(body),
    })
  }

  let res: Response
  try {
    res = await send()
  } catch {
    throw new ApiError(0, 'network_error')
  }

  if (unsafe && res.status === 400) {
    const error = await toError(res.clone())
    if (error.code === 'csrf_failed') {
      await refreshCsrf()
      res = await send()
    }
  }

  if (res.status === 401 && onUnauthorized && !url.startsWith('/api/auth/')) onUnauthorized()
  if (!res.ok) throw await toError(res)
  return res
}

async function json<T>(res: Response): Promise<T> {
  if (res.status === 204) return undefined as T
  return (await res.json()) as T
}

export const api = {
  get: async <T>(url: string) => json<T>(await request('GET', url)),
  /** Loads every page of a paged list, for selects that need the complete list. */
  getAll: async <T>(url: string): Promise<T[]> => {
    const items: T[] = []
    const sep = url.includes('?') ? '&' : '?'
    for (let page = 1; ; page++) {
      const res = await json<{ items: T[]; total: number }>(
        await request('GET', `${url}${sep}page=${page}&pageSize=${MAX_PAGE_SIZE}`))
      items.push(...res.items)
      if (res.items.length === 0 || items.length >= res.total) return items
    }
  },
  post: async <T>(url: string, body: unknown = {}) => json<T>(await request('POST', url, body)),
  put: async <T>(url: string, body: unknown) => json<T>(await request('PUT', url, body)),
  del: async (url: string) => {
    await request('DELETE', url)
  },
  /** Downloads a file (e.g. an .xlsx export) using the browser's save dialog. */
  download: async (url: string) => {
    const res = await request('GET', url)
    const disposition = res.headers.get('Content-Disposition') ?? ''
    const match = /filename\*=UTF-8''([^;]+)|filename="?([^";]+)"?/i.exec(disposition)
    const fileName = decodeURIComponent(match?.[1] ?? match?.[2] ?? 'export.xlsx')
    const blob = await res.blob()
    const href = URL.createObjectURL(blob)
    const a = document.createElement('a')
    a.href = href
    a.download = fileName
    document.body.appendChild(a)
    a.click()
    a.remove()
    URL.revokeObjectURL(href)
  },
}

export function query(params: Record<string, string | number | boolean | null | undefined>): string {
  const search = new URLSearchParams()
  for (const [key, value] of Object.entries(params)) {
    if (value !== undefined && value !== null && value !== '') search.set(key, String(value))
  }
  const s = search.toString()
  return s ? `?${s}` : ''
}
