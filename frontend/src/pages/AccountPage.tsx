import { useState, type FormEvent } from 'react'
import { useTranslation } from 'react-i18next'
import { ApiError, api } from '../api/client'
import { useAuth } from '../auth/AuthContext'
import { ErrorBanner, Field, PageHeader } from '../components/ui'
import { useFieldError } from '../lib/useFieldError'
import { toApiError } from '../lib/useLoad'

export function AccountPage() {
  const { t } = useTranslation()
  const { me } = useAuth()
  const [currentPassword, setCurrent] = useState('')
  const [newPassword, setNew] = useState('')
  const [error, setError] = useState<ApiError | null>(null)
  const [done, setDone] = useState(false)
  const fieldError = useFieldError(error)

  const onSubmit = async (e: FormEvent) => {
    e.preventDefault()
    setError(null)
    setDone(false)
    try {
      await api.post('/api/auth/change-password', { currentPassword, newPassword })
      setCurrent('')
      setNew('')
      setDone(true)
    } catch (err) {
      setError(toApiError(err))
    }
  }

  return (
    <>
      <PageHeader title={t('account.title')} />
      <div className="card narrow">
        <p><strong>{me?.displayName}</strong></p>
        <p className="muted">{me?.email}{me?.groupName ? ` · ${me.groupName}` : ''}</p>
      </div>
      <form className="card narrow" onSubmit={onSubmit} noValidate>
        <h2>{t('account.changePassword')}</h2>
        {done && <div className="alert alert-success" role="status">{t('account.passwordChanged')}</div>}
        <ErrorBanner error={error} />
        <Field label={t('account.currentPassword')} error={fieldError('currentPassword')} required>
          {(id, d) => <input id={id} type="password" autoComplete="current-password" aria-describedby={d}
            value={currentPassword} onChange={(e) => setCurrent(e.target.value)} />}
        </Field>
        <Field label={t('account.newPassword')} hint={t('account.passwordHint')} error={fieldError('newPassword')} required>
          {(id, d) => <input id={id} type="password" autoComplete="new-password" aria-describedby={d}
            value={newPassword} onChange={(e) => setNew(e.target.value)} />}
        </Field>
        <div className="form-actions">
          <button type="submit" className="btn btn-primary" disabled={!currentPassword || !newPassword}>
            {t('common.save')}
          </button>
        </div>
      </form>
    </>
  )
}
