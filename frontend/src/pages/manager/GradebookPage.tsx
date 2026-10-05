import { useState, type FormEvent } from 'react'
import { useTranslation } from 'react-i18next'
import { Link } from 'react-router-dom'
import { ApiError, api } from '../../api/client'
import type { Discipline, GradeSheet, Group, Period } from '../../api/types'
import { Badge, Empty, ErrorBanner, Field, Loading, PageHeader, Select } from '../../components/ui'
import { formatInstant } from '../../lib/format'
import { parseGrade } from '../../lib/grades'
import { groupLabel } from '../../lib/labels'
import { useConfirm } from '../../lib/useConfirm'
import { useFieldError } from '../../lib/useFieldError'
import { toApiError, useLoad } from '../../lib/useLoad'

export function GradebookPage() {
  const { t } = useTranslation()
  const [groupId, setGroupId] = useState('')
  const [disciplineId, setDisciplineId] = useState('')
  const [periodId, setPeriodId] = useState('')
  const groups = useLoad(() => api.get<Group[]>('/api/manager/groups?status=All'), 'groups')
  const disciplines = useLoad(() => api.get<Discipline[]>('/api/manager/disciplines'), 'disciplines')
  const periods = useLoad(() => api.get<Period[]>('/api/manager/periods'), 'periods')
  const url = groupId && disciplineId && periodId ? `/api/manager/grades/sheet/${groupId}/${disciplineId}/${periodId}` : ''
  const sheet = useLoad(() => (url ? api.get<GradeSheet>(url) : Promise.resolve(null)), url)
  const current = sheet.data && sheet.data.groupId === groupId && sheet.data.disciplineId === disciplineId
    && sheet.data.periodId === periodId ? sheet.data : null

  return (
    <>
      <div className="layout-fluid" hidden />
      <PageHeader title={t('gradebook.title')} actions={<Link className="btn" to="/manager/grades">{t('nav.grades')}</Link>} />
      <p className="muted">{t('gradebook.intro')}</p>
      <ErrorBanner error={groups.error ?? disciplines.error ?? periods.error} />
      <div className="filters">
        <Field label={t('common.group')} required>
          {(id) => <Select id={id} value={groupId} onChange={setGroupId} items={groups.data} label={groupLabel(t)}
            placeholder={t('common.selectPlaceholder')} required />}
        </Field>
        <Field label={t('common.discipline')} required>
          {(id) => <Select id={id} value={disciplineId} onChange={setDisciplineId} items={disciplines.data}
            label={(d) => d.name} placeholder={t('common.selectPlaceholder')} required />}
        </Field>
        <Field label={t('common.period')} required>
          {(id) => <Select id={id} value={periodId} onChange={setPeriodId} items={periods.data}
            label={(p) => p.name} placeholder={t('common.selectPlaceholder')} required />}
        </Field>
      </div>
      {periods.data && periods.data.length === 0 && <div className="alert alert-info">{t('grades.noPeriods')}</div>}
      {!url ? <p className="muted">{t('gradebook.choose')}</p>
        : <ErrorBanner error={sheet.error} />}
      {url && !sheet.error && (!current ? <Loading /> : <SheetEditor key={url} initial={current} url={url} />)}
    </>
  )
}

