import { useState, type FormEvent } from 'react'
import { useTranslation } from 'react-i18next'
import { ApiError, api, query } from '../../api/client'
import type { Discipline, Grade, Group, Period, Student } from '../../api/types'
import { Badge, Empty, ErrorBanner, ExportButton, Field, FormActions, Loading, Modal, PageHeader, Select } from '../../components/ui'
import { formatInstant } from '../../lib/format'
import { parseGrade } from '../../lib/grades'
import { groupLabel } from '../../lib/labels'
import { useFieldError } from '../../lib/useFieldError'
import { toApiError, useLoad } from '../../lib/useLoad'

interface Form {
  id: string | null
  status: Grade['status'] | null
  studentId: string
  disciplineId: string
  periodId: string
  value: string
}

export function GradesPage() {
  const { t } = useTranslation()
  const [groupId, setGroupId] = useState('')
  const [periodId, setPeriodId] = useState('')
  const [disciplineId, setDisciplineId] = useState('')
  const groups = useLoad(() => api.get<Group[]>('/api/manager/groups?status=All'), 'groups')
  const periods = useLoad(() => api.get<Period[]>('/api/manager/periods'), 'periods')
  const disciplines = useLoad(() => api.get<Discipline[]>('/api/manager/disciplines'), 'disciplines')
  const students = useLoad(() => api.get<Student[]>(`/api/manager/students${query({ groupId })}`), `students-${groupId}`)
  const q = query({ groupId, periodId, disciplineId })
  const list = useLoad(() => api.get<Grade[]>(`/api/manager/grades${q}`), q)
  const [selected, setSelected] = useState<Set<string>>(new Set())
  const [form, setForm] = useState<Form | null>(null)
  const [error, setError] = useState<ApiError | null>(null)
  const [localError, setLocalError] = useState<string | null>(null)
  const [actionError, setActionError] = useState<ApiError | null>(null)
  const [saving, setSaving] = useState(false)
  const fieldError = useFieldError(error)

  const isEditable = (g: Grade) => groups.data?.find((x) => x.id === g.groupId)?.status !== 'Archived'
  const drafts = list.data?.filter((g) => g.status === 'Draft' && isEditable(g)) ?? []

  const open = (g?: Grade) => {
    setError(null)
    setLocalError(null)
    setForm({
      id: g?.id ?? null,
      status: g?.status ?? null,
      studentId: g?.studentId ?? '',
      disciplineId: g?.disciplineId ?? disciplineId,
      periodId: g?.periodId ?? periodId,
      value: g ? String(g.value) : '',
    })
  }

  const onSubmit = async (e: FormEvent) => {
    e.preventDefault()
    if (!form) return
    const parsed = parseGrade(form.value)
    if (!parsed.ok) {
      setLocalError(t(`validation.${parsed.error}`))
      return
    }
    setLocalError(null)
    setSaving(true)
    setError(null)
    try {
      if (form.id) await api.put(`/api/manager/grades/${form.id}`, { value: parsed.value })
      else
        await api.post('/api/manager/grades', {
          studentId: form.studentId || null,
          disciplineId: form.disciplineId || null,
          periodId: form.periodId || null,
          value: parsed.value,
        })
      setForm(null)
      list.reload()
    } catch (err) {
      setError(toApiError(err))
    } finally {
      setSaving(false)
    }
  }

  const act = async (action: () => Promise<unknown>, confirmText?: string) => {
    if (confirmText && !window.confirm(confirmText)) return
    setActionError(null)
    try {
      await action()
      setSelected(new Set())
      list.reload()
    } catch (err) {
      setActionError(toApiError(err))
    }
  }

  const toggle = (id: string) => {
    const next = new Set(selected)
    if (next.has(id)) next.delete(id)
    else next.add(id)
    setSelected(next)
  }

  const activeStudents = students.data?.filter((s) => s.groupStatus === 'Active')

  return (
    <>
      <PageHeader
        title={t('grades.title')}
        actions={
          <>
            <ExportButton url={`/api/manager/exports/grades${q}`} />
            {selected.size > 0 && (
              <button type="button" className="btn"
                onClick={() => act(() => api.post('/api/manager/grades/publish', { ids: [...selected] }))}>
                {t('grades.publishSelected', { count: selected.size })}
              </button>
            )}
            <button type="button" className="btn btn-primary" onClick={() => open()} disabled={!periods.data?.length}>
              {t('grades.new')}
            </button>
          </>
        }
      />
      {periods.data && periods.data.length === 0 && <div className="alert alert-info">{t('grades.noPeriods')}</div>}
      <div className="filters">
        <Field label={t('common.filterGroup')}>
          {(id) => <Select id={id} value={groupId} onChange={setGroupId} items={groups.data} label={groupLabel(t)} placeholder={t('common.all')} />}
        </Field>
        <Field label={t('common.filterPeriod')}>
          {(id) => <Select id={id} value={periodId} onChange={setPeriodId} items={periods.data} label={(p) => p.name} placeholder={t('common.all')} />}
        </Field>
        <Field label={t('common.filterDiscipline')}>
          {(id) => <Select id={id} value={disciplineId} onChange={setDisciplineId} items={disciplines.data} label={(d) => d.name} placeholder={t('common.all')} />}
        </Field>
      </div>
      <ErrorBanner error={list.error ?? actionError} />
      {list.loading && !list.data ? <Loading /> : !list.data?.length ? <Empty /> : (
        <div className="table-wrap">
          <table>
            <thead>
              <tr>
                <th className="check-col">
                  <input type="checkbox" aria-label={t('grades.selectAll')}
                    checked={drafts.length > 0 && drafts.every((g) => selected.has(g.id))}
                    onChange={(e) => setSelected(e.target.checked ? new Set(drafts.map((g) => g.id)) : new Set())} />
                </th>
                <th>{t('grades.student')}</th>
                <th>{t('common.group')}</th>
                <th>{t('common.discipline')}</th>
                <th>{t('common.period')}</th>
                <th>{t('grades.valueShort')}</th>
                <th>{t('common.status')}</th>
                <th className="actions-col">{t('common.actions')}</th>
              </tr>
            </thead>
            <tbody>
              {list.data.map((g) => {
                const editable = isEditable(g)
                return (
                  <tr key={g.id}>
                    <td className="check-col">
                      {g.status === 'Draft' && editable && (
                        <input type="checkbox" checked={selected.has(g.id)} onChange={() => toggle(g.id)}
                          aria-label={`${g.studentName} — ${g.disciplineName}`} />
                      )}
                    </td>
                    <td data-label={t('grades.student')}>{g.studentName}</td>
                    <td data-label={t('common.group')}>{g.groupName}</td>
                    <td data-label={t('common.discipline')}>{g.disciplineName}</td>
                    <td data-label={t('common.period')}>{g.periodName}</td>
                    <td data-label={t('grades.valueShort')} className="num"><strong>{g.value}</strong></td>
                    <td data-label={t('common.status')}>
                      <Badge tone={g.status === 'Published' ? 'green' : 'amber'}>{t(`gradeStatus.${g.status}`)}</Badge>
                      {g.publishedAt && <small className="muted block">{formatInstant(g.publishedAt)}</small>}
                    </td>
                    <td className="actions-col">
                      {editable && (
                        <>
                          <button type="button" className="btn btn-small" onClick={() => open(g)}>{t('common.edit')}</button>
                          {g.status === 'Draft' ? (
                            <>
                              <button type="button" className="btn btn-small btn-primary"
                                onClick={() => act(() => api.post(`/api/manager/grades/${g.id}/publish`))}>
                                {t('grades.publish')}
                              </button>
                              <button type="button" className="btn btn-small btn-danger"
                                onClick={() => act(() => api.del(`/api/manager/grades/${g.id}`), t('common.confirmDelete'))}>
                                {t('common.delete')}
                              </button>
                            </>
                          ) : (
                            <button type="button" className="btn btn-small"
                              onClick={() => act(() => api.post(`/api/manager/grades/${g.id}/unpublish`))}>
                              {t('grades.unpublish')}
                            </button>
                          )}
                        </>
                      )}
                    </td>
                  </tr>
                )
              })}
            </tbody>
          </table>
        </div>
      )}

      {form && (
        <Modal title={form.id ? t('grades.editTitle') : t('grades.new')} onClose={() => setForm(null)}>
          <form onSubmit={onSubmit} noValidate>
            <ErrorBanner error={error} />
            {!form.id && (
              <>
                <Field label={t('grades.student')} required error={fieldError('studentId')}>
                  {(id, d) => <Select id={id} describedBy={d} value={form.studentId} items={activeStudents}
                    label={(s) => `${s.fullName} · ${s.groupName}`} placeholder={t('common.selectPlaceholder')}
                    onChange={(v) => setForm({ ...form, studentId: v })} required />}
                </Field>
                <Field label={t('common.discipline')} required error={fieldError('disciplineId')}>
                  {(id, d) => <Select id={id} describedBy={d} value={form.disciplineId} items={disciplines.data}
                    label={(x) => x.name} placeholder={t('common.selectPlaceholder')}
                    onChange={(v) => setForm({ ...form, disciplineId: v })} required />}
                </Field>
                <Field label={t('common.period')} required error={fieldError('periodId')}>
                  {(id, d) => <Select id={id} describedBy={d} value={form.periodId} items={periods.data}
                    label={(x) => x.name} placeholder={t('common.selectPlaceholder')}
                    onChange={(v) => setForm({ ...form, periodId: v })} required />}
                </Field>
              </>
            )}
            {form.status === 'Published' && <div className="alert alert-info">{t('grades.publishedEditNote')}</div>}
            <Field label={t('grades.value')} required hint={t('grades.rangeHint')} error={localError ?? fieldError('value')}>
              {(id, d) => <input id={id} type="number" inputMode="numeric" min={0} max={100} step={1} required
                aria-describedby={d} value={form.value} onChange={(e) => setForm({ ...form, value: e.target.value })} />}
            </Field>
            <FormActions saving={saving} onCancel={() => setForm(null)} />
          </form>
        </Modal>
      )}
    </>
  )
}
