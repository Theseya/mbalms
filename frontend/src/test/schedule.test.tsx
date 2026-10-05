import type { ReactElement } from 'react'
import { fireEvent, render, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { MemoryRouter } from 'react-router-dom'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import type { Discipline, Group, Lesson, LessonOverlap, StudentLesson, Teacher } from '../api/types'
import { setLanguage } from '../i18n'
import { formatLocalDate, formatLocalTime } from '../lib/format'
import { SchedulePage } from '../pages/manager/SchedulePage'
import { StudentSchedulePage } from '../pages/student/StudentPages'

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

const groups: Group[] = [{ id: 'g1', name: 'MBA-01', startDate: null, endDate: null, status: 'Active', archivedAt: null, studentCount: 2 }]
const disciplines: Discipline[] = [{ id: 'd1', name: 'Корпоративные финансы', description: null }]
const teachers: Teacher[] = [{ id: 't1', lastName: 'Петрова', firstName: 'Анна', middleName: null, fullName: 'Петрова Анна', email: null }]

const lesson = (overrides: Partial<Lesson> = {}): Lesson => ({
  id: 'l1', groupId: 'g1', groupName: 'MBA-01', groupStatus: 'Active', disciplineId: 'd1', disciplineName: 'Корпоративные финансы',
  teacherId: 't1', teacherName: 'Петрова Анна', startsAt: '2030-04-01T06:30:00Z', endsAt: '2030-04-01T08:00:00Z',
  startsAtLocal: '2030-04-01T09:30:00', endsAtLocal: '2030-04-01T11:00:00', format: 'Hybrid', location: 'Ауд. 305',
  comment: null, status: 'Scheduled', ...overrides,
})

function references(call: Call, lessons: Lesson[] = [], overlaps: LessonOverlap[] = []): unknown {
  const path = call.url.pathname
  if (path === '/api/manager/groups') return groups
  if (path === '/api/manager/disciplines') return disciplines
  if (path === '/api/manager/teachers') return teachers
  if (path === '/api/manager/lessons/overlaps') return overlaps
  if (path === '/api/manager/lessons') return call.method === 'GET' ? lessons : lesson()
  return undefined
}

async function fillForm(dialog: HTMLElement, date: string, start: string, end: string) {
  await userEvent.selectOptions(within(dialog).getByLabelText(/^Группа/), 'g1')
  await userEvent.selectOptions(within(dialog).getByLabelText(/^Дисциплина/), 'd1')
  await userEvent.selectOptions(within(dialog).getByLabelText(/^Преподаватель/), 't1')
  fireEvent.change(within(dialog).getByLabelText(/^Дата/), { target: { value: date } })
  fireEvent.change(within(dialog).getByLabelText(/^Начало/), { target: { value: start } })
  fireEvent.change(within(dialog).getByLabelText(/^Окончание/), { target: { value: end } })
}

const renderPage = (page: ReactElement) => render(<MemoryRouter>{page}</MemoryRouter>)

beforeEach(() => setLanguage('ru'))
afterEach(() => vi.unstubAllGlobals())

describe('SchedulePage', () => {
  it('sends wall-clock times of the app time zone without an offset, with format, place, comment and status', async () => {
    const calls = mockApi((c) => references(c))
    renderPage(<SchedulePage />)

    await userEvent.click(await screen.findByRole('button', { name: 'Новое занятие' }))
    const dialog = screen.getByRole('dialog', { name: 'Новое занятие' })
    expect(dialog).toHaveTextContent('Europe/Moscow')
    await fillForm(dialog, '2030-04-01', '09:30', '11:00')
    await userEvent.selectOptions(within(dialog).getByLabelText('Формат'), 'Hybrid')
    await userEvent.type(within(dialog).getByLabelText('Место / ссылка'), 'Ауд. 305')
    await userEvent.type(within(dialog).getByLabelText('Комментарий'), 'Принести кейс')
    await userEvent.selectOptions(within(dialog).getByLabelText('Статус занятия'), 'Cancelled')
    expect(within(dialog).getByText(/Отменённое занятие остаётся в расписании студентов/)).toBeInTheDocument()
    await userEvent.click(within(dialog).getByRole('button', { name: 'Сохранить' }))

    await waitFor(() => expect(calls.find((c) => c.method === 'POST' && c.url.pathname === '/api/manager/lessons')).toBeDefined())
    expect(calls.find((c) => c.method === 'POST' && c.url.pathname === '/api/manager/lessons')?.body).toEqual({
      groupId: 'g1', disciplineId: 'd1', teacherId: 't1', startsAt: '2030-04-01T09:30', endsAt: '2030-04-01T11:00',
      format: 'Hybrid', location: 'Ауд. 305', comment: 'Принести кейс', status: 'Cancelled',
    })
  })

  it('shows the server time validation error under the end field', async () => {
    mockApi((c) => (c.method === 'POST'
      ? json({ code: 'validation_failed', errors: { endsAt: ['end_before_start'] } }, 400)
      : references(c)))
    renderPage(<SchedulePage />)

    await userEvent.click(await screen.findByRole('button', { name: 'Новое занятие' }))
    const dialog = screen.getByRole('dialog', { name: 'Новое занятие' })
    await fillForm(dialog, '2030-04-01', '12:00', '11:00')
    await userEvent.click(within(dialog).getByRole('button', { name: 'Сохранить' }))

    const end = within(dialog).getByLabelText(/^Окончание/)
    expect(await within(dialog).findByText('Окончание должно быть позже начала')).toBeInTheDocument()
    expect(end.getAttribute('aria-describedby')).toBeTruthy()
  })

  it('warns about overlaps of the group or teacher but still allows saving', async () => {
    const overlap: LessonOverlap = {
      id: 'l9', groupName: 'MBA-01', disciplineName: 'Маркетинг', teacherName: 'Сидоров Олег',
      startsAtLocal: '2030-04-01T10:00:00', endsAtLocal: '2030-04-01T11:30:00', sameGroup: true, sameTeacher: false,
    }
    const calls = mockApi((c) => references(c, [], [overlap]))
    renderPage(<SchedulePage />)

    await userEvent.click(await screen.findByRole('button', { name: 'Новое занятие' }))
    const dialog = screen.getByRole('dialog', { name: 'Новое занятие' })
    await fillForm(dialog, '2030-04-01', '09:30', '11:00')

    const warning = await within(dialog).findByText('Пересечение по времени')
    expect(warning.closest('[role="status"]')).toHaveTextContent('10:00–11:30 · Маркетинг · MBA-01 · Сидоров Олег (у этой группы)')
    const check = calls.filter((c) => c.url.pathname === '/api/manager/lessons/overlaps').at(-1)!
    expect(Object.fromEntries(check.url.searchParams)).toEqual({
      startsAt: '2030-04-01T09:30', endsAt: '2030-04-01T11:00', groupId: 'g1', teacherId: 't1',
    })

    await userEvent.click(within(dialog).getByRole('button', { name: 'Сохранить' }))
    await waitFor(() => expect(calls.some((c) => c.method === 'POST' && c.url.pathname === '/api/manager/lessons')).toBe(true))
  })

  it('shows the status and asks before deleting; cancel keeps the lesson', async () => {
    const calls = mockApi((c) => references(c, [lesson({ status: 'Cancelled' })]))
    renderPage(<SchedulePage />)

    const row = (await screen.findByText('Корпоративные финансы')).closest('tr')!
    expect(within(row).getByText('Отменено')).toBeInTheDocument()
    expect(within(row).getByText('09:30–11:00')).toBeInTheDocument()

    await userEvent.click(within(row).getByRole('button', { name: 'Удалить' }))
    const dialog = screen.getByRole('dialog', { name: 'Удалить занятие' })
    expect(dialog).toHaveTextContent('Удалить занятие «Корпоративные финансы» 01.04.2030, 09:30 (группа MBA-01)?')
    await userEvent.click(within(dialog).getByRole('button', { name: 'Отмена' }))
    expect(calls.some((c) => c.method === 'DELETE')).toBe(false)
  })

  it('passes the group and date range filters to the Excel export', async () => {
    const calls = mockApi((c) => (c.url.pathname === '/api/manager/exports/schedule'
      ? new Response(new Blob(['xlsx']), { status: 200, headers: { 'Content-Disposition': 'attachment; filename=schedule.xlsx' } })
      : references(c)))
    URL.createObjectURL = vi.fn(() => 'blob:x')
    URL.revokeObjectURL = vi.fn()
    renderPage(<SchedulePage />)

    await userEvent.selectOptions(await screen.findByLabelText('Группа'), 'g1')
    fireEvent.change(screen.getByLabelText('С'), { target: { value: '2030-04-01' } })
    fireEvent.change(screen.getByLabelText('По'), { target: { value: '2030-04-30' } })
    await userEvent.click(screen.getByRole('button', { name: 'Выгрузить в Excel' }))

    await waitFor(() => expect(calls.some((c) => c.url.pathname === '/api/manager/exports/schedule')).toBe(true))
    const params = Object.fromEntries(calls.find((c) => c.url.pathname === '/api/manager/exports/schedule')!.url.searchParams)
    expect(params).toEqual({ groupId: 'g1', from: '2030-04-01', to: '2030-04-30', lang: 'ru' })
  })
})

describe('StudentSchedulePage', () => {
  it('marks cancelled lessons and shows times as entered in the app time zone', async () => {
    const own: StudentLesson[] = [
      { id: 'a', disciplineName: 'Стратегия', teacherName: 'Петрова Анна', startsAt: '2031-05-01T07:00:00Z', endsAt: '2031-05-01T08:00:00Z',
        startsAtLocal: '2031-05-01T10:00:00', endsAtLocal: '2031-05-01T11:00:00', format: null, location: null, comment: null, status: 'Cancelled' },
      { id: 'b', disciplineName: 'Маркетинг', teacherName: 'Петрова Анна', startsAt: '2031-05-01T21:30:00Z', endsAt: '2031-05-01T22:30:00Z',
        startsAtLocal: '2031-05-02T00:30:00', endsAtLocal: '2031-05-02T01:30:00', format: null, location: null, comment: null, status: 'Scheduled' },
    ]
    mockApi(() => own)
    renderPage(<StudentSchedulePage />)

    const cancelled = (await screen.findByRole('heading', { name: 'Стратегия' })).closest('article')!
    expect(within(cancelled).getByText('Отменено')).toBeInTheDocument()
    expect(cancelled).toHaveClass('lesson-cancelled')
    const scheduled = screen.getByRole('heading', { name: 'Маркетинг' }).closest('article')!
    expect(scheduled).toHaveTextContent('00:30–01:30')
    expect(within(scheduled).queryByText('Отменено')).not.toBeInTheDocument()
  })

  it('formats wall-clock values without shifting them by the browser time zone', () => {
    expect(formatLocalTime('2031-05-02T00:30:00')).toBe('00:30')
    expect(formatLocalDate('2031-05-02T00:30:00')).toBe('02.05.2031')
  })
})
