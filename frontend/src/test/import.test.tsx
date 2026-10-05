import { render, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { MemoryRouter } from 'react-router-dom'
import type { Discipline } from '../api/types'
import { setLanguage } from '../i18n'
import { DisciplinesPage } from '../pages/manager/ReferencePage'
import { StudentsPage } from '../pages/manager/StudentsPage'

const json = (body: unknown, status = 200) =>
  new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } })

beforeEach(() => setLanguage('ru'))
afterEach(() => vi.unstubAllGlobals())

describe('DisciplinesPage import', () => {
  it('opens import dialog and confirms preview', async () => {
    const disciplines: Discipline[] = [
      { id: 'd1', name: 'Финансы', description: 'old' },
    ]
    const fetchMock = vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
      const url = new URL(String(input), 'http://localhost')
      const method = init?.method ?? 'GET'
      if (url.pathname === '/api/auth/csrf') return json({ token: 't' })
      if (url.pathname === '/api/manager/disciplines' && method === 'GET') return json(disciplines)
      if (url.pathname === '/api/manager/imports/disciplines/preview' && method === 'POST') {
        return json({
          importId: 'imp1',
          createCount: 1,
          updateCount: 0,
          conflictCount: 0,
          errorCount: 0,
          rows: [{ rowNumber: 2, action: 'Create', values: { name: 'Маркетинг', description: null }, errors: [] }],
          fileErrors: [],
        })
      }
      if (url.pathname === '/api/manager/imports/disciplines/confirm' && method === 'POST') {
        const body = JSON.parse(String(init?.body)) as { importId: string }
        expect(body.importId).toBe('imp1')
        return json({ created: 1, updated: 0, skipped: 0 })
      }
      return json({}, 404)
    })
    vi.stubGlobal('fetch', fetchMock)

    render(<DisciplinesPage />)
    await userEvent.click(await screen.findByRole('button', { name: 'Импорт' }))
    const dialog = screen.getByRole('dialog', { name: 'Импорт из Excel' })
    expect(dialog).toHaveTextContent('Скачайте шаблон')

    const file = new File([new Uint8Array([1, 2, 3])], 'd.xlsx', {
      type: 'application/vnd.openxmlformats-officedocument.spreadsheetml.sheet',
    })
    const input = within(dialog).getByLabelText('Выбрать файл') as HTMLInputElement
    await userEvent.upload(input, file)

    await waitFor(() => expect(within(dialog).getByText(/К созданию: 1/)).toBeInTheDocument())
    await userEvent.click(within(dialog).getByRole('button', { name: 'Подтвердить импорт' }))
    await waitFor(() => expect(within(dialog).getByText(/Создано: 1/)).toBeInTheDocument())
  })
})

describe('StudentsPage import', () => {
  it('requires passwords for create rows and sends them only on confirm', async () => {
    const fetchMock = vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
      const url = new URL(String(input), 'http://localhost')
      const method = init?.method ?? 'GET'
      if (url.pathname === '/api/auth/csrf') return json({ token: 't' })
      if (url.pathname === '/api/manager/groups' && method === 'GET') return json([])
      if (url.pathname === '/api/manager/students' && method === 'GET') {
        return json({ items: [], total: 0, page: 1, pageSize: 25 })
      }
      if (url.pathname === '/api/manager/imports/students/preview' && method === 'POST') {
        return json({
          importId: 'stu1',
          createCount: 1,
          updateCount: 0,
          conflictCount: 0,
          errorCount: 0,
          rows: [{
            rowNumber: 2,
            action: 'Create',
            values: { lastName: 'Петров', firstName: 'Пётр', middleName: null, email: 'p@test.local', group: 'MBA-1' },
            errors: [],
          }],
          fileErrors: [],
        })
      }
      if (url.pathname === '/api/manager/imports/students/confirm' && method === 'POST') {
        const body = JSON.parse(String(init?.body)) as {
          importId: string
          passwords?: { rowNumber: number; password: string }[]
        }
        expect(body.importId).toBe('stu1')
        expect(body.passwords).toEqual([{ rowNumber: 2, password: 'Student-Pass-1' }])
        return json({ created: 1, updated: 0, skipped: 0 })
      }
      return json({}, 404)
    })
    vi.stubGlobal('fetch', fetchMock)

    render(<MemoryRouter><StudentsPage /></MemoryRouter>)
    await userEvent.click(await screen.findByRole('button', { name: 'Импорт' }))
    const dialog = screen.getByRole('dialog', { name: 'Импорт из Excel' })

    const file = new File([new Uint8Array([1, 2, 3])], 's.xlsx', {
      type: 'application/vnd.openxmlformats-officedocument.spreadsheetml.sheet',
    })
    await userEvent.upload(within(dialog).getByLabelText('Выбрать файл') as HTMLInputElement, file)

    await waitFor(() => expect(within(dialog).getByText(/К созданию: 1/)).toBeInTheDocument())
    const confirmBtn = within(dialog).getByRole('button', { name: 'Подтвердить импорт' })
    expect(confirmBtn).toBeDisabled()

    const pwd = within(dialog).getByLabelText(/Пароль для строки 2/)
    await userEvent.type(pwd, 'Student-Pass-1')
    expect(confirmBtn).toBeEnabled()
    await userEvent.click(confirmBtn)
    await waitFor(() => expect(within(dialog).getByText(/Создано: 1/)).toBeInTheDocument())
  })
})
