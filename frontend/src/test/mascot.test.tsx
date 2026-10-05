import { act, render, screen } from '@testing-library/react'
import { MemoryRouter } from 'react-router-dom'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import type { Dashboard, StudentSurveyListItem } from '../api/types'
import App from '../App'
import { AuthProvider } from '../auth/AuthContext'
import { CrowIllustration } from '../components/SurveyMascot'
import { mascotState } from '../lib/mascot'
import { setLanguage } from '../i18n'

const survey = (id: string, overrides: Partial<StudentSurveyListItem> = {}): StudentSurveyListItem => ({
  id, type: 'ServiceSurvey', title: `Survey ${id}`, status: 'Open', teacherName: null, disciplineName: null,
  opensAtLocal: null, closesAtLocal: null, submitted: false, submittedAt: null, canRespond: true, ...overrides,
})
const done = (id: string) => survey(id, { submitted: true, submittedAt: '2026-10-01T09:00:00Z', canRespond: false })
const missed = (id: string) => survey(id, { status: 'Closed', canRespond: false })

const me = { id: 'u1', email: 'anna@example.test', role: 'Student', displayName: 'Anna', groupName: 'MBA-01', timeZone: 'Europe/Moscow' }
const dashboard: Dashboard = { nextLesson: null, upcomingLessons: [], pendingSurveys: 0, unreadNotifications: 0, recentGrades: [] }

function renderHome(surveys: StudentSurveyListItem[] | 'error') {
  vi.stubGlobal('fetch', vi.fn(async (input: RequestInfo | URL) => {
    const path = new URL(String(input), 'http://localhost').pathname
    if (path === '/api/student/surveys' && surveys === 'error') return new Response(null, { status: 500 })
    const body = path === '/api/auth/me' ? me
      : path === '/api/student/dashboard' ? { ...dashboard, pendingSurveys: 3 }
      : path === '/api/student/surveys' ? surveys
      : path === '/api/notifications/unread-count' ? { count: 0 }
      : path === '/api/auth/csrf' ? { token: 't' }
      : undefined
    return body === undefined ? new Response(null, { status: 404 }) : new Response(JSON.stringify(body), { status: 200 })
  }))
  render(<MemoryRouter initialEntries={['/student']}><AuthProvider><App /></AuthProvider></MemoryRouter>)
}

/** The mascot card is a link whose accessible name is its text. */
const card = async (name: RegExp) => {
  const link = await screen.findByRole('link', { name })
  expect(link).toHaveClass('mascot')
  return link
}

beforeEach(() => setLanguage('ru'))
afterEach(() => {
  vi.unstubAllGlobals()
  act(() => setLanguage('ru'))
})

describe('mascotState', () => {
  it('is pending while at least one survey can be answered', () => {
    expect(mascotState([survey('a'), done('b')])).toEqual({ mood: 'pending', remaining: 1, link: '/student/surveys/a' })
    expect(mascotState([survey('a'), survey('b')])).toEqual({ mood: 'pending', remaining: 2, link: '/student/surveys' })
  })

  it('is done when nothing is left and something was answered', () => {
    expect(mascotState([done('a'), missed('b')])).toEqual({ mood: 'done', remaining: 0, link: '/student/surveys' })
  })

  it('is neutral without surveys or with only unavailable ones', () => {
    expect(mascotState([]).mood).toBe('none')
    expect(mascotState([missed('a'), survey('b', { canRespond: false, status: 'Open' })]).mood).toBe('none')
  })
})

describe('Survey mascot on the student home page', () => {
  it('looks thoughtful and links straight to the only unanswered survey', async () => {
    renderHome([survey('s1'), done('s2')])
    const link = await card(/Остался 1 опрос/)

    expect(link).toHaveAttribute('data-mood', 'pending')
    expect(link).toHaveAttribute('href', '/student/surveys/s1')
    expect(link).toHaveTextContent('Ваше мнение поможет улучшить программу.')
    expect(link).toHaveTextContent('Пройти опрос →')
    const svg = link.querySelector('svg')!
    expect(svg).toHaveAttribute('aria-hidden', 'true')
    expect(svg).toHaveAttribute('focusable', 'false')
  })

  it('uses Russian plural forms and links to the list when several surveys are left', async () => {
    renderHome([survey('s1'), survey('s2')])
    const link = await card(/Осталось 2 опроса/)
    expect(link).toHaveAttribute('href', '/student/surveys')
    expect(link).toHaveTextContent('Перейти к опросам →')
  })

  it('uses the many form for five surveys', async () => {
    renderHome(['1', '2', '3', '4', '5'].map((id) => survey(id)))
    await card(/Осталось 5 опросов/)
  })

  it('looks happy when all available surveys are completed', async () => {
    renderHome([done('s1'), missed('s2')])
    const link = await card(/Все опросы пройдены/)
    expect(link).toHaveAttribute('data-mood', 'done')
    expect(link).toHaveAttribute('href', '/student/surveys')
    expect(link).toHaveTextContent('Спасибо за ответы!')
  })

  it('is neutral and friendly when there are no surveys', async () => {
    renderHome([])
    const link = await card(/Новых опросов нет/)
    expect(link).toHaveAttribute('data-mood', 'none')
    expect(link).toHaveTextContent('Здесь появятся опросы для вашей группы.')
  })

  it('shows the texts in English', async () => {
    act(() => setLanguage('en'))
    renderHome([survey('s1'), survey('s2'), done('s3')])
    const link = await card(/2 surveys remaining/)
    expect(link).toHaveTextContent('Your feedback helps improve the programme.')
  })

  it.each([
    [[survey('s1')], /^1 survey remaining/],
    [[done('s1')], /^All surveys completed/],
    [[], /^No surveys right now/],
  ])('shows the English state text %#', async (list, name) => {
    act(() => setLanguage('en'))
    renderHome(list)
    await card(name)
  })
})

describe('CrowIllustration', () => {
  it.each(['pending', 'done', 'none'] as const)('has the same size and is hidden from assistive technology (%s)', (mood) => {
    const { container } = render(<CrowIllustration mood={mood} />)
    const svg = container.querySelector('svg')!
    expect([svg.getAttribute('viewBox'), svg.getAttribute('width'), svg.getAttribute('height')]).toEqual(['0 0 120 120', '88', '88'])
    expect(svg).toHaveAttribute('aria-hidden', 'true')
    expect(svg.querySelector('title, text')).toBeNull()
  })

  it('falls back to the plain counter when the survey list cannot be loaded', async () => {
    renderHome('error')
    const link = await screen.findByRole('link', { name: /3\s*Опросы, ожидающие ответа/ })
    expect(link).not.toHaveClass('mascot')
    expect(link).toHaveAttribute('href', '/student/surveys')
  })
})
