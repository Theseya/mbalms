import type { ReactElement } from 'react'
import { fireEvent, render, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { MemoryRouter, Route, Routes } from 'react-router-dom'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import type { Discipline, Group, StudentSurveyDetail, SurveyDetail, SurveyListItem, SurveyQuestion, SurveyResponse, Teacher } from '../api/types'
import { setLanguage } from '../i18n'
import { SurveyEditPage } from '../pages/manager/SurveyEditPage'
import { SurveyResultsPage } from '../pages/manager/SurveyResultsPage'
import { SurveysPage } from '../pages/manager/SurveysPage'
import { SurveyTakePage } from '../pages/student/SurveyTakePage'

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
const teachers: Teacher[] = [{ id: 't1', lastName: 'Петрова', firstName: 'Анна', middleName: null, fullName: 'Петрова Анна', email: null }]
const disciplines: Discipline[] = [{ id: 'd1', name: 'Финансы', description: null }]

const questions: SurveyQuestion[] = [
  { id: 'q1', order: 0, text: 'Понятность материала', type: 'Scale', isRequired: true, scaleMin: 1, scaleMax: 5,
    scaleMinLabel: 'Совсем непонятно', scaleMaxLabel: 'Полностью понятно', options: [] },
  { id: 'q2', order: 1, text: 'Формат', type: 'SingleChoice', isRequired: true, scaleMin: null, scaleMax: null,
    scaleMinLabel: null, scaleMaxLabel: null, options: [{ id: 'o1', order: 0, text: 'Очно' }, { id: 'o2', order: 1, text: 'Онлайн' }] },
  { id: 'q3', order: 2, text: 'Комментарий', type: 'Text', isRequired: false, scaleMin: null, scaleMax: null,
    scaleMinLabel: null, scaleMaxLabel: null, options: [] },
]

const detail = (overrides: Partial<SurveyDetail> = {}): SurveyDetail => ({
  id: 's1', type: 'TeachingEvaluation', title: 'Оценка курса', description: null, status: 'Open', groupId: 'g1',
  groupName: 'MBA-01', groupStatus: 'Active', teacherId: 't1', teacherName: 'Петрова Анна', disciplineId: 'd1',
  disciplineName: 'Финансы', opensAtLocal: null, closesAtLocal: null, publishedAt: '2026-10-01T09:00:00Z',
  responseCount: 1, studentCount: 2, questions, ...overrides,
})

const studentDetail = (overrides: Partial<StudentSurveyDetail> = {}): StudentSurveyDetail => ({
  id: 's1', type: 'TeachingEvaluation', title: 'Оценка курса', description: null, status: 'Open', teacherName: 'Петрова Анна',
  disciplineName: 'Финансы', opensAtLocal: null, closesAtLocal: null, submitted: false, submittedAt: null, canRespond: true,
  questions, myAnswers: [], ...overrides,
})

const renderAt = (path: string, route: string, page: ReactElement) =>
  render(<MemoryRouter initialEntries={[path]}><Routes><Route path={route} element={page} /><Route path="*" element={null} /></Routes></MemoryRouter>)

const references = (c: Call) => {
  const path = c.url.pathname
  if (path === '/api/manager/groups') return groups
  if (path === '/api/manager/teachers') return teachers
  if (path === '/api/manager/disciplines') return disciplines
  return undefined
}

beforeEach(() => setLanguage('ru'))
afterEach(() => vi.unstubAllGlobals())

describe('SurveyEditPage', () => {
  it('requires a teacher and discipline for a teaching evaluation and sends scale labels', async () => {
    const calls = mockApi((c) => (c.method === 'POST' ? detail({ status: 'Draft' }) : references(c)))
    renderAt('/manager/surveys/new', '/manager/surveys/new', <SurveyEditPage />)

    await userEvent.type(await screen.findByLabelText(/^Название/), 'Оценка курса')
    await userEvent.selectOptions(screen.getByLabelText(/^Группа/), 'g1')
    expect(screen.getByLabelText(/^Преподаватель/)).toBeRequired()
    expect(screen.getByLabelText(/^Дисциплина/)).toBeRequired()
    await userEvent.selectOptions(screen.getByLabelText(/^Преподаватель/), 't1')
    await userEvent.selectOptions(screen.getByLabelText(/^Дисциплина/), 'd1')
    await userEvent.type(screen.getByLabelText(/^Текст вопроса/), 'Понятность')
    await userEvent.type(screen.getByLabelText('Подпись минимума'), 'Плохо')
    await userEvent.type(screen.getByLabelText('Подпись максимума'), 'Отлично')
    await userEvent.click(screen.getByRole('button', { name: 'Сохранить' }))

    await waitFor(() => expect(calls.some((c) => c.method === 'POST')).toBe(true))
    expect(calls.find((c) => c.method === 'POST')?.body).toMatchObject({
      type: 'TeachingEvaluation', teacherId: 't1', disciplineId: 'd1', groupId: 'g1',
      questions: [{ text: 'Понятность', type: 'Scale', scaleMin: 1, scaleMax: 5, scaleMinLabel: 'Плохо', scaleMaxLabel: 'Отлично' }],
    })
  })

  it('hides teacher and discipline for a service survey and does not send them', async () => {
    const calls = mockApi((c) => (c.method === 'POST' ? detail({ status: 'Draft' }) : references(c)))
    renderAt('/manager/surveys/new', '/manager/surveys/new', <SurveyEditPage />)
    await userEvent.selectOptions(await screen.findByLabelText(/^Преподаватель/), 't1')
    await userEvent.selectOptions(screen.getByLabelText(/^Тип( \*)?$/), 'ServiceSurvey')
    expect(screen.queryByLabelText(/^Преподаватель/)).not.toBeInTheDocument()
    await userEvent.click(screen.getByRole('button', { name: 'Сохранить' }))
    await waitFor(() => expect(calls.some((c) => c.method === 'POST')).toBe(true))
    expect(calls.find((c) => c.method === 'POST')?.body).toMatchObject({ type: 'ServiceSurvey', teacherId: null, disciplineId: null })
  })

  it('shows a server error under the teacher field', async () => {
    mockApi((c) => (c.method === 'POST'
      ? json({ code: 'validation_failed', errors: { teacherId: ['required'] } }, 400)
      : references(c)))
    renderAt('/manager/surveys/new', '/manager/surveys/new', <SurveyEditPage />)
    await userEvent.click(await screen.findByRole('button', { name: 'Сохранить' }))
    expect(await screen.findByText('Обязательное поле')).toBeInTheDocument()
    expect(screen.getByLabelText(/^Преподаватель/)).toHaveAccessibleDescription('Обязательное поле')
  })

  it('publishes a draft only after confirmation', async () => {
    const calls = mockApi((c) => (c.url.pathname === '/api/manager/surveys/s1' ? detail({ status: 'Draft', responseCount: 0 })
      : c.method === 'POST' ? detail() : references(c)))
    renderAt('/manager/surveys/s1', '/manager/surveys/:id', <SurveyEditPage />)
    await userEvent.click(await screen.findByRole('button', { name: 'Опубликовать' }))
    let dialog = screen.getByRole('dialog', { name: 'Опубликовать опрос' })
    expect(dialog).toHaveTextContent('Студенты группы получат уведомление')
    await userEvent.click(within(dialog).getByRole('button', { name: 'Отмена' }))
    expect(calls.some((c) => c.method === 'POST')).toBe(false)

    await userEvent.click(screen.getByRole('button', { name: 'Опубликовать' }))
    dialog = screen.getByRole('dialog', { name: 'Опубликовать опрос' })
    await userEvent.click(within(dialog).getByRole('button', { name: 'Опубликовать' }))
    await waitFor(() => expect(calls.some((c) => c.method === 'POST' && c.url.pathname === '/api/manager/surveys/s1/open')).toBe(true))
  })
})

describe('SurveysPage', () => {
  const item = (overrides: Partial<SurveyListItem>): SurveyListItem => ({
    id: 's1', type: 'ServiceSurvey', title: 'Столовая', status: 'Open', groupId: 'g1', groupName: 'MBA-01', groupStatus: 'Active',
    teacherName: null, disciplineName: null, opensAtLocal: null, closesAtLocal: null, questionCount: 3, responseCount: 1,
    studentCount: 2, createdAt: '2026-10-01T09:00:00Z', ...overrides,
  })

  it('confirms closing and reopening and shows how many students answered', async () => {
    const calls = mockApi((c) => (c.url.pathname === '/api/manager/surveys'
      ? [item({}), item({ id: 's2', title: 'Библиотека', status: 'Closed' })]
      : c.method === 'POST' ? detail() : references(c)))
    render(<MemoryRouter><SurveysPage /></MemoryRouter>)

    expect(await screen.findAllByText('1 из 2')).toHaveLength(2)
    await userEvent.click(screen.getByRole('button', { name: 'Закрыть' }))
    await userEvent.click(within(screen.getByRole('dialog', { name: 'Закрыть опрос' })).getByRole('button', { name: 'Закрыть опрос' }))
    await waitFor(() => expect(calls.some((c) => c.url.pathname === '/api/manager/surveys/s1/close')).toBe(true))

    await userEvent.click(screen.getByRole('button', { name: 'Открыть снова' }))
    const dialog = screen.getByRole('dialog', { name: 'Открыть опрос снова' })
    expect(dialog).toHaveTextContent('Повторных уведомлений не будет')
  })
})

describe('SurveyTemplates', () => {
  const listItem = (overrides: Partial<SurveyListItem> = {}): SurveyListItem => ({
    id: 's1', type: 'ServiceSurvey', title: 'Столовая', status: 'Open', groupId: 'g1', groupName: 'MBA-01', groupStatus: 'Active',
    teacherName: null, disciplineName: null, opensAtLocal: null, closesAtLocal: null, questionCount: 3, responseCount: 1,
    studentCount: 2, createdAt: '2026-10-01T09:00:00Z', ...overrides,
  })
  const templateDetail = {
    id: 'tpl1', type: 'TeachingEvaluation' as const, title: 'Шаблон курса', description: 'Desc',
    createdAt: '2026-10-01T09:00:00Z', updatedAt: '2026-10-01T09:00:00Z', questions,
  }

  it('saves a survey as a template from the list', async () => {
    const calls = mockApi((c) => {
      if (c.url.pathname === '/api/manager/surveys' && c.method === 'GET') return [listItem()]
      if (c.url.pathname.endsWith('/save-as-template')) return templateDetail
      return references(c) ?? []
    })
    render(<MemoryRouter><SurveysPage /></MemoryRouter>)
    await userEvent.click(await screen.findByRole('button', { name: 'Сохранить как шаблон' }))
    await userEvent.click(within(screen.getByRole('dialog')).getByRole('button', { name: 'Сохранить как шаблон' }))
    await waitFor(() => expect(calls.some((c) => c.url.pathname === '/api/manager/surveys/s1/save-as-template')).toBe(true))
  })

  it('creates a draft survey from a template with teacher and discipline', async () => {
    const { SurveyFromTemplatePage } = await import('../pages/manager/SurveyFromTemplatePage')
    const calls = mockApi((c) => {
      if (c.url.pathname === '/api/manager/survey-templates' && c.method === 'GET') {
        return [{ id: 'tpl1', type: 'TeachingEvaluation', title: 'Шаблон курса', questionCount: 3,
          createdAt: '2026-10-01T09:00:00Z', updatedAt: '2026-10-01T09:00:00Z' }]
      }
      if (c.url.pathname === '/api/manager/survey-templates/tpl1') return templateDetail
      if (c.url.pathname === '/api/manager/surveys/from-template') return detail({ status: 'Draft', id: 's-new' })
      return references(c)
    })
    renderAt('/manager/surveys/from-template?templateId=tpl1', '/manager/surveys/from-template', <SurveyFromTemplatePage />)

    await screen.findByText(/Вопросов в шаблоне: 3/)
    await userEvent.selectOptions(screen.getByLabelText(/^Группа/), 'g1')
    await userEvent.selectOptions(screen.getByLabelText(/^Преподаватель/), 't1')
    await userEvent.selectOptions(screen.getByLabelText(/^Дисциплина/), 'd1')
    await userEvent.click(screen.getByRole('button', { name: 'Создать черновик опроса' }))
    await waitFor(() => expect(calls.some((c) => c.url.pathname === '/api/manager/surveys/from-template')).toBe(true))
    const body = calls.find((c) => c.url.pathname === '/api/manager/surveys/from-template')?.body as Record<string, unknown>
    expect(body).toMatchObject({ templateId: 'tpl1', groupId: 'g1', teacherId: 't1', disciplineId: 'd1' })
  })

  it('lists templates and deletes one', async () => {
    const { SurveyTemplatesPage } = await import('../pages/manager/SurveyTemplatesPage')
    const calls = mockApi((c) => {
      if (c.url.pathname === '/api/manager/survey-templates' && c.method === 'GET') {
        return [{ id: 'tpl1', type: 'ServiceSurvey', title: 'Сервис', questionCount: 2,
          createdAt: '2026-10-01T09:00:00Z', updatedAt: '2026-10-02T09:00:00Z' }]
      }
      return undefined
    })
    render(<MemoryRouter><SurveyTemplatesPage /></MemoryRouter>)
    expect(await screen.findByText('Сервис')).toBeInTheDocument()
    await userEvent.click(screen.getByRole('button', { name: 'Удалить' }))
    await userEvent.click(within(screen.getByRole('dialog')).getByRole('button', { name: 'Удалить' }))
    await waitFor(() => expect(calls.some((c) => c.method === 'DELETE' && c.url.pathname === '/api/manager/survey-templates/tpl1')).toBe(true))
  })
})

describe('SurveyTakePage', () => {
  const take = () => renderAt('/student/surveys/s1', '/student/surveys/:id', <SurveyTakePage />)

  it('shows scale labels and blocks submission until required questions are answered', async () => {
    const calls = mockApi(() => studentDetail())
    take()
    expect(await screen.findByText('1 — Совсем непонятно')).toBeInTheDocument()
    expect(screen.getByText('5 — Полностью понятно')).toBeInTheDocument()
    expect(screen.getByText(/Опрос не анонимный/)).toBeInTheDocument()

    await userEvent.click(screen.getByRole('button', { name: 'Отправить ответ' }))
    expect(screen.getAllByText('Обязательное поле')).toHaveLength(2)
    expect(calls.some((c) => c.method === 'POST')).toBe(false)
  })

  it('sends only the value of each question type', async () => {
    const calls = mockApi((c) => (c.method === 'POST' ? json({ id: 'r1' }, 201) : studentDetail()))
    take()
    await userEvent.click(await screen.findByLabelText('4'))
    await userEvent.click(screen.getByLabelText('Онлайн'))
    fireEvent.change(screen.getByLabelText('Комментарий'), { target: { value: 'Всё хорошо' } })
    await userEvent.click(screen.getByRole('button', { name: 'Отправить ответ' }))
    await waitFor(() => expect(calls.some((c) => c.method === 'POST')).toBe(true))
    expect(calls.find((c) => c.method === 'POST')?.body).toEqual({
      answers: [
        { questionId: 'q1', intValue: 4 },
        { questionId: 'q2', optionId: 'o2' },
        { questionId: 'q3', textValue: 'Всё хорошо' },
      ],
    })
  })

  it('reports a repeated submission', async () => {
    mockApi((c) => (c.method === 'POST' ? json({ code: 'survey_already_submitted' }, 409) : studentDetail()))
    take()
    await userEvent.click(await screen.findByLabelText('3'))
    await userEvent.click(screen.getByLabelText('Очно'))
    await userEvent.click(screen.getByRole('button', { name: 'Отправить ответ' }))
    expect(await screen.findByText('Вы уже ответили на этот опрос.')).toBeInTheDocument()
  })

  it('shows only the student’s own status and answers after submission', async () => {
    mockApi(() => studentDetail({
      submitted: true, submittedAt: '2026-10-02T10:00:00Z', canRespond: false,
      myAnswers: [{ questionId: 'q1', intValue: 5, optionId: null, textValue: null }, { questionId: 'q2', intValue: null, optionId: 'o1', textValue: null }],
    }))
    take()
    expect(await screen.findByText(/Спасибо! Ваш ответ отправлен/)).toBeInTheDocument()
    expect(screen.getByText('Очно')).toBeInTheDocument()
    expect(screen.queryByRole('button', { name: 'Отправить ответ' })).not.toBeInTheDocument()
    expect(screen.queryByText('Среднее')).not.toBeInTheDocument()
  })

  it('does not offer the form when the survey is not accepting answers', async () => {
    mockApi(() => studentDetail({ canRespond: false, opensAtLocal: '2099-01-01T09:00:00' }))
    take()
    expect(await screen.findByText('Опрос сейчас не принимает ответы.')).toBeInTheDocument()
    expect(screen.queryByRole('button', { name: 'Отправить ответ' })).not.toBeInTheDocument()
  })
})

describe('SurveyResultsPage', () => {
  it('shows each response with its author and the participation count', async () => {
    const responses: SurveyResponse[] = [{
      id: 'r1', studentId: 'st1', studentName: 'Аннина Мария', groupName: 'MBA-01', submittedAt: '2026-10-02T10:00:00Z',
      answers: [
        { questionId: 'q1', intValue: 5, optionId: null, optionText: null, textValue: null },
        { questionId: 'q2', intValue: null, optionId: 'o2', optionText: 'Онлайн', textValue: null },
        { questionId: 'q3', intValue: null, optionId: null, optionText: null, textValue: 'Отличный курс' },
      ],
    }]
    const calls = mockApi((c) => (c.url.pathname.endsWith('/responses') ? responses
      : c.url.pathname.startsWith('/api/manager/exports/') ? new Response('xlsx', { status: 200 }) : detail()))
    vi.spyOn(window, 'alert').mockImplementation(() => {})
    renderAt('/manager/surveys/s1/results', '/manager/surveys/:id/results', <SurveyResultsPage />)

    expect(await screen.findByText('Аннина Мария')).toBeInTheDocument()
    expect(screen.getByText('Ответили: 1 из 2')).toBeInTheDocument()
    expect(screen.getByText('Отличный курс')).toBeInTheDocument()
    expect(screen.getByText(/«Полностью понятно»/)).toBeInTheDocument()
    await userEvent.click(screen.getByRole('button', { name: 'Выгрузить в Excel' }))
    await waitFor(() => expect(calls.some((c) => c.url.pathname === '/api/manager/exports/surveys/s1')).toBe(true))
    expect(calls.find((c) => c.url.pathname === '/api/manager/exports/surveys/s1')?.url.searchParams.get('lang')).toBe('ru')
  })
})
