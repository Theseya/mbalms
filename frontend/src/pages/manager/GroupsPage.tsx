import { useState, type FormEvent } from 'react'
import { useTranslation } from 'react-i18next'
import { Link } from 'react-router-dom'
import { ApiError, api, query } from '../../api/client'
import type { Group, Program } from '../../api/types'
import { ImportDialog } from '../../components/ImportDialog'
import {
  Badge, Empty, ErrorBanner, ExportButton, Field, FormActions, Loading, Modal, PageHeader, Pagination, SearchField,
} from '../../components/ui'
import { formatInstant, formatLocalDate } from '../../lib/format'
import { PAGE_SIZE, matchesSearch, pageOf } from '../../lib/paging'
import { useConfirm } from '../../lib/useConfirm'
import { useFieldError } from '../../lib/useFieldError'
import { toApiError, useLoad } from '../../lib/useLoad'

type Filter = 'Active' | 'Archived' | 'All'
interface Form { id: string | null; name: string; startDate: string; endDate: string }

const groupImportColumns = [
  { key: 'name', labelKey: 'common.name' },
  { key: 'startDate', labelKey: 'common.startDate' },
  { key: 'endDate', labelKey: 'common.endDate' },
]

export function GroupsPage() {
  const { t } = useTranslation()
  const [filter, setFilter] = useState<Filter>('Active')
  const [search, setSearch] = useState('')
  const [page, setPage] = useState(1)
  const list = useLoad(() => api.get<Group[]>(`/api/manager/groups${query({ status: filter })}`), filter)
  const [form, setForm] = useState<Form | null>(null)
  const [importOpen, setImportOpen] = useState(false)
  const [error, setError] = useState<ApiError | null>(null)
  const [actionError, setActionError] = useState<ApiError | null>(null)
  const [saving, setSaving] = useState(false)
  const fieldError = useFieldError(error)
  const { confirm, dialog } = useConfirm()

  const filtered = list.data?.filter((g) => matchesSearch(search, g.name)) ?? []
  const shown = pageOf(filtered, page)

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

  const act = async (action: () => Promise<unknown>) => {
    setActionError(null)
    try {
      await action()
      list.reload()
    } catch (err) {
      setActionError(toApiError(err))
    }
  }

  const onArchive = async (g: Group) => {
    const ok = await confirm({
      title: t('groups.confirmArchiveTitle'),
      message: t('groups.confirmArchive', { name: g.name, count: g.studentCount }),
      confirmLabel: t('groups.archive'),
    })
    if (ok) await act(() => api.post(`/api/manager/groups/${g.id}/archive`))
  }

  const onDelete = async (g: Group) => {
    const ok = await confirm({
      title: t('groups.confirmDeleteTitle'),
      message: t('groups.confirmDelete', { name: g.name }),
      confirmLabel: t('common.delete'),
      danger: true,
    })
    if (ok) await act(() => api.del(`/api/manager/groups/${g.id}`))
  }

  return (
    <>
      <div className="layout-fluid" hidden />
      <PageHeader
        title={t('groups.title')}
        actions={
          <>
            <ExportButton url={`/api/manager/exports/groups${query({ status: filter })}`} />
            <button type="button" className="btn" onClick={() => setImportOpen(true)}>{t('import.open')}</button>
            <button type="button" className="btn btn-primary" onClick={() => open()}>{t('groups.new')}</button>
          </>
        }
      />
      <ProgramCard />
      <div className="tabs" role="tablist">
        {(['Active', 'Archived', 'All'] as const).map((f) => (
          <button key={f} type="button" role="tab" aria-selected={filter === f}
            className={filter === f ? 'tab active' : 'tab'} onClick={() => { setFilter(f); setPage(1) }}>
            {t(`groupFilter.${f}`)}
          </button>
        ))}
      </div>
      <div className="filters">
        <SearchField value={search} placeholder={t('groups.searchPlaceholder')}
          onChange={(v) => { setSearch(v); setPage(1) }} />
      </div>
      <ErrorBanner error={list.error ?? actionError} />
      {list.loading && !list.data ? <Loading /> : !shown.items.length ? <Empty /> : (
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
              {shown.items.map((g) => (
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
                      <button type="button" className="btn btn-small" onClick={() => onArchive(g)}>{t('groups.archive')}</button>
                    ) : (
                      <button type="button" className="btn btn-small"
                        onClick={() => act(() => api.post(`/api/manager/groups/${g.id}/restore`))}>
                        {t('groups.restore')}
                      </button>
                    )}
                    {g.studentCount === 0 && (
                      <button type="button" className="btn btn-small btn-danger" onClick={() => onDelete(g)}>{t('common.delete')}</button>
                    )}
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
      <Pagination page={shown.page} pageSize={PAGE_SIZE} total={filtered.length} onChange={setPage} />

      {form && (
        <Modal title={form.id ? t('groups.editTitle') : t('groups.new')} onClose={() => setForm(null)}>
          <form onSubmit={onSubmit} noValidate>
            <ErrorBanner error={error} />
            <Field label={t('common.name')} required error={fieldError('name')}>
              {(id, d) => <input id={id} aria-describedby={d} value={form.name} required maxLength={100}
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
      {importOpen && (
        <ImportDialog
          entityPath="/api/manager/imports/groups"
          previewColumns={groupImportColumns}
          onClose={() => setImportOpen(false)}
          onImported={() => list.reload()}
        />
      )}
      {dialog}
    </>
  )
}

function ProgramCard() {
  const { t } = useTranslation()
  const program = useLoad(() => api.get<Program>('/api/manager/program'), 'program')
  const [name, setName] = useState<string | null>(null)
  const [error, setError] = useState<ApiError | null>(null)
  const [saving, setSaving] = useState(false)
  const fieldError = useFieldError(error)

  const onSubmit = async (e: FormEvent) => {
    e.preventDefault()
    if (name === null) return
    setSaving(true)
    setError(null)
    try {
      await api.put('/api/manager/program', { name: name.trim() })
      setName(null)
      program.reload()
    } catch (err) {
      setError(toApiError(err))
    } finally {
      setSaving(false)
    }
  }

  if (program.error) return <ErrorBanner error={program.error} />
  if (!program.data) return null
  return (
    <section className="card program-card" aria-label={t('program.title')}>
      <div>
        <small className="muted">{t('program.title')}</small>
        <strong>{program.data.name}</strong>
      </div>
      <button type="button" className="btn btn-small" onClick={() => { setError(null); setName(program.data!.name) }}>
        {t('program.rename')}
      </button>
      {name !== null && (
        <Modal title={t('program.editTitle')} onClose={() => setName(null)}>
          <form onSubmit={onSubmit} noValidate>
            <ErrorBanner error={error} />
            <Field label={t('common.name')} required error={fieldError('name')}>
              {(id, d) => <input id={id} aria-describedby={d} value={name} required maxLength={200}
                onChange={(e) => setName(e.target.value)} />}
            </Field>
            <FormActions saving={saving} onCancel={() => setName(null)} />
          </form>
        </Modal>
      )}
    </section>
  )
}
