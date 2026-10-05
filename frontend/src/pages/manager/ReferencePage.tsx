import { useState, type FormEvent } from 'react'
import { useTranslation } from 'react-i18next'
import { ApiError, api } from '../../api/client'
import type { Discipline, Period, Teacher } from '../../api/types'
import { ImportDialog, type ImportPreviewColumn } from '../../components/ImportDialog'
import {
  Empty, ErrorBanner, ExportButton, Field, FormActions, Loading, Modal, PageHeader, Pagination, SearchField,
} from '../../components/ui'
import { formatLocalDate } from '../../lib/format'
import { PAGE_SIZE, matchesSearch, pageOf } from '../../lib/paging'
import { useConfirm } from '../../lib/useConfirm'
import { useFieldError } from '../../lib/useFieldError'
import { toApiError, useLoad } from '../../lib/useLoad'

type FieldKind = 'text' | 'email' | 'textarea' | 'date'

interface FieldDef {
  name: string
  label: string
  kind: FieldKind
  required?: boolean
  maxLength?: number
}

interface ColumnDef<T> {
  label: string
  value: (item: T) => string
}

interface ReferenceConfig<T> {
  title: string
  newLabel: string
  editLabel: string
  endpoint: string
  exportUrl?: string
  /** When set, shows Import next to Export (Excel import vertical for this entity). */
  importPath?: string
  importPreviewColumns?: ImportPreviewColumn[]
  fields: FieldDef[]
  columns: ColumnDef<T>[]
  /** Shown in the delete confirmation. */
  itemName: (item: T) => string
  searchValues: (item: T) => (string | null | undefined)[]
  searchPlaceholder?: string
}

type Values = Record<string, string>