function SheetEditor({ initial, url }: { initial: GradeSheet; url: string }) {
  const { t } = useTranslation()
  const [sheet, setSheet] = useState(initial)
  const [values, setValues] = useState(() => valuesOf(initial))
  const [error, setError] = useState<ApiError | null>(null)
  const [sent, setSent] = useState<string[]>([])
  const [notice, setNotice] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)
  const { confirm, dialog } = useConfirm()
  const fieldError = useFieldError(error)
  const readOnly = sheet.groupStatus === 'Archived'

  const rows = sheet.rows.map((row) => {
    const raw = values[row.studentId] ?? ''
    const parsed = raw.trim() === '' && row.value === null ? null : parseGrade(raw)
    const dirty = parsed !== null && (!parsed.ok || parsed.value !== row.value)
    const index = sent.indexOf(row.studentId)
    const message = parsed && !parsed.ok ? t(`validation.${parsed.error}`)
      : index >= 0 ? fieldError(`entries[${index}].value`) ?? fieldError(`entries[${index}].studentId`) : undefined
    return { row, raw, parsed, dirty, message }
  })
  const changes = rows.filter((r) => r.dirty)
  const invalid = changes.some((r) => r.parsed && !r.parsed.ok)
  const drafts = sheet.rows.filter((r) => r.status === 'Draft' && r.gradeId)

  const reset = (next: GradeSheet) => {
    setSheet(next)
    setValues(valuesOf(next))
    setSent([])
  }

  const run = async (action: () => Promise<string>) => {
    setBusy(true)
    setError(null)
    setNotice(null)
    try {
      setNotice(await action())
    } catch (err) {
      setError(toApiError(err))
    } finally {
      setBusy(false)
    }
  }

  const save = async (e: FormEvent) => {
    e.preventDefault()
    setNotice(null)
    if (invalid) return
    if (changes.length === 0) {
      setNotice(t('gradebook.noChanges'))
      return
    }
    const published = changes.filter((r) => r.row.status === 'Published')
    if (published.length > 0 && !(await confirm({
      title: t('gradebook.confirmChangesTitle'),
      message: t('gradebook.confirmChanges'),
      confirmLabel: t('gradebook.confirmChangesAction'),
      details: published.map((r) => `${r.row.studentName}: ${r.row.value} → ${r.raw.trim()}`),
    }))) return
    const entries = changes.map((r) => ({ studentId: r.row.studentId, value: r.parsed?.ok ? r.parsed.value : null }))
    setSent(entries.map((x) => x.studentId))
    await run(async () => {
      reset(await api.put<GradeSheet>(url, { entries, confirmPublishedChanges: published.length > 0 }))
      return t('gradebook.saved')
    })
  }

  const publish = async () => {
    const count = drafts.length
    if (!(await confirm({
      title: t('gradebook.confirmPublishTitle'),
      message: t('gradebook.confirmPublish', { count }),
      confirmLabel: t('grades.publish'),
    }))) return
    await run(async () => {
      await api.post('/api/manager/grades/publish', { ids: drafts.map((r) => r.gradeId) })
      reset(await api.get<GradeSheet>(url))
      return t('gradebook.published', { count })
    })
  }

  if (sheet.rows.length === 0) return <Empty>{t('gradebook.noStudents')}</Empty>

  return (
    <form onSubmit={save} noValidate>
      {readOnly && <div className="alert alert-info">{t('gradebook.archived')}</div>}
      <ErrorBanner error={error} />
      {notice && <div className="alert alert-success" role="status">{notice}</div>}
      <div className="table-wrap">
        <table>
          <thead>
            <tr>
              <th>{t('grades.student')}</th>
              <th>{t('grades.value')}</th>
              <th>{t('common.status')}</th>
            </tr>
          </thead>
          <tbody>
            {rows.map(({ row, raw, dirty, message }) => {
              const errorId = message ? `grade-error-${row.studentId}` : undefined
              return (
                <tr key={row.studentId} className={dirty ? 'row-dirty' : undefined}>
                  <td data-label={t('grades.student')}>{row.studentName}</td>
                  <td data-label={t('grades.value')}>
                    <input className="grade-input" type="number" inputMode="numeric" min={0} max={100} step={1}
                      aria-label={`${t('grades.valueShort')}: ${row.studentName}`} aria-invalid={message ? true : undefined}
                      aria-describedby={errorId} value={raw} disabled={readOnly || busy}
                      onChange={(e) => setValues({ ...values, [row.studentId]: e.target.value })} />
                    {dirty && !message && <small className="muted"> {t('gradebook.changed')}</small>}
                    {message && <small id={errorId} className="field-error block">{message}</small>}
                  </td>
                  <td data-label={t('common.status')}>
                    {row.status
                      ? <Badge tone={row.status === 'Published' ? 'green' : 'amber'}>{t(`gradeStatus.${row.status}`)}</Badge>
                      : <span className="muted">{t('gradebook.noGrade')}</span>}
                    {row.publishedAt && <small className="muted block">{formatInstant(row.publishedAt)}</small>}
                  </td>
                </tr>
              )
            })}
          </tbody>
        </table>
      </div>
      <p className="muted"><small>{t('grades.rangeHint')}. {t('gradebook.emptyHint')}</small></p>
      {!readOnly && (
        <div className="form-actions">
          {changes.length > 0 && <span className="muted">{t('gradebook.unsaved', { count: changes.length })}</span>}
          <button type="button" className="btn" onClick={publish}
            disabled={busy || drafts.length === 0 || changes.length > 0}
            title={changes.length > 0 ? t('gradebook.publishBlocked') : undefined}>
            {t('gradebook.publishDrafts', { count: drafts.length })}
          </button>
          <button type="submit" className="btn btn-primary" disabled={busy || invalid}>{t('common.save')}</button>
        </div>
      )}
      {dialog}
    </form>
  )
}

function valuesOf(sheet: GradeSheet): Record<string, string> {
  return Object.fromEntries(sheet.rows.map((r) => [r.studentId, r.value === null ? '' : String(r.value)]))
}
