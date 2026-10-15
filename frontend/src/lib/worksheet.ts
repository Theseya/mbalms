export type Parsed = { ok: true; value: number | null } | { ok: false; error: string }

const DECIMAL = /^\d+([.,]\d+)?$/

function parseDecimal(raw: string, max: number, emptyError: string | null): Parsed {
  const s = raw.trim()
  if (s === '') return emptyError ? { ok: false, error: emptyError } : { ok: true, value: null }
  if (!DECIMAL.test(s)) return { ok: false, error: 'invalid' }
  const [, decimals = ''] = s.split(/[.,]/)
  const value = Number(s.replace(',', '.'))
  if (value > max) return { ok: false, error: 'range' }
  if (decimals.length > 2) return { ok: false, error: 'too_many_decimals' }
  return { ok: true, value }
}

/** Score 0–10 with up to two decimals; empty is "not filled in", never 0. */
export const parseScore = (raw: string) => parseDecimal(raw, 10, null)

/** Weight or threshold in percent, up to two decimals. */
export const parsePercent = (raw: string, required: boolean) => parseDecimal(raw, 100, required ? 'required' : null)

export function parseWhole(raw: string, max: number | null, maxError = 'range'): Parsed {
  const s = raw.trim()
  if (s === '') return { ok: true, value: null }
  if (!/^\d+$/.test(s)) return { ok: false, error: 'integer' }
  const value = Number(s)
  if (max !== null && value > max) return { ok: false, error: maxError }
  return { ok: true, value }
}