function ReferencePage<T extends { id: string }>({ config }: { config: ReferenceConfig<T> }) {
  const { t } = useTranslation()
  const list = useLoad(() => api.get<T[]>(config.endpoint), config.endpoint)
  const [editing, setEditing] = useState<{ id: string | null; values: Values } | null>(null)
  const [importOpen, setImportOpen] = useState(false)
  const [error, setError] = useState<ApiError | null>(null)
  const [actionError, setActionError] = useState<ApiError | null>(null)
  const [saving, setSaving] = useState(false)
  const [search, setSearch] = useState('')
  const [page, setPage] = useState(1)
  const fieldError = useFieldError(error)
  const { confirm, dialog } = useConfirm()
  const filtered = list.data?.filter((item) => matchesSearch(search, ...config.searchValues(item))) ?? []
  const shown = pageOf(filtered, page)

  const emptyValues = () => Object.fromEntries(config.fields.map((f) => [f.name, '']))
  const open = (item?: T) => {
    setError(null)
    const values = emptyValues()
    if (item) {
      for (const f of config.fields) values[f.name] = String((item as Record<string, unknown>)[f.name] ?? '')
    }
    setEditing({ id: item?.id ?? null, values })
  }

  const onSubmit = async (e: FormEvent) => {
    e.preventDefault()
    if (!editing) return
    setSaving(true)
    setError(null)
    const body = Object.fromEntries(Object.entries(editing.values).map(([k, v]) => [k, v.trim() === '' ? null : v.trim()]))
    try {
      if (editing.id) await api.put(`${config.endpoint}/${editing.id}`, body)
      else await api.post(config.endpoint, body)
      setEditing(null)
      list.reload()
    } catch (err) {
      setError(toApiError(err))
    } finally {
      setSaving(false)
    }
  }

  const onDelete = async (item: T) => {
    const ok = await confirm({
      title: t('reference.confirmDeleteTitle'),
      message: t('reference.confirmDelete', { name: config.itemName(item) }),
      confirmLabel: t('common.delete'),
      danger: true,
    })
    if (!ok) return
    setActionError(null)
    try {
      await api.del(`${config.endpoint}/${item.id}`)
      list.reload()
    } catch (err) {
      setActionError(toApiError(err))
    }
  }

  return (
    <>
      <PageHeader
        title={config.title}
        actions={
          <>
            {config.exportUrl && <ExportButton url={config.exportUrl} />}
            {config.importPath && (
              <button type="button" className="btn" onClick={() => setImportOpen(true)}>{t('import.open')}</button>
            )}
            <button type="button" className="btn btn-primary" onClick={() => open()}>{config.newLabel}</button>
          </>
        }
      />
      <div className="filters">
        <SearchField value={search} placeholder={config.searchPlaceholder}
          onChange={(v) => { setSearch(v); setPage(1) }} />
      </div>
      <ErrorBanner error={list.error ?? actionError} />
      {list.loading && !list.data ? <Loading /> : !shown.items.length ? <Empty /> : (
        <div className="table-wrap">
          <table>
            <thead>
              <tr>
                {config.columns.map((c) => <th key={c.label}>{c.label}</th>)}
                <th className="actions-col">{t('common.actions')}</th>
              </tr>
            </thead>
            <tbody>
              {shown.items.map((item) => (
                <tr key={item.id}>
                  {config.columns.map((c) => <td key={c.label} data-label={c.label}>{c.value(item)}</td>)}
                  <td className="actions-col">
                    <button type="button" className="btn btn-small" onClick={() => open(item)}>{t('common.edit')}</button>
                    <button type="button" className="btn btn-small btn-danger" onClick={() => onDelete(item)}>{t('common.delete')}</button>
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
      <Pagination page={shown.page} pageSize={PAGE_SIZE} total={filtered.length} onChange={setPage} />

      {editing && (
        <Modal title={editing.id ? config.editLabel : config.newLabel} onClose={() => setEditing(null)}>
          <form onSubmit={onSubmit} noValidate>
            <ErrorBanner error={error} />
            {config.fields.map((f) => (
              <Field key={f.name} label={f.label} required={f.required} error={fieldError(f.name)}>
                {(id, d) => {
                  const common = {
                    id,
                    'aria-describedby': d,
                    value: editing.values[f.name],
                    required: f.required,
                    maxLength: f.maxLength,
                    onChange: (e: { target: { value: string } }) =>
                      setEditing({ ...editing, values: { ...editing.values, [f.name]: e.target.value } }),
                  }
                  return f.kind === 'textarea' ? <textarea rows={3} {...common} /> : <input type={f.kind} {...common} />
                }}
              </Field>
            ))}
            <FormActions saving={saving} onCancel={() => setEditing(null)} />
          </form>
        </Modal>
      )}
      {dialog}
      {importOpen && config.importPath && config.importPreviewColumns && (
        <ImportDialog
          entityPath={config.importPath}
          previewColumns={config.importPreviewColumns}
          onClose={() => setImportOpen(false)}
          onImported={() => list.reload()}
        />
      )}
    </>
  )
}

export function TeachersPage() {
  const { t } = useTranslation()
  return (
    <ReferencePage<Teacher>
      config={{
        title: t('teachers.title'),
        newLabel: t('teachers.new'),
        editLabel: t('teachers.editTitle'),
        endpoint: '/api/manager/teachers',
        exportUrl: '/api/manager/exports/teachers',
        importPath: '/api/manager/imports/teachers',
        importPreviewColumns: [
          { key: 'lastName', labelKey: 'common.lastName' },
          { key: 'firstName', labelKey: 'common.firstName' },
          { key: 'middleName', labelKey: 'common.middleName' },
          { key: 'email', labelKey: 'common.email' },
        ],
        fields: [
          { name: 'lastName', label: t('common.lastName'), kind: 'text', required: true, maxLength: 100 },
          { name: 'firstName', label: t('common.firstName'), kind: 'text', required: true, maxLength: 100 },
          { name: 'middleName', label: t('common.middleName'), kind: 'text', maxLength: 100 },
          { name: 'email', label: t('common.email'), kind: 'email', maxLength: 256 },
        ],
        columns: [
          { label: t('students.fullName'), value: (x) => x.fullName },
          { label: t('common.email'), value: (x) => x.email ?? '—' },
        ],
        itemName: (x) => x.fullName,
        searchValues: (x) => [x.fullName, x.email],
        searchPlaceholder: t('students.searchPlaceholder'),
      }}
    />
  )
}

export function DisciplinesPage() {
  const { t } = useTranslation()
  return (
    <ReferencePage<Discipline>
      config={{
        title: t('disciplines.title'),
        newLabel: t('disciplines.new'),
        editLabel: t('disciplines.editTitle'),
        endpoint: '/api/manager/disciplines',
        exportUrl: '/api/manager/exports/disciplines',
        importPath: '/api/manager/imports/disciplines',
        importPreviewColumns: [
          { key: 'name', labelKey: 'common.name' },
          { key: 'description', labelKey: 'common.description' },
        ],
        fields: [
          { name: 'name', label: t('common.name'), kind: 'text', required: true, maxLength: 200 },
          { name: 'description', label: t('common.description'), kind: 'textarea', maxLength: 2000 },
        ],
        columns: [
          { label: t('common.name'), value: (x) => x.name },
          { label: t('common.description'), value: (x) => x.description ?? '—' },
        ],
        itemName: (x) => x.name,
        searchValues: (x) => [x.name, x.description],
      }}
    />
  )
}

export function PeriodsPage() {
  const { t } = useTranslation()
  return (
    <ReferencePage<Period>
      config={{
        title: t('periods.title'),
        newLabel: t('periods.new'),
        editLabel: t('periods.editTitle'),
        endpoint: '/api/manager/periods',
        itemName: (x) => x.name,
        searchValues: (x) => [x.name],
        fields: [
          { name: 'name', label: t('common.name'), kind: 'text', required: true, maxLength: 100 },
          { name: 'startDate', label: t('common.startDate'), kind: 'date' },
          { name: 'endDate', label: t('common.endDate'), kind: 'date' },
        ],
        columns: [
          { label: t('common.name'), value: (x) => x.name },
          { label: t('common.startDate'), value: (x) => formatLocalDate(x.startDate) },
          { label: t('common.endDate'), value: (x) => formatLocalDate(x.endDate) },
        ],
      }}
    />
  )
}
