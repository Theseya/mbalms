import { useEffect, useState } from 'react'
import { useTranslation } from 'react-i18next'
import { Link } from 'react-router-dom'
import { api, query } from '../../api/client'
import type { ChangeLogAction, ChangeLogAuthor, ChangeLogEntityType, ChangeLogField, ChangeLogListItem, PagedResult } from '../../api/types'
import { Empty, ErrorBanner, Field, Loading, PageHeader, Pagination, Select } from '../../components/ui'
import { formatInstant } from '../../lib/format'
import { PAGE_SIZE } from '../../lib/paging'
import { useLoad } from '../../lib/useLoad'

const ACTIONS: ChangeLogAction[] = ['Created', 'Updated', 'Published', 'Unpublished', 'Deleted', 'Opened', 'Closed']
const ENTITY_TYPES: ChangeLogEntityType[] = [
  'Announcement', 'Lesson', 'Survey', 'SurveyTemplate', 'Grade', 'Group', 'Discipline', 'AcademicPeriod', 'Teacher', 'Student',
  'WorkSheet', 'AssessmentElement', 'AssessmentCategory',
]

/** Shown in the Object column; omitted from Created details to avoid repeating context. */
const OBJECT_CONTEXT_FIELDS = new Set(['student', 'title', 'name', 'group', 'groupIds', 'discipline', 'period'])

const MARKER_FIELDS = new Set(['body', 'comment', 'description', 'email', 'password', 'questionsStructure', 'value', 'manualFinal'])
/** Working-sheet scores: "score:<element name>" with a set / changed / cleared marker, never the score. */
const SCORE_PREFIX = 'score:'

const isMarkerField = (field: string) => MARKER_FIELDS.has(field) || field.startsWith(SCORE_PREFIX)

function formatChangeValue(field: string, value: unknown, t: (key: string, opts?: Record<string, unknown>) => string): string {
  if (field.startsWith(SCORE_PREFIX)) {
    const element = field.slice(SCORE_PREFIX.length)
    if (value === 'set') return t('changeLog.scoreSet', { element })
    if (value === 'changed') return t('changeLog.scoreChanged', { element })
    if (value === 'cleared') return t('changeLog.scoreCleared', { element })
    return t('common.none')
  }
  if (field === 'manualFinal') {
    if (value === 'set') return t('changeLog.manualFinalSet')
    if (value === 'changed') return t('changeLog.manualFinalChanged')
    if (value === 'cleared') return t('changeLog.manualFinalCleared')
    return t('common.none')
  }
  if (MARKER_FIELDS.has(field)) {
    if (value === 'set') {
      if (field === 'comment') return t('changeLog.commentSet')
      if (field === 'description') return t('changeLog.descriptionSet')
      if (field === 'email') return t('changeLog.emailSet')
      if (field === 'password') return t('changeLog.passwordSet')
      if (field === 'value') return t('changeLog.valueSet')
      return t('changeLog.bodySet')
    }
    if (value === 'changed') {
      if (field === 'comment') return t('changeLog.commentChanged')
      if (field === 'description') return t('changeLog.descriptionChanged')
      if (field === 'email') return t('changeLog.emailChanged')
      if (field === 'questionsStructure') return t('changeLog.questionsStructureChanged')
      if (field === 'value') return t('changeLog.valueChanged')
      return t('changeLog.bodyChanged')
    }
    if (value === 'reset' && field === 'password') return t('changeLog.passwordReset')
    if (value === 'cleared' && field === 'description') return t('changeLog.descriptionCleared')
    if (value === 'cleared' && field === 'email') return t('changeLog.emailCleared')
    return t('common.none')
  }
  if (value === null || value === undefined) return t('common.none')
  if (typeof value === 'boolean') return value ? t('common.yes') : t('common.no')
  if (Array.isArray(value)) return value.length === 0 ? t('common.none') : value.map(String).join(', ')
  if (field === 'format') return t(`format.${String(value)}`, { defaultValue: String(value) })
  if (field === 'type') return t(`surveyType.${String(value)}`, { defaultValue: String(value) })
  if (field === 'status') {
    const key = String(value)
    if (key === 'Scheduled' || key === 'Cancelled') return t(`lessonStatus.${key}`)
    if (key === 'Open' || key === 'Closed') return t(`surveyStatus.${key}`)
    if (key === 'Active' || key === 'Archived') return t(`groupStatus.${key}`)
    if (key === 'Draft' || key === 'Published') {
      return t(`gradeStatus.${key}`, { defaultValue: t(`announcementStatus.${key}`, { defaultValue: key }) })
    }
    if (key === 'Unpublished') return t(`announcementStatus.${key}`)
    return t(`announcementStatus.${key}`, { defaultValue: key })
  }
  return String(value)
}

function isMarkerValue(field: string, value: unknown): value is 'set' | 'changed' | 'cleared' | 'reset' {
  return isMarkerField(field)
    && (value === 'set' || value === 'changed' || value === 'cleared' || value === 'reset')
}

