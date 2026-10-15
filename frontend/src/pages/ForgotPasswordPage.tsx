import { useState, type FormEvent } from 'react'
import { useTranslation } from 'react-i18next'
import { Link } from 'react-router-dom'
import { ApiError, api } from '../api/client'
import { LanguageSwitcher } from '../components/Layout'
import { ErrorBanner, Field } from '../components/ui'
import { toApiError } from '../lib/useLoad'

export function ForgotPasswordPage() {
  const { t } = useTranslation()
  const [email, setEmail] = useState('')
  const [error, setError] = useState<ApiError | null>(null)
  const [busy, setBusy] = useState(false)
  const [sent, setSent] = useState(false)

  const onSubmit = async (e: FormEvent) => {
    e.preventDefault()
    setBusy(true)
    setError(null)
    try {
      await api.post('/api/auth/forgot-password', { email: email.trim() })
      setSent(true)
    } catch (err) {
      setError(toApiError(err))
    } finally {
      setBusy(false)
    }
  }

  return (
    <div className="login-page">
      <div className="login-lang"><LanguageSwitcher /></div>
      <form className="card login-card" onSubmit={onSubmit} noValidate>
        <p className="brand">{t('app.title')}</p>
        <h1>{t('forgotPassword.title')}</h1>
        <ErrorBanner error={error} />
        {sent ? (
          <p>{t('forgotPassword.sent')}</p>
        ) : (
          <>
            <p className="muted">{t('forgotPassword.hint')}</p>
            <Field label={t('login.email')} required>
              {(id) => (
                <input id={id} type="email" autoComplete="username" value={email} required
                  onChange={(e) => setEmail(e.target.value)} />
              )}
            </Field>
            <button type="submit" className="btn btn-primary btn-block" disabled={busy || !email}>
              {t('forgotPassword.submit')}
            </button>
          </>
        )}
        <p className="muted" style={{ marginTop: '1rem' }}>
          <Link to="/login">{t('forgotPassword.backLogin')}</Link>
        </p>
      </form>
    </div>
  )
}
