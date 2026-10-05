export type Role = 'Manager' | 'Student'
export type GroupStatus = 'Active' | 'Archived'
export type LessonFormat = 'Offline' | 'Online' | 'Hybrid'
export type LessonStatus = 'Scheduled' | 'Cancelled'
export type GradeStatus = 'Draft' | 'Published'
export type GradeChangeAction = 'Created' | 'Updated' | 'Published' | 'Unpublished' | 'Deleted'
export type SurveyType = 'TeachingEvaluation' | 'ServiceSurvey'
export type SurveyStatus = 'Draft' | 'Open' | 'Closed'
export type QuestionType = 'Scale' | 'SingleChoice' | 'Text'
export type NotificationType = 'SurveyAssigned' | 'ScheduleChanged' | 'GradePublished'

/** Wall-clock date-time in the application time zone, e.g. "2026-10-05T10:00:00". */
export type LocalDateTime = string
/** ISO instant in UTC. */
export type Instant = string
/** ISO date, e.g. "2026-09-01". */
export type DateOnly = string

export interface Me {
  id: string
  email: string
  role: Role
  displayName: string
  groupName: string | null
  timeZone: string
}

export interface PagedResult<T> {
  items: T[]
  total: number
  page: number
  pageSize: number
}

export interface Program {
  id: string
  name: string
}

export interface Group {
  id: string
  name: string
  startDate: DateOnly | null
  endDate: DateOnly | null
  status: GroupStatus
  archivedAt: Instant | null
  studentCount: number
}

export interface Student {
  id: string
  lastName: string
  firstName: string
  middleName: string | null
  fullName: string
  email: string
  groupId: string
  groupName: string
  groupStatus: GroupStatus
}

export interface Teacher {
  id: string
  lastName: string
  firstName: string
  middleName: string | null
  fullName: string
  email: string | null
}

export interface Discipline {
  id: string
  name: string
  description: string | null
}

export interface Period {
  id: string
  name: string
  startDate: DateOnly | null
  endDate: DateOnly | null
}

export interface Lesson {
  id: string
  groupId: string
  groupName: string
  groupStatus: GroupStatus
  disciplineId: string
  disciplineName: string
  teacherId: string
  teacherName: string
  startsAt: Instant
  endsAt: Instant
  startsAtLocal: LocalDateTime
  endsAtLocal: LocalDateTime
  format: LessonFormat | null
  location: string | null
  comment: string | null
  status: LessonStatus
}

export interface LessonOverlap {
  id: string
  groupName: string
  disciplineName: string
  teacherName: string
  startsAtLocal: LocalDateTime
  endsAtLocal: LocalDateTime
  sameGroup: boolean
  sameTeacher: boolean
}

export interface Grade {
  id: string
  studentId: string
  studentName: string
  groupId: string
  groupName: string
  disciplineId: string
  disciplineName: string
  periodId: string
  periodName: string
  value: number
  status: GradeStatus
  updatedAt: Instant
  publishedAt: Instant | null
}

export interface GradeSheetRow {
  studentId: string
  studentName: string
  gradeId: string | null
  value: number | null
  status: GradeStatus | null
  publishedAt: Instant | null
}

export interface GradeSheet {
  groupId: string
  groupName: string
  groupStatus: GroupStatus
  disciplineId: string
  disciplineName: string
  periodId: string
  periodName: string
  rows: GradeSheetRow[]
}

export interface GradeHistoryEntry {
  id: string
  gradeId: string
  action: GradeChangeAction
  oldValue: number | null
  newValue: number | null
  oldStatus: GradeStatus | null
  newStatus: GradeStatus | null
  changedBy: string | null
  changedAt: Instant
}

export interface SurveyOption {
  id: string
  order: number
  text: string
}

export interface SurveyQuestion {
  id: string
  order: number
  text: string
  type: QuestionType
  isRequired: boolean
  scaleMin: number | null
  scaleMax: number | null
  scaleMinLabel: string | null
  scaleMaxLabel: string | null
  options: SurveyOption[]
}

export interface SurveyListItem {
  id: string
  type: SurveyType
  title: string
  status: SurveyStatus
  groupId: string
  groupName: string
  groupStatus: GroupStatus
  teacherName: string | null
  disciplineName: string | null
  opensAtLocal: LocalDateTime | null
  closesAtLocal: LocalDateTime | null
  questionCount: number
  responseCount: number
  studentCount: number
  createdAt: Instant
}

export interface SurveyDetail {
  id: string
  type: SurveyType
  title: string
  description: string | null
  status: SurveyStatus
  groupId: string
  groupName: string
  groupStatus: GroupStatus
  teacherId: string | null
  teacherName: string | null
  disciplineId: string | null
  disciplineName: string | null
  opensAtLocal: LocalDateTime | null
  closesAtLocal: LocalDateTime | null
  publishedAt: Instant | null
  responseCount: number
  studentCount: number
  questions: SurveyQuestion[]
}

export interface ResponseAnswer {
  questionId: string
  intValue: number | null
  optionId: string | null
  optionText: string | null
  textValue: string | null
}

export interface SurveyResponse {
  id: string
  studentId: string
  studentName: string
  groupName: string
  submittedAt: Instant
  answers: ResponseAnswer[]
}

export interface StudentLesson {
  id: string
  disciplineName: string
  teacherName: string
  startsAt: Instant
  endsAt: Instant
  startsAtLocal: LocalDateTime
  endsAtLocal: LocalDateTime
  format: LessonFormat | null
  location: string | null
  comment: string | null
  status: LessonStatus
}

export interface StudentGrade {
  id: string
  disciplineName: string
  periodName: string
  value: number
  publishedAt: Instant | null
}

export interface StudentSurveyListItem {
  id: string
  type: SurveyType
  title: string
  status: SurveyStatus
  teacherName: string | null
  disciplineName: string | null
  opensAtLocal: LocalDateTime | null
  closesAtLocal: LocalDateTime | null
  submitted: boolean
  submittedAt: Instant | null
  canRespond: boolean
}

export interface StudentAnswer {
  questionId: string
  intValue: number | null
  optionId: string | null
  textValue: string | null
}

export interface StudentSurveyDetail extends StudentSurveyListItem {
  description: string | null
  questions: SurveyQuestion[]
  myAnswers: StudentAnswer[]
}

export interface Dashboard {
  nextLesson: StudentLesson | null
  upcomingLessons: StudentLesson[]
  pendingSurveys: number
  unreadNotifications: number
  recentGrades: StudentGrade[]
}

export interface AppNotification {
  id: string
  type: NotificationType
  payload: Record<string, string>
  createdAt: Instant
  readAt: Instant | null
}

export type ImportRowAction = 'Create' | 'Update' | 'Conflict' | 'Error' | 'Skip'

export interface ImportRowError {
  field: string
  code: string
  sheet?: string | null
  row?: number | null
  column?: string | null
}

export interface ImportCellError {
  sheet: string
  row: number
  column: string
  code: string
  field?: string | null
}

export interface ImportPreviewRow {
  rowNumber: number
  action: ImportRowAction
  values: Record<string, string | null>
  errors: ImportRowError[]
  warnings?: { code: string; message?: string | null }[] | null
}

export interface ImportPreview {
  importId: string
  createCount: number
  updateCount: number
  conflictCount: number
  errorCount: number
  rows: ImportPreviewRow[]
  fileErrors: ImportCellError[]
}

export interface ImportConfirmResult {
  created: number
  updated: number
  skipped: number
}
