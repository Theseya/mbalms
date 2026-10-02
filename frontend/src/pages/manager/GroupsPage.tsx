import { useState, type FormEvent } from 'react'
import { useTranslation } from 'react-i18next'
import { Link } from 'react-router-dom'
import { ApiError, api, query } from '../../api/client'
import type { Group } from '../../api/types'
import { Badge, Empty, ErrorBanner, ExportButton, Field, FormActions, Loading, Modal, PageHeader } from '../../components/ui'
import { formatInstant, formatLocalDate } from '../../lib/format'
import { useFieldError } from '../../lib/useFieldError'
import { toApiError, useLoad } from '../../lib/useLoad'

type Filter = 'Active' | 'Archived' | 'All'
interface Form { id: string | null; name: string; startDate: string; endDate: string }

export function GroupsPage() {
  const { t } = useTranslation()
  const [filter, setFilter] = useState<Filter>('Active')
  const list = useLoad(() => api.get<Group[]>(`/api/manager/groups${query({ status: filter })}`), filter)
  const [form, setForm] = useState<Form | null>(null)
  const [error, setError] = useState<ApiError | null>(null)
  const [actionError, setActionError] = useState<ApiError | null>(null)
  const [saving, setSaving] = useState(false)
  const fieldError = useFieldError(error)

  const open = (g?: Group) => {
    setError(null)
    setForm({ id: g?.id ?? null, name: g?.name ?? '', startDate: g?.startDate ?? '', endDate: g?.endDate ?? '' })
  }

  const onSubmit = async (e: FormEvent) => {
    e.preventDefault()
    if (!form) return
    setSaving(true)
    setError(null)
    const body = { name: form.name.trim(), startDate: form.startDate || null, endDate: form.endDate || null }
    try {
      if (form.id) await api.put(`/api/manager/groups/${form.id}`, body)
      else await api.post('/api/manager/groups', body)
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
      list.reload()
    } catch (err) {
      setActionError(toApiError(err))
    }
  }

  return (
    <>
      <PageHeader
        title={t('groups.title')}
        actions={
          <>
            <ExportButton url={`/api/manager/exports/groups${query({ status: filter })}`} />
            <button type="button" className="btn btn-primary" onClick={() => open()}>{t('groups.new')}</button>
          </>
        }
      />
      <div className="tabs" role="tablist">
        {(['Active', 'Archived', 'All'] as const).map((f) => (
          <button key={f} type="button" role="tab" aria-selected={filter === f}
            className={filter === f ? 'tab active' : 'tab'} onClick={() => setFilter(f)}>
            {t(`groupFilter.${f}`)}
          </button>
        ))}
      </div>
      <ErrorBanner error={list.error ?? actionError} />
      {list.loading && !list.data ? <Loading /> : !list.data?.length ? <Empty /> : (
        <div className="table-wrap">
          <table>
            <thead>
              <tr>
                <th>{t('common.name')}</th>
                <th>{t('common.startDate')}</th>
                <th>{t('common.endDate')}</th>
                <th>{t('groups.students')}</th>
                <th>{t('common.status')}</th>
                <th className="actions-col">{t('common.actions')}</th>
              </tr>
            </thead>
            <tbody>
              {list.data.map((g) => (
                <tr key={g.id}>
                  <td data-label={t('common.name')}>
                    <Link to={`/manager/students?groupId=${g.id}`}>{g.name}</Link>
                  </td>
                  <td data-label={t('common.startDate')}>{formatLocalDate(g.startDate)}</td>
                  <td data-label={t('common.endDate')}>{formatLocalDate(g.endDate)}</td>
                  <td data-label={t('groups.students')}>{g.studentCount}</td>
                  <td data-label={t('common.status')}>
                    <Badge tone={g.status === 'Active' ? 'green' : 'gray'}>{t(`groupStatus.${g.status}`)}</Badge>
                    {g.archivedAt && <small className="muted block">{t('groups.archivedAt')} {formatInstant(g.archivedAt)}</small>}
                  </td>
                  <td className="actions-col">
                    <button type="button" className="btn btn-small" onClick={() => open(g)}>{t('common.edit')}</button>
                    {g.status === 'Active' ? (
                      <button type="button" className="btn btn-small"
                        onClick={() => act(() => api.post(`/api/manager/groups/${g.id}/archive`), t('groups.confirmArchive'))}>
                        {t('groups.archive')}
                      </button>
                    ) : (
                      <button type="button" className="btn btn-small"
                        onClick={() => act(() => api.post(`/api/manager/groups/${g.id}/restore`))}>
                        {t('groups.restore')}
                      </button>
                    )}
                    {g.studentCount === 0 && (
                      <button type="button" className="btn btn-small btn-danger"
                        onClick={() => act(() => api.del(`/api/manager/groups/${g.id}`), t('common.confirmDelete'))}>
                        {t('common.delete')}
                      </button>
                    )}
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}

      {form && (
        <Modal title={form.id ? t('groups.editTitle') : t('groups.new')} onClose={() => setForm(null)}>
          <form onSubmit={onSubmit} noValidate>
            <ErrorBanner error={error} />
            <Field label={t('common.name')} required error={fieldError('name')}>
              {(id, d) => <input id={id} aria-describedby={d} value={form.name} required
                onChange={(e) => setForm({ ...form, name: e.target.value })} />}
            </Field>
            <div className="grid-2">
              <Field label={t('common.startDate')} error={fieldError('startDate')}>
                {(id, d) => <input id={id} type="date" aria-describedby={d} value={form.startDate}
                  onChange={(e) => setForm({ ...form, startDate: e.target.value })} />}
              </Field>
              <Field label={t('common.endDate')} error={fieldError('endDate')}>
                {(id, d) => <input id={id} type="date" aria-describedby={d} value={form.endDate}
                  onChange={(e) => setForm({ ...form, endDate: e.target.value })} />}
              </Field>
            </div>
            <FormActions saving={saving} onCancel={() => setForm(null)} />
          </form>
        </Modal>
      )}
    </>
  )
}
