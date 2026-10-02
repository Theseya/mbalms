import { act, render, screen } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { MemoryRouter } from 'react-router-dom'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import type { SurveyQuestion } from '../api/types'
import { AuthProvider } from '../auth/AuthContext'
import { setLanguage } from '../i18n'
import en from '../i18n/en'
import ru from '../i18n/ru'
import { parseGrade } from '../lib/grades'
import { LoginPage } from '../pages/LoginPage'
import { validateAnswers } from '../lib/surveyValidation'

function keys(obj: object, prefix = ''): string[] {
  return Object.entries(obj).flatMap(([k, v]) =>
    typeof v === 'object' && v !== null ? keys(v, `${prefix}${k}.`) : [`${prefix}${k}`])
}

describe('localization', () => {
  it('has the same keys in Russian and English', () => {
    expect(keys(en).sort()).toEqual(keys(ru).sort())
  })

  it('has no empty translations', () => {
    const empty = [...keys(ru), ...keys(en)].filter((k) => {
      const value = k.split('.').reduce<unknown>((o, p) => (o as Record<string, unknown>)[p], ru)
      return value === ''
    })
    expect(empty).toEqual([])
  })
})

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
    { id: 'q1', order: 0, text: 'Scale', type: 'Scale', isRequired: true, scaleMin: 1, scaleMax: 5, options: [] },
    { id: 'q2', order: 1, text: 'Choice', type: 'SingleChoice', isRequired: true, scaleMin: null, scaleMax: null,
      options: [{ id: 'o1', order: 0, text: 'A' }] },
    { id: 'q3', order: 2, text: 'Text', type: 'Text', isRequired: false, scaleMin: null, scaleMax: null, options: [] },
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
