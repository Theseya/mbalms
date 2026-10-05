import { act, render, screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { MemoryRouter } from 'react-router-dom'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import errorsCs from '../../../backend/src/MbaLms.Api/Infrastructure/Errors.cs?raw'
import enumsCs from '../../../backend/src/MbaLms.Api/Domain/Enums.cs?raw'
import gradesCs from '../../../backend/src/MbaLms.Api/Controllers/Manager/GradesController.cs?raw'
import lessonsCs from '../../../backend/src/MbaLms.Api/Controllers/Manager/LessonsController.cs?raw'
import type {
  AppNotification, Dashboard, Discipline, Grade, Group, Lesson, Me, Period, Student, StudentGrade, StudentLesson,
  StudentSurveyDetail, SurveyDetail, SurveyListItem, SurveyQuestion, SurveyResponse, Teacher,
} from '../api/types'
import App from '../App'
import { AuthProvider } from '../auth/AuthContext'
import i18n, { STORAGE_KEY, initialLanguage, setLanguage } from '../i18n'
import en from '../i18n/en'
import ru from '../i18n/ru'
import { LoginPage } from '../pages/LoginPage'

type Dict = Record<string, unknown>

const leaves = (obj: Dict, prefix = ''): [string, string][] =>
  Object.entries(obj).flatMap(([k, v]) =>
    typeof v === 'object' && v !== null ? leaves(v as Dict, `${prefix}${k}.`) : [[`${prefix}${k}`, String(v)] as [string, string]])

const lookup = (dict: Dict, key: string): unknown =>
  key.split('.').reduce<unknown>((o, p) => (o && typeof o === 'object' ? (o as Dict)[p] : undefined), dict)

/** i18next plural forms: Russian needs _few/_many that English does not have. */
const PLURAL = /_(zero|one|two|few|many|other)$/
const baseKey = (key: string) => key.replace(PLURAL, '')
const hasKey = (dict: Dict, key: string) =>
  typeof lookup(dict, key) === 'string' || typeof lookup(dict, `${key}_other`) === 'string'

const placeholders = (s: string) => [...s.matchAll(/\{\{\s*(\w+)\s*\}\}/g)].map((m) => m[1]).sort()

const sources = import.meta.glob(['../**/*.{ts,tsx}', '!../test/**', '!../i18n/**'], {
  query: '?raw', import: 'default', eager: true,
}) as Record<string, string>

/** Constant values of a C# static class, e.g. ErrorCodes. */
function csConstants(source: string, className: string): string[] {
  const body = new RegExp(`class ${className}\\s*\\{([\\s\\S]*?)\\n\\}`).exec(source)?.[1] ?? ''
  return [...body.matchAll(/const string \w+ = "([^"]+)"/g)].map((m) => m[1])
}

function csEnum(source: string, name: string): string[] {
  const body = new RegExp(`enum ${name}\\s*\\{([^}]*)\\}`).exec(source)?.[1] ?? ''
  return [...body.matchAll(/^\s*(\w+)\s*=/gm)].map((m) => m[1])
}

describe('localization resources', () => {
  const dictionaries = { ru, en } as Record<string, Dict>

  it('has the same keys in Russian and English', () => {
    const keys = (dict: Dict) => [...new Set(leaves(dict).map(([k]) => baseKey(k)))].sort()
    expect(keys(en)).toEqual(keys(ru))
  })

  it('has the forms each language needs for plural keys', () => {
    const plurals = [...new Set(leaves(ru).map(([k]) => k).filter((k) => PLURAL.test(k)).map(baseKey))]
    expect(plurals.length).toBeGreaterThan(0)
    for (const key of plurals) {
      for (const form of ['one', 'few', 'many', 'other']) expect(typeof lookup(ru, `${key}_${form}`)).toBe('string')
      for (const form of ['one', 'other']) expect(typeof lookup(en, `${key}_${form}`)).toBe('string')
    }
  })

  it.each(['ru', 'en'])('has no empty strings in %s', (lang) => {
    expect(leaves(dictionaries[lang]).filter(([, v]) => v.trim() === '').map(([k]) => k)).toEqual([])
  })

  it('uses the same interpolation placeholders in both languages', () => {
    const english = (k: string) => String(lookup(en, k) ?? lookup(en, `${baseKey(k)}_other`))
    const mismatched = leaves(ru).filter(([k, v]) => placeholders(v).join() !== placeholders(english(k)).join())
    expect(mismatched.map(([k]) => k)).toEqual([])
  })

  it('defines every key used with a literal t(...) call in the source', () => {
    expect(Object.keys(sources).length).toBeGreaterThan(10)
    const missing = Object.entries(sources).flatMap(([file, src]) =>
      [...src.matchAll(/\bt\(\s*'([\w.]+)'/g)]
        .map((m) => m[1])
        .filter((key) => !hasKey(ru, key) || !hasKey(en, key))
        .map((key) => `${file}: ${key}`))
    expect(missing).toEqual([])
  })

  it('defines the key groups used with t(`prefix.${...}`) in the source', () => {
    const prefixes = new Set(Object.values(sources).flatMap((src) => [...src.matchAll(/\bt\(`([\w.]+)\$\{/g)].map((m) => m[1])))
    expect(prefixes.size).toBeGreaterThan(5)
    const allKeys = leaves(ru).map(([k]) => k)
    expect([...prefixes].filter((p) => !allKeys.some((k) => k.startsWith(p)))).toEqual([])
  })

  it('translates every error code of the API and of the client', () => {
    const apiCodes = csConstants(errorsCs, 'ErrorCodes')
    expect(apiCodes).toContain('validation_failed')
    const clientCodes = ['unauthorized', 'forbidden', 'not_found', 'too_many_requests', 'server_error', 'network_error', 'generic']
    const missing = [...apiCodes, ...clientCodes].filter((c) => typeof lookup(ru, `errors.${c}`) !== 'string')
    expect(missing).toEqual([])
  })

  it('translates every field validation code of the API', () => {
    const codes = csConstants(errorsCs, 'FieldCodes')
    expect(codes).toContain('required')
    expect([...codes, 'invalid'].filter((c) => typeof lookup(ru, `validation.${c}`) !== 'string')).toEqual([])
  })

  it.each([
    ['GroupStatus', 'groupStatus'],
    ['LessonFormat', 'format'],
    ['LessonStatus', 'lessonStatus'],
    ['GradeStatus', 'gradeStatus'],
    ['GradeChangeAction', 'gradeAction'],
    ['SurveyType', 'surveyType'],
    ['SurveyStatus', 'surveyStatus'],
    ['QuestionType', 'questionType'],
  ])('translates every value of the %s enum', (enumName, prefix) => {
    const values = csEnum(enumsCs, enumName)
    expect(values.length).toBeGreaterThan(1)
    expect(values.filter((v) => typeof lookup(ru, `${prefix}.${v}`) !== 'string')).toEqual([])
  })

  it('has a text for every notification type and change sent by the API', () => {
    expect(csEnum(enumsCs, 'NotificationType').sort()).toEqual(['GradePublished', 'ScheduleChanged', 'SurveyAssigned'])
    for (const change of ['created', 'updated', 'deleted', 'cancelled']) {
      expect(lessonsCs).toContain(`"${change}"`)
      expect(typeof lookup(ru, `notifications.ScheduleChanged_${change}`)).toBe('string')
    }
    expect(gradesCs).toContain('Notify(grade, "published")')
    expect(gradesCs).toContain('Notify(grade, "updated")')
    for (const key of ['SurveyAssigned', 'GradePublished', 'GradePublished_updated'])
      expect(typeof lookup(ru, `notifications.${key}`)).toBe('string')
  })
})

describe('language selection', () => {
  afterEach(() => {
    vi.unstubAllGlobals()
    act(() => setLanguage('ru'))
  })

  it('defaults to Russian and ignores unknown stored values', () => {
    localStorage.removeItem(STORAGE_KEY)
    expect(initialLanguage()).toBe('ru')
    localStorage.setItem(STORAGE_KEY, 'de')
    expect(initialLanguage()).toBe('ru')
    localStorage.setItem(STORAGE_KEY, 'en')
    expect(initialLanguage()).toBe('en')
  })

  it('switches the interface, the page language and keeps the choice for the next visit', async () => {
    vi.stubGlobal('fetch', vi.fn(async () => new Response(JSON.stringify({ code: 'unauthorized' }), { status: 401 })))
    localStorage.removeItem(STORAGE_KEY)
    act(() => setLanguage('ru'))
    render(<MemoryRouter><AuthProvider><LoginPage /></AuthProvider></MemoryRouter>)
    expect(screen.getByRole('heading', { name: 'Вход в кабинет' })).toBeInTheDocument()

    await userEvent.selectOptions(screen.getByLabelText('Язык'), 'en')

    expect(await screen.findByRole('heading', { name: 'Sign in' })).toBeInTheDocument()
    expect(screen.getByLabelText(/^Password/)).toBeInTheDocument()
    expect(document.documentElement.lang).toBe('en')
    expect(localStorage.getItem(STORAGE_KEY)).toBe('en')
    expect(initialLanguage()).toBe('en')
  })

  it('falls back to the default text instead of rendering an empty string', () => {
    i18n.addResource('en', 'translation', 'test.empty', '')
    act(() => setLanguage('en'))
    expect(i18n.t('test.empty', { defaultValue: 'fallback' })).not.toBe('')
  })
})

describe('pages in English', () => {
  const manager: Me = { id: 'u1', email: 'manager@example.test', role: 'Manager', displayName: 'Jane Manager', groupName: null, timeZone: 'Europe/Moscow' }
  const studentMe: Me = { id: 'u2', email: 'anna@example.test', role: 'Student', displayName: 'Anna Smith', groupName: 'MBA-01', timeZone: 'Europe/Moscow' }
  const group: Group = { id: 'g1', name: 'MBA-01', startDate: '2026-09-01', endDate: null, status: 'Active', archivedAt: null, studentCount: 2 }
  const student: Student = {
    id: 's1', lastName: 'Smith', firstName: 'Anna', middleName: null, fullName: 'Smith Anna', email: 'anna@example.test',
    groupId: 'g1', groupName: 'MBA-01', groupStatus: 'Active',
  }
  const teacher: Teacher = { id: 't1', lastName: 'Brown', firstName: 'Tom', middleName: null, fullName: 'Brown Tom', email: null }
  const discipline: Discipline = { id: 'd1', name: 'Corporate Finance', description: null }
  const period: Period = { id: 'p1', name: 'Term 1', startDate: '2026-09-01', endDate: '2026-12-31' }
  const times = { startsAt: '2030-04-01T06:30:00Z', endsAt: '2030-04-01T08:00:00Z', startsAtLocal: '2030-04-01T09:30:00', endsAtLocal: '2030-04-01T11:00:00' }
  const lesson: Lesson = {
    id: 'l1', groupId: 'g1', groupName: 'MBA-01', groupStatus: 'Active', disciplineId: 'd1', disciplineName: 'Corporate Finance',
    teacherId: 't1', teacherName: 'Brown Tom', ...times, format: 'Online', location: 'Room 1', comment: null, status: 'Scheduled',
  }
  const grade: Grade = {
    id: 'gr1', studentId: 's1', studentName: 'Smith Anna', groupId: 'g1', groupName: 'MBA-01', disciplineId: 'd1',
    disciplineName: 'Corporate Finance', periodId: 'p1', periodName: 'Term 1', value: 85, status: 'Published',
    updatedAt: '2026-10-01T09:00:00Z', publishedAt: '2026-10-01T09:00:00Z',
  }
  const noScale = { scaleMin: null, scaleMax: null, scaleMinLabel: null, scaleMaxLabel: null }
  const questions: SurveyQuestion[] = [
    { id: 'q1', order: 0, text: 'Clarity', type: 'Scale', isRequired: true, scaleMin: 1, scaleMax: 5, scaleMinLabel: 'Poor', scaleMaxLabel: 'Excellent', options: [] },
    { id: 'q2', order: 1, text: 'Recommend?', type: 'SingleChoice', isRequired: true, ...noScale, options: [{ id: 'o1', order: 0, text: 'Yes' }] },
    { id: 'q3', order: 2, text: 'Comments', type: 'Text', isRequired: false, ...noScale, options: [] },
  ]
  const surveyBase = {
    id: 'sv1', type: 'TeachingEvaluation' as const, title: 'Course feedback', status: 'Open' as const,
    teacherName: 'Brown Tom', disciplineName: 'Corporate Finance', opensAtLocal: '2026-10-01T09:00:00', closesAtLocal: '2030-10-10T09:00:00',
  }
  const surveyItem: SurveyListItem = {
    ...surveyBase, groupId: 'g1', groupName: 'MBA-01', groupStatus: 'Active', questionCount: 3, responseCount: 1, studentCount: 2,
    createdAt: '2026-09-30T09:00:00Z',
  }
  const surveyDetail: SurveyDetail = {
    ...surveyBase, description: null, groupId: 'g1', groupName: 'MBA-01', groupStatus: 'Active', teacherId: 't1', disciplineId: 'd1',
    publishedAt: '2026-10-01T06:00:00Z', responseCount: 1, studentCount: 2, questions,
  }
  const response: SurveyResponse = {
    id: 'r1', studentId: 's1', studentName: 'Smith Anna', groupName: 'MBA-01', submittedAt: '2026-10-02T09:00:00Z',
    answers: [
      { questionId: 'q1', intValue: 4, optionId: null, optionText: null, textValue: null },
      { questionId: 'q2', intValue: null, optionId: 'o1', optionText: 'Yes', textValue: null },
      { questionId: 'q3', intValue: null, optionId: null, optionText: null, textValue: 'Great course' },
    ],
  }
  const studentLesson: StudentLesson = {
    id: 'l1', disciplineName: 'Corporate Finance', teacherName: 'Brown Tom', ...times, format: 'Hybrid', location: 'Room 1', comment: 'Bring a laptop', status: 'Scheduled',
  }
  const studentGrade: StudentGrade = { id: 'gr1', disciplineName: 'Corporate Finance', periodName: 'Term 1', value: 85, publishedAt: '2026-10-01T09:00:00Z' }
  const studentSurvey: StudentSurveyDetail = {
    ...surveyBase, submitted: false, submittedAt: null, canRespond: true, description: null, questions, myAnswers: [],
  }
  const dashboard: Dashboard = { nextLesson: studentLesson, upcomingLessons: [studentLesson], pendingSurveys: 1, unreadNotifications: 2, recentGrades: [studentGrade] }
  const notifications: AppNotification[] = [
    { id: 'n1', type: 'SurveyAssigned', payload: { surveyId: 'sv1', title: 'Course feedback' }, createdAt: '2026-10-02T09:00:00Z', readAt: null },
    { id: 'n2', type: 'ScheduleChanged', payload: { lessonId: 'l1', disciplineName: 'Corporate Finance', startsAtLocal: times.startsAtLocal, change: 'cancelled' }, createdAt: '2026-10-02T08:00:00Z', readAt: null },
    { id: 'n3', type: 'GradePublished', payload: { gradeId: 'gr1', disciplineName: 'Corporate Finance', periodName: 'Term 1', change: 'updated' }, createdAt: '2026-10-01T09:00:00Z', readAt: '2026-10-01T10:00:00Z' },
  ]

  const responses = (me: Me): Record<string, unknown> => ({
    '/api/auth/me': me,
    '/api/manager/program': { id: 'pr1', name: 'Executive MBA' },
    '/api/manager/groups': [group],
    '/api/manager/students': { items: [student], total: 1, page: 1, pageSize: 20 },
    '/api/manager/teachers': [teacher],
    '/api/manager/disciplines': [discipline],
    '/api/manager/periods': [period],
    '/api/manager/lessons': [lesson],
    '/api/manager/lessons/overlaps': [],
    '/api/manager/grades': [grade],
    '/api/manager/surveys': [surveyItem],
    '/api/manager/surveys/sv1': surveyDetail,
    '/api/manager/surveys/sv1/responses': [response],
    '/api/student/dashboard': dashboard,
    '/api/student/schedule': [studentLesson],
    '/api/student/grades': [studentGrade],
    '/api/student/surveys': [studentSurvey],
    '/api/student/surveys/sv1': studentSurvey,
    '/api/notifications': notifications,
    '/api/notifications/unread-count': { count: 2 },
    '/api/auth/csrf': { token: 'test-token' },
  })

  const rawKey = new RegExp(`\\b(${Object.keys(en).join('|')})\\.[A-Za-z_]+`)

  beforeEach(() => act(() => setLanguage('en')))
  afterEach(() => {
    vi.unstubAllGlobals()
    act(() => setLanguage('ru'))
  })

  it.each([
    ['/manager/groups', manager],
    ['/manager/students', manager],
    ['/manager/teachers', manager],
    ['/manager/disciplines', manager],
    ['/manager/periods', manager],
    ['/manager/schedule', manager],
    ['/manager/grades', manager],
    ['/manager/gradebook', manager],
    ['/manager/surveys', manager],
    ['/manager/surveys/new', manager],
    ['/manager/surveys/sv1', manager],
    ['/manager/surveys/sv1/results', manager],
    ['/account', manager],
    ['/student', studentMe],
    ['/student/schedule', studentMe],
    ['/student/grades', studentMe],
    ['/student/surveys', studentMe],
    ['/student/surveys/sv1', studentMe],
    ['/student/notifications', studentMe],
  ])('%s has no Russian text and no untranslated keys', async (path, me) => {
    const data = responses(me)
    vi.stubGlobal('fetch', vi.fn(async (input: RequestInfo | URL) => {
      const body = data[new URL(String(input), 'http://localhost').pathname]
      return body === undefined
        ? new Response(JSON.stringify({ code: 'not_found' }), { status: 404 })
        : new Response(JSON.stringify(body), { status: 200, headers: { 'Content-Type': 'application/json' } })
    }))

    render(<MemoryRouter initialEntries={[path]}><AuthProvider><App /></AuthProvider></MemoryRouter>)

    await screen.findByRole('heading', { level: 1 })
    await waitFor(() => expect(screen.queryByRole('status')).not.toBeInTheDocument())
    expect(screen.queryByRole('alert')).not.toBeInTheDocument()
    const html = document.body.innerHTML
    expect(html.match(/[А-Яа-яЁё]+/g)).toBeNull()
    expect(html.match(rawKey)).toBeNull()
  })
})
