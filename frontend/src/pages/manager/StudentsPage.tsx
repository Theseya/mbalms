import { useState, type FormEvent } from 'react'
import { useTranslation } from 'react-i18next'
import { useSearchParams } from 'react-router-dom'
import { ApiError, api, query } from '../../api/client'
import type { Group, Student } from '../../api/types'
import { Badge, Empty, ErrorBanner, ExportButton, Field, FormActions, Loading, Modal, PageHeader, Select } from '../../components/ui'
import { groupLabel } from '../../lib/labels'
import { useFieldError } from '../../lib/useFieldError'
import { toApiError, useLoad } from '../../lib/useLoad'

interface Form {
  id: string | null
  lastName: string
  firstName: string
  middleName: string
  email: string
  groupId: string
  password: string
}

export function StudentsPage() {
  const { t } = useTranslation()
  const [params, setParams] = useSearchParams()
  const groupId = params.get('groupId') ?? ''
  const includeArchived = params.get('archived') === '1'
  const groups = useLoad(() => api.get<Group[]>('/api/manager/groups?status=All'), 'groups')
  const listQuery = query({ groupId, includeArchived: includeArchived || undefined })
  const list = useLoad(() => api.get<Student[]>(`/api/manager/students${listQuery}`), listQuery)
  const [form, setForm] = useState<Form | null>(null)
  const [error, setError] = useState<ApiError | null>(null)
  const [actionError, setActionError] = useState<ApiError | null>(null)
  const [saving, setSaving] = useState(false)
  const fieldError = useFieldError(error)
  const activeGroups = groups.data?.filter((g) => g.status === 'Active')

  const setFilter = (key: string, value: string) => {
    const next = new URLSearchParams(params)
    if (value) next.set(key, value)
    else next.delete(key)
    setParams(next, { replace: true })
  }

  const open = (s?: Student) => {
    setError(null)
    setForm({
      id: s?.id ?? null,
      lastName: s?.lastName ?? '',
      firstName: s?.firstName ?? '',
      middleName: s?.middleName ?? '',
      email: s?.email ?? '',
      groupId: s?.groupId ?? groupId,
      password: '',
    })
  }

  const onSubmit = async (e: FormEvent) => {
    e.preventDefault()
    if (!form) return
    setSaving(true)
    setError(null)
    const body = {
      lastName: form.lastName.trim(),
      firstName: form.firstName.trim(),
      middleName: form.middleName.trim() || null,
      email: form.email.trim(),
      groupId: form.groupId || null,
      password: form.password || null,
    }
    try {
      if (form.id) await api.put(`/api/manager/students/${form.id}`, body)
      else await api.post('/api/manager/students', body)
      setForm(null)
      list.reload()
    } catch (err) {
      setError(toApiError(err))
    } finally {
      setSaving(false)
    }
  }

  const onDelete = async (s: Student) => {
    if (!window.confirm(t('common.confirmDelete'))) return
    setActionError(null)
    try {
      await api.del(`/api/manager/students/${s.id}`)
      list.reload()
    } catch (err) {
      setActionError(toApiError(err))
    }
  }

  const editingGroups = form?.id ? groups.data?.filter((g) => g.status === 'Active' || g.id === form.groupId) : activeGroups

  return (
    <>
      <PageHeader
        title={t('students.title')}
        actions={
          <>
            <ExportButton url={`/api/manager/exports/students${listQuery}`} />
            <button type="button" className="btn btn-primary" onClick={() => open()}>{t('students.new')}</button>
          </>
        }
      />
      <div className="filters">
        <Field label={t('common.filterGroup')}>
          {(id) => (
            <Select id={id} value={groupId} onChange={(v) => setFilter('groupId', v)} items={groups.data}
              label={groupLabel(t)} placeholder={t('common.all')} />
          )}
        </Field>
        {!groupId && (
          <label className="checkbox">
            <input type="checkbox" checked={includeArchived} onChange={(e) => setFilter('archived', e.target.checked ? '1' : '')} />
            {t('common.includeArchived')}
          </label>
        )}
      </div>
      <ErrorBanner error={list.error ?? actionError} />
      {list.loading && !list.data ? <Loading /> : !list.data?.length ? <Empty /> : (
        <div className="table-wrap">
          <table>
            <thead>
              <tr>
                <th>{t('students.fullName')}</th>
                <th>{t('common.email')}</th>
                <th>{t('common.group')}</th>
                <th className="actions-col">{t('common.actions')}</th>
              </tr>
            </thead>
            <tbody>
              {list.data.map((s) => (
                <tr key={s.id}>
                  <td data-label={t('students.fullName')}>{s.fullName}</td>
                  <td data-label={t('common.email')}>{s.email}</td>
                  <td data-label={t('common.group')}>
                    {s.groupName}{' '}
                    {s.groupStatus === 'Archived' && <Badge tone="gray">{t('groupStatus.Archived')}</Badge>}
                  </td>
                  <td className="actions-col">
                    <button type="button" className="btn btn-small" onClick={() => open(s)}>{t('common.edit')}</button>
                    <button type="button" className="btn btn-small btn-danger" onClick={() => onDelete(s)}>{t('common.delete')}</button>
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}

      {form && (
        <Modal title={form.id ? t('students.editTitle') : t('students.new')} onClose={() => setForm(null)}>
          <form onSubmit={onSubmit} noValidate>
            <ErrorBanner error={error} />
            <div className="grid-2">
              <Field label={t('common.lastName')} required error={fieldError('lastName')}>
                {(id, d) => <input id={id} aria-describedby={d} value={form.lastName} required
                  onChange={(e) => setForm({ ...form, lastName: e.target.value })} />}
              </Field>
              <Field label={t('common.firstName')} required error={fieldError('firstName')}>
                {(id, d) => <input id={id} aria-describedby={d} value={form.firstName} required
                  onChange={(e) => setForm({ ...form, firstName: e.target.value })} />}
              </Field>
            </div>
            <Field label={t('common.middleName')} error={fieldError('middleName')}>
              {(id, d) => <input id={id} aria-describedby={d} value={form.middleName}
                onChange={(e) => setForm({ ...form, middleName: e.target.value })} />}
            </Field>
            <Field label={t('common.email')} required error={fieldError('email')}>
              {(id, d) => <input id={id} type="email" autoComplete="off" aria-describedby={d} value={form.email} required
                onChange={(e) => setForm({ ...form, email: e.target.value })} />}
            </Field>
            <Field label={t('common.group')} required error={fieldError('groupId')}>
              {(id, d) => <Select id={id} describedBy={d} value={form.groupId} required items={editingGroups}
                label={groupLabel(t)} placeholder={t('common.selectPlaceholder')}
                onChange={(v) => setForm({ ...form, groupId: v })} />}
            </Field>
            <Field
              label={form.id ? t('students.newPassword') : t('students.password')}
              required={!form.id}
              hint={form.id ? t('students.newPasswordHint') : `${t('account.passwordHint')} ${t('students.passwordHint')}`}
              error={fieldError('password')}
            >
              {(id, d) => <input id={id} type="password" autoComplete="new-password" aria-describedby={d} value={form.password}
                onChange={(e) => setForm({ ...form, password: e.target.value })} />}
            </Field>
            <FormActions saving={saving} onCancel={() => setForm(null)} />
          </form>
        </Modal>
      )}
    </>
  )
}
