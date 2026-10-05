import { act, render, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { MemoryRouter } from 'react-router-dom'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import type { AppNotification } from '../api/types'
import App from '../App'
import { AuthProvider } from '../auth/AuthContext'
import { setLanguage } from '../i18n'
import { NotificationsPage } from '../pages/student/StudentPages'

interface Call { method: string; path: string }

const notification = (id: string, type: AppNotification['type'], payload: Record<string, string>, readAt: string | null = null): AppNotification =>
  ({ id, type, payload, createdAt: '2026-10-02T09:00:00Z', readAt })

const all: AppNotification[] = [
  notification('n1', 'SurveyAssigned', { surveyId: 'sv1', title: 'Course feedback', surveyType: 'ServiceSurvey' }),
  notification('n2', 'ScheduleChanged', { lessonId: 'l1', disciplineName: 'Finance', startsAtLocal: '2030-04-01T09:30:00', change: 'created' }),
  notification('n3', 'ScheduleChanged', { lessonId: 'l1', disciplineName: 'Finance', startsAtLocal: '2030-04-01T09:30:00', change: 'updated' }),
  notification('n4', 'ScheduleChanged', { lessonId: 'l1', disciplineName: 'Finance', startsAtLocal: '2030-04-01T09:30:00', change: 'cancelled' }),
  notification('n5', 'ScheduleChanged', { lessonId: 'l1', disciplineName: 'Finance', startsAtLocal: '2030-04-01T09:30:00', change: 'deleted' }),
  notification('n6', 'GradePublished', { gradeId: 'gr1', disciplineName: 'Finance', periodName: 'Term 1', change: 'published' }),
  notification('n7', 'GradePublished', { gradeId: 'gr1', disciplineName: 'Finance', periodName: 'Term 1', change: 'updated' }, '2026-10-02T10:00:00Z'),
  // Notifications created before the change field existed.
  notification('n8', 'GradePublished', { gradeId: 'gr2', disciplineName: 'Strategy', periodName: 'Term 1' }, '2026-10-02T10:00:00Z'),
]

function mockApi(list: () => AppNotification[]): Call[] {
  const calls: Call[] = []
  vi.stubGlobal('fetch', vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
    const path = new URL(String(input), 'http://localhost').pathname
    calls.push({ method: init?.method ?? 'GET', path })
    if (path === '/api/auth/csrf') return new Response(JSON.stringify({ token: 't' }), { status: 200 })
    if (path === '/api/notifications') return new Response(JSON.stringify(list()), { status: 200 })
    return new Response(null, { status: 204 })
  }))
  return calls
}

const items = () => screen.getAllByRole('listitem')
const renderPage = () => render(<MemoryRouter><NotificationsPage /></MemoryRouter>)

beforeEach(() => setLanguage('ru'))
afterEach(() => vi.unstubAllGlobals())

