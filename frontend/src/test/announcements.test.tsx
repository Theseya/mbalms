import { render, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { MemoryRouter, Route, Routes } from 'react-router-dom'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import type { AnnouncementDetail, AnnouncementListItem, Dashboard, Group, StudentAnnouncement } from '../api/types'
import { setLanguage } from '../i18n'
import { AnnouncementEditPage } from '../pages/manager/AnnouncementEditPage'
import { AnnouncementsPage } from '../pages/manager/AnnouncementsPage'
import { StudentAnnouncementsPage, StudentHomePage } from '../pages/student/StudentPages'
import { AuthProvider } from '../auth/AuthContext'

interface Call { method: string; url: URL; body: unknown }

const json = (body: unknown, status = 200) =>
  new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } })

function mockApi(handler: (call: Call) => unknown): Call[] {
  const calls: Call[] = []
  vi.stubGlobal('fetch', vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
    const call = {
      method: init?.method ?? 'GET',
      url: new URL(String(input), 'http://localhost'),
      body: init?.body ? JSON.parse(String(init.body)) : undefined,
    }
    calls.push(call)
    if (call.url.pathname === '/api/auth/csrf') return json({ token: 'test-token' })
    const result = handler(call)
    if (result instanceof Response) return result
    return result === undefined ? new Response(null, { status: 204 }) : json(result)
  }))
  return calls
}

const groups: Group[] = [
  { id: 'g1', name: 'MBA-01', startDate: null, endDate: null, status: 'Active', archivedAt: null, studentCount: 2 },
  { id: 'g2', name: 'MBA-02', startDate: null, endDate: null, status: 'Active', archivedAt: null, studentCount: 1 },
]

const item = (overrides: Partial<AnnouncementListItem> = {}): AnnouncementListItem => ({
  id: 'a1', title: 'Важное объявление', status: 'Draft', isPinned: false, isExpired: false,
  createdAt: '2026-10-01T09:00:00Z', updatedAt: '2026-10-01T09:00:00Z', publishedAt: null,
  expiresAtLocal: null, groups: [{ id: 'g1', name: 'MBA-01', status: 'Active' }], ...overrides,
})

const detail = (overrides: Partial<AnnouncementDetail> = {}): AnnouncementDetail => ({
  ...item(), body: 'Текст\nвторая строка', ...overrides,
})

const studentItem = (overrides: Partial<StudentAnnouncement> = {}): StudentAnnouncement => ({
  id: 'a1', title: 'Важное объявление', body: 'Текст\nвторая строка', isPinned: true,
  publishedAt: '2026-10-01T09:00:00Z', publishedAtLocal: '2026-10-01T12:00:00', expiresAtLocal: null, ...overrides,
})

beforeEach(() => setLanguage('ru'))
afterEach(() => vi.unstubAllGlobals())