function changeFieldLabel(field: string, t: (key: string, opts?: Record<string, unknown>) => string): string {
  return t(`changeLog.fields.${field}`, { defaultValue: field })
}

function entityHref(e: ChangeLogListItem): string | null {
  if (!e.entityExists) return null
  if (e.entityType === 'Announcement') return `/manager/announcements/${e.entityId}`
  if (e.entityType === 'Lesson') return '/manager/schedule'
  if (e.entityType === 'Survey') return `/manager/surveys/${e.entityId}`
  if (e.entityType === 'SurveyTemplate') return `/manager/survey-templates/${e.entityId}`
  if (e.entityType === 'Grade') return '/manager/grades'
  if (e.entityType === 'Group') return '/manager/groups'
  if (e.entityType === 'Discipline') return '/manager/disciplines'
  if (e.entityType === 'AcademicPeriod') return '/manager/periods'
  if (e.entityType === 'Teacher') return '/manager/teachers'
  if (e.entityType === 'Student') return '/manager/students'
  if (e.entityType === 'WorkSheet' || e.entityType === 'AssessmentElement' || e.entityType === 'AssessmentCategory') return '/manager/worksheet'
  return null
}

function changeFieldSide(c: ChangeLogField, side: 'old' | 'new'): string | null {
  const raw = side === 'old' ? c.old : c.new
  if (raw === null || raw === undefined) return null
  if (typeof raw === 'string' && raw.trim() === '') return null
  if (Array.isArray(raw) && raw.length === 0) return null
  return String(raw)
}

/** Prefer structured *new* change values; never fall back to old (Updated must not show the previous name). */
function objectPrimaryName(e: ChangeLogListItem): string {
  const fromChangesNew = (field: string): string | null => {
    const c = e.changes.find((x) => x.field === field)
    if (!c) return null
    return changeFieldSide(c, 'new')
  }
  if (e.entityType === 'Grade') {
    const student = fromChangesNew('student')
    if (student) return student
  }
  const title = fromChangesNew('title') ?? fromChangesNew('name')
  if (title) return title
  return e.entityTitle || e.entityId
}

function objectSecondaryParts(e: ChangeLogListItem): string[] {
  const fromChangesNew = (field: string): string | null => {
    const c = e.changes.find((x) => x.field === field)
    if (!c) return null
    return changeFieldSide(c, 'new')
  }
  const parts: string[] = []
  const discipline = fromChangesNew('discipline')
  const period = fromChangesNew('period')
  if (discipline) parts.push(discipline)
  if (period) parts.push(period)
  if (e.groupNames.length > 0) {
    for (const name of e.groupNames) {
      if (!parts.includes(name)) parts.push(name)
    }
  } else {
    const group = fromChangesNew('group')
    if (group && !parts.includes(group)) parts.push(group)
  }
  return parts
}

function ChangeDetails({ action, changes, source, t }: {
  action: ChangeLogAction
  changes: ChangeLogField[]
  source: string | null
  t: (key: string, opts?: Record<string, unknown>) => string
}) {
  const visible = action === 'Created'
    ? changes.filter((c) => !OBJECT_CONTEXT_FIELDS.has(c.field))
    : changes

  if (visible.length === 0 && !source) return <p className="muted">{t('changeLog.noFieldChanges')}</p>

  return (
    <div>
      {source && (
        <p className="muted change-log-source">{t(`changeLog.sources.${source}`, { defaultValue: source })}</p>
      )}
      {visible.length === 0 ? (
        <p className="muted">{t('changeLog.noFieldChanges')}</p>
      ) : (
        <ul className="change-log-fields">
          {visible.map((c) => {
            if (isMarkerValue(c.field, c.new)) {
              return (
                <li key={c.field}>
                  <span className="change-log-new">{formatChangeValue(c.field, c.new, t)}</span>
                </li>
              )
            }
            const label = changeFieldLabel(c.field, t)
            const hasOld = c.old !== null && c.old !== undefined
            if (!hasOld) {
              return (
                <li key={c.field}>
                  <strong>{label}</strong>
                  {': '}
                  <span className="change-log-new">{formatChangeValue(c.field, c.new, t)}</span>
                </li>
              )
            }
            return (
              <li key={c.field}>
                <strong>{label}</strong>
                {': '}
                <span className="change-log-old">{formatChangeValue(c.field, c.old, t)}</span>
                {' → '}
                <span className="change-log-new">{formatChangeValue(c.field, c.new, t)}</span>
              </li>
            )
          })}
        </ul>
      )}
    </div>
  )
}

function ObjectCell({ e, t }: {
  e: ChangeLogListItem
  t: (key: string, opts?: Record<string, unknown>) => string
}) {
  const href = entityHref(e)
  const name = objectPrimaryName(e)
  const meta = objectSecondaryParts(e)

  return (
    <div className="change-log-object">
      <div className="change-log-object-main">
        <span className="muted">{t(`changeLog.entityTypes.${e.entityType}`)}</span>
        {' · '}
        {href ? (
          <Link to={href}>{name}</Link>
        ) : (
          <span>
            {name}
            {!e.entityExists && <> <span className="muted">({t('changeLog.deletedObject')})</span></>}
          </span>
        )}
      </div>
      {meta.length > 0 && (
        <small className="muted block change-log-object-meta">{meta.join(' · ')}</small>
      )}
    </div>
  )
}

