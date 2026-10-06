import { useEffect, useState, type FormEvent } from 'react'
import { useTranslation } from 'react-i18next'
import { Link, useNavigate, useParams } from 'react-router-dom'
import { ApiError, api } from '../../api/client'
import type { QuestionType, SurveyTemplateDetail, SurveyType } from '../../api/types'
import { ErrorBanner, Field, Loading, PageHeader } from '../../components/ui'
import { useFieldError } from '../../lib/useFieldError'
import { toApiError, useLoad } from '../../lib/useLoad'

const TYPES: SurveyType[] = ['TeachingEvaluation', 'ServiceSurvey']
const QUESTION_TYPES: QuestionType[] = ['Scale', 'SingleChoice', 'Text']

interface QuestionForm {
  key: string
  text: string
  type: QuestionType
  isRequired: boolean
  scaleMin: string
  scaleMax: string
  scaleMinLabel: string
  scaleMaxLabel: string
  options: string[]
}

let keySeq = 0
const newKey = () => `tq${++keySeq}`

function newQuestion(type: QuestionType = 'Scale'): QuestionForm {
  return {
    key: newKey(), text: '', type, isRequired: true, scaleMin: '1', scaleMax: '5',
    scaleMinLabel: '', scaleMaxLabel: '', options: ['', ''],
  }
}

