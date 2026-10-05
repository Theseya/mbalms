import { fireEvent, render, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { MemoryRouter } from 'react-router-dom'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import type { Discipline, Grade, GradeSheet, GradeSheetRow, Group, Period } from '../api/types'
import { setLanguage } from '../i18n'
import { GradebookPage } from '../pages/manager/GradebookPage'
import { GradesPage } from '../pages/manager/GradesPage'

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

const groups: Group[] = [{ id: 'g1', name: 'MBA-01', startDate: null, endDate: null, status: 'Active', archivedAt: null, studentCount: 3 }]
const disciplines: Discipline[] = [{ id: 'd1', name: 'Финансы', description: null }]
const periods: Period[] = [{ id: 'p1', name: 'Семестр 1', startDate: '2026-09-01', endDate: '2026-12-31' }]
const sheetUrl = '/api/manager/grades/sheet/g1/d1/p1'

const rows: GradeSheetRow[] = [
  { studentId: 's1', studentName: 'Аннина Иван', gradeId: null, value: null, status: null, publishedAt: null },
  { studentId: 's2', studentName: 'Борисов Иван', gradeId: 'gr2', value: 40, status: 'Draft', publishedAt: null },
  { studentId: 's3', studentName: 'Волков Иван', gradeId: 'gr3', value: 60, status: 'Published', publishedAt: '2026-10-01T09:00:00Z' },
]
const sheet = (overrides: Partial<GradeSheet> = {}): GradeSheet => ({
  groupId: 'g1', groupName: 'MBA-01', groupStatus: 'Active', disciplineId: 'd1', disciplineName: 'Финансы',
  periodId: 'p1', periodName: 'Семестр 1', rows, ...overrides,
})

function gradebookApi(current: () => GradeSheet = () => sheet()) {
  return mockApi((c) => {
    const path = c.url.pathname
    if (path === '/api/manager/groups') return groups
    if (path === '/api/manager/disciplines') return disciplines
    if (path === '/api/manager/periods') return periods
    if (path === sheetUrl) return current()
    return undefined
  })
}

async function openSheet() {
  render(<MemoryRouter><GradebookPage /></MemoryRouter>)
  await userEvent.selectOptions(await screen.findByLabelText(/^Группа/), 'g1')
  await userEvent.selectOptions(screen.getByLabelText(/^Дисциплина/), 'd1')
  await userEvent.selectOptions(screen.getByLabelText(/^Учебный период/), 'p1')
  await screen.findByText('Аннина Иван')
}

const input = (name: string) => screen.getByLabelText(`Оценка: ${name}`) as HTMLInputElement
const type = (name: string, value: string) => fireEvent.change(input(name), { target: { value } })
const puts = (calls: Call[]) => calls.filter((c) => c.method === 'PUT')

beforeEach(() => setLanguage('ru'))
afterEach(() => vi.unstubAllGlobals())

describe('GradebookPage', () => {
  it('lists every student of the selected group with values and statuses', async () => {
    gradebookApi()
    await openSheet()
    expect(input('Аннина Иван').value).toBe('')
    expect(input('Борисов Иван').value).toBe('40')
    expect(input('Волков Иван').value).toBe('60')
    expect(screen.getByText('Нет оценки')).toBeInTheDocument()
    expect(screen.getByText('Черновик')).toBeInTheDocument()
    expect(screen.getByText('Опубликована')).toBeInTheDocument()
  })

  it('saves boundary values 0 and 100 and sends only changed rows', async () => {
    const calls = gradebookApi()
    await openSheet()
    type('Аннина Иван', '0')
    type('Борисов Иван', '100')
    expect(screen.getByText('Несохранённых изменений: 2')).toBeInTheDocument()
    await userEvent.click(screen.getByRole('button', { name: 'Сохранить' }))

    await waitFor(() => expect(puts(calls)).toHaveLength(1))
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument()
    expect(puts(calls)[0].url.pathname).toBe(sheetUrl)
    expect(puts(calls)[0].body).toEqual({
      entries: [{ studentId: 's1', value: 0 }, { studentId: 's2', value: 100 }],
      confirmPublishedChanges: false,
    })
    expect(await screen.findByText('Ведомость сохранена.')).toBeInTheDocument()
  })

  it.each(['-1', '101', '50.5'])('rejects %s locally without calling the API', async (value) => {
    const calls = gradebookApi()
    await openSheet()
    type('Аннина Иван', value)
    expect(input('Аннина Иван')).toHaveAttribute('aria-invalid', 'true')
    expect(screen.getByText(value === '50.5' ? 'Введите целое число' : 'Значение вне допустимого диапазона')).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Сохранить' })).toBeDisabled()
    expect(puts(calls)).toHaveLength(0)
  })

  it('asks for confirmation before changing a published grade', async () => {
    const calls = gradebookApi()
    await openSheet()
    type('Волков Иван', '75')
    await userEvent.click(screen.getByRole('button', { name: 'Сохранить' }))

    let dialog = await screen.findByRole('dialog', { name: 'Изменить опубликованные оценки' })
    expect(within(dialog).getByText('Волков Иван: 60 → 75')).toBeInTheDocument()
    await userEvent.click(within(dialog).getByRole('button', { name: 'Отмена' }))
    expect(puts(calls)).toHaveLength(0)

    await userEvent.click(screen.getByRole('button', { name: 'Сохранить' }))
    dialog = await screen.findByRole('dialog', { name: 'Изменить опубликованные оценки' })
    await userEvent.click(within(dialog).getByRole('button', { name: 'Сохранить изменения' }))
    await waitFor(() => expect(puts(calls)).toHaveLength(1))
    expect(puts(calls)[0].body).toEqual({ entries: [{ studentId: 's3', value: 75 }], confirmPublishedChanges: true })
  })

  it('publishes drafts after confirmation, but only when nothing is unsaved', async () => {
    const calls = gradebookApi()
    await openSheet()
    const publish = screen.getByRole('button', { name: 'Опубликовать черновики (1)' })
    type('Аннина Иван', '90')
    expect(publish).toBeDisabled()
    type('Аннина Иван', '')
    expect(publish).toBeEnabled()

    await userEvent.click(publish)
    const dialog = await screen.findByRole('dialog', { name: 'Опубликовать оценки' })
    await userEvent.click(within(dialog).getByRole('button', { name: 'Опубликовать' }))
    await waitFor(() => expect(calls.some((c) => c.method === 'POST' && c.url.pathname === '/api/manager/grades/publish')).toBe(true))
    expect(calls.find((c) => c.url.pathname === '/api/manager/grades/publish')?.body).toEqual({ ids: ['gr2'] })
  })

  it('shows server field errors next to the student', async () => {
    const calls = mockApi((c) => {
      if (c.method === 'PUT') return json({ code: 'validation_failed', errors: { 'entries[0].studentId': ['not_found'] } }, 400)
      const path = c.url.pathname
      return path === '/api/manager/groups' ? groups : path === '/api/manager/disciplines' ? disciplines
        : path === '/api/manager/periods' ? periods : path === sheetUrl ? sheet() : undefined
    })
    await openSheet()
    type('Аннина Иван', '50')
    await userEvent.click(screen.getByRole('button', { name: 'Сохранить' }))
    await waitFor(() => expect(puts(calls)).toHaveLength(1))
    expect(await screen.findByText('Значение не найдено')).toBeInTheDocument()
    expect(input('Аннина Иван')).toHaveAttribute('aria-invalid', 'true')
  })

  it('makes the gradebook of an archived group read-only', async () => {
    gradebookApi(() => sheet({ groupStatus: 'Archived' }))
    await openSheet()
    expect(screen.getByText('Группа в архиве: ведомость доступна только для просмотра.')).toBeInTheDocument()
    expect(input('Аннина Иван')).toBeDisabled()
    expect(screen.queryByRole('button', { name: 'Сохранить' })).not.toBeInTheDocument()
  })
})

describe('GradesPage editing', () => {
  const grade = (overrides: Partial<Grade> = {}): Grade => ({
    id: 'gr3', studentId: 's3', studentName: 'Волков Иван', groupId: 'g1', groupName: 'MBA-01',
    disciplineId: 'd1', disciplineName: 'Финансы', periodId: 'p1', periodName: 'Семестр 1',
    value: 60, status: 'Published', updatedAt: '2026-10-01T09:00:00Z', publishedAt: '2026-10-01T09:00:00Z', ...overrides,
  })

  function gradesApi(list: Grade[]) {
    return mockApi((c) => {
      const path = c.url.pathname
      if (path === '/api/manager/groups') return groups
      if (path === '/api/manager/disciplines') return disciplines
      if (path === '/api/manager/periods') return periods
      if (path === '/api/manager/students') return { items: [], total: 0, page: 1, pageSize: 200 }
      if (path === '/api/manager/grades') return list
      if (c.method === 'PUT') return list[0]
      return undefined
    })
  }

  async function edit(value: string) {
    render(<MemoryRouter><GradesPage /></MemoryRouter>)
    await userEvent.click(await screen.findByRole('button', { name: 'Изменить' }))
    const form = screen.getByRole('dialog', { name: 'Изменение оценки' })
    fireEvent.change(within(form).getByLabelText(/^Оценка \(0–100\)/), { target: { value } })
    await userEvent.click(within(form).getByRole('button', { name: 'Сохранить' }))
  }

  it('requires confirmation to change a published grade and sends the confirmation flag', async () => {
    const calls = gradesApi([grade()])
    await edit('75')
    const dialog = await screen.findByRole('dialog', { name: 'Изменить опубликованную оценку' })
    expect(dialog).toHaveTextContent('Волков Иван, 60 → 75')
    expect(puts(calls)).toHaveLength(0)
    await userEvent.click(within(dialog).getByRole('button', { name: 'Изменить' }))
    await waitFor(() => expect(puts(calls)).toHaveLength(1))
    expect(puts(calls)[0].body).toEqual({ value: 75, confirmPublishedChange: true })
  })

  it('edits drafts without confirmation', async () => {
    const calls = gradesApi([grade({ status: 'Draft', publishedAt: null })])
    await edit('0')
    await waitFor(() => expect(puts(calls)).toHaveLength(1))
    expect(screen.queryByRole('dialog', { name: 'Изменить опубликованную оценку' })).not.toBeInTheDocument()
    expect(puts(calls)[0].body).toEqual({ value: 0, confirmPublishedChange: false })
  })
})
