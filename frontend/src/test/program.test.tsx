import { fireEvent, render, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { MemoryRouter } from 'react-router-dom'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import type { Discipline, DisciplineProgram, StudentGrade, StudentLesson, StudentProgram } from '../api/types'
import { setLanguage } from '../i18n'
import { DisciplinesPage } from '../pages/manager/ReferencePage'
import { StudentGradesPage, StudentSchedulePage } from '../pages/student/StudentPages'

interface Call { method: string; url: URL; body: unknown }

const json = (body: unknown, status = 200) =>
  new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } })

function mockApi(handler: (call: Call) => unknown): Call[] {
  const calls: Call[] = []
  vi.stubGlobal('fetch', vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
    const call = { method: init?.method ?? 'GET', url: new URL(String(input), 'http://localhost'), body: init?.body }
    calls.push(call)
    if (call.url.pathname === '/api/auth/csrf') return json({ token: 'test-token' })
    const result = handler(call)
    if (result instanceof Response) return result
    return result === undefined ? new Response(null, { status: 204 }) : json(result)
  }))
  return calls
}

const MAX = 1024 * 1024
const program: DisciplineProgram = { fileName: 'pud-finance.pdf', sizeBytes: 300 * 1024, uploadedAt: '2026-10-01T09:00:00Z' }
const discipline = (overrides: Partial<Discipline> = {}): Discipline => ({
  id: 'd1', name: 'Финансы', description: null, program: null, ...overrides,
})
const pdf = (name = 'new-pud.pdf', size = 2048) => new File([new Uint8Array(size)], name, { type: 'application/pdf' })
const uploads = (calls: Call[]) => calls.filter((c) => c.method === 'POST' && c.url.pathname === '/api/manager/disciplines/d1/program')

function managerApi(initial: Discipline, onUpload?: (call: Call) => unknown) {
  return mockApi((call) => {
    const path = call.url.pathname
    if (path === '/api/manager/disciplines/program-limits') return { maxBytes: MAX }
    if (path === '/api/manager/disciplines') return [initial]
    if (path === '/api/manager/disciplines/d1/program' && call.method === 'POST') {
      return onUpload ? onUpload(call) : { fileName: 'new-pud.pdf', sizeBytes: 2048, uploadedAt: '2026-10-02T09:00:00Z' }
    }
    if (path === '/api/manager/disciplines/d1/program' && call.method === 'DELETE') return new Response(null, { status: 204 })
  })
}

async function openEdit() {
  const user = userEvent.setup()
  render(<MemoryRouter><DisciplinesPage /></MemoryRouter>)
  await screen.findByText('Финансы')
  await user.click(screen.getByRole('button', { name: 'Изменить' }))
  const dialog = screen.getByRole('dialog')
  const section = within(dialog).getByRole('region', { name: 'Программа учебной дисциплины (ПУД)' })
  return { user, dialog, section }
}

const fileInput = (section: HTMLElement) => section.querySelector<HTMLInputElement>('input[type="file"]')!
const choose = (section: HTMLElement, file: File) => fireEvent.change(fileInput(section), { target: { files: [file] } })

beforeEach(() => setLanguage('ru'))
afterEach(() => vi.unstubAllGlobals())