export function SurveyTemplateEditPage() {
  const { t } = useTranslation()
  const { id } = useParams()
  const navigate = useNavigate()
  const isNew = !id
  const loaded = useLoad(
    () => (id ? api.get<SurveyTemplateDetail>(`/api/manager/survey-templates/${id}`) : Promise.resolve(undefined)),
    id ?? 'new-template',
  )
  const [type, setType] = useState<SurveyType>('TeachingEvaluation')
  const [title, setTitle] = useState('')
  const [description, setDescription] = useState('')
  const [questions, setQuestions] = useState<QuestionForm[]>([newQuestion()])
  const [ready, setReady] = useState(isNew)
  const [error, setError] = useState<ApiError | null>(null)
  const [saving, setSaving] = useState(false)
  const fieldError = useFieldError(error)

  useEffect(() => {
    if (!loaded.data) return
    setType(loaded.data.type)
    setTitle(loaded.data.title)
    setDescription(loaded.data.description ?? '')
    setQuestions(loaded.data.questions.map((q) => ({
      key: newKey(),
      text: q.text,
      type: q.type,
      isRequired: q.isRequired,
      scaleMin: q.scaleMin?.toString() ?? '1',
      scaleMax: q.scaleMax?.toString() ?? '5',
      scaleMinLabel: q.scaleMinLabel ?? '',
      scaleMaxLabel: q.scaleMaxLabel ?? '',
      options: q.options.length ? q.options.map((o) => o.text) : ['', ''],
    })))
    setReady(true)
  }, [loaded.data])

  if (!isNew && (!ready || loaded.loading)) return loaded.error ? <ErrorBanner error={loaded.error} /> : <Loading />

  const setQ = (index: number, patch: Partial<QuestionForm>) =>
    setQuestions(questions.map((q, i) => (i === index ? { ...q, ...patch } : q)))
  const moveQ = (index: number, delta: number) => {
    const qs = [...questions]
    const target = index + delta
    if (target < 0 || target >= qs.length) return
    ;[qs[index], qs[target]] = [qs[target], qs[index]]
    setQuestions(qs)
  }

  const body = () => ({
    type,
    title: title.trim(),
    description: description.trim() || null,
    questions: questions.map((q) => ({
      text: q.text.trim(),
      type: q.type,
      isRequired: q.isRequired,
      scaleMin: q.type === 'Scale' ? Number(q.scaleMin) : null,
      scaleMax: q.type === 'Scale' ? Number(q.scaleMax) : null,
      scaleMinLabel: q.type === 'Scale' ? q.scaleMinLabel.trim() || null : null,
      scaleMaxLabel: q.type === 'Scale' ? q.scaleMaxLabel.trim() || null : null,
      options: q.type === 'SingleChoice' ? q.options.map((o) => o.trim()).filter(Boolean) : null,
    })),
  })

  const onSubmit = async (e: FormEvent) => {
    e.preventDefault()
    setSaving(true)
    setError(null)
    try {
      const saved = isNew
        ? await api.post<SurveyTemplateDetail>('/api/manager/survey-templates', body())
        : await api.put<SurveyTemplateDetail>(`/api/manager/survey-templates/${id}`, body())
      navigate(`/manager/survey-templates/${saved.id}`, { replace: true })
      if (!isNew) loaded.reload()
    } catch (err) {
      setError(toApiError(err))
    } finally {
      setSaving(false)
    }
  }

  const qError = (i: number, field: string) => fieldError(`questions[${i}].${field}`)

  return (
    <>
      <PageHeader
        title={isNew ? t('surveyTemplates.new') : title || t('surveyTemplates.edit')}
        actions={
          <>
            <Link to="/manager/survey-templates" className="btn">{t('common.back')}</Link>
            {!isNew && (
              <Link to={`/manager/surveys/from-template?templateId=${id}`} className="btn btn-primary">
                {t('surveyTemplates.createSurvey')}
              </Link>
            )}
          </>
        }
      />
      <p className="muted">{t('surveyTemplates.structureOnlyNote')}</p>
      <ErrorBanner error={error} />
      <form onSubmit={onSubmit} noValidate>
        <fieldset className="card">
          <Field label={t('surveys.type')} required error={fieldError('type')}>
            {(fid) => (
              <select id={fid} value={type} onChange={(e) => setType(e.target.value as SurveyType)}>
                {TYPES.map((x) => <option key={x} value={x}>{t(`surveyType.${x}`)}</option>)}
              </select>
            )}
          </Field>
          <Field label={t('surveys.surveyTitle')} required error={fieldError('title')}>
            {(fid, d) => <input id={fid} aria-describedby={d} value={title} onChange={(e) => setTitle(e.target.value)} />}
          </Field>
          <Field label={t('common.description')} error={fieldError('description')}>
            {(fid, d) => (
              <textarea id={fid} rows={3} aria-describedby={d} value={description}
                onChange={(e) => setDescription(e.target.value)} />
            )}
          </Field>
        </fieldset>

        <h2>{t('surveys.questions')}</h2>
        <fieldset className="questions">
          {questions.map((q, i) => (
            <div key={q.key} className="card question">
              <div className="question-head">
                <strong>{i + 1}.</strong>
                <div className="question-tools">
                  <button type="button" className="btn btn-small" onClick={() => moveQ(i, -1)} disabled={i === 0}>{t('surveys.moveUp')}</button>
                  <button type="button" className="btn btn-small" onClick={() => moveQ(i, 1)} disabled={i === questions.length - 1}>{t('surveys.moveDown')}</button>
                  <button type="button" className="btn btn-small btn-danger" onClick={() => setQuestions(questions.filter((_, j) => j !== i))}
                    disabled={questions.length <= 1}>{t('surveys.removeQuestion')}</button>
                </div>
              </div>
              <Field label={t('surveys.questionText')} required error={qError(i, 'text')}>
                {(fid, d) => <input id={fid} aria-describedby={d} value={q.text} onChange={(e) => setQ(i, { text: e.target.value })} />}
              </Field>
              <div className="grid-2">
                <Field label={t('surveys.questionType')}>
                  {(fid) => (
                    <select id={fid} value={q.type} onChange={(e) => setQ(i, { type: e.target.value as QuestionType })}>
                      {QUESTION_TYPES.map((x) => <option key={x} value={x}>{t(`questionType.${x}`)}</option>)}
                    </select>
                  )}
                </Field>
                <label className="checkbox">
                  <input type="checkbox" checked={q.isRequired} onChange={(e) => setQ(i, { isRequired: e.target.checked })} />
                  {t('surveys.requiredQuestion')}
                </label>
              </div>
              {q.type === 'Scale' && (
                <div className="grid-2">
                  <Field label={t('surveys.scaleMin')} required error={qError(i, 'scaleMin')}>
                    {(fid, d) => <input id={fid} type="number" aria-describedby={d} value={q.scaleMin} onChange={(e) => setQ(i, { scaleMin: e.target.value })} />}
                  </Field>
                  <Field label={t('surveys.scaleMax')} required error={qError(i, 'scaleMax')}>
                    {(fid, d) => <input id={fid} type="number" aria-describedby={d} value={q.scaleMax} onChange={(e) => setQ(i, { scaleMax: e.target.value })} />}
                  </Field>
                  <Field label={t('surveys.scaleMinLabel')}>
                    {(fid) => <input id={fid} value={q.scaleMinLabel} placeholder={t('surveys.scaleLabelPlaceholder')}
                      onChange={(e) => setQ(i, { scaleMinLabel: e.target.value })} />}
                  </Field>
                  <Field label={t('surveys.scaleMaxLabel')}>
                    {(fid) => <input id={fid} value={q.scaleMaxLabel} onChange={(e) => setQ(i, { scaleMaxLabel: e.target.value })} />}
                  </Field>
                </div>
              )}
              {q.type === 'SingleChoice' && (
                <Field label={t('surveys.options')} required error={qError(i, 'options')}>
                  {() => (
                    <div className="stack">
                      {q.options.map((opt, oi) => (
                        <div key={oi} className="option-row">
                          <input value={opt} onChange={(e) => {
                            const options = [...q.options]
                            options[oi] = e.target.value
                            setQ(i, { options })
                          }} />
                          <button type="button" className="btn btn-small" disabled={q.options.length <= 2}
                            onClick={() => setQ(i, { options: q.options.filter((_, j) => j !== oi) })}>×</button>
                        </div>
                      ))}
                      <button type="button" className="btn btn-small" onClick={() => setQ(i, { options: [...q.options, ''] })}>
                        {t('surveys.addOption')}
                      </button>
                    </div>
                  )}
                </Field>
              )}
            </div>
          ))}
          <button type="button" className="btn" onClick={() => setQuestions([...questions, newQuestion()])}>{t('surveys.addQuestion')}</button>
        </fieldset>
        <div className="form-actions">
          <button type="submit" className="btn btn-primary" disabled={saving}>{t('common.save')}</button>
        </div>
      </form>
    </>
  )
}