describe('manager announcements', () => {
  it('lists status, groups, pin and expired markers', async () => {
    mockApi((call) => {
      if (call.url.pathname === '/api/manager/groups') return groups
      if (call.url.pathname === '/api/manager/announcements') return [
        item({ status: 'Published', isPinned: true, isExpired: true, publishedAt: '2026-10-01T09:00:00Z' }),
        item({ id: 'a2', title: 'Черновик', status: 'Draft', groups: [
          { id: 'g1', name: 'MBA-01', status: 'Active' },
          { id: 'g2', name: 'MBA-02', status: 'Active' },
        ] }),
      ]
    })
    render(<MemoryRouter><AnnouncementsPage /></MemoryRouter>)
    expect(await screen.findByRole('heading', { name: 'Объявления' })).toBeInTheDocument()
    expect(screen.getByText('Важно')).toBeInTheDocument()
    expect(screen.getByText('Срок отображения истёк')).toBeInTheDocument()
    expect(screen.getByText('MBA-01, MBA-02')).toBeInTheDocument()
    expect(screen.getAllByText('Опубликовано').length).toBeGreaterThan(0)
  })

  it('publishes and deletes with confirmation', async () => {
    const user = userEvent.setup()
    const calls = mockApi((call) => {
      if (call.url.pathname === '/api/manager/groups') return groups
      if (call.url.pathname === '/api/manager/announcements') return [item()]
      if (call.method === 'POST' && call.url.pathname.endsWith('/publish')) return detail({ status: 'Published' })
      if (call.method === 'DELETE') return new Response(null, { status: 204 })
    })
    render(<MemoryRouter><AnnouncementsPage /></MemoryRouter>)
    await screen.findByText('Важное объявление')

    await user.click(screen.getByRole('button', { name: 'Опубликовать' }))
    await user.click(within(screen.getByRole('dialog')).getByRole('button', { name: 'Опубликовать' }))
    await waitFor(() => expect(calls.some((c) => c.method === 'POST' && c.url.pathname.endsWith('/publish'))).toBe(true))
  })

  it('creates announcement with groups and pin', async () => {
    const user = userEvent.setup()
    const calls = mockApi((call) => {
      if (call.url.pathname === '/api/manager/groups') return groups
      if (call.method === 'POST' && call.url.pathname === '/api/manager/announcements') {
        return detail({ id: 'new1', title: (call.body as { title: string }).title })
      }
    })
    render(
      <MemoryRouter initialEntries={['/manager/announcements/new']}>
        <Routes>
          <Route path="/manager/announcements/new" element={<AnnouncementEditPage />} />
          <Route path="/manager/announcements" element={<p>list</p>} />
        </Routes>
      </MemoryRouter>,
    )
    await screen.findByLabelText(/Заголовок/)
    await user.type(screen.getByLabelText(/Заголовок/), 'Новость')
    await user.type(screen.getByLabelText(/^Текст/), 'Строка 1{enter}Строка 2')
    await user.click(screen.getByLabelText('MBA-01'))
    await user.click(screen.getByLabelText(/Закрепить/))
    await user.click(screen.getByRole('button', { name: 'Сохранить' }))
    await waitFor(() => {
      const post = calls.find((c) => c.method === 'POST' && c.url.pathname === '/api/manager/announcements')
      expect(post?.body).toMatchObject({ title: 'Новость', isPinned: true, groupIds: ['g1'] })
    })
  })

  it('blocks save-and-publish when show-until is already past without saving', async () => {
    const user = userEvent.setup()
    const calls = mockApi((call) => {
      if (call.url.pathname === '/api/manager/groups') return groups
    })
    render(
      <MemoryRouter initialEntries={['/manager/announcements/new']}>
        <Routes>
          <Route path="/manager/announcements/new" element={<AnnouncementEditPage />} />
        </Routes>
      </MemoryRouter>,
    )
    await screen.findByLabelText(/Заголовок/)
    await user.type(screen.getByLabelText(/Заголовок/), 'Просрочено')
    await user.type(screen.getByLabelText(/^Текст/), 'Текст')
    await user.click(screen.getByLabelText('MBA-01'))
    const expires = screen.getByLabelText(/Показывать до/)
    await user.clear(expires)
    await user.type(expires, '2020-01-01T12:00')
    await user.click(screen.getByRole('button', { name: 'Сохранить и опубликовать' }))
    expect(await screen.findByText('Некорректное значение')).toBeInTheDocument()
    expect(calls.some((c) => c.method === 'POST')).toBe(false)
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument()
  })

  it('keeps created id when publish fails and retries with PUT', async () => {
    const user = userEvent.setup()
    let publishAttempts = 0
    const calls = mockApi((call) => {
      if (call.url.pathname === '/api/manager/groups') return groups
      if (call.method === 'POST' && call.url.pathname === '/api/manager/announcements') {
        const body = call.body as { title: string; body: string; expiresAt: string | null; groupIds: string[] }
        return detail({
          id: 'created-1',
          title: body.title,
          body: body.body,
          expiresAtLocal: body.expiresAt,
          groups: [{ id: 'g1', name: 'MBA-01', status: 'Active' }],
        })
      }
      if (call.method === 'GET' && call.url.pathname === '/api/manager/announcements/created-1') {
        return detail({
          id: 'created-1',
          title: 'Частичный успех',
          body: 'Тело',
          expiresAtLocal: '2030-12-31T18:00:00',
          groups: [{ id: 'g1', name: 'MBA-01', status: 'Active' }],
        })
      }
      if (call.method === 'PUT' && call.url.pathname === '/api/manager/announcements/created-1') {
        return detail({
          id: 'created-1',
          title: (call.body as { title: string }).title,
          body: (call.body as { body: string }).body,
          status: 'Draft',
          expiresAtLocal: '2030-12-31T18:00:00',
          groups: [{ id: 'g1', name: 'MBA-01', status: 'Active' }],
        })
      }
      if (call.method === 'POST' && call.url.pathname.endsWith('/publish')) {
        publishAttempts += 1
        return json({ code: 'validation_failed', errors: { expiresAt: ['invalid'] } }, 400)
      }
    })
    render(
      <MemoryRouter initialEntries={['/manager/announcements/new']}>
        <Routes>
          <Route path="/manager/announcements/new" element={<AnnouncementEditPage />} />
          <Route path="/manager/announcements/:id" element={<AnnouncementEditPage />} />
          <Route path="/manager/announcements" element={<p>list</p>} />
        </Routes>
      </MemoryRouter>,
    )
    await screen.findByLabelText(/Заголовок/)
    await user.type(screen.getByLabelText(/Заголовок/), 'Частичный успех')
    await user.type(screen.getByLabelText(/^Текст/), 'Тело')
    await user.click(screen.getByLabelText('MBA-01'))
    await user.type(screen.getByLabelText(/Показывать до/), '2030-12-31T18:00')
    await user.click(screen.getByRole('button', { name: 'Сохранить и опубликовать' }))
    await user.click(within(screen.getByRole('dialog')).getByRole('button', { name: 'Опубликовать' }))

    expect(await screen.findByText('Объявление сохранено как черновик, но не опубликовано.')).toBeInTheDocument()
    expect(screen.getByText('Проверьте правильность заполнения полей.')).toBeInTheDocument()
    expect(await screen.findByRole('heading', { name: 'Редактирование объявления' })).toBeInTheDocument()

    const createPosts = () => calls.filter((c) => c.method === 'POST' && c.url.pathname === '/api/manager/announcements')
    expect(createPosts()).toHaveLength(1)
    expect(publishAttempts).toBe(1)

    await user.click(screen.getByRole('button', { name: 'Сохранить и опубликовать' }))
    await user.click(within(screen.getByRole('dialog')).getByRole('button', { name: 'Опубликовать' }))
    await waitFor(() => expect(publishAttempts).toBe(2))
    expect(createPosts()).toHaveLength(1)
    expect(calls.some((c) => c.method === 'PUT' && c.url.pathname === '/api/manager/announcements/created-1')).toBe(true)
  })

  it('save-and-publish succeeds and returns to the list', async () => {
    const user = userEvent.setup()
    const calls = mockApi((call) => {
      if (call.url.pathname === '/api/manager/groups') return groups
      if (call.method === 'POST' && call.url.pathname === '/api/manager/announcements') {
        return detail({ id: 'pub-1', title: 'Ок', status: 'Draft' })
      }
      if (call.method === 'POST' && call.url.pathname.endsWith('/publish')) {
        return detail({ id: 'pub-1', title: 'Ок', status: 'Published', publishedAt: '2026-10-06T10:00:00Z' })
      }
    })
    render(
      <MemoryRouter initialEntries={['/manager/announcements/new']}>
        <Routes>
          <Route path="/manager/announcements/new" element={<AnnouncementEditPage />} />
          <Route path="/manager/announcements" element={<p>list-ok</p>} />
        </Routes>
      </MemoryRouter>,
    )
    await screen.findByLabelText(/Заголовок/)
    await user.type(screen.getByLabelText(/Заголовок/), 'Ок')
    await user.type(screen.getByLabelText(/^Текст/), 'Текст')
    await user.click(screen.getByLabelText('MBA-01'))
    await user.click(screen.getByRole('button', { name: 'Сохранить и опубликовать' }))
    await user.click(within(screen.getByRole('dialog')).getByRole('button', { name: 'Опубликовать' }))
    expect(await screen.findByText('list-ok')).toBeInTheDocument()
    expect(calls.some((c) => c.method === 'POST' && c.url.pathname.endsWith('/publish'))).toBe(true)
  })

  it('does not publish when save fails', async () => {
    const user = userEvent.setup()
    const calls = mockApi((call) => {
      if (call.url.pathname === '/api/manager/groups') return groups
      if (call.method === 'POST' && call.url.pathname === '/api/manager/announcements') {
        return json({ code: 'validation_failed', errors: { title: ['required'] } }, 400)
      }
    })
    render(
      <MemoryRouter initialEntries={['/manager/announcements/new']}>
        <Routes>
          <Route path="/manager/announcements/new" element={<AnnouncementEditPage />} />
        </Routes>
      </MemoryRouter>,
    )
    await screen.findByLabelText(/Заголовок/)
    await user.type(screen.getByLabelText(/^Текст/), 'Текст')
    await user.click(screen.getByLabelText('MBA-01'))
    await user.click(screen.getByRole('button', { name: 'Сохранить и опубликовать' }))
    await user.click(within(screen.getByRole('dialog')).getByRole('button', { name: 'Опубликовать' }))
    expect(await screen.findByText('Обязательное поле')).toBeInTheDocument()
    expect(calls.some((c) => c.url.pathname.endsWith('/publish'))).toBe(false)
  })

  it('shows saved-not-published notice when publish fails on existing draft', async () => {
    const user = userEvent.setup()
    mockApi((call) => {
      if (call.url.pathname === '/api/manager/groups') return groups
      if (call.method === 'GET' && call.url.pathname === '/api/manager/announcements/a1') {
        return detail({ id: 'a1', status: 'Draft', expiresAtLocal: '2030-12-31T18:00:00' })
      }
      if (call.method === 'PUT' && call.url.pathname === '/api/manager/announcements/a1') {
        return detail({ id: 'a1', status: 'Draft', title: (call.body as { title: string }).title })
      }
      if (call.method === 'POST' && call.url.pathname.endsWith('/publish')) {
        return json({ code: 'validation_failed', errors: { expiresAt: ['invalid'] } }, 400)
      }
    })
    render(
      <MemoryRouter initialEntries={['/manager/announcements/a1']}>
        <Routes>
          <Route path="/manager/announcements/:id" element={<AnnouncementEditPage />} />
        </Routes>
      </MemoryRouter>,
    )
    await screen.findByDisplayValue('Важное объявление')
    await user.click(screen.getByRole('button', { name: 'Сохранить и опубликовать' }))
    await user.click(within(screen.getByRole('dialog')).getByRole('button', { name: 'Опубликовать' }))
    expect(await screen.findByText('Изменения сохранены, но объявление не опубликовано.')).toBeInTheDocument()
    expect(screen.getByDisplayValue('Важное объявление')).toBeInTheDocument()
    expect(screen.getByRole('heading', { name: 'Редактирование объявления' })).toBeInTheDocument()
  })

  it('shows empty state', async () => {
    mockApi((call) => {
      if (call.url.pathname === '/api/manager/groups') return groups
      if (call.url.pathname === '/api/manager/announcements') return []
    })
    render(<MemoryRouter><AnnouncementsPage /></MemoryRouter>)
    expect(await screen.findByText('Объявлений пока нет')).toBeInTheDocument()
  })
})

