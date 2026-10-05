import { useState, type FormEvent } from 'react'
import { useTranslation } from 'react-i18next'
import { useSearchParams } from 'react-router-dom'
import { ApiError, api, query } from '../../api/client'
import type { Group, PagedResult, Student } from '../../api/types'
import { ImportDialog } from '../../components/ImportDialog'
import {
  Badge, Empty, ErrorBanner, ExportButton, Field, FormActions, Loading, Modal, PageHeader, Pagination, SearchField, Select,
} from '../../components/ui'
import { groupLabel } from '../../lib/labels'
import { PAGE_SIZE, useDebounced } from '../../lib/paging'
import { useConfirm } from '../../lib/useConfirm'
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

const studentImportColumns = [
  { key: 'lastName', labelKey: 'common.lastName' },
  { key: 'firstName', labelKey: 'common.firstName' },
  { key: 'middleName', labelKey: 'common.middleName' },
  { key: 'email', labelKey: 'common.email' },
  { key: 'group', labelKey: 'common.group' },
]

export function StudentsPage() {
  const { t } = useTranslation()
  const [params, setParams] = useSearchParams()
  const groupId = params.get('groupId') ?? ''
  const includeArchived = params.get('archived') === '1'
  const groups = useLoad(() => api.get<Group[]>('/api/manager/groups?status=All'), 'groups')
  const [search, setSearch] = useState('')
  const [page, setPage] = useState(1)
  const debouncedSearch = useDebounced(search.trim())
  const filterQuery = query({ groupId, includeArchived: includeArchived || undefined, search: debouncedSearch })
  const listQuery = query({ groupId, includeArchived: includeArchived || undefined, search: debouncedSearch, page, pageSize: PAGE_SIZE })
  const list = useLoad(() => api.get<PagedResult<Student>>(`/api/manager/students${listQuery}`), listQuery)
  const [form, setForm] = useState<Form | null>(null)
  const [importOpen, setImportOpen] = useState(false)
  const [error, setError] = useState<ApiError | null>(null)
  const [actionError, setActionError] = useState<ApiError | null>(null)
  const [saving, setSaving] = useState(false)
  const fieldError = useFieldError(error)
  const { confirm, dialog } = useConfirm()
  const activeGroups = groups.data?.filter((g) => g.status === 'Active')
  const rows = list.data?.items ?? []

  const setFilter = (key: string, value: string) => {
    const next = new URLSearchParams(params)
    if (value) next.set(key, value)
    else next.delete(key)
    setParams(next, { replace: true })
    setPage(1)
  }

  const onSearch = (value: string) => {
    setSearch(value)
    setPage(1)
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
    const ok = await confirm({
      title: t('students.confirmDeleteTitle'),
      message: t('students.confirmDelete', { name: s.fullName, email: s.email }),
      confirmLabel: t('common.delete'),
      danger: true,
    })
    if (!ok) return
    setActionError(null)
    try {
      await api.del(`/api/manager/students/${s.id}`)
      if (rows.length === 1 && page > 1) setPage(page - 1)
      else list.reload()
    } catch (err) {
      setActionError(toApiError(err))
    }
  }

  const editingGroups = form?.id ? groups.data?.filter((g) => g.status === 'Active' || g.id === form.groupId) : activeGroups

  return (
    <>
      <div className="layout-fluid" hidden />
      <PageHeader
        title={t('students.title')}
        actions={
          <>
            <ExportButton url={`/api/manager/exports/students${filterQuery}`} />
            <button type="button" className="btn" onClick={() => setImportOpen(true)}>{t('import.open')}</button>
            <button type="button" className="btn btn-primary" onClick={() => open()}>{t('students.new')}</button>
          </>
        }
      />
      <div className="filters">
        <SearchField value={search} onChange={onSearch} placeholder={t('students.searchPlaceholder')} />
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
      {list.loading && !list.data ? <Loading /> : !rows.length ? <Empty /> : (
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
              {rows.map((s) => (
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
      {list.data && <Pagination page={list.data.page} pageSize={list.data.pageSize} total={list.data.total} onChange={setPage} />}

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
      {importOpen && (
        <ImportDialog
          entityPath="/api/manager/imports/students"
          previewColumns={studentImportColumns}
          requirePasswordsForCreates
          onClose={() => setImportOpen(false)}
          onImported={() => list.reload()}
        />
      )}
      {dialog}
    </>
  )
}
