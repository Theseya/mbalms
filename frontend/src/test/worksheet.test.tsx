import { fireEvent, render, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { MemoryRouter } from 'react-router-dom'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import type { AssessmentSettings, Discipline, Group, Period, TransferPreview, WorkSheet, WorkSheetRow } from '../api/types'
import { setLanguage } from '../i18n'
import { parseScore } from '../lib/worksheet'
import { WorkSheetPage } from '../pages/manager/WorkSheetPage'

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
const periods: Period[] = [
  { id: 'p1', name: 'Семестр 1', startDate: '2026-09-01', endDate: '2026-12-31' },
  { id: 'p2', name: 'Семестр 2', startDate: '2027-01-10', endDate: '2027-05-31' },
]
const sheetUrl = '/api/manager/worksheets/g1/d1/p1'
const springUrl = '/api/manager/worksheets/g1/d1/p2'
const settingsUrl = '/api/manager/worksheets/disciplines/d1/settings'

const settings: AssessmentSettings = {
  disciplineId: 'd1', disciplineName: 'Финансы', attendanceThreshold: 80, attendanceWeight: 0,
  calculationMode: 'Weighted', includeAttendanceInAverage: false, averageTerms: ['Кейс', 'Экзамен'],
  weightTotal: 100, weightsComplete: true, readyForTransfer: true,
  categories: [
    { id: 'c1', name: 'Кейс', weight: 40, kind: 'Manual', includeInAverage: true, legacyManualAttendance: false, works: [{ id: 'e1', name: 'Кейс', scoreCount: 4 }] },
    { id: 'c2', name: 'Экзамен', weight: 60, kind: 'Manual', includeInAverage: true, legacyManualAttendance: false, works: [{ id: 'e2', name: 'Экзамен', scoreCount: 3 }] },
    { id: 'c-att', name: 'Посещаемость', weight: 0, kind: 'Attendance', includeInAverage: true, legacyManualAttendance: false, works: [] },
  ],
}

const base: Omit<WorkSheetRow, 'studentId' | 'studentName'> = {
  inGroup: true, currentGroupName: null, scores: {}, categoryScores: {}, attendedLessons: null, attendancePercent: null,
  attendanceScore: null, attendanceContribution: null, attendanceStatus: 'Unknown',
  calculationState: 'MissingScores', missingCount: 2, calculatedRaw: null, calculatedRounded: null, manualFinal: null, final: null,
  transferredFinal: null, transferredAt: null, grade: null,
}
const rows: WorkSheetRow[] = [
  {
    ...base, studentId: 's1', studentName: 'Аннина Иван', scores: { e1: 7.5, e2: 7.4 }, attendedLessons: 8, attendancePercent: 80,
    attendanceStatus: 'Met', calculationState: 'Calculated', missingCount: 0, calculatedRaw: 7.44, calculatedRounded: 7, final: 7,
  },
  {
    ...base, studentId: 's2', studentName: 'Борисов Иван', scores: { e1: 9, e2: 0 }, attendedLessons: 7, attendancePercent: 70,
    attendanceStatus: 'NotMet', calculationState: 'Calculated', missingCount: 0, calculatedRaw: 3.6, calculatedRounded: 4,
    manualFinal: 6, final: 6, transferredFinal: 6, transferredAt: '2026-10-10T09:00:00Z',
    grade: { id: 'gr2', value: 60, status: 'Published' },
  },
  { ...base, studentId: 's3', studentName: 'Волков Иван', scores: { e1: 5 }, missingCount: 1 },
  {
    ...base, studentId: 's4', studentName: 'Григорьев Иван', inGroup: false, currentGroupName: 'MBA-02', scores: { e1: 8, e2: 8 },
    calculationState: 'Calculated', missingCount: 0, calculatedRaw: 8, calculatedRounded: 8, final: 8,
  },
]
const sheet = (overrides: Partial<WorkSheet> = {}): WorkSheet => ({
  id: 'ws1', groupId: 'g1', groupName: 'MBA-01', groupStatus: 'Active', disciplineId: 'd1', disciplineName: 'Финансы',
  periodId: 'p1', periodName: 'Семестр 1', totalLessons: 10, attendanceThreshold: 80, attendanceWeight: 0,
  categories: settings.categories.map((c) => ({ id: c.id, name: c.name, weight: c.weight, kind: c.kind, includeInAverage: c.includeInAverage, legacyManualAttendance: c.legacyManualAttendance, works: c.works.map((w) => ({ id: w.id, name: w.name })) })),
  calculationMode: 'Weighted', includeAttendanceInAverage: false, averageTerms: ['Кейс', 'Экзамен'],
  weightTotal: 100, weightsComplete: true, readyForTransfer: true, hasTransfers: true, rows, ...overrides,
})
const preview: TransferPreview = {
  periodId: 'p1', periodName: 'Семестр 1', factor: 10,
  items: [
    { studentId: 's1', studentName: 'Аннина Иван', final: 7, manual: false, value: 70, change: 'New', currentValue: null, currentStatus: null },
    { studentId: 's2', studentName: 'Борисов Иван', final: 5, manual: true, value: 50, change: 'Changed', currentValue: 60, currentStatus: 'Published' },
  ],
  skipped: [
    { studentId: 's3', studentName: 'Волков Иван', reason: 'MissingScores', missingCount: 1 },
    { studentId: 's4', studentName: 'Григорьев Иван', reason: 'NotInGroup', missingCount: 0 },
  ],
}

function worksheetApi(current: () => WorkSheet = () => sheet(), extra: (c: Call) => unknown = () => undefined) {
  return mockApi((c) => {
    const path = c.url.pathname
    const handled = extra(c)
    if (handled !== undefined) return handled
    if (path === '/api/manager/groups') return groups
    if (path === '/api/manager/disciplines') return disciplines
    if (path === '/api/manager/periods') return periods
    if (path === '/api/manager/worksheets') {
      return [{ id: 'ws1', groupId: 'g1', disciplineId: 'd1', periodId: 'p1', periodName: 'Семестр 1', hasTransfers: true, updatedAt: '2026-10-10T09:00:00Z' }]
    }
    if (path === settingsUrl) {
      if (c.method === 'PUT') {
        const body = c.body as {
          confirmScoreRemoval?: boolean
          categories?: { works?: { id?: string | null }[] }[]
        }
        const kept = new Set((body.categories ?? []).flatMap((cat) => (cat.works ?? []).map((w) => w.id).filter(Boolean)))
        const manuals = settings.categories.filter((cat) => cat.kind === 'Manual')
        const works = manuals.flatMap((cat) => cat.works.filter((w) => !kept.has(w.id))
          .map((w) => ({ id: w.id, name: w.name, categoryName: cat.name, scoreCount: w.scoreCount })))
        const scored = works.filter((w) => w.scoreCount > 0)
        if (scored.length > 0 && !body.confirmScoreRemoval) {
          return json({
            code: 'scores_removal_not_confirmed',
            details: {
              categories: manuals.filter((cat) => cat.works.length > 0 && cat.works.every((w) => !kept.has(w.id)))
                .map((cat) => ({
                  id: cat.id, name: cat.name, scoreCount: cat.works.reduce((sum, w) => sum + w.scoreCount, 0),
                })),
              works: scored,
              scoreCount: scored.reduce((sum, w) => sum + w.scoreCount, 0),
            },
          }, 409)
        }
      }
      return settings
    }
    if (path === sheetUrl || path === `${sheetUrl}/rows`) return current()
    if (path === `${sheetUrl}/transfer` && c.method === 'GET') return preview
    if (path === `${sheetUrl}/transfer`) return { created: 1, updated: 1, unchanged: 0 }
    return undefined
  })
}

async function openSheet() {
  render(<MemoryRouter><WorkSheetPage /></MemoryRouter>)
  await userEvent.selectOptions(await screen.findByLabelText(/^Дисциплина/), 'd1')
  await userEvent.selectOptions(screen.getByLabelText(/^Группа/), 'g1')
  await userEvent.selectOptions(screen.getByLabelText(/^Учебный период/), 'p1')
  await screen.findByText('Аннина Иван')
}

const score = (element: string, student: string) => screen.getByLabelText(`${element}: ${student}`) as HTMLInputElement
const manual = (student: string) => screen.getByLabelText(`Итог вручную: ${student}`) as HTMLInputElement
const saveButton = () => screen.getAllByRole('button', { name: 'Сохранить' }).at(-1)!
const puts = (calls: Call[], path: string) => calls.filter((c) => c.method === 'PUT' && c.url.pathname === path)

beforeEach(() => setLanguage('ru'))
afterEach(() => vi.unstubAllGlobals())

describe('parseScore', () => {
  it.each([
    ['', null], ['0', 0], ['10', 10], ['7,5', 7.5], ['7.25', 7.25],
  ])('accepts %s', (raw, value) => expect(parseScore(raw)).toEqual({ ok: true, value }))

  it.each([
    ['10.01', 'range'], ['11', 'range'], ['7.555', 'too_many_decimals'], ['-1', 'invalid'], ['abc', 'invalid'],
  ])('rejects %s', (raw, error) => expect(parseScore(raw)).toEqual({ ok: false, error }))
})

describe('WorkSheetPage', () => {
  it('shows scores, empty versus zero, calculation, manual final and attendance with text', async () => {
    worksheetApi()
    await openSheet()
    expect(score('Экзамен', 'Борисов Иван').value).toBe('0')
    expect(score('Экзамен', 'Волков Иван').value).toBe('')
    expect(screen.getByText('расчёт 7,44 → 7')).toBeInTheDocument()
    expect(screen.getByText('Не заполнено: 1')).toBeInTheDocument()

    const borisov = screen.getByText('Борисов Иван').closest('tr')!
    expect(within(borisov).getByText('вручную')).toBeInTheDocument()
    expect(within(borisov).getByText('перенесено: 6')).toBeInTheDocument()
    expect(within(borisov).getByText('в журнале: 60 · Опубликована')).toBeInTheDocument()
    expect(within(borisov).getByText('7 из 10 · 70% ·')).toBeInTheDocument()
    expect(within(borisov).getByText('Не выполнено')).toHaveClass('badge-red')

    const annina = screen.getByText('Аннина Иван').closest('tr')!
    expect(within(annina).getByText('8 из 10 · 80% ·')).toBeInTheDocument()
    expect(within(annina).getByText('Выполнено')).toHaveClass('badge-green')
    expect(within(annina).getByText('Требование: не менее 80%')).toBeInTheDocument()
    expect(within(screen.getByText('Волков Иван').closest('tr')!).getByText('Нет данных')).toHaveClass('badge-gray')
  })

  it('keeps the row of a student who left the group read-only', async () => {
    worksheetApi()
    await openSheet()
    const row = screen.getByText('Григорьев Иван').closest('tr')!
    expect(within(row).getByText('выбыл из группы')).toBeInTheDocument()
    expect(within(row).getByText('сейчас в группе MBA-02')).toBeInTheDocument()
    expect(score('Кейс', 'Григорьев Иван')).toBeDisabled()
    expect(manual('Григорьев Иван')).toBeDisabled()
  })

  it('validates locally and sends the complete state of changed rows only', async () => {
    const calls = worksheetApi()
    await openSheet()
    fireEvent.change(score('Экзамен', 'Волков Иван'), { target: { value: '10,5' } })
    expect(score('Экзамен', 'Волков Иван')).toHaveAttribute('aria-invalid', 'true')
    expect(screen.getByText('Значение вне допустимого диапазона')).toBeInTheDocument()
    expect(saveButton()).toBeDisabled()
    fireEvent.change(score('Экзамен', 'Волков Иван'), { target: { value: '7.555' } })
    expect(screen.getByText('Не более двух знаков после запятой')).toBeInTheDocument()

    fireEvent.change(score('Экзамен', 'Волков Иван'), { target: { value: '0' } })
    fireEvent.change(score('Кейс', 'Аннина Иван'), { target: { value: '' } })
    expect(screen.getByText('Несохранённых изменений: 2. Перенос — после сохранения.')).toBeInTheDocument()
    await userEvent.click(saveButton())

    await waitFor(() => expect(puts(calls, `${sheetUrl}/rows`)).toHaveLength(1))
    expect(puts(calls, `${sheetUrl}/rows`)[0].body).toEqual({
      rows: [
        { studentId: 's1', scores: { e1: null, e2: 7.4 }, attendedLessons: 8, manualFinal: null },
        { studentId: 's3', scores: { e1: 5, e2: 0 }, attendedLessons: null, manualFinal: null },
      ],
    })
    expect(await screen.findByText('Ведомость сохранена.')).toBeInTheDocument()
  })

  it('rejects attendance above the total and a manual final outside 0–10', async () => {
    worksheetApi()
    await openSheet()
    fireEvent.change(screen.getByLabelText('Посещено занятий: Волков Иван'), { target: { value: '11' } })
    expect(screen.getByText('Больше, чем всего занятий')).toBeInTheDocument()
    fireEvent.change(manual('Волков Иван'), { target: { value: '11' } })
    expect(manual('Волков Иван')).toHaveAttribute('aria-invalid', 'true')
    fireEvent.change(manual('Аннина Иван'), { target: { value: '7.5' } })
    expect(screen.getByText('Введите целое число')).toBeInTheDocument()
    expect(saveButton()).toBeDisabled()
  })

  it('clears the manual final explicitly', async () => {
    const calls = worksheetApi()
    await openSheet()
    await userEvent.click(screen.getByRole('button', { name: 'Снять ручной итог: Борисов Иван' }))
    expect(manual('Борисов Иван').value).toBe('')
    await userEvent.click(saveButton())
    await waitFor(() => expect(puts(calls, `${sheetUrl}/rows`)).toHaveLength(1))
    expect(puts(calls, `${sheetUrl}/rows`)[0].body).toEqual({
      rows: [{ studentId: 's2', scores: { e1: 9, e2: 0 }, attendedLessons: 7, manualFinal: null }],
    })
  })

  it('blocks the transfer while there are unsaved changes', async () => {
    worksheetApi()
    await openSheet()
    const transfer = screen.getByRole('button', { name: 'Перенести итоговые оценки' })
    expect(transfer).toBeEnabled()
    fireEvent.change(score('Кейс', 'Аннина Иван'), { target: { value: '9' } })
    expect(transfer).toBeDisabled()
  })

  it('opens a separate sheet per academic period and never changes the period of a sheet', async () => {
    const empty = rows.filter((r) => r.inGroup).map((r) => ({
      ...base, studentId: r.studentId, studentName: r.studentName, missingCount: 2,
    }))
    const calls = worksheetApi(() => sheet(), (c) => {
      if (c.url.pathname === springUrl) {
        return sheet({ id: null, periodId: 'p2', periodName: 'Семестр 2', totalLessons: null, hasTransfers: false, rows: empty })
      }
      if (c.url.pathname === `${springUrl}/settings`) {
        return sheet({ id: 'ws2', periodId: 'p2', periodName: 'Семестр 2', totalLessons: 12, hasTransfers: false, rows: empty })
      }
      return undefined
    })
    await openSheet()
    const period = screen.getByLabelText(/^Учебный период/)
    expect(within(period).getByRole('option', { name: 'Семестр 1 — есть ведомость, оценки перенесены' })).toBeInTheDocument()
    expect(within(period).getByRole('option', { name: 'Семестр 2' })).toBeInTheDocument()
    expect(screen.getByRole('heading', { name: 'Ведомость: MBA-01 · Финансы · Семестр 1' })).toBeInTheDocument()
    expect(screen.getAllByLabelText(/^Учебный период/)).toHaveLength(1)

    await userEvent.selectOptions(period, 'p2')
    expect(await screen.findByRole('heading', { name: 'Ведомость: MBA-01 · Финансы · Семестр 2' })).toBeInTheDocument()
    expect(screen.getByText(/Ведомости за этот период ещё нет/)).toBeInTheDocument()
    expect(score('Кейс', 'Аннина Иван').value).toBe('')
    expect(screen.queryByText('перенесено: 6')).not.toBeInTheDocument()

    fireEvent.change(screen.getByLabelText(/^Всего занятий/), { target: { value: '12' } })
    await userEvent.click(saveButton())
    await waitFor(() => expect(puts(calls, `${springUrl}/settings`)).toHaveLength(1))
    expect(puts(calls, `${springUrl}/settings`)[0].body).toEqual({ totalLessons: 12 })
    expect(puts(calls, `${sheetUrl}/settings`)).toHaveLength(0)
  })

  it('offers adding the first element right after choosing the discipline', async () => {
    const empty: AssessmentSettings = { ...settings, categories: [], attendanceWeight: 0, weightTotal: 0, weightsComplete: false, readyForTransfer: false, attendanceThreshold: null, averageTerms: [] }
    const calls = worksheetApi(() => sheet(), (c) => (c.url.pathname === settingsUrl
      ? (c.method === 'PUT' ? { ...settings, attendanceThreshold: null } : empty) : undefined))
    render(<MemoryRouter><WorkSheetPage /></MemoryRouter>)
    await userEvent.selectOptions(await screen.findByLabelText(/^Дисциплина/), 'd1')
    expect(await screen.findByText('Категории контроля ещё не заданы.')).toBeInTheDocument()
    expect(screen.queryByText(/в настройках дисциплины/)).not.toBeInTheDocument()
    expect(screen.queryByText(/Сумма весов/)).not.toBeInTheDocument()

    const add = screen.getByRole('button', { name: 'Добавить категорию' })
    expect(add).toBeEnabled()
    await userEvent.click(add)
    const editor = await screen.findByRole('dialog', { name: 'Элементы контроля и посещаемость' })
    fireEvent.change(within(editor).getByLabelText(/^Название категории/), { target: { value: 'Экзамен' } })
    fireEvent.change(within(editor).getByLabelText(/^Название работы/), { target: { value: 'Экзамен' } })
    fireEvent.change(within(editor).getByLabelText(/^Вес, %/), { target: { value: '100' } })
    await userEvent.click(within(editor).getByRole('button', { name: 'Сохранить' }))
    await waitFor(() => expect(puts(calls, settingsUrl)).toHaveLength(1))
    expect(puts(calls, settingsUrl)[0].body).toMatchObject({
      attendanceWeight: 0,
      calculationMode: 'Weighted',
      includeAttendanceInAverage: false,
      categories: [{ id: null, name: 'Экзамен', weight: 100, includeInAverage: true, works: [{ id: null, name: 'Экзамен' }] }],
    })
  })

  it('keeps element editing available with unsaved rows and keeps their input', async () => {
    worksheetApi()
    await openSheet()
    fireEvent.change(score('Кейс', 'Волков Иван'), { target: { value: '6' } })
    const edit = screen.getByRole('button', { name: 'Изменить категории' })
    expect(edit).toBeEnabled()
    await userEvent.click(edit)
    const editor = await screen.findByRole('dialog', { name: 'Элементы контроля и посещаемость' })
    fireEvent.change(within(editor).getAllByLabelText(/^Порог посещаемости/)[0], { target: { value: '75' } })
    await userEvent.click(within(editor).getByRole('button', { name: 'Сохранить' }))
    await screen.findByText('Настройки сохранены.')
    expect(score('Кейс', 'Волков Иван').value).toBe('6')
  })

  it('saves total lessons with the rows and checks attendance against the entered total', async () => {
    const noTotal = () => sheet({ id: null, totalLessons: null, hasTransfers: false, rows: [{ ...base, studentId: 's1', studentName: 'Аннина Иван' }] })
    const calls = worksheetApi(noTotal, (c) => (c.url.pathname === `${sheetUrl}/settings`
      ? sheet({ totalLessons: 10, rows: [{ ...base, studentId: 's1', studentName: 'Аннина Иван' }] }) : undefined))
    await openSheet()
    const attended = screen.getByLabelText('Посещено занятий: Аннина Иван')
    fireEvent.change(attended, { target: { value: '8' } })
    expect(screen.getByText('Сначала укажите, сколько всего занятий')).toBeInTheDocument()
    expect(saveButton()).toBeDisabled()

    fireEvent.change(screen.getByLabelText(/^Всего занятий/), { target: { value: '10' } })
    expect(screen.queryByText('Сначала укажите, сколько всего занятий')).not.toBeInTheDocument()
    expect(screen.getAllByText('Не сохранено')).toHaveLength(2)
    expect(screen.getAllByText('Пересчитается после сохранения').length).toBeGreaterThan(0)
    expect(screen.getByText('/ 10')).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Перенести итоговые оценки' })).toBeDisabled()
    expect(screen.getByText('Несохранённых изменений: 2. Перенос — после сохранения.')).toBeInTheDocument()

    await userEvent.click(saveButton())
    await waitFor(() => expect(puts(calls, `${sheetUrl}/rows`)).toHaveLength(1))
    const order = calls.filter((c) => c.method === 'PUT').map((c) => c.url.pathname)
    expect(order).toEqual([`${sheetUrl}/settings`, `${sheetUrl}/rows`])
    expect(puts(calls, `${sheetUrl}/settings`)[0].body).toEqual({ totalLessons: 10 })
    expect(await screen.findByText('Ведомость сохранена.')).toBeInTheDocument()
  })

  it('previews the transfer, lists skipped rows and requires confirming changes', async () => {
    const calls = worksheetApi()
    await openSheet()
    await userEvent.click(screen.getByRole('button', { name: 'Перенести итоговые оценки' }))
    const dialog = await screen.findByRole('dialog', { name: 'Перенос итоговых оценок' })
    expect(await within(dialog).findByText(/по шкале журнала \(итог × 10\)/)).toBeInTheDocument()
    expect(within(dialog).getByText('Новая оценка')).toBeInTheDocument()
    expect(within(dialog).getByText('Изменится')).toBeInTheDocument()
    expect(within(dialog).getByText('Волков Иван: не заполнено: 1')).toBeInTheDocument()
    expect(within(dialog).getByText('Григорьев Иван: выбыл из группы')).toBeInTheDocument()

    const submit = within(dialog).getByRole('button', { name: 'Перенести (2)' })
    expect(submit).toBeDisabled()
    await userEvent.click(within(dialog).getByRole('checkbox'))
    await userEvent.click(submit)

    await waitFor(() => expect(calls.some((c) => c.method === 'POST' && c.url.pathname === `${sheetUrl}/transfer`)).toBe(true))
    expect(calls.find((c) => c.method === 'POST')!.body).toEqual({
      entries: [{ studentId: 's1', value: 70 }, { studentId: 's2', value: 50 }],
      confirmChanges: true,
    })
    expect(await screen.findByText('Перенос выполнен: новых 1, изменено 1, без изменений 0.')).toBeInTheDocument()
  })

  it('asks before removing a category that has scores and only then sends confirmation', async () => {
    const calls = worksheetApi()
    await openSheet()
    expect(screen.getByText('Категории: Кейс — 40%; Экзамен — 60%; Посещаемость — автоматически — 0%')).toBeInTheDocument()
    await userEvent.click(screen.getByRole('button', { name: 'Изменить категории' }))
    const editor = await screen.findByRole('dialog', { name: 'Элементы контроля и посещаемость' })
    await userEvent.click(within(editor).getByRole('button', { name: 'Удалить категорию Кейс' }))
    expect(within(editor).getByText(/Сумма весов: 60%/)).toBeInTheDocument()
    expect(within(editor).getByText(/Пока это черновик/)).toBeInTheDocument()
    fireEvent.change(within(editor).getByLabelText(/^Название категории/), { target: { value: 'Экзамен итоговый' } })
    await userEvent.click(within(editor).getByRole('button', { name: 'Сохранить' }))

    const confirm = await screen.findByRole('dialog', { name: 'Подтвердить удаление' })
    expect(within(confirm).getByText(/не ограничены открытой ведомостью/)).toBeInTheDocument()
    expect(within(confirm).getByText(/Оценки журнала не изменятся/)).toBeInTheDocument()
    expect(within(confirm).getByText('Категория «Кейс» — баллов: 4')).toBeInTheDocument()
    expect(within(confirm).getByText('Работа «Кейс» (Кейс) — баллов: 4')).toBeInTheDocument()
    expect(within(confirm).getByText('Всего баллов к удалению: 4.')).toBeInTheDocument()
    expect(puts(calls, settingsUrl)).toHaveLength(1)
    expect(puts(calls, settingsUrl)[0].body).toMatchObject({ confirmScoreRemoval: false })

    await userEvent.click(within(confirm).getByRole('button', { name: 'Подтвердить удаление' }))
    await waitFor(() => expect(puts(calls, settingsUrl)).toHaveLength(2))
    expect(puts(calls, settingsUrl)[1].body).toEqual({
      attendanceThreshold: 80,
      attendanceWeight: 0,
      calculationMode: 'Weighted',
      includeAttendanceInAverage: false,
      categories: [{ id: 'c2', name: 'Экзамен итоговый', weight: 60, includeInAverage: true, works: [{ id: 'e2', name: 'Экзамен' }] }],
      confirmScoreRemoval: true,
    })
  })

  it('cancels score-removal confirmation without sending the confirmed request and keeps edits', async () => {
    const calls = worksheetApi()
    await openSheet()
    await userEvent.click(screen.getByRole('button', { name: 'Изменить категории' }))
    const editor = await screen.findByRole('dialog', { name: 'Элементы контроля и посещаемость' })
    await userEvent.click(within(editor).getByRole('button', { name: 'Удалить категорию Кейс' }))
    fireEvent.change(within(editor).getByLabelText(/^Название категории/), { target: { value: 'Экзамен итоговый' } })
    await userEvent.click(within(editor).getByRole('button', { name: 'Сохранить' }))

    const confirm = await screen.findByRole('dialog', { name: 'Подтвердить удаление' })
    await userEvent.click(within(confirm).getByRole('button', { name: 'Отмена' }))
    expect(screen.queryByRole('dialog', { name: 'Подтвердить удаление' })).not.toBeInTheDocument()
    expect(within(editor).getByLabelText(/^Название категории/)).toHaveValue('Экзамен итоговый')
    expect(within(editor).queryByDisplayValue('Кейс')).not.toBeInTheDocument()
    expect(puts(calls, settingsUrl)).toHaveLength(1)
    expect(puts(calls, settingsUrl)[0].body).toMatchObject({ confirmScoreRemoval: false })
    expect(within(editor).getByRole('button', { name: 'Сохранить' })).toBeEnabled()
  })

  it('asks before removing a work that has scores even when the client score count is stale', async () => {
    const stale: AssessmentSettings = {
      ...settings,
      categories: settings.categories.map((c) => ({
        ...c,
        works: c.works.map((w) => ({ ...w, scoreCount: 0 })),
      })),
    }
    const calls = worksheetApi(() => sheet(), (c) => {
      if (c.url.pathname !== settingsUrl) return undefined
      if (c.method !== 'PUT') return stale
      const body = c.body as { confirmScoreRemoval?: boolean; categories?: { works?: { id?: string | null }[] }[] }
      const kept = new Set((body.categories ?? []).flatMap((cat) => (cat.works ?? []).map((w) => w.id).filter(Boolean)))
      if (!kept.has('e1') && !body.confirmScoreRemoval) {
        return json({
          code: 'scores_removal_not_confirmed',
          details: {
            categories: [{ id: 'c1', name: 'Кейс', scoreCount: 4 }],
            works: [{ id: 'e1', name: 'Кейс', categoryName: 'Кейс', scoreCount: 4 }],
            scoreCount: 4,
          },
        }, 409)
      }
      return settings
    })
    await openSheet()
    await userEvent.click(screen.getByRole('button', { name: 'Изменить категории' }))
    const editor = await screen.findByRole('dialog', { name: 'Элементы контроля и посещаемость' })
    await userEvent.click(within(editor).getByRole('button', { name: 'Удалить категорию Кейс' }))
    await userEvent.click(within(editor).getByRole('button', { name: 'Сохранить' }))
    const confirm = await screen.findByRole('dialog', { name: 'Подтвердить удаление' })
    expect(within(confirm).getByText('Всего баллов к удалению: 4.')).toBeInTheDocument()
    expect(puts(calls, settingsUrl)[0].body).toMatchObject({ confirmScoreRemoval: false })
    await userEvent.click(within(confirm).getByRole('button', { name: 'Подтвердить удаление' }))
    await waitFor(() => expect(puts(calls, settingsUrl)).toHaveLength(2))
    expect(puts(calls, settingsUrl)[1].body).toMatchObject({ confirmScoreRemoval: true })
  })

  it('deletes an empty work without asking for confirmation', async () => {
    const withEmpty: AssessmentSettings = {
      ...settings,
      categories: [
        {
          ...settings.categories[0],
          works: [
            { id: 'e1', name: 'Кейс', scoreCount: 4 },
            { id: 'e-empty', name: 'Черновик', scoreCount: 0 },
          ],
        },
        settings.categories[1],
        settings.categories[2],
      ],
    }
    const calls = worksheetApi(() => sheet(), (c) => (c.url.pathname === settingsUrl && c.method !== 'PUT' ? withEmpty : undefined))
    await openSheet()
    await userEvent.click(screen.getByRole('button', { name: 'Изменить категории' }))
    const editor = await screen.findByRole('dialog', { name: 'Элементы контроля и посещаемость' })
    await userEvent.click(within(editor).getByRole('button', { name: 'Удалить работу Черновик' }))
    await userEvent.click(within(editor).getByRole('button', { name: 'Сохранить' }))
    await waitFor(() => expect(puts(calls, settingsUrl)).toHaveLength(1))
    expect(screen.queryByRole('dialog', { name: 'Подтвердить удаление' })).not.toBeInTheDocument()
    expect(puts(calls, settingsUrl)[0].body).toMatchObject({
      confirmScoreRemoval: false,
      categories: [
        { id: 'c1', works: [{ id: 'e1', name: 'Кейс' }] },
        { id: 'c2', works: [{ id: 'e2', name: 'Экзамен' }] },
      ],
    })
  })

  it('asks before removing a single work that has scores', async () => {
    const twoWorks: AssessmentSettings = {
      ...settings,
      categories: [
        {
          ...settings.categories[0],
          works: [
            { id: 'e1', name: 'Кейс', scoreCount: 4 },
            { id: 'e1b', name: 'Кейс-2', scoreCount: 2 },
          ],
        },
        settings.categories[1],
        settings.categories[2],
      ],
    }
    const calls = worksheetApi(() => sheet(), (c) => {
      if (c.url.pathname !== settingsUrl) return undefined
      if (c.method !== 'PUT') return twoWorks
      const body = c.body as { confirmScoreRemoval?: boolean; categories?: { works?: { id?: string | null }[] }[] }
      const kept = new Set((body.categories ?? []).flatMap((cat) => (cat.works ?? []).map((w) => w.id).filter(Boolean)))
      if (!kept.has('e1b') && !body.confirmScoreRemoval) {
        return json({
          code: 'scores_removal_not_confirmed',
          details: {
            categories: [],
            works: [{ id: 'e1b', name: 'Кейс-2', categoryName: 'Кейс', scoreCount: 2 }],
            scoreCount: 2,
          },
        }, 409)
      }
      return twoWorks
    })
    await openSheet()
    await userEvent.click(screen.getByRole('button', { name: 'Изменить категории' }))
    const editor = await screen.findByRole('dialog', { name: 'Элементы контроля и посещаемость' })
    await userEvent.click(within(editor).getByRole('button', { name: 'Удалить работу Кейс-2' }))
    await userEvent.click(within(editor).getByRole('button', { name: 'Сохранить' }))
    const confirm = await screen.findByRole('dialog', { name: 'Подтвердить удаление' })
    expect(within(confirm).getByText('Работа «Кейс-2» (Кейс) — баллов: 2')).toBeInTheDocument()
    expect(within(confirm).queryByText(/Категория «Кейс»/)).not.toBeInTheDocument()
    await userEvent.click(within(confirm).getByRole('button', { name: 'Подтвердить удаление' }))
    await waitFor(() => expect(puts(calls, settingsUrl)).toHaveLength(2))
    expect(puts(calls, settingsUrl)[1].body).toMatchObject({
      confirmScoreRemoval: true,
      categories: [
        { id: 'c1', works: [{ id: 'e1', name: 'Кейс' }] },
        { id: 'c2', works: [{ id: 'e2', name: 'Экзамен' }] },
      ],
    })
  })

  it('adds elements with decimal weights and checks names', async () => {
    const calls = worksheetApi()
    await openSheet()
    await userEvent.click(screen.getByRole('button', { name: 'Изменить категории' }))
    const editor = await screen.findByRole('dialog', { name: 'Элементы контроля и посещаемость' })
    await userEvent.click(within(editor).getByRole('button', { name: 'Добавить категорию' }))
    const names = within(editor).getAllByLabelText(/^Название категории/)
    const works = within(editor).getAllByLabelText(/^Название работы/)
    const weights = within(editor).getAllByLabelText(/^Вес, %/)
    fireEvent.change(names[2], { target: { value: 'кейс' } })
    fireEvent.change(works[2], { target: { value: 'Эссе' } })
    fireEvent.change(weights[2], { target: { value: '12,5' } })
    await userEvent.click(within(editor).getByRole('button', { name: 'Сохранить' }))
    expect(within(editor).getByText('Уже используется')).toBeInTheDocument()
    expect(within(editor).getByText(/Сумма весов: 112,5%/)).toBeInTheDocument()
    expect(puts(calls, settingsUrl)).toHaveLength(0)

    fireEvent.change(names[2], { target: { value: 'Эссе' } })
    await userEvent.click(within(editor).getByRole('button', { name: 'Сохранить' }))
    await waitFor(() => expect(puts(calls, settingsUrl)).toHaveLength(1))
    expect(puts(calls, settingsUrl)[0].body).toMatchObject({
      attendanceWeight: 0,
      categories: [
        { id: 'c1' },
        { id: 'c2' },
        { id: null, name: 'Эссе', weight: 12.5, works: [{ id: null, name: 'Эссе' }] },
      ],
      confirmScoreRemoval: false,
    })
  })

  it('keeps a similarly named manual category distinct from the automatic attendance block', async () => {
    const manualAttendance = {
      id: 'c-manual', name: 'Посещаемость', weight: 100, kind: 'Manual' as const, includeInAverage: true, legacyManualAttendance: true,
      works: [{ id: 'e1', name: 'Работа', scoreCount: 1 }],
    }
    const autoAttendance = {
      id: 'c-att', name: 'Посещаемость', weight: 0, kind: 'Attendance' as const, includeInAverage: true, legacyManualAttendance: false, works: [],
    }
    const calls = worksheetApi(
      () => sheet({
        categories: [
          { id: 'c-manual', name: 'Посещаемость', weight: 100, kind: 'Manual', includeInAverage: true, legacyManualAttendance: true, works: [{ id: 'e1', name: 'Работа' }] },
          { id: 'c-att', name: 'Посещаемость', weight: 0, kind: 'Attendance', includeInAverage: true, legacyManualAttendance: false, works: [] },
        ],
      }),
      (c) => c.url.pathname === settingsUrl ? { ...settings, categories: [manualAttendance, autoAttendance] } : undefined,
    )
    await openSheet()
    expect(screen.getByRole('columnheader', { name: /Посещаемость 100%/ })).toBeInTheDocument()
    expect(screen.getByRole('columnheader', { name: /Посещаемость — автоматически/ })).toBeInTheDocument()
    expect(screen.queryByText('Посещаемость — ручная, из прежних настроек')).not.toBeInTheDocument()

    await userEvent.click(screen.getByRole('button', { name: 'Изменить категории' }))
    const editor = await screen.findByRole('dialog', { name: 'Элементы контроля и посещаемость' })
    expect(within(editor).getByRole('heading', { name: 'Автоматическая посещаемость' })).toBeInTheDocument()
    expect(within(editor).getByLabelText(/^Вес посещаемости/)).toBeInTheDocument()
    expect(within(editor).getByLabelText(/^Порог посещаемости/)).toBeInTheDocument()
    expect(within(editor).getByText(/Это ручные баллы/)).toBeInTheDocument()

    await userEvent.click(within(editor).getByRole('button', { name: 'Добавить категорию' }))
    const names = within(editor).getAllByLabelText(/^Название категории/)
    fireEvent.change(names[1], { target: { value: 'ПОСЕЩАЕМОСТЬ' } })
    expect(within(editor).getAllByText(/Это ручные баллы/).length).toBeGreaterThanOrEqual(2)
    expect(puts(calls, settingsUrl)).toHaveLength(0)
  })

  it('shows average composition and the double-count warning without excluding anything', async () => {
    const calls = worksheetApi()
    await openSheet()
    expect(screen.getByText(/Режим расчёта/)).toBeInTheDocument()
    expect(screen.getByText('Взвешенный')).toBeInTheDocument()
    expect(screen.getByText(/применяется ко всем её группам и учебным периодам/)).toBeInTheDocument()

    await userEvent.click(screen.getByRole('button', { name: 'Изменить категории' }))
    const editor = await screen.findByRole('dialog', { name: 'Элементы контроля и посещаемость' })
    await userEvent.click(within(editor).getByRole('radio', { name: 'Среднее арифметическое' }))
    expect(within(editor).getByText(/В расчёт входят: Кейс; Экзамен/)).toBeInTheDocument()
    const include = within(editor).getAllByRole('checkbox', { name: 'Учитывать в среднем' })
    expect(include).toHaveLength(2)
    expect(include[0]).toBeChecked()
    await userEvent.click(within(editor).getByRole('checkbox', { name: 'Учитывать автоматическую посещаемость в среднем' }))
    expect(within(editor).getByText(/В расчёт входят: Кейс; Экзамен; Посещаемость — автоматически/)).toBeInTheDocument()
    expect(within(editor).queryByText(/могут одновременно оказаться/)).not.toBeInTheDocument()

    fireEvent.change(within(editor).getAllByLabelText(/^Название категории/)[0], { target: { value: 'Посещаемость' } })
    expect(within(editor).getByText(/могут одновременно оказаться/)).toBeInTheDocument()
    await userEvent.click(include[1])
    expect(within(editor).getByText(/В расчёт входят: Кейс; Посещаемость — автоматически/)).toBeInTheDocument()
    await userEvent.click(within(editor).getByRole('button', { name: 'Сохранить' }))
    await waitFor(() => expect(puts(calls, settingsUrl)).toHaveLength(1))
    expect(puts(calls, settingsUrl)[0].body).toMatchObject({
      calculationMode: 'Average',
      includeAttendanceInAverage: true,
      categories: [
        { id: 'c1', includeInAverage: true },
        { id: 'c2', includeInAverage: false },
      ],
    })
  })
})
