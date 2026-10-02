import { useState, type FormEvent } from 'react'
import { useTranslation } from 'react-i18next'
import { Navigate, useNavigate } from 'react-router-dom'
import { ApiError } from '../api/client'
import { useAuth } from '../auth/AuthContext'
import { LanguageSwitcher } from '../components/Layout'
import { ErrorBanner, Field } from '../components/ui'
import { toApiError } from '../lib/useLoad'

export function LoginPage() {
  const { t } = useTranslation()
  const { me, login } = useAuth()
  const navigate = useNavigate()
  const [email, setEmail] = useState('')
  const [password, setPassword] = useState('')
  const [error, setError] = useState<ApiError | null>(null)
  const [busy, setBusy] = useState(false)

  if (me) return <Navigate to={me.role === 'Manager' ? '/manager/groups' : '/student'} replace />

  const onSubmit = async (e: FormEvent) => {
    e.preventDefault()
    setBusy(true)
    setError(null)
    try {
      const user = await login(email, password)
      navigate(user.role === 'Manager' ? '/manager/groups' : '/student', { replace: true })
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
        <h1>{t('login.title')}</h1>
        <ErrorBanner error={error} />
        <Field label={t('login.email')} required>
          {(id) => (
            <input id={id} type="email" autoComplete="username" value={email} required
              onChange={(e) => setEmail(e.target.value)} />
          )}
        </Field>
        <Field label={t('login.password')} required>
          {(id) => (
            <input id={id} type="password" autoComplete="current-password" value={password} required
              onChange={(e) => setPassword(e.target.value)} />
          )}
        </Field>
        <button type="submit" className="btn btn-primary btn-block" disabled={busy || !email || !password}>
          {t('login.submit')}
        </button>
      </form>
    </div>
  )
}
