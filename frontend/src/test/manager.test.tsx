import type { ReactNode } from 'react'
import { act, render, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { MemoryRouter } from 'react-router-dom'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import type { Group, Student, Teacher } from '../api/types'
import { setLanguage } from '../i18n'
import { GroupsPage } from '../pages/manager/GroupsPage'
import { TeachersPage } from '../pages/manager/ReferencePage'
import { StudentsPage } from '../pages/manager/StudentsPage'

interface Call { method: string; url: URL; body: unknown }
type Handler = (call: Call) => unknown

const json = (body: unknown, status = 200) =>
  new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } })

/** Routes fetch calls to `handler` and records them; `undefined` from the handler means 204. */
function mockApi(handler: Handler): Call[] {
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

const group = (i: number, overrides: Partial<Group> = {}): Group => ({
  id: `g${i}`, name: `MBA-${String(i).padStart(2, '0')}`, startDate: null, endDate: null,
  status: 'Active', archivedAt: null, studentCount: 3, ...overrides,
})

const student = (i: number): Student => ({
  id: `s${i}`, lastName: `Иванов${i}`, firstName: 'Иван', middleName: null, fullName: `Иванов${i} Иван`,
  email: `s${i}@test.local`, groupId: 'g1', groupName: 'MBA-01', groupStatus: 'Active',
})

const programAnd = (rest: Handler): Handler => (call) =>
  call.url.pathname === '/api/manager/program' ? { id: 'p1', name: 'Executive MBA' } : rest(call)

const renderPage = (page: ReactNode) => render(<MemoryRouter>{page}</MemoryRouter>)

beforeEach(() => setLanguage('ru'))
afterEach(() => vi.unstubAllGlobals())

describe('GroupsPage', () => {
  it('asks for confirmation with the group name before archiving and does nothing on cancel', async () => {
    const calls = mockApi(programAnd(({ url }) => (url.pathname === '/api/manager/groups' ? [group(1)] : group(1, { status: 'Archived' }))))
    renderPage(<GroupsPage />)

    await userEvent.click(await screen.findByRole('button', { name: 'Архивировать' }))
    const dialog = screen.getByRole('dialog', { name: 'Архивировать группу' })
    expect(dialog).toHaveTextContent('Архивировать группу «MBA-01» (студентов: 3)? Ничего не удаляется')
    await userEvent.click(within(dialog).getByRole('button', { name: 'Отмена' }))
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument()
    expect(calls.some((c) => c.method === 'POST')).toBe(false)

    await userEvent.click(screen.getByRole('button', { name: 'Архивировать' }))
    await userEvent.click(within(screen.getByRole('dialog')).getByRole('button', { name: 'Архивировать' }))
    await waitFor(() => expect(calls.some((c) => c.method === 'POST' && c.url.pathname === '/api/manager/groups/g1/archive')).toBe(true))
  })

  it('confirms deletion of an empty group as a dangerous action', async () => {
    const calls = mockApi(programAnd(({ url }) => (url.pathname === '/api/manager/groups' ? [group(1, { studentCount: 0 })] : undefined)))
    renderPage(<GroupsPage />)

    await userEvent.click(await screen.findByRole('button', { name: 'Удалить' }))
    const dialog = screen.getByRole('dialog', { name: 'Удалить группу' })
    expect(dialog).toHaveTextContent('Удалить группу «MBA-01»? Действие нельзя отменить.')
    await userEvent.click(within(dialog).getByRole('button', { name: 'Удалить' }))
    await waitFor(() => expect(calls.some((c) => c.method === 'DELETE' && c.url.pathname === '/api/manager/groups/g1')).toBe(true))
  })

  it('shows the server duplicate-name error next to the field', async () => {
    mockApi(programAnd(({ method }) => (method === 'POST'
      ? json({ code: 'validation_failed', errors: { name: ['duplicate'] } }, 400)
      : [])))
    renderPage(<GroupsPage />)

    await userEvent.click(await screen.findByRole('button', { name: 'Новая группа' }))
    const dialog = screen.getByRole('dialog', { name: 'Новая группа' })
    await userEvent.type(within(dialog).getByLabelText(/Название/), 'MBA-01')
    await userEvent.click(within(dialog).getByRole('button', { name: 'Сохранить' }))

    expect(await within(dialog).findByText('Уже используется')).toBeInTheDocument()
    expect(within(dialog).getByRole('alert')).toHaveTextContent('Проверьте правильность заполнения полей.')
  })

  it('searches and pages the group table', async () => {
    mockApi(programAnd(() => Array.from({ length: 30 }, (_, i) => group(i + 1))))
    renderPage(<GroupsPage />)

    expect(await screen.findByText('MBA-01')).toBeInTheDocument()
    expect(screen.getAllByRole('row')).toHaveLength(26)
    expect(screen.getByText('1–25 из 30')).toBeInTheDocument()

    await userEvent.click(screen.getByRole('button', { name: 'Вперёд' }))
    expect(screen.getAllByRole('row')).toHaveLength(6)
    expect(screen.getByText('MBA-30')).toBeInTheDocument()

    await userEvent.type(screen.getByLabelText('Поиск'), 'mba-07')
    expect(screen.getAllByRole('row')).toHaveLength(2)
    expect(screen.getByText('MBA-07')).toBeInTheDocument()
    expect(screen.queryByRole('navigation', { name: 'Страницы' })).not.toBeInTheDocument()
  })

  it('renames the single programme', async () => {
    const calls = mockApi(programAnd(() => []))
    renderPage(<GroupsPage />)

    const card = await screen.findByRole('region', { name: 'Программа' })
    expect(card).toHaveTextContent('Executive MBA')
    await userEvent.click(within(card).getByRole('button', { name: 'Переименовать' }))
    const input = within(screen.getByRole('dialog', { name: 'Название программы' })).getByLabelText(/Название/)
    await userEvent.clear(input)
    await userEvent.type(input, '  MBA 2026  ')
    await userEvent.click(screen.getByRole('button', { name: 'Сохранить' }))

    await waitFor(() => expect(calls.find((c) => c.method === 'PUT')?.body).toEqual({ name: 'MBA 2026' }))
    expect(calls.find((c) => c.method === 'PUT')?.url.pathname).toBe('/api/manager/program')
  })

  it('uses English texts after switching the language', async () => {
    mockApi(programAnd(({ url }) => (url.pathname === '/api/manager/groups' ? [group(1)] : undefined)))
    act(() => setLanguage('en'))
    renderPage(<GroupsPage />)

    await userEvent.click(await screen.findByRole('button', { name: 'Archive' }))
    expect(screen.getByRole('dialog', { name: 'Archive group' })).toHaveTextContent('Archive group "MBA-01" (3 students)?')
    expect(screen.getByRole('region', { name: 'Programme' })).toBeInTheDocument()
  })
})

describe('StudentsPage', () => {
  const studentsHandler: Handler = ({ url, method }) => {
    if (url.pathname === '/api/manager/groups') return [group(1)]
    if (method === 'DELETE') return undefined
    const page = Number(url.searchParams.get('page'))
    const items = page === 2 ? [student(26)] : Array.from({ length: 25 }, (_, i) => student(i + 1))
    return { items, total: 26, page, pageSize: 25 }
  }

  it('sends search and page to the server and resets to the first page on a new search', async () => {
    const calls = mockApi(studentsHandler)
    renderPage(<StudentsPage />)
    const listCalls = () => calls.filter((c) => c.url.pathname === '/api/manager/students')

    expect(await screen.findByText('Иванов1 Иван')).toBeInTheDocument()
    expect(listCalls()[0].url.searchParams.get('pageSize')).toBe('25')
    expect(screen.getByText('1–25 из 26')).toBeInTheDocument()

    await userEvent.click(screen.getByRole('button', { name: 'Вперёд' }))
    expect(await screen.findByText('Иванов26 Иван')).toBeInTheDocument()
    expect(listCalls().at(-1)?.url.searchParams.get('page')).toBe('2')

    await userEvent.type(screen.getByLabelText('Поиск'), '  петр ')
    await waitFor(() => expect(listCalls().at(-1)?.url.searchParams.get('search')).toBe('петр'))
    expect(listCalls().at(-1)?.url.searchParams.get('page')).toBe('1')
  })

  it('confirms student deletion showing name and email', async () => {
    const calls = mockApi(studentsHandler)
    renderPage(<StudentsPage />)

    const row = (await screen.findByText('Иванов1 Иван')).closest('tr')!
    await userEvent.click(within(row).getByRole('button', { name: 'Удалить' }))
    const dialog = screen.getByRole('dialog', { name: 'Удалить студента' })
    expect(dialog).toHaveTextContent('Удалить студента «Иванов1 Иван» (s1@test.local)? Учётная запись будет удалена')
    await userEvent.click(within(dialog).getByRole('button', { name: 'Удалить' }))
    await waitFor(() => expect(calls.some((c) => c.method === 'DELETE' && c.url.pathname === '/api/manager/students/s1')).toBe(true))
  })
})

describe('TeachersPage', () => {
  const teachers: Teacher[] = [
    { id: 't1', lastName: 'Петрова', firstName: 'Анна', middleName: null, fullName: 'Петрова Анна', email: null },
    { id: 't2', lastName: 'Сидоров', firstName: 'Олег', middleName: null, fullName: 'Сидоров Олег', email: 'o@test.local' },
  ]

  it('filters by name and does not delete without confirmation', async () => {
    const calls = mockApi(() => teachers)
    renderPage(<TeachersPage />)

    await userEvent.type(await screen.findByLabelText('Поиск'), 'сидор')
    expect(screen.queryByText('Петрова Анна')).not.toBeInTheDocument()
    await userEvent.click(screen.getByRole('button', { name: 'Удалить' }))
    const dialog = screen.getByRole('dialog', { name: 'Удалить запись' })
    expect(dialog).toHaveTextContent('Удалить «Сидоров Олег»?')
    await userEvent.keyboard('{Escape}')
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument()
    expect(calls.some((c) => c.method === 'DELETE')).toBe(false)
  })
})
