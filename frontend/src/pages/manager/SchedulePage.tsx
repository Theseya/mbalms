import { useState, type FormEvent } from 'react'
import { useTranslation } from 'react-i18next'
import { ApiError, api, query } from '../../api/client'
import type { Discipline, Group, Lesson, LessonFormat, Teacher } from '../../api/types'
import { Badge, Empty, ErrorBanner, ExportButton, Field, FormActions, Loading, Modal, PageHeader, Select } from '../../components/ui'
import { formatLocalDate, formatLocalTime, getAppTimeZone } from '../../lib/format'
import { groupLabel } from '../../lib/labels'
import { useFieldError } from '../../lib/useFieldError'
import { toApiError, useLoad } from '../../lib/useLoad'

const FORMATS: LessonFormat[] = ['Offline', 'Online', 'Hybrid']

interface Form {
  id: string | null
  groupId: string
  disciplineId: string
  teacherId: string
  date: string
  start: string
  end: string
  format: string
  location: string
  comment: string
}

export function SchedulePage() {
  const { t } = useTranslation()
  const [groupId, setGroupId] = useState('')
  const [from, setFrom] = useState('')
  const [to, setTo] = useState('')
  const groups = useLoad(() => api.get<Group[]>('/api/manager/groups?status=All'), 'groups')
  const disciplines = useLoad(() => api.get<Discipline[]>('/api/manager/disciplines'), 'disciplines')
  const teachers = useLoad(() => api.get<Teacher[]>('/api/manager/teachers'), 'teachers')
  const q = query({ groupId, from, to })
  const list = useLoad(() => api.get<Lesson[]>(`/api/manager/lessons${q}`), q)
  const [form, setForm] = useState<Form | null>(null)
  const [error, setError] = useState<ApiError | null>(null)
  const [actionError, setActionError] = useState<ApiError | null>(null)
  const [saving, setSaving] = useState(false)
  const fieldError = useFieldError(error)

  const open = (l?: Lesson) => {
    setError(null)
    setForm({
      id: l?.id ?? null,
      groupId: l?.groupId ?? groupId,
      disciplineId: l?.disciplineId ?? '',
      teacherId: l?.teacherId ?? '',
      date: l ? l.startsAtLocal.slice(0, 10) : '',
      start: l ? l.startsAtLocal.slice(11, 16) : '',
      end: l ? l.endsAtLocal.slice(11, 16) : '',
      format: l?.format ?? '',
      location: l?.location ?? '',
      comment: l?.comment ?? '',
    })
  }

  const onSubmit = async (e: FormEvent) => {
    e.preventDefault()
    if (!form) return
    setSaving(true)
    setError(null)
    const body = {
      groupId: form.groupId || null,
      disciplineId: form.disciplineId || null,
      teacherId: form.teacherId || null,
      startsAt: form.date && form.start ? `${form.date}T${form.start}` : null,
      endsAt: form.date && form.end ? `${form.date}T${form.end}` : null,
      format: form.format || null,
      location: form.location.trim() || null,
      comment: form.comment.trim() || null,
    }
    try {
      if (form.id) await api.put(`/api/manager/lessons/${form.id}`, body)
      else await api.post('/api/manager/lessons', body)
      setForm(null)
      list.reload()
    } catch (err) {
      setError(toApiError(err))
    } finally {
      setSaving(false)
    }
  }

  const onDelete = async (l: Lesson) => {
    if (!window.confirm(t('common.confirmDelete'))) return
    setActionError(null)
    try {
      await api.del(`/api/manager/lessons/${l.id}`)
      list.reload()
    } catch (err) {
      setActionError(toApiError(err))
    }
  }

  const activeGroups = groups.data?.filter((g) => g.status === 'Active')

  return (
    <>
      <PageHeader
        title={t('schedule.title')}
        actions={
          <>
            <ExportButton url={`/api/manager/exports/schedule${q}`} />
            <button type="button" className="btn btn-primary" onClick={() => open()}>{t('schedule.new')}</button>
          </>
        }
      />
      <p className="muted">{t('schedule.timeZoneNote', { tz: getAppTimeZone() })}</p>
      <div className="filters">
        <Field label={t('common.filterGroup')}>
          {(id) => <Select id={id} value={groupId} onChange={setGroupId} items={groups.data} label={groupLabel(t)} placeholder={t('common.all')} />}
        </Field>
        <Field label={t('common.from')}>
          {(id) => <input id={id} type="date" value={from} onChange={(e) => setFrom(e.target.value)} />}
        </Field>
        <Field label={t('common.to')}>
          {(id) => <input id={id} type="date" value={to} onChange={(e) => setTo(e.target.value)} />}
        </Field>
      </div>
      <ErrorBanner error={list.error ?? actionError} />
      {list.loading && !list.data ? <Loading /> : !list.data?.length ? <Empty>{t('schedule.noLessons')}</Empty> : (
        <div className="table-wrap">
          <table>
            <thead>
              <tr>
                <th>{t('schedule.date')}</th>
                <th>{t('schedule.time')}</th>
                <th>{t('common.group')}</th>
                <th>{t('common.discipline')}</th>
                <th>{t('common.teacher')}</th>
                <th>{t('schedule.format')}</th>
                <th>{t('schedule.location')}</th>
                <th className="actions-col">{t('common.actions')}</th>
              </tr>
            </thead>
            <tbody>
              {list.data.map((l) => (
                <tr key={l.id}>
                  <td data-label={t('schedule.date')}>{formatLocalDate(l.startsAtLocal)}</td>
                  <td data-label={t('schedule.time')}>{formatLocalTime(l.startsAtLocal)}–{formatLocalTime(l.endsAtLocal)}</td>
                  <td data-label={t('common.group')}>
                    {l.groupName} {l.groupStatus === 'Archived' && <Badge tone="gray">{t('groupStatus.Archived')}</Badge>}
                  </td>
                  <td data-label={t('common.discipline')}>{l.disciplineName}</td>
                  <td data-label={t('common.teacher')}>{l.teacherName}</td>
                  <td data-label={t('schedule.format')}>{l.format ? t(`format.${l.format}`) : '—'}</td>
                  <td data-label={t('schedule.location')} className="wrap">{l.location ?? '—'}</td>
                  <td className="actions-col">
                    {l.groupStatus === 'Active' && (
                      <>
                        <button type="button" className="btn btn-small" onClick={() => open(l)}>{t('common.edit')}</button>
                        <button type="button" className="btn btn-small btn-danger" onClick={() => onDelete(l)}>{t('common.delete')}</button>
                      </>
                    )}
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}

      {form && (
        <Modal title={form.id ? t('schedule.editTitle') : t('schedule.new')} onClose={() => setForm(null)}>
          <form onSubmit={onSubmit} noValidate>
            <ErrorBanner error={error} />
            <Field label={t('common.group')} required error={fieldError('groupId')}>
              {(id, d) => <Select id={id} describedBy={d} value={form.groupId} items={activeGroups} label={(g) => g.name}
                placeholder={t('common.selectPlaceholder')} onChange={(v) => setForm({ ...form, groupId: v })} required />}
            </Field>
            <Field label={t('common.discipline')} required error={fieldError('disciplineId')}>
              {(id, d) => <Select id={id} describedBy={d} value={form.disciplineId} items={disciplines.data} label={(x) => x.name}
                placeholder={t('common.selectPlaceholder')} onChange={(v) => setForm({ ...form, disciplineId: v })} required />}
            </Field>
            <Field label={t('common.teacher')} required error={fieldError('teacherId')}>
              {(id, d) => <Select id={id} describedBy={d} value={form.teacherId} items={teachers.data} label={(x) => x.fullName}
                placeholder={t('common.selectPlaceholder')} onChange={(v) => setForm({ ...form, teacherId: v })} required />}
            </Field>
            <div className="grid-3">
              <Field label={t('schedule.date')} required error={fieldError('startsAt')}>
                {(id, d) => <input id={id} type="date" aria-describedby={d} value={form.date} required
                  onChange={(e) => setForm({ ...form, date: e.target.value })} />}
              </Field>
              <Field label={t('schedule.startsAt')} required>
                {(id) => <input id={id} type="time" value={form.start} required
                  onChange={(e) => setForm({ ...form, start: e.target.value })} />}
              </Field>
              <Field label={t('schedule.endsAt')} required error={fieldError('endsAt')}>
                {(id, d) => <input id={id} type="time" aria-describedby={d} value={form.end} required
                  onChange={(e) => setForm({ ...form, end: e.target.value })} />}
              </Field>
            </div>
            <small className="hint">{t('schedule.timeZoneNote', { tz: getAppTimeZone() })}</small>
            <Field label={t('schedule.format')}>
              {(id) => (
                <select id={id} value={form.format} onChange={(e) => setForm({ ...form, format: e.target.value })}>
                  <option value="">—</option>
                  {FORMATS.map((f) => <option key={f} value={f}>{t(`format.${f}`)}</option>)}
                </select>
              )}
            </Field>
            <Field label={t('schedule.location')} error={fieldError('location')}>
              {(id, d) => <input id={id} aria-describedby={d} value={form.location}
                onChange={(e) => setForm({ ...form, location: e.target.value })} />}
            </Field>
            <Field label={t('schedule.comment')} error={fieldError('comment')}>
              {(id, d) => <textarea id={id} rows={2} aria-describedby={d} value={form.comment}
                onChange={(e) => setForm({ ...form, comment: e.target.value })} />}
            </Field>
            <FormActions saving={saving} onCancel={() => setForm(null)} />
          </form>
        </Modal>
      )}
    </>
  )
}
