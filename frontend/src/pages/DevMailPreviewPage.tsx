import { useTranslation } from 'react-i18next'
import { Link, useParams } from 'react-router-dom'
import { api } from '../api/client'
import { ErrorBanner, Loading, PageHeader } from '../components/ui'
import { useLoad } from '../lib/useLoad'

interface CapturedEmail {
  id: string
  capturedAt: string
  to: string
  subject: string
  textBody: string
  purpose: string | null
}

/** Development-only UI for prepared (not delivered) mail. API returns 404 outside Development/Testing. */
export function DevMailPreviewPage() {
  const { t } = useTranslation()
  const { id } = useParams()
  const mail = useLoad(() => api.get<CapturedEmail>(`/api/dev/mail/${id}`), id ?? '')

  const linkMatch = mail.data?.textBody.match(/https?:\/\/\S*\/set-password\?[^\s]+/)
  const setupHref = linkMatch?.[0]?.replace(/[.)\]]+$/, '')

  return (
    <>
      <PageHeader title={t('devMail.title')} />
      <p className="muted">{t('devMail.disclaimer')}</p>
      <ErrorBanner error={mail.error} />
      {mail.loading && !mail.data ? <Loading /> : mail.data && (
        <article className="card" style={{ maxWidth: '40rem', whiteSpace: 'pre-wrap' }}>
          <p><strong>{t('devMail.to')}:</strong> {mail.data.to}</p>
          <p><strong>{t('devMail.subject')}:</strong> {mail.data.subject}</p>
          {mail.data.purpose && <p><strong>{t('devMail.purpose')}:</strong> {mail.data.purpose}</p>}
          <hr />
          <pre style={{ whiteSpace: 'pre-wrap', fontFamily: 'inherit' }}>{mail.data.textBody}</pre>
          {setupHref && (
            <p style={{ marginTop: '1rem' }}>
              <a className="btn btn-primary" href={setupHref}>{t('devMail.openSetupLink')}</a>
            </p>
          )}
        </article>
      )}
      <p style={{ marginTop: '1rem' }}>
        <Link to="/manager/students">{t('devMail.backStudents')}</Link>
      </p>
    </>
  )
}
