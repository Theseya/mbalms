import { act, render, screen, within } from '@testing-library/react'
import { MemoryRouter, Route, Routes } from 'react-router-dom'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import type { Dashboard, StudentSurveyListItem } from '../api/types'
import App from '../App'
import { AuthProvider } from '../auth/AuthContext'
import { CrowIllustration, SurveyCardMascot } from '../components/SurveyMascot'
import { mascotState, surveyCardCaptionKey, surveyCardMood } from '../lib/mascot'
import { setLanguage } from '../i18n'
import { StudentSurveysPage } from '../pages/student/StudentPages'

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

function renderSurveyList(surveys: StudentSurveyListItem[]) {
  vi.stubGlobal('fetch', vi.fn(async (input: RequestInfo | URL) => {
    const path = new URL(String(input), 'http://localhost').pathname
    const body = path === '/api/student/surveys' ? surveys
      : path === '/api/auth/csrf' ? { token: 't' }
      : undefined
    return body === undefined ? new Response(null, { status: 404 }) : new Response(JSON.stringify(body), { status: 200 })
  }))
  render(
    <MemoryRouter initialEntries={['/student/surveys']}>
      <Routes>
        <Route path="/student/surveys" element={<StudentSurveysPage />} />
      </Routes>
    </MemoryRouter>,
  )
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

describe('surveyCardMood', () => {
  it('marks an available unanswered survey as pending', () => {
    expect(surveyCardMood(survey('a'))).toBe('pending')
    expect(surveyCardCaptionKey(survey('a'))).toBe('studentSurveys.mascot.notCompleted')
  })

  it('marks a submitted survey as done', () => {
    expect(surveyCardMood(done('a'))).toBe('done')
    expect(surveyCardCaptionKey(done('a'))).toBe('studentSurveys.mascot.completed')
  })

  it('marks closed and other unavailable surveys as neutral with distinct captions', () => {
    expect(surveyCardMood(missed('a'))).toBe('none')
    expect(surveyCardCaptionKey(missed('a'))).toBe('studentSurveys.mascot.closed')
    const blocked = survey('b', { canRespond: false, status: 'Open' })
    expect(surveyCardMood(blocked)).toBe('none')
    expect(surveyCardCaptionKey(blocked)).toBe('studentSurveys.mascot.unavailable')
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

describe('Survey card mascot on the surveys list', () => {
  it('shows pending, completed and closed captions in Russian and keeps answer buttons', async () => {
    renderSurveyList([
      survey('open', { title: 'Open survey' }),
      done('done'),
      missed('closed'),
    ])

    const openCard = (await screen.findByRole('heading', { name: 'Open survey' })).closest('article')!
    expect(within(openCard).getByText('Опрос не пройден')).toBeInTheDocument()
    expect(within(openCard).getByRole('link', { name: 'Ответить' })).toHaveAttribute('href', '/student/surveys/open')
    expect(openCard.querySelector('.survey-card-mascot')).toHaveAttribute('data-mood', 'pending')

    const doneCard = screen.getByRole('heading', { name: 'Survey done' }).closest('article')!
    expect(within(doneCard).getByText('Опрос пройден')).toBeInTheDocument()
    expect(within(doneCard).getByRole('link', { name: 'Посмотреть' })).toHaveAttribute('href', '/student/surveys/done')
    expect(doneCard.querySelector('.survey-card-mascot')).toHaveAttribute('data-mood', 'done')

    const closedCard = screen.getByRole('heading', { name: 'Survey closed' }).closest('article')!
    expect(within(closedCard).getByText('Опрос закрыт')).toBeInTheDocument()
    expect(within(closedCard).getByRole('link', { name: 'Посмотреть' })).toBeInTheDocument()
    expect(closedCard.querySelector('.survey-card-mascot')).toHaveAttribute('data-mood', 'none')
  })

  it('shows English captions for not completed, completed and unavailable', async () => {
    act(() => setLanguage('en'))
    renderSurveyList([
      survey('open'),
      done('done'),
      survey('blocked', { canRespond: false, status: 'Open', title: 'Blocked survey' }),
    ])

    expect(await screen.findByText('Survey not completed')).toBeInTheDocument()
    expect(screen.getByText('Survey completed')).toBeInTheDocument()
    expect(screen.getByText('Survey unavailable')).toBeInTheDocument()
    expect(screen.getByRole('link', { name: 'Answer' })).toBeInTheDocument()
  })

  it('renders SurveyCardMascot caption without relying on animation', () => {
    const { container } = render(<SurveyCardMascot survey={survey('x')} />)
    expect(container.querySelector('[data-mood="pending"]')).toHaveTextContent('Опрос не пройден')
    expect(container.querySelector('svg')).toHaveAttribute('aria-hidden', 'true')
  })

  it('keeps captions meaningful when motion hooks are present for reduced-motion CSS', () => {
    const { container, rerender } = render(<SurveyCardMascot survey={survey('x')} />)
    expect(container.querySelector('.survey-card-mascot.mascot-pending .mascot-eyes')).toBeTruthy()
    expect(container).toHaveTextContent('Опрос не пройден')

    rerender(<SurveyCardMascot survey={done('y')} />)
    expect(container.querySelector('.survey-card-mascot.mascot-done')).toBeTruthy()
    expect(container).toHaveTextContent('Опрос пройден')

    rerender(<SurveyCardMascot survey={missed('z')} />)
    expect(container.querySelector('.survey-card-mascot.mascot-none')).toBeTruthy()
    expect(container).toHaveTextContent('Опрос закрыт')
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
