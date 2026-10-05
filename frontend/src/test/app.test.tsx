import { act, render, screen, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { MemoryRouter } from 'react-router-dom'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import type { Grade, GradeHistoryEntry, SurveyQuestion } from '../api/types'
import { AuthProvider } from '../auth/AuthContext'
import { setLanguage } from '../i18n'
import { parseGrade } from '../lib/grades'
import { LoginPage } from '../pages/LoginPage'
import { GradesPage } from '../pages/manager/GradesPage'
import { validateAnswers } from '../lib/surveyValidation'

describe('parseGrade', () => {
  it.each(['0', '55', '100'])('accepts %s', (v) => {
    expect(parseGrade(v)).toEqual({ ok: true, value: Number(v) })
  })

  it.each([
    ['-1', 'range'],
    ['101', 'range'],
    ['50.5', 'integer'],
    ['abc', 'integer'],
    ['', 'required'],
  ])('rejects %s', (v, error) => {
    expect(parseGrade(v)).toEqual({ ok: false, error })
  })
})

describe('validateAnswers', () => {
  const questions: SurveyQuestion[] = [
    { id: 'q1', order: 0, text: 'Scale', type: 'Scale', isRequired: true, scaleMin: 1, scaleMax: 5,
      scaleMinLabel: null, scaleMaxLabel: null, options: [] },
    { id: 'q2', order: 1, text: 'Choice', type: 'SingleChoice', isRequired: true, scaleMin: null, scaleMax: null,
      scaleMinLabel: null, scaleMaxLabel: null, options: [{ id: 'o1', order: 0, text: 'A' }] },
    { id: 'q3', order: 2, text: 'Text', type: 'Text', isRequired: false, scaleMin: null, scaleMax: null,
      scaleMinLabel: null, scaleMaxLabel: null, options: [] },
  ]

  it('requires mandatory questions', () => {
    expect(validateAnswers(questions, {})).toEqual({ q1: 'required', q2: 'required' })
  })

  it('checks the scale range', () => {
    expect(validateAnswers(questions, { q1: { intValue: 6 }, q2: { optionId: 'o1' } })).toEqual({ q1: 'range' })
  })

  it('accepts a complete answer with optional text omitted', () => {
    expect(validateAnswers(questions, { q1: { intValue: 3 }, q2: { optionId: 'o1' } })).toEqual({})
  })
})

describe('LoginPage', () => {
  beforeEach(() => {
    vi.stubGlobal('fetch', vi.fn(async () => new Response(JSON.stringify({ code: 'unauthorized' }), { status: 401 })))
    setLanguage('ru')
  })
  afterEach(() => {
    vi.unstubAllGlobals()
  })

  it('renders in Russian by default and switches to English', async () => {
    render(
      <MemoryRouter>
        <AuthProvider>
          <LoginPage />
        </AuthProvider>
      </MemoryRouter>,
    )
    expect(await screen.findByRole('heading', { name: 'Вход в кабинет' })).toBeInTheDocument()

    await userEvent.selectOptions(screen.getByLabelText('Язык'), 'en')
    expect(await screen.findByRole('heading', { name: 'Sign in' })).toBeInTheDocument()
    expect(document.documentElement.lang).toBe('en')

    act(() => setLanguage('ru'))
  })
})

describe('GradesPage history', () => {
  const grade: Grade = {
    id: 'g1', studentId: 's1', studentName: 'Иванова Мария', groupId: 'gr1', groupName: 'MBA-2026',
    disciplineId: 'd1', disciplineName: 'Финансы', periodId: 'p1', periodName: 'Семестр 1',
    value: 80, status: 'Published', updatedAt: '2026-10-02T10:00:00Z', publishedAt: '2026-10-02T10:00:00Z',
  }
  const history: GradeHistoryEntry[] = [
    { id: 'h2', gradeId: 'g1', action: 'Updated', oldValue: 70, newValue: 80, oldStatus: 'Published',
      newStatus: 'Published', changedBy: 'manager@mba.local', changedAt: '2026-10-02T10:00:00Z' },
    { id: 'h1', gradeId: 'g1', action: 'Created', oldValue: null, newValue: 70, oldStatus: null,
      newStatus: 'Draft', changedBy: 'manager@mba.local', changedAt: '2026-10-01T09:00:00Z' },
  ]

  beforeEach(() => {
    setLanguage('ru')
    vi.stubGlobal('fetch', vi.fn(async (input: RequestInfo | URL) => {
      const url = String(input)
      const body = url.includes('/history') ? history
        : url.includes('/api/manager/grades') ? [grade]
        : url.includes('/api/manager/students') ? { items: [], total: 0, page: 1, pageSize: 200 }
        : []
      return new Response(JSON.stringify(body), { status: 200, headers: { 'Content-Type': 'application/json' } })
    }))
  })
  afterEach(() => {
    vi.unstubAllGlobals()
  })

  it('shows who changed the grade, old and new values and statuses', async () => {
    render(<MemoryRouter><GradesPage /></MemoryRouter>)
    await userEvent.click(await screen.findByRole('button', { name: 'История' }))

    const dialog = await screen.findByRole('dialog', { name: /История оценки: Иванова Мария · Финансы · Семестр 1/ })
    expect(await within(dialog).findByText('Изменена')).toBeInTheDocument()
    expect(within(dialog).getByText('Создана')).toBeInTheDocument()
    expect(within(dialog).getByText('70 → 80')).toBeInTheDocument()
    expect(within(dialog).getByText('— → 70')).toBeInTheDocument()
    expect(within(dialog).getByText('— → Черновик')).toBeInTheDocument()
    expect(within(dialog).getAllByText('manager@mba.local')).toHaveLength(2)
  })
})
