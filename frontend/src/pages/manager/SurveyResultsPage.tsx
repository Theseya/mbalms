import { useTranslation } from 'react-i18next'
import { Link, useParams } from 'react-router-dom'
import { api } from '../../api/client'
import type { SurveyDetail, SurveyQuestion, SurveyResponse } from '../../api/types'
import { Badge, Empty, ErrorBanner, ExportButton, Loading, PageHeader } from '../../components/ui'
import { formatInstant } from '../../lib/format'
import { surveyStatusTone as statusTone } from '../../lib/labels'
import { useLoad } from '../../lib/useLoad'

function answerText(q: SurveyQuestion, r: SurveyResponse): string {
  const a = r.answers.find((x) => x.questionId === q.id)
  if (!a) return '—'
  if (q.type === 'Scale') return a.intValue?.toString() ?? '—'
  if (q.type === 'SingleChoice') return a.optionText ?? '—'
  return a.textValue ?? '—'
}

function Summary({ q, responses }: { q: SurveyQuestion; responses: SurveyResponse[] }) {
  const { t } = useTranslation()
  const answers = responses.map((r) => r.answers.find((a) => a.questionId === q.id)).filter((a) => a !== undefined)
  if (q.type === 'Scale') {
    const values = answers.map((a) => a.intValue).filter((v): v is number => v !== null)
    const avg = values.length ? values.reduce((s, v) => s + v, 0) / values.length : null
    return (
      <p>
        {t('surveys.average')}: <strong>{avg === null ? '—' : avg.toFixed(2)}</strong>{' '}
        <span className="muted">({q.scaleMin}–{q.scaleMax}, n = {values.length})</span>
      </p>
    )
  }
  if (q.type === 'SingleChoice') {
    const total = answers.length || 1
    return (
      <ul className="bars">
        {q.options.map((o) => {
          const count = answers.filter((a) => a.optionId === o.id).length
          return (
            <li key={o.id}>
              <span>{o.text}</span>
              <span className="bar" style={{ width: `${(count / total) * 100}%` }} aria-hidden="true" />
              <span className="muted">{count}</span>
            </li>
          )
        })}
      </ul>
    )
  }
  return <p className="muted">n = {answers.filter((a) => a.textValue).length}</p>
}

export function SurveyResultsPage() {
  const { t } = useTranslation()
  const { id } = useParams()
  const survey = useLoad(() => api.get<SurveyDetail>(`/api/manager/surveys/${id}`), `s-${id}`)
  const responses = useLoad(() => api.get<SurveyResponse[]>(`/api/manager/surveys/${id}/responses`), `r-${id}`)

  if (survey.error || responses.error) return <ErrorBanner error={survey.error ?? responses.error} />
  if (!survey.data || !responses.data) return <Loading />
  const s = survey.data
  const rs = responses.data

  return (
    <>
      <PageHeader
        title={`${t('surveys.results')}: ${s.title}`}
        actions={
          <>
            <Link to={`/manager/surveys/${s.id}`} className="btn">{t('common.back')}</Link>
            <ExportButton url={`/api/manager/exports/surveys/${s.id}`} />
          </>
        }
      />
      <p>
        <Badge tone={statusTone(s.status)}>{t(`surveyStatus.${s.status}`)}</Badge>{' '}
        {t(`surveyType.${s.type}`)} · {s.groupName}{s.teacherName ? ` · ${s.teacherName}` : ''}
      </p>
      <p className="muted">{t('surveys.notAnonymous')}</p>

      {rs.length === 0 ? <Empty>{t('surveys.noResponses')}</Empty> : (
        <>
          <h2>{t('surveys.summary')}</h2>
          <div className="summary-grid">
            {s.questions.map((q, i) => (
              <div key={q.id} className="card">
                <p><strong>{i + 1}. {q.text}</strong></p>
                <Summary q={q} responses={rs} />
              </div>
            ))}
          </div>
          <h2>{t('surveys.results')} ({rs.length})</h2>
          <div className="table-wrap">
            <table>
              <thead>
                <tr>
                  <th>{t('surveys.respondent')}</th>
                  <th>{t('surveys.submittedAt')}</th>
                  {s.questions.map((q, i) => <th key={q.id} title={q.text}>{i + 1}. {q.text}</th>)}
                </tr>
              </thead>
              <tbody>
                {rs.map((r) => (
                  <tr key={r.id}>
                    <td data-label={t('surveys.respondent')}>{r.studentName}</td>
                    <td data-label={t('surveys.submittedAt')}>{formatInstant(r.submittedAt)}</td>
                    {s.questions.map((q, i) => (
                      <td key={q.id} data-label={`${i + 1}. ${q.text}`} className="wrap">{answerText(q, r)}</td>
                    ))}
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        </>
      )}
    </>
  )
}