describe('student announcements', () => {
  it('shows announcements section with compact empty state on home', async () => {
    const me = { id: 'u1', email: 'a@t.local', role: 'Student', displayName: 'Анна', groupName: 'MBA-01', timeZone: 'Europe/Moscow' }
    const dashboard: Dashboard = {
      nextLesson: null, upcomingLessons: [], pendingSurveys: 0, unreadNotifications: 0, recentGrades: [], recentAnnouncements: [],
    }
    mockApi((call) => {
      if (call.url.pathname === '/api/auth/me') return me
      if (call.url.pathname === '/api/student/dashboard') return dashboard
      if (call.url.pathname === '/api/student/surveys') return []
      if (call.url.pathname === '/api/notifications/unread-count') return { count: 0 }
    })
    render(
      <MemoryRouter>
        <AuthProvider>
          <StudentHomePage />
        </AuthProvider>
      </MemoryRouter>,
    )
    expect(await screen.findByText('Актуальных объявлений нет')).toBeInTheDocument()
    expect(screen.getByRole('link', { name: /Все объявления/ })).toBeInTheDocument()
  })

  it('lists pinned announcements first with preserved line breaks', async () => {
    mockApi((call) => {
      if (call.url.pathname === '/api/student/announcements') return [
        studentItem({ id: 'p1', title: 'Закреплённое', isPinned: true }),
        studentItem({ id: 'p2', title: 'Обычное', isPinned: false }),
      ]
    })
    const { container } = render(<MemoryRouter><StudentAnnouncementsPage /></MemoryRouter>)
    await screen.findByRole('heading', { level: 1, name: 'Объявления' })
    const cards = container.querySelectorAll('.announcement-card h2')
    expect([...cards].map((el) => el.textContent)).toEqual(['Закреплённое', 'Обычное'])
    expect(screen.getAllByText(/вторая строка/).length).toBe(2)
  })
})
