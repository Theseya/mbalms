import { useEffect, useId, type ReactNode } from 'react'
import { useTranslation } from 'react-i18next'
import { ApiError, api } from '../api/client'
import { currentLanguage } from '../i18n'

export function PageHeader({ title, actions }: { title: string; actions?: ReactNode }) {
  return (
    <div className="page-header">
      <h1>{title}</h1>
      {actions && <div className="page-actions">{actions}</div>}
    </div>
  )
}

export function Loading() {
  const { t } = useTranslation()
  return <p className="muted" role="status">{t('app.loading')}</p>
}

export function ErrorBanner({ error }: { error: ApiError | null | undefined }) {
  const { t } = useTranslation()
  if (!error) return null
  return (
    <div className="alert alert-error" role="alert">
      {t(`errors.${error.code}`, { defaultValue: t('errors.generic') })}
    </div>
  )
}

export function Empty({ children }: { children?: ReactNode }) {
  const { t } = useTranslation()
  return <p className="empty">{children ?? t('common.empty')}</p>
}

export function Badge({ tone, children }: { tone: 'green' | 'gray' | 'blue' | 'amber' | 'red'; children: ReactNode }) {
  return <span className={`badge badge-${tone}`}>{children}</span>
}

interface FieldProps {
  label: string
  error?: string
  hint?: string
  required?: boolean
  children: (id: string, describedBy: string | undefined) => ReactNode
}

export function Field({ label, error, hint, required, children }: FieldProps) {
  const id = useId()
  const hintId = hint ? `${id}-hint` : undefined
  const errorId = error ? `${id}-error` : undefined
  const describedBy = [hintId, errorId].filter(Boolean).join(' ') || undefined
  return (
    <div className={`field${error ? ' field-invalid' : ''}`}>
      <label htmlFor={id}>
        {label}
        {required && <span className="req" aria-hidden="true"> *</span>}
      </label>
      {children(id, describedBy)}
      {hint && <small id={hintId} className="hint">{hint}</small>}
      {error && <small id={errorId} className="field-error">{error}</small>}
    </div>
  )
}

export function Modal({ title, onClose, children, wide }: { title: string; onClose: () => void; children: ReactNode; wide?: boolean }) {
  const { t } = useTranslation()
  const titleId = useId()
  useEffect(() => {
    const onKey = (e: KeyboardEvent) => {
      if (e.key === 'Escape') onClose()
    }
    document.addEventListener('keydown', onKey)
    return () => document.removeEventListener('keydown', onKey)
  }, [onClose])

  return (
    <div className="modal-backdrop" onMouseDown={(e) => e.target === e.currentTarget && onClose()}>
      <div className={`modal${wide ? ' modal-wide' : ''}`} role="dialog" aria-modal="true" aria-labelledby={titleId}>
        <div className="modal-header">
          <h2 id={titleId}>{title}</h2>
          <button type="button" className="btn-icon" onClick={onClose} aria-label={t('common.close')}>×</button>
        </div>
        {children}
      </div>
    </div>
  )
}

export function FormActions({ saving, onCancel, submitLabel }: { saving: boolean; onCancel: () => void; submitLabel?: string }) {
  const { t } = useTranslation()
  return (
    <div className="form-actions">
      <button type="button" className="btn" onClick={onCancel} disabled={saving}>{t('common.cancel')}</button>
      <button type="submit" className="btn btn-primary" disabled={saving}>{submitLabel ?? t('common.save')}</button>
    </div>
  )
}

export function ExportButton({ url }: { url: string }) {
  const { t } = useTranslation()
  const onClick = async () => {
    const sep = url.includes('?') ? '&' : '?'
    try {
      await api.download(`${url}${sep}lang=${currentLanguage()}`)
    } catch (e) {
      const code = e instanceof ApiError ? e.code : 'server_error'
      window.alert(t(`errors.${code}`, { defaultValue: t('errors.generic') }))
    }
  }
  return (
    <button type="button" className="btn" onClick={onClick}>
      {t('common.exportExcel')}
    </button>
  )
}

export function Select<T extends { id: string }>({
  id, value, onChange, items, label, placeholder, describedBy, required,
}: {
  id: string
  value: string
  onChange: (value: string) => void
  items: T[] | undefined
  label: (item: T) => string
  placeholder?: string
  describedBy?: string
  required?: boolean
}) {
  return (
    <select id={id} value={value} onChange={(e) => onChange(e.target.value)} aria-describedby={describedBy} required={required}>
      <option value="">{placeholder ?? '—'}</option>
      {items?.map((item) => (
        <option key={item.id} value={item.id}>{label(item)}</option>
      ))}
    </select>
  )
}