export function ChangeLogPage() {
  const { t } = useTranslation()
  const [page, setPage] = useState(1)
  const [from, setFrom] = useState('')
  const [to, setTo] = useState('')
  const [actorUserId, setActorUserId] = useState('')
  const [action, setAction] = useState('')
  const [entityType, setEntityType] = useState('')

  const authors = useLoad(() => api.get<ChangeLogAuthor[]>('/api/manager/change-log/authors'), 'change-log-authors')

  const listQuery = query({
    from: from ? new Date(from).toISOString() : undefined,
    to: to ? new Date(to).toISOString() : undefined,
    actorUserId: actorUserId || undefined,
    action: action || undefined,
    entityType: entityType || undefined,
    page,
    pageSize: PAGE_SIZE,
  })
  const list = useLoad(
    () => api.get<PagedResult<ChangeLogListItem>>(`/api/manager/change-log${listQuery}`),
    listQuery,
  )

  useEffect(() => { setPage(1) }, [from, to, actorUserId, action, entityType])

  const rows = list.data?.items ?? []
  const actionItems = ACTIONS.map((a) => ({ id: a }))
  const entityTypeItems = ENTITY_TYPES.map((id) => ({ id }))

  const actorLabel = (e: ChangeLogListItem) => {
    if (e.actorIsSystem) return t('changeLog.system')
    if (e.actorMissing) return t('changeLog.deletedUser', { email: e.actorEmail ?? e.actorUserId })
    return e.actorEmail ?? t('common.none')
  }

  return (
    <div className="layout-fluid">
      <PageHeader title={t('changeLog.title')} />
      <p className="muted">{t('changeLog.intro')}</p>
      <p className="muted">{t('changeLog.gradeValuesHint')}</p>
      <ErrorBanner error={list.error ?? authors.error} />

      <div className="filters change-log-filters">
        <Field label={t('changeLog.from')}>
          {(id) => <input id={id} type="datetime-local" value={from} onChange={(e) => setFrom(e.target.value)} />}
        </Field>
        <Field label={t('changeLog.to')}>
          {(id) => <input id={id} type="datetime-local" value={to} onChange={(e) => setTo(e.target.value)} />}
        </Field>
        <Field label={t('changeLog.author')}>
          {(id) => (
            <Select
              id={id}
              value={actorUserId}
              onChange={setActorUserId}
              items={authors.data}
              label={(a) => a.email}
              placeholder={t('common.all')}
            />
          )}
        </Field>
        <Field label={t('changeLog.action')}>
          {(id) => (
            <Select
              id={id}
              value={action}
              onChange={setAction}
              items={actionItems}
              label={(a) => t(`changeLog.actions.${a.id}`)}
              placeholder={t('common.all')}
            />
          )}
        </Field>
        <Field label={t('changeLog.entityType')}>
          {(id) => (
            <Select
              id={id}
              value={entityType}
              onChange={(value) => {
                setEntityType(value)
                setPage(1)
              }}
              items={entityTypeItems}
              label={(e) => t(`changeLog.filterEntityTypes.${e.id}`)}
              placeholder={t('changeLog.filterEntityTypes.all')}
            />
          )}
        </Field>
      </div>

      {list.loading && !list.data ? <Loading /> : rows.length === 0 ? (
        <Empty>{t('changeLog.empty')}</Empty>
      ) : (
        <div className="table-wrap">
          <table className="data-table change-log-table">
            <thead>
              <tr>
                <th>{t('changeLog.occurredAt')}</th>
                <th>{t('changeLog.author')}</th>
                <th>{t('changeLog.action')}</th>
                <th>{t('changeLog.object')}</th>
                <th>{t('changeLog.details')}</th>
              </tr>
            </thead>
            <tbody>
              {rows.map((e) => (
                <tr key={e.id}>
                  <td data-label={t('changeLog.occurredAt')} className="change-log-when">{formatInstant(e.occurredAt)}</td>
                  <td data-label={t('changeLog.author')} className="change-log-author">{actorLabel(e)}</td>
                  <td data-label={t('changeLog.action')} className="change-log-action">{t(`changeLog.actions.${e.action}`)}</td>
                  <td data-label={t('changeLog.object')} className="change-log-object-cell">
                    <ObjectCell e={e} t={t} />
                  </td>
                  <td data-label={t('changeLog.details')} className="change-log-details-cell">
                    <ChangeDetails action={e.action} changes={e.changes} source={e.source} t={t} />
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}

      {list.data && (
        <Pagination page={list.data.page} pageSize={list.data.pageSize} total={list.data.total} onChange={setPage} />
      )}
    </div>
  )
}