describe('manager discipline programme (ПУД)', () => {
  it('shows the empty state and uploads a PDF, then offers viewing', async () => {
    const calls = managerApi(discipline())
    const { user, section } = await openEdit()
    expect(within(section).getByText('ПУД не загружена.')).toBeInTheDocument()
    expect(await within(section).findByText('Только PDF, не больше 1 МБ.')).toBeInTheDocument()
    expect(within(section).queryByRole('button', { name: 'Просмотреть ПУД' })).toBeNull()
    expect(fileInput(section).accept).toContain('application/pdf')

    choose(section, pdf())
    expect(await within(section).findByText('new-pud.pdf')).toBeInTheDocument()
    expect(uploads(calls)).toHaveLength(1)
    expect((uploads(calls)[0].body as FormData).get('file')).toBeInstanceOf(File)

    await user.click(within(section).getByRole('button', { name: 'Просмотреть ПУД' }))
    const frame = section.querySelector('object')!
    expect(frame.getAttribute('type')).toBe('application/pdf')
    expect(frame.getAttribute('data')).toMatch(/^\/api\/manager\/disciplines\/d1\/program\?v=/)
    const link = within(section).getByRole('link', { name: 'Открыть в новой вкладке' })
    expect(link).toHaveAttribute('target', '_blank')
    expect(link.getAttribute('href')).toBe(frame.getAttribute('data'))
  })

  it('rejects non-PDF and oversize files before uploading', async () => {
    const calls = managerApi(discipline())
    const { section } = await openEdit()
    await within(section).findByText('Только PDF, не больше 1 МБ.')

    choose(section, new File(['x'], 'notes.docx'))
    expect(await within(section).findByText(/Файл не является PDF-документом/)).toBeInTheDocument()
    choose(section, pdf('big.pdf', MAX + 1))
    expect(await within(section).findByText('Файл слишком большой.')).toBeInTheDocument()
    expect(uploads(calls)).toHaveLength(0)
    expect(within(section).getByText('ПУД не загружена.')).toBeInTheDocument()
  })

  it('replaces only after confirmation and keeps the old file when replacement fails', async () => {
    const calls = managerApi(discipline({ program }), () => json({ code: 'file_not_pdf' }, 400))
    const { user, section } = await openEdit()
    expect(within(section).getByText('pud-finance.pdf')).toBeInTheDocument()

    choose(section, pdf())
    let confirmDialog = await screen.findByRole('dialog', { name: 'Заменить ПУД?' })
    await user.click(within(confirmDialog).getByRole('button', { name: 'Отмена' }))
    expect(uploads(calls)).toHaveLength(0)

    choose(section, pdf())
    confirmDialog = await screen.findByRole('dialog', { name: 'Заменить ПУД?' })
    await user.click(within(confirmDialog).getByRole('button', { name: 'Заменить' }))
    expect(await within(section).findByText(/Файл не является PDF-документом/)).toBeInTheDocument()
    expect(uploads(calls)).toHaveLength(1)
    expect(within(section).getByText('pud-finance.pdf')).toBeInTheDocument()
    expect(within(section).getByRole('button', { name: 'Просмотреть ПУД' })).toBeInTheDocument()
  })

  it('maps a proxy 413 without a JSON body to the size error', async () => {
    managerApi(discipline(), () => new Response('<html>413</html>', { status: 413 }))
    const { section } = await openEdit()
    choose(section, pdf())
    expect(await within(section).findByText('Файл слишком большой.')).toBeInTheDocument()
  })

  it('deletes with confirmation and returns to the empty state', async () => {
    const calls = managerApi(discipline({ program }))
    const { user, section } = await openEdit()
    await user.click(within(section).getByRole('button', { name: 'Удалить' }))
    const confirmDialog = await screen.findByRole('dialog', { name: 'Удалить ПУД?' })
    expect(within(confirmDialog).getByText(/pud-finance\.pdf/)).toBeInTheDocument()
    await user.click(within(confirmDialog).getByRole('button', { name: 'Удалить' }))
    expect(await within(section).findByText('ПУД не загружена.')).toBeInTheDocument()
    expect(calls.some((c) => c.method === 'DELETE' && c.url.pathname === '/api/manager/disciplines/d1/program')).toBe(true)
  })

  it('shows the programme column and explains that a new discipline must be saved first', async () => {
    const user = userEvent.setup()
    managerApi(discipline({ program }))
    render(<MemoryRouter><DisciplinesPage /></MemoryRouter>)
    expect(await screen.findByRole('columnheader', { name: 'ПУД' })).toBeInTheDocument()
    expect(screen.getByText('pud-finance.pdf')).toBeInTheDocument()
    await user.click(screen.getByRole('button', { name: 'Новая дисциплина' }))
    expect(within(screen.getByRole('dialog')).getByText('ПУД можно будет загрузить после сохранения дисциплины.')).toBeInTheDocument()
  })
})

