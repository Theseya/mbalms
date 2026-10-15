import { useState, type FormEvent } from 'react'
import { useTranslation } from 'react-i18next'
import { Link, useNavigate, useSearchParams } from 'react-router-dom'
import { ApiError, api } from '../api/client'
import { LanguageSwitcher } from '../components/Layout'
import { ErrorBanner, Field } from '../components/ui'
import { toApiError } from '../lib/useLoad'

export function SetPasswordPage() {
  const { t } = useTranslation()
  const navigate = useNavigate()
  const [params] = useSearchParams()
  const emailFromLink = params.get('email') ?? ''
  const token = params.get('token') ?? ''
  const [email, setEmail] = useState(emailFromLink)
  const [password, setPassword] = useState('')
  const [confirm, setConfirm] = useState('')
  const [error, setError] = useState<ApiError | null>(null)
  const [busy, setBusy] = useState(false)
  const [done, setDone] = useState(false)

  const linkInvalid = !emailFromLink || !token

  const onSubmit = async (e: FormEvent) => {
    e.preventDefault()
    if (password !== confirm) {
      setError(new ApiError(400, 'validation_failed', { confirm: ['invalid'] }))
      return
    }
    setBusy(true)
    setError(null)
    try {
      await api.post('/api/auth/set-password', { email: email.trim(), token, newPassword: password })
      setDone(true)
    } catch (err) {
      setError(toApiError(err))
    } finally {
      setBusy(false)
    }
  }

  return (
    <div className="login-page">
      <div className="login-lang"><LanguageSwitcher /></div>
      <div className="card login-card">
        <p className="brand">{t('app.title')}</p>
        <h1>{t('setPassword.title')}</h1>
        {linkInvalid ? (
          <ErrorBanner error={new ApiError(400, 'invalid_password_token')} />
        ) : done ? (
          <>
            <p>{t('setPassword.success')}</p>
            <button type="button" className="btn btn-primary btn-block" onClick={() => navigate('/login', { replace: true })}>
              {t('setPassword.goLogin')}
            </button>
          </>
        ) : (
          <form onSubmit={onSubmit} noValidate>
            <ErrorBanner error={error} />
            <Field label={t('login.email')} required>
              {(id) => (
                <input id={id} type="email" autoComplete="username" value={email} required readOnly
                  onChange={(e) => setEmail(e.target.value)} />
              )}
            </Field>
            <Field label={t('setPassword.newPassword')} required hint={t('account.passwordHint')}>
              {(id) => (
                <input id={id} type="password" autoComplete="new-password" value={password} required
                  onChange={(e) => setPassword(e.target.value)} />
              )}
            </Field>
            <Field label={t('setPassword.confirmPassword')} required>
              {(id) => (
                <input id={id} type="password" autoComplete="new-password" value={confirm} required
                  onChange={(e) => setConfirm(e.target.value)} />
              )}
            </Field>
            <button type="submit" className="btn btn-primary btn-block" disabled={busy || !password || !confirm}>
              {t('setPassword.submit')}
            </button>
          </form>
        )}
        <p className="muted" style={{ marginTop: '1rem' }}>
          <Link to="/login">{t('setPassword.backLogin')}</Link>
        </p>
      </div>
    </div>
  )
}
