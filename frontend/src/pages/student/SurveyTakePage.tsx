import { useState, type FormEvent } from 'react'
import { useTranslation } from 'react-i18next'
import { Link, useParams } from 'react-router-dom'
import { ApiError, api } from '../../api/client'
import type { StudentSurveyDetail, SurveyQuestion } from '../../api/types'
import { ErrorBanner, Loading, PageHeader } from '../../components/ui'
import { formatInstant } from '../../lib/format'
import { validateAnswers, type AnswerValue } from '../../lib/surveyValidation'
import { toApiError, useLoad } from '../../lib/useLoad'

export function SurveyTakePage() {
  const { t } = useTranslation()
  const { id } = useParams()
  const survey = useLoad(() => api.get<StudentSurveyDetail>(`/api/student/surveys/${id}`), `survey-${id}`)
  const [answers, setAnswers] = useState<Record<string, AnswerValue>>({})
  const [errors, setErrors] = useState<Record<string, string>>({})
  const [error, setError] = useState<ApiError | null>(null)
  const [saving, setSaving] = useState(false)

  if (survey.error) return <ErrorBanner error={survey.error} />
  if (!survey.data) return <Loading />
  const s = survey.data

  const set = (qid: string, value: AnswerValue) => setAnswers({ ...answers, [qid]: value })

  const onSubmit = async (e: FormEvent) => {
    e.preventDefault()
    const local = validateAnswers(s.questions, answers)
    setErrors(local)
    if (Object.keys(local).length) return
    setSaving(true)
    setError(null)
    try {
      await api.post(`/api/student/surveys/${s.id}/responses`, {
        answers: s.questions
          .filter((q) => answers[q.id])
          .map((q) => ({ questionId: q.id, ...answers[q.id] })),
      })
      survey.reload()
    } catch (err) {
      const apiError = toApiError(err)
      setError(apiError)
      const fieldErrors: Record<string, string> = {}
      for (const [k, v] of Object.entries(apiError.fieldErrors)) fieldErrors[k] = v[0]
      setErrors(fieldErrors)
    } finally {
      setSaving(false)
    }
  }

  const mine = (q: SurveyQuestion) => s.myAnswers.find((a) => a.questionId === q.id)
  const myAnswerText = (q: SurveyQuestion) => {
    const a = mine(q)
    if (!a) return '—'
    if (q.type === 'Scale') return String(a.intValue)
    if (q.type === 'SingleChoice') return q.options.find((o) => o.id === a.optionId)?.text ?? '—'
    return a.textValue ?? '—'
  }

  return (
    <>
      <PageHeader title={s.title} actions={<Link to="/student/surveys" className="btn">{t('common.back')}</Link>} />
      <p className="muted">{t(`surveyType.${s.type}`)}{s.teacherName ? ` · ${s.teacherName}` : ''}{s.disciplineName ? ` · ${s.disciplineName}` : ''}</p>
      {s.description && <p className="pre">{s.description}</p>}

      {s.submitted ? (
        <>
          <div className="alert alert-success" role="status">
            {t('studentSurveys.thanks', { date: formatInstant(s.submittedAt) })}
          </div>
          <h2>{t('studentSurveys.yourAnswers')}</h2>
          <dl className="answers">
            {s.questions.map((q, i) => (
              <div key={q.id}>
                <dt>{i + 1}. {q.text}</dt>
                <dd>{myAnswerText(q)}</dd>
              </div>
            ))}
          </dl>
        </>
      ) : !s.canRespond ? (
        <div className="alert alert-info">{s.status === 'Closed' ? t('studentSurveys.closedNote') : t('errors.survey_not_accepting')}</div>
      ) : (
        <form onSubmit={onSubmit} noValidate>
          <div className="alert alert-info">{t('studentSurveys.notAnonymousNote')}</div>
          <ErrorBanner error={error} />
          {s.questions.map((q, i) => {
            const err = errors[q.id]
            const errId = `err-${q.id}`
            return (
              <fieldset key={q.id} className={`card question${err ? ' field-invalid' : ''}`} aria-describedby={err ? errId : undefined}>
                <legend>
                  {i + 1}. {q.text}
                  {q.isRequired && <span className="req" aria-hidden="true"> *</span>}
                </legend>
                {q.type === 'Scale' && (
                  <>
                    <div className="scale" role="radiogroup" aria-label={q.text}>
                      {Array.from({ length: (q.scaleMax ?? 0) - (q.scaleMin ?? 0) + 1 }, (_, k) => (q.scaleMin ?? 0) + k).map((v) => (
                        <label key={v} className={answers[q.id]?.intValue === v ? 'scale-item selected' : 'scale-item'}>
                          <input type="radio" name={q.id} value={v} checked={answers[q.id]?.intValue === v}
                            onChange={() => set(q.id, { intValue: v })} />
                          <span>{v}</span>
                        </label>
                      ))}
                    </div>
                    <small className="hint block">{t('studentSurveys.scaleHint', { min: q.scaleMin, max: q.scaleMax })}</small>
                  </>
                )}
                {q.type === 'SingleChoice' && q.options.map((o) => (
                  <label key={o.id} className="radio">
                    <input type="radio" name={q.id} value={o.id} checked={answers[q.id]?.optionId === o.id}
                      onChange={() => set(q.id, { optionId: o.id })} />
                    {o.text}
                  </label>
                ))}
                {q.type === 'Text' && (
                  <textarea rows={4} maxLength={4000} aria-label={q.text} value={answers[q.id]?.textValue ?? ''}
                    onChange={(e) => set(q.id, { textValue: e.target.value })} />
                )}
                {err && <small id={errId} className="field-error block">{t(`validation.${err}`, { defaultValue: t('validation.invalid') })}</small>}
              </fieldset>
            )
          })}
          <div className="form-actions">
            <button type="submit" className="btn btn-primary" disabled={saving}>{t('studentSurveys.submit')}</button>
          </div>
        </form>
      )}
    </>
  )
}
