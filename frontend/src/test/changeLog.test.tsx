import { render, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { MemoryRouter, Route, Routes } from 'react-router-dom'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import type { ChangeLogListItem, PagedResult } from '../api/types'
import { setLanguage } from '../i18n'
import { ChangeLogPage } from '../pages/manager/ChangeLogPage'

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

const entry = (overrides: Partial<ChangeLogListItem> = {}): ChangeLogListItem => ({
  id: 'cl1',
  occurredAt: '2026-10-06T10:00:00Z',
  actorUserId: 'u1',
  actorEmail: 'manager@test.local',
  actorIsSystem: false,
  actorMissing: false,
  action: 'Created',
  entityType: 'Announcement',
  entityId: 'a1',
  entityTitle: 'Новость',
  entityExists: true,
  groupNames: ['MBA-01'],
  source: null,
  changes: [
    { field: 'title', old: null, new: 'Новость' },
    { field: 'body', old: null, new: 'set' },
  ],
  ...overrides,
})

function renderPage(items: ChangeLogListItem[]) {
  mockApi((call) => {
    if (call.url.pathname === '/api/manager/change-log/authors') return [{ id: 'u1', email: 'manager@test.local' }]
    if (call.url.pathname === '/api/manager/change-log') {
      return { items, total: items.length, page: 1, pageSize: 25 } satisfies PagedResult<ChangeLogListItem>
    }
  })
  return render(
    <MemoryRouter initialEntries={['/manager/change-log']}>
      <Routes>
        <Route path="/manager/change-log" element={<ChangeLogPage />} />
        <Route path="/manager/announcements/:id" element={<p>announcement</p>} />
        <Route path="/manager/grades" element={<p>grades</p>} />
        <Route path="/manager/schedule" element={<p>schedule</p>} />
        <Route path="/manager/groups" element={<p>groups</p>} />
        <Route path="/manager/disciplines" element={<p>disciplines</p>} />
        <Route path="/manager/periods" element={<p>periods</p>} />
        <Route path="/manager/teachers" element={<p>teachers</p>} />
        <Route path="/manager/students" element={<p>students</p>} />
      </Routes>
    </MemoryRouter>,
  )
}

function dataRows(): HTMLElement[] {
  return screen.getAllByRole('row').slice(1)
}

function detailsCell(row: HTMLElement): HTMLElement {
  return row.querySelectorAll('td')[4] as HTMLElement
}

beforeEach(() => setLanguage('ru'))
afterEach(() => vi.unstubAllGlobals())

describe('change log page', () => {
  it('lists entries with details visible and does not link deleted objects', async () => {
    renderPage([
      entry(),
      entry({
        id: 'cl2',
        action: 'Deleted',
        entityExists: false,
        entityTitle: 'Удалённое',
        changes: [{ field: 'title', old: 'Удалённое', new: null }],
      }),
    ])

    expect(await screen.findByRole('heading', { name: 'Журнал изменений' })).toBeInTheDocument()
    const fluid = document.querySelector('.layout-fluid')
    expect(fluid).toBeTruthy()
    expect(fluid).not.toHaveAttribute('hidden')
    expect(screen.getAllByText('manager@test.local').length).toBeGreaterThan(0)
    expect(screen.getByRole('link', { name: 'Новость' })).toHaveAttribute('href', '/manager/announcements/a1')
    expect(screen.queryByRole('link', { name: 'Удалённое' })).not.toBeInTheDocument()
    expect(screen.getByText(/удалён/)).toBeInTheDocument()
    expect(screen.queryByText('Показать изменения')).not.toBeInTheDocument()
    expect(screen.queryByRole('group')).not.toBeInTheDocument()
    expect(document.querySelector('details')).toBeNull()
    expect(screen.getAllByText('Текст задан').length).toBeGreaterThan(0)
  })

  it('keeps author email on one line and scrolls the table wrap, not collapsed details', async () => {
    const longEmail = 'manager.with.a.very.long.address@mba.example.local'
    renderPage([
      entry({
        actorEmail: longEmail,
        changes: [
          { field: 'title', old: null, new: 'Новость' },
          { field: 'body', old: null, new: 'set' },
        ],
      }),
    ])

    expect(await screen.findByText(longEmail)).toBeInTheDocument()
    expect(screen.queryByText('Показать изменения')).not.toBeInTheDocument()
    expect(document.querySelector('details')).toBeNull()

    const authorCell = screen.getByText(longEmail)
    expect(authorCell).toHaveClass('change-log-author')
    expect(authorCell).toHaveTextContent(longEmail)
    expect(authorCell.textContent).not.toMatch(/\s{2,}/)

    const wrap = document.querySelector('.table-wrap')
    expect(wrap).toBeTruthy()
    expect(wrap!.classList.contains('table-wrap')).toBe(true)
    expect(document.querySelector('.change-log-table')).toBeTruthy()
    expect(screen.getByText('Текст задан')).toBeInTheDocument()
  })

  it('shows grade value markers without arrows, field label, or numeric scores', async () => {
    renderPage([
      entry({
        id: 'g1',
        action: 'Created',
        entityType: 'Grade',
        entityId: 'grade-1',
        entityTitle: 'Иванов Иван · Финансы · Семестр',
        groupNames: ['MBA-01'],
        changes: [
          { field: 'student', old: null, new: 'Иванов Иван' },
          { field: 'group', old: null, new: 'MBA-01' },
          { field: 'discipline', old: null, new: 'Финансы' },
          { field: 'period', old: null, new: 'Семестр' },
          { field: 'status', old: null, new: 'Draft' },
          { field: 'value', old: null, new: 'set' },
        ],
      }),
      entry({
        id: 'g2',
        action: 'Updated',
        entityType: 'Grade',
        entityId: 'grade-1',
        entityTitle: 'Иванов Иван · Финансы · Семестр',
        groupNames: ['MBA-01'],
        changes: [{ field: 'value', old: null, new: 'changed' }],
      }),
    ])

    const createdRow = (await screen.findByRole('link', { name: 'Иванов Иван' })).closest('tr')!
    expect(within(createdRow).getByRole('link', { name: 'Иванов Иван' })).toHaveAttribute('href', '/manager/grades')
    expect(within(createdRow).queryByRole('link', { name: /Финансы · Семестр/ })).not.toBeInTheDocument()
    expect(within(createdRow).getByText('Финансы · Семестр · MBA-01')).toBeInTheDocument()

    const createdDetails = detailsCell(createdRow)
    expect(within(createdDetails).getByText('Оценка задана')).toBeInTheDocument()
    expect(createdDetails.textContent).not.toMatch(/Оценка:\s*Оценка/)
    expect(createdDetails.textContent).not.toMatch(/—\s*→/)
    expect(createdDetails.textContent).not.toContain('→')
    expect(within(createdDetails).getByText('Статус')).toBeInTheDocument()
    expect(within(createdDetails).getByText('Черновик')).toBeInTheDocument()
    expect(within(createdDetails).queryByText('Иванов Иван')).not.toBeInTheDocument()
    expect(within(createdDetails).queryByText('77')).not.toBeInTheDocument()
    expect(within(createdDetails).queryByText('Финансы')).not.toBeInTheDocument()

    const updatedDetails = detailsCell(dataRows()[1]!)
    expect(within(updatedDetails).getByText('Оценка изменена')).toBeInTheDocument()
    expect(updatedDetails.textContent).not.toMatch(/Оценка:\s*Оценка/)
    expect(updatedDetails.textContent).not.toContain('→')
    expect(within(updatedDetails).queryByText('88')).not.toBeInTheDocument()
  })

  it('renders create fields without empty left side and updates as was → became without strikethrough', async () => {
    renderPage([
      entry({
        id: 'a1',
        action: 'Created',
        changes: [
          { field: 'title', old: null, new: 'Новость' },
          { field: 'status', old: null, new: 'Draft' },
          { field: 'body', old: null, new: 'set' },
        ],
      }),
      entry({
        id: 'a2',
        action: 'Updated',
        entityTitle: 'Новость v2',
        changes: [
          { field: 'title', old: 'Новость', new: 'Новость v2' },
          { field: 'status', old: 'Draft', new: 'Published' },
        ],
      }),
    ])

    await screen.findByRole('heading', { name: 'Журнал изменений' })
    const rows = dataRows()
    const created = detailsCell(rows[0]!)
    expect(within(created).getByText('Текст задан')).toBeInTheDocument()
    expect(within(created).getByText('Статус')).toBeInTheDocument()
    expect(created.textContent).not.toContain('→')
    expect(created.textContent).not.toMatch(/—\s*→/)

    const updated = detailsCell(rows[1]!)
    expect(within(updated).getByText('Заголовок')).toBeInTheDocument()
    expect(within(updated).getByText('Новость')).toBeInTheDocument()
    expect(within(updated).getByText('Новость v2')).toBeInTheDocument()
    expect(within(updated).getByText('Статус')).toBeInTheDocument()
    expect(within(updated).getByText('Черновик')).toBeInTheDocument()
    expect(within(updated).getByText('Опубликована')).toBeInTheDocument()
    expect(updated.textContent).toMatch(/Новость\s*→\s*Новость v2/)
    expect(updated.textContent).toMatch(/Черновик\s*→\s*Опубликована/)
    const oldNodes = updated.querySelectorAll('.change-log-old')
    expect(oldNodes.length).toBeGreaterThan(0)
    for (const node of oldNodes) {
      expect(getComputedStyle(node).textDecoration).not.toMatch(/line-through/)
    }
  })

  it('shows short source labels in details', async () => {
    renderPage([
      entry({
        id: 'imp',
        action: 'Updated',
        entityType: 'Lesson',
        entityTitle: 'Финансы · 2035-06-10 14:00',
        source: 'excelImport',
        changes: [{ field: 'location', old: 'A', new: 'B' }],
      }),
      entry({
        id: 'tpl',
        action: 'Created',
        entityType: 'Survey',
        entityTitle: 'Опрос из шаблона',
        source: 'fromTemplate',
        changes: [
          { field: 'title', old: null, new: 'Опрос из шаблона' },
          { field: 'status', old: null, new: 'Draft' },
        ],
      }),
      entry({
        id: 'srv',
        action: 'Created',
        entityType: 'SurveyTemplate',
        entityTitle: 'Шаблон из опроса',
        source: 'fromSurvey',
        changes: [
          { field: 'title', old: null, new: 'Шаблон из опроса' },
          { field: 'status', old: null, new: 'Draft' },
        ],
      }),
    ])

    await screen.findByRole('heading', { name: 'Журнал изменений' })
    const rows = dataRows()
    expect(within(detailsCell(rows[0]!)).getByText('Excel-импорт')).toBeInTheDocument()
    expect(within(detailsCell(rows[1]!)).getByText('Из шаблона')).toBeInTheDocument()
    expect(within(detailsCell(rows[2]!)).getByText('Из опроса')).toBeInTheDocument()
  })

  it('keeps Updated object title from entityTitle/new, shows context field diffs, and falls back to entityTitle', async () => {
    renderPage([
      entry({
        id: 'upd-title',
        action: 'Updated',
        entityTitle: 'Новое название',
        changes: [
          { field: 'title', old: 'Старое название', new: 'Новое название' },
          { field: 'groupIds', old: ['MBA-01'], new: ['MBA-02'] },
        ],
      }),
      entry({
        id: 'upd-value',
        action: 'Updated',
        entityType: 'Grade',
        entityTitle: 'Иванов Иван · Финансы · Семестр',
        groupNames: ['MBA-01'],
        changes: [{ field: 'value', old: null, new: 'changed' }],
      }),
    ])

    expect(await screen.findByRole('link', { name: 'Новое название' })).toBeInTheDocument()
    expect(screen.queryByRole('link', { name: 'Старое название' })).not.toBeInTheDocument()
    expect(screen.getByRole('link', { name: 'Иванов Иван · Финансы · Семестр' })).toBeInTheDocument()

    const updatedDetails = detailsCell(dataRows()[0]!)
    expect(updatedDetails.textContent).toMatch(/Заголовок:\s*Старое название\s*→\s*Новое название/)
    expect(updatedDetails.textContent).toMatch(/Группы:/)
    expect(within(updatedDetails).getByText('MBA-01')).toBeInTheDocument()
    expect(within(updatedDetails).getByText('MBA-02')).toBeInTheDocument()
  })

  it('applies action filter on the server query', async () => {
    const user = userEvent.setup()
    const calls = mockApi((call) => {
      if (call.url.pathname === '/api/manager/change-log/authors') return []
      if (call.url.pathname === '/api/manager/change-log') {
        return { items: [], total: 0, page: 1, pageSize: 25 } satisfies PagedResult<ChangeLogListItem>
      }
    })
    render(<MemoryRouter><ChangeLogPage /></MemoryRouter>)
    await screen.findByRole('heading', { name: 'Журнал изменений' })
    await user.selectOptions(screen.getByLabelText('Действие'), 'Published')
    await waitFor(() => {
      const listCalls = calls.filter((c) => c.url.pathname === '/api/manager/change-log')
      expect(listCalls.some((c) => c.url.searchParams.get('action') === 'Published')).toBe(true)
    })
  })

  it('sends selected entityType, keeps other filters, resets page, and omits entityType for all types', async () => {
    const user = userEvent.setup()
    const calls = mockApi((call) => {
      if (call.url.pathname === '/api/manager/change-log/authors') return []
      if (call.url.pathname === '/api/manager/change-log') {
        const page = Number(call.url.searchParams.get('page') ?? '1')
        return {
          items: [entry({ id: `cl-p${page}` })],
          total: 40,
          page,
          pageSize: 25,
        } satisfies PagedResult<ChangeLogListItem>
      }
    })

    render(<MemoryRouter><ChangeLogPage /></MemoryRouter>)
    expect(await screen.findByRole('heading', { name: 'Журнал изменений' })).toBeInTheDocument()
    expect(screen.getByText(/Журнал фиксирует действия/)).toBeInTheDocument()

    await waitFor(() => {
      expect(calls.some((c) => c.url.pathname === '/api/manager/change-log')).toBe(true)
    })
    expect(
      calls
        .filter((c) => c.url.pathname === '/api/manager/change-log')
        .every((c) => !c.url.searchParams.has('entityType')),
    ).toBe(true)

    await user.selectOptions(screen.getByLabelText('Действие'), 'Published')
    await waitFor(() => {
      const listCalls = calls.filter((c) => c.url.pathname === '/api/manager/change-log')
      expect(listCalls.some((c) => c.url.searchParams.get('action') === 'Published')).toBe(true)
    })

    await user.click(screen.getByRole('button', { name: 'Вперёд' }))
    await waitFor(() => {
      const listCalls = calls.filter((c) => c.url.pathname === '/api/manager/change-log')
      expect(listCalls.some((c) => c.url.searchParams.get('page') === '2' && c.url.searchParams.get('action') === 'Published')).toBe(true)
    })

    await user.selectOptions(screen.getByLabelText('Тип объекта'), 'Lesson')
    await waitFor(() => {
      const listCalls = calls.filter((c) => c.url.pathname === '/api/manager/change-log')
      expect(listCalls.some((c) =>
        c.url.searchParams.get('entityType') === 'Lesson'
        && c.url.searchParams.get('action') === 'Published'
        && c.url.searchParams.get('page') === '1',
      )).toBe(true)
    })

    await user.selectOptions(screen.getByLabelText('Тип объекта'), '')
    await waitFor(() => {
      const listCalls = calls.filter((c) => c.url.pathname === '/api/manager/change-log')
      expect(listCalls.some((c) =>
        !c.url.searchParams.has('entityType')
        && c.url.searchParams.get('action') === 'Published'
        && c.url.searchParams.get('page') === '1',
      )).toBe(true)
    })
  })

  it('keeps object-type filter and never shows expand/collapse details control', async () => {
    renderPage([entry()])
    expect(await screen.findByLabelText('Тип объекта')).toBeInTheDocument()
    const typeFilter = screen.getByLabelText('Тип объекта')
    expect(within(typeFilter).getByRole('option', { name: 'Все типы' })).toBeInTheDocument()
    expect(within(typeFilter).getByRole('option', { name: 'Преподаватели' })).toBeInTheDocument()
    expect(within(typeFilter).getByRole('option', { name: 'Студенты' })).toBeInTheDocument()
    expect(within(typeFilter).getByRole('option', { name: 'Дисциплины' })).toBeInTheDocument()
    expect(within(typeFilter).getByRole('option', { name: 'Группы' })).toBeInTheDocument()
    expect(screen.queryByText('Показать изменения')).not.toBeInTheDocument()
    expect(document.querySelector('details')).toBeNull()
    expect(screen.queryByRole('button', { name: /показать изменения/i })).not.toBeInTheDocument()
  })

  it('translates every wired entity type label without raw i18n keys', async () => {
    const types: Array<{ entityType: ChangeLogListItem['entityType']; title: string; label: string }> = [
      { entityType: 'Announcement', title: 'A', label: 'Объявление' },
      { entityType: 'Lesson', title: 'L', label: 'Занятие' },
      { entityType: 'Survey', title: 'S', label: 'Опрос' },
      { entityType: 'SurveyTemplate', title: 'ST', label: 'Шаблон опроса' },
      { entityType: 'Grade', title: 'G', label: 'Оценка' },
      { entityType: 'Group', title: 'Gr', label: 'Группа' },
      { entityType: 'Discipline', title: 'D', label: 'Дисциплина' },
      { entityType: 'AcademicPeriod', title: 'P', label: 'Учебный период' },
      { entityType: 'Teacher', title: 'T', label: 'Преподаватель' },
      { entityType: 'Student', title: 'St', label: 'Студент' },
    ]
    renderPage(types.map((x, i) => entry({
      id: `et${i}`,
      entityType: x.entityType,
      entityTitle: x.title,
      entityId: `id-${i}`,
      groupNames: [],
      changes: x.entityType === 'Teacher'
        ? [{ field: 'lastName', old: null, new: x.title }]
        : [{ field: 'name', old: null, new: x.title }, { field: 'title', old: null, new: x.title }],
    })))

    await screen.findByRole('heading', { name: 'Журнал изменений' })
    for (const x of types) {
      expect(screen.getByText(x.label)).toBeInTheDocument()
    }
    expect(document.body.textContent).not.toMatch(/changeLog\.entityTypes\./)
    expect(document.body.textContent).not.toMatch(/changeLog\.fields\./)
    expect(document.body.textContent).not.toMatch(/changeLog\.email/)
  })

  it('renders teacher create/update email markers as phrases without address, arrows, or raw keys', async () => {
    renderPage([
      entry({
        id: 't-create',
        action: 'Created',
        entityType: 'Teacher',
        entityTitle: 'Сидоров Пётр Иванович',
        groupNames: [],
        changes: [
          { field: 'lastName', old: null, new: 'Сидоров' },
          { field: 'firstName', old: null, new: 'Пётр' },
          { field: 'middleName', old: null, new: 'Иванович' },
          { field: 'email', old: null, new: 'set' },
        ],
      }),
      entry({
        id: 't-changed',
        action: 'Updated',
        entityType: 'Teacher',
        entityTitle: 'Сидоров Пётр',
        groupNames: [],
        changes: [{ field: 'email', old: null, new: 'changed' }],
      }),
      entry({
        id: 't-cleared',
        action: 'Updated',
        entityType: 'Teacher',
        entityTitle: 'Сидоров Пётр',
        groupNames: [],
        changes: [{ field: 'email', old: null, new: 'cleared' }],
      }),
      entry({
        id: 't-fio',
        action: 'Updated',
        entityType: 'Teacher',
        entityTitle: 'Иванов Пётр',
        groupNames: [],
        changes: [
          { field: 'lastName', old: 'Сидоров', new: 'Иванов' },
        ],
      }),
    ])

    expect((await screen.findAllByText('Преподаватель')).length).toBeGreaterThanOrEqual(4)
    expect(screen.getByText('Email задан')).toBeInTheDocument()
    expect(screen.getByText('Email изменён')).toBeInTheDocument()
    expect(screen.getByText('Email очищен')).toBeInTheDocument()
    expect(screen.getAllByText('Фамилия').length).toBeGreaterThanOrEqual(1)
    expect(screen.getByText('Имя')).toBeInTheDocument()
    expect(screen.getByText('Отчество')).toBeInTheDocument()

    const created = detailsCell(dataRows()[0]!)
    expect(within(created).getByText('Email задан')).toBeInTheDocument()
    expect(created.textContent).not.toContain('→')
    expect(created.textContent).not.toMatch(/—\s*→/)
    expect(created.textContent).not.toMatch(/\bset\b/)
    expect(created.textContent).toMatch(/Фамилия:\s*Сидоров/)
    expect(created.textContent).toMatch(/Имя:\s*Пётр/)
    expect(created.textContent).toMatch(/Отчество:\s*Иванович/)

    const changed = detailsCell(dataRows()[1]!)
    expect(within(changed).getByText('Email изменён')).toBeInTheDocument()
    expect(changed.textContent).not.toContain('→')
    expect(changed.textContent).not.toMatch(/\bchanged\b/)

    const cleared = detailsCell(dataRows()[2]!)
    expect(within(cleared).getByText('Email очищен')).toBeInTheDocument()
    expect(cleared.textContent).not.toContain('→')
    expect(cleared.textContent).not.toMatch(/\bcleared\b/)

    const fio = detailsCell(dataRows()[3]!)
    expect(fio.textContent).toMatch(/Фамилия:\s*Сидоров\s*→\s*Иванов/)
    const oldNodes = fio.querySelectorAll('.change-log-old')
    expect(oldNodes.length).toBeGreaterThan(0)
    for (const node of oldNodes) {
      expect(getComputedStyle(node).textDecoration).not.toMatch(/line-through/)
    }

    expect(document.body.textContent).not.toMatch(/secret@|teacher\.|annqa\./i)
    expect(document.body.textContent).not.toMatch(/changeLog\.(entityTypes|fields|email)/)
    expect(screen.queryByText('Показать изменения')).not.toBeInTheDocument()
    expect(await screen.findByRole('link', { name: 'Сидоров Пётр Иванович' })).toHaveAttribute('href', '/manager/teachers')
  })

  it('shows student password markers and links student objects', async () => {
    renderPage([
      entry({
        id: 's1',
        action: 'Created',
        entityType: 'Student',
        entityTitle: 'Иванов Иван',
        groupNames: ['MBA-01'],
        changes: [
          { field: 'lastName', old: null, new: 'Иванов' },
          { field: 'password', old: null, new: 'set' },
          { field: 'email', old: null, new: 'set' },
        ],
      }),
      entry({
        id: 's2',
        action: 'Updated',
        entityType: 'Student',
        entityTitle: 'Иванов Иван',
        groupNames: ['MBA-01'],
        changes: [{ field: 'password', old: null, new: 'reset' }],
      }),
    ])
    const links = await screen.findAllByRole('link', { name: 'Иванов Иван' })
    expect(links.length).toBeGreaterThanOrEqual(1)
    expect(links[0]).toHaveAttribute('href', '/manager/students')
    expect(screen.getByText('Пароль задан')).toBeInTheDocument()
    expect(screen.getByText('Пароль сброшен')).toBeInTheDocument()
    expect(screen.getByText('Email задан')).toBeInTheDocument()
    expect(screen.getAllByText('Студент').length).toBeGreaterThanOrEqual(1)
    expect(document.body.textContent).not.toMatch(/(^|[^а-яА-Я])set([^а-яА-Я]|$)|(^|[^а-яА-Я])reset([^а-яА-Я]|$)/)
  })

  it('shows email marker without arrow and links teacher objects', async () => {
    renderPage([
      entry({
        id: 't1',
        action: 'Updated',
        entityType: 'Teacher',
        entityTitle: 'Петрова Анна',
        groupNames: [],
        changes: [{ field: 'email', old: null, new: 'changed' }],
      }),
    ])
    expect(await screen.findByRole('link', { name: 'Петрова Анна' })).toHaveAttribute('href', '/manager/teachers')
    expect(screen.getByText('Email изменён')).toBeInTheDocument()
    expect(screen.getByText('Преподаватель')).toBeInTheDocument()
    expect(screen.queryByText(/→/)).not.toBeInTheDocument()
    expect(screen.queryByText('changeLog.entityTypes.Teacher')).not.toBeInTheDocument()
  })

  it('shows description cleared marker and links group/discipline/period objects', async () => {
    renderPage([
      entry({
        id: 'd1',
        action: 'Updated',
        entityType: 'Discipline',
        entityTitle: 'Финансы',
        groupNames: [],
        changes: [{ field: 'description', old: null, new: 'cleared' }],
      }),
      entry({
        id: 'g1',
        action: 'Created',
        entityType: 'Group',
        entityId: 'group-1',
        entityTitle: 'MBA-01',
        groupNames: ['MBA-01'],
        changes: [
          { field: 'name', old: null, new: 'MBA-01' },
          { field: 'status', old: null, new: 'Active' },
        ],
      }),
      entry({
        id: 'p1',
        action: 'Created',
        entityType: 'AcademicPeriod',
        entityTitle: 'Семестр 1',
        groupNames: [],
        changes: [{ field: 'name', old: null, new: 'Семестр 1' }],
      }),
    ])

    expect(await screen.findByRole('link', { name: 'MBA-01' })).toHaveAttribute('href', '/manager/groups')
    expect(screen.getByRole('link', { name: 'Финансы' })).toHaveAttribute('href', '/manager/disciplines')
    expect(screen.getByRole('link', { name: 'Семестр 1' })).toHaveAttribute('href', '/manager/periods')
    expect(screen.getByText('Описание очищено')).toBeInTheDocument()
    expect(screen.getByText('Активная')).toBeInTheDocument()
  })
})