const lesson = (overrides: Partial<StudentLesson> = {}): StudentLesson => ({
  id: 'l1', disciplineId: 'd1', disciplineName: 'Финансы', teacherName: 'Иванов И.', startsAt: '2099-01-10T07:00:00Z',
  endsAt: '2099-01-10T08:30:00Z', startsAtLocal: '2099-01-10T10:00:00', endsAtLocal: '2099-01-10T11:30:00',
  format: null, location: null, comment: null, status: 'Scheduled', ...overrides,
})
const studentPrograms: StudentProgram[] = [{ disciplineId: 'd1', disciplineName: 'Финансы', fileName: 'pud-finance.pdf' }]

describe('student programme (ПУД)', () => {
  it('offers the programme on lesson cards that have one and opens a viewer', async () => {
    const user = userEvent.setup()
    mockApi((call) => {
      if (call.url.pathname === '/api/student/schedule') return [
        lesson(),
        lesson({ id: 'l2', disciplineId: 'd2', disciplineName: 'Маркетинг', startsAt: '2099-01-11T07:00:00Z', endsAt: '2099-01-11T08:30:00Z' }),
      ]
      if (call.url.pathname === '/api/student/programs') return studentPrograms
    })
    render(<MemoryRouter><StudentSchedulePage /></MemoryRouter>)
    await screen.findByText('Маркетинг')
    const buttons = await screen.findAllByRole('button', { name: 'Просмотреть ПУД' })
    expect(buttons).toHaveLength(1)

    await user.click(buttons[0])
    const viewer = screen.getByRole('dialog', { name: 'ПУД: pud-finance.pdf' })
    expect(viewer.querySelector('object')!.getAttribute('data')).toBe('/api/student/disciplines/d1/program')
    expect(within(viewer).getByRole('link', { name: 'Открыть в новой вкладке' }))
      .toHaveAttribute('href', '/api/student/disciplines/d1/program')
    expect(within(viewer).queryByRole('button', { name: /Загрузить|Заменить|Удалить/ })).toBeNull()
  })

  it('offers the programme in the grades table', async () => {
    const grades: StudentGrade[] = [
      { id: 'gr1', disciplineId: 'd1', disciplineName: 'Финансы', periodName: 'Модуль 1', value: 90, publishedAt: '2026-10-01T09:00:00Z' },
      { id: 'gr2', disciplineId: 'd2', disciplineName: 'Маркетинг', periodName: 'Модуль 1', value: 80, publishedAt: '2026-10-01T09:00:00Z' },
    ]
    mockApi((call) => {
      if (call.url.pathname === '/api/student/grades') return grades
      if (call.url.pathname === '/api/student/programs') return studentPrograms
    })
    render(<MemoryRouter><StudentGradesPage /></MemoryRouter>)
    await screen.findByText('Маркетинг')
    await waitFor(() => expect(screen.getAllByRole('button', { name: 'Просмотреть ПУД' })).toHaveLength(1))
    const row = screen.getByText('Финансы').closest('tr')!
    expect(within(row).getByRole('button', { name: 'Просмотреть ПУД' })).toBeInTheDocument()
  })

  it('hides the action when programmes cannot be loaded', async () => {
    mockApi((call) => {
      if (call.url.pathname === '/api/student/schedule') return [lesson()]
      if (call.url.pathname === '/api/student/programs') return json({ code: 'server_error' }, 500)
    })
    render(<MemoryRouter><StudentSchedulePage /></MemoryRouter>)
    await screen.findByText('Финансы')
    await new Promise((r) => setTimeout(r, 20))
    expect(screen.queryByRole('button', { name: 'Просмотреть ПУД' })).toBeNull()
  })
})