describe('NotificationsPage', () => {
  it('renders every notification type in Russian with links to the related page', async () => {
    mockApi(() => all)
    renderPage()
    await screen.findByText('Новый опрос: «Course feedback»')

    const texts = items().map((li) => within(li).getByRole('link').textContent)
    expect(texts).toEqual([
      'Новый опрос: «Course feedback»',
      'В расписание добавлено занятие: Finance, 01.04.2030, 09:30',
      'Изменено занятие: Finance, 01.04.2030, 09:30',
      'Отменено занятие: Finance, 01.04.2030, 09:30',
      'Занятие удалено из расписания: Finance, 01.04.2030, 09:30',
      'Опубликована оценка: Finance (Term 1)',
      'Изменена опубликованная оценка: Finance (Term 1)',
      'Опубликована оценка: Strategy (Term 1)',
    ])
    const hrefs = items().map((li) => within(li).getByRole('link').getAttribute('href'))
    expect(hrefs).toEqual([
      '/student/surveys/sv1',
      '/student/schedule', '/student/schedule', '/student/schedule', '/student/schedule',
      '/student/grades', '/student/grades', '/student/grades',
    ])
    expect(screen.getAllByText('02.10.2026, 12:00')).toHaveLength(all.length)
  })

  it('renders the same notifications in English without translating the entered data', async () => {
    mockApi(() => all)
    act(() => setLanguage('en'))
    renderPage()
    await screen.findByText('New survey: “Course feedback”')

    expect(items().map((li) => within(li).getByRole('link').textContent)).toEqual([
      'New survey: “Course feedback”',
      'Lesson added to the schedule: Finance, 01/04/2030, 09:30',
      'Lesson changed: Finance, 01/04/2030, 09:30',
      'Lesson cancelled: Finance, 01/04/2030, 09:30',
      'Lesson removed from the schedule: Finance, 01/04/2030, 09:30',
      'Grade published: Finance (Term 1)',
      'Published grade changed: Finance (Term 1)',
      'Grade published: Strategy (Term 1)',
    ])
    expect(screen.getByRole('heading', { name: 'Notifications' })).toBeInTheDocument()
  })

  it('distinguishes unread notifications and marks one as read', async () => {
    const list = [all[0], all[6]]
    const calls = mockApi(() => list)
    renderPage()
    await screen.findByText('Новый опрос: «Course feedback»')

    const [unread, read] = items()
    expect(unread).toHaveClass('unread')
    expect(read).toHaveClass('read')
    expect(within(read).queryByRole('button')).not.toBeInTheDocument()

    await userEvent.click(within(unread).getByRole('button', { name: 'Прочитано' }))
    await waitFor(() => expect(calls.filter((c) => c.path === '/api/notifications').length).toBe(2))
    expect(calls).toContainEqual({ method: 'POST', path: '/api/notifications/n1/read' })
  })

  it('marks all as read and hides the button when nothing is unread', async () => {
    let list = [all[0], all[1]]
    const calls = mockApi(() => list)
    renderPage()
    const button = await screen.findByRole('button', { name: 'Отметить все как прочитанные' })

    list = list.map((n) => ({ ...n, readAt: '2026-10-02T10:00:00Z' }))
    await userEvent.click(button)

    expect(calls).toContainEqual({ method: 'POST', path: '/api/notifications/read-all' })
    await waitFor(() => expect(screen.queryByRole('button', { name: 'Отметить все как прочитанные' })).not.toBeInTheDocument())
    expect(items().every((li) => li.classList.contains('read'))).toBe(true)
  })

  it('updates the unread counter in the header right after marking a notification as read', async () => {
    let list = [all[0], all[1]]
    const me = { id: 'u1', email: 'anna@example.test', role: 'Student', displayName: 'Anna', groupName: 'MBA-01', timeZone: 'Europe/Moscow' }
    vi.stubGlobal('fetch', vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
      const path = new URL(String(input), 'http://localhost').pathname
      const body = path === '/api/auth/me' ? me
        : path === '/api/auth/csrf' ? { token: 't' }
        : path === '/api/notifications' ? list
        : path === '/api/notifications/unread-count' ? { count: list.filter((n) => !n.readAt).length }
        : undefined
      if (init?.method === 'POST' && path === '/api/notifications/n1/read')
        list = list.map((n) => (n.id === 'n1' ? { ...n, readAt: '2026-10-02T10:00:00Z' } : n))
      return body === undefined ? new Response(null, { status: 204 }) : new Response(JSON.stringify(body), { status: 200 })
    }))
    render(<MemoryRouter initialEntries={['/student/notifications']}><AuthProvider><App /></AuthProvider></MemoryRouter>)

    expect(await screen.findByRole('link', { name: 'Уведомления, непрочитанных: 2' })).toBeInTheDocument()
    const survey = (await screen.findByText('Новый опрос: «Course feedback»')).closest('li')!
    await userEvent.click(within(survey).getByRole('button', { name: 'Прочитано' }))

    expect(await screen.findByRole('link', { name: 'Уведомления, непрочитанных: 1' })).toBeInTheDocument()
  })

  it('shows an empty state', async () => {
    mockApi(() => [])
    renderPage()
    expect(await screen.findByText('Уведомлений нет')).toBeInTheDocument()
  })
})
