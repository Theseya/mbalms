export const GRADE_MIN = 0
export const GRADE_MAX = 100

export type GradeParseResult = { ok: true; value: number } | { ok: false; error: 'required' | 'integer' | 'range' }

/** Client-side check mirroring the API rule: whole number from 0 to 100. */
export function parseGrade(raw: string): GradeParseResult {
  const text = raw.trim()
  if (text === '') return { ok: false, error: 'required' }
  if (!/^-?\d+$/.test(text)) return { ok: false, error: 'integer' }
  const value = Number(text)
  if (value < GRADE_MIN || value > GRADE_MAX) return { ok: false, error: 'range' }
  return { ok: true, value }
}
