import { useEffect, useState, type FormEvent } from 'react'
import { useTranslation } from 'react-i18next'
import { Link, useNavigate, useParams } from 'react-router-dom'
import { ApiError, api } from '../../api/client'
import type { Discipline, Group, QuestionType, SurveyDetail, SurveyType, Teacher } from '../../api/types'
import { Badge, ErrorBanner, Field, Loading, PageHeader, Select } from '../../components/ui'
import { formatLocalDateTime, getAppTimeZone, toInputDateTime } from '../../lib/format'
import { surveyStatusTone as statusTone } from '../../lib/labels'
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
  options: string[]
}

interface SurveyForm {
  type: SurveyType
  title: string
  description: string
  groupId: string
  teacherId: string
  disciplineId: string
  opensAt: string
  closesAt: string
  questions: QuestionForm[]
}

let keySeq = 0
const newKey = () => `q${++keySeq}`

function newQuestion(type: QuestionType = 'Scale'): QuestionForm {
  return { key: newKey(), text: '', type, isRequired: true, scaleMin: '1', scaleMax: '5', options: ['', ''] }
}

function fromDetail(s: SurveyDetail): SurveyForm {
  return {
    type: s.type,
    title: s.title,
    description: s.description ?? '',
    groupId: s.groupId,
    teacherId: s.teacherId ?? '',
    disciplineId: s.disciplineId ?? '',
    opensAt: toInputDateTime(s.opensAtLocal),
    closesAt: toInputDateTime(s.closesAtLocal),
    questions: s.questions.map((q) => ({
      key: newKey(),
      text: q.text,
      type: q.type,
      isRequired: q.isRequired,
      scaleMin: q.scaleMin?.toString() ?? '1',
      scaleMax: q.scaleMax?.toString() ?? '5',
      options: q.options.length ? q.options.map((o) => o.text) : ['', ''],
    })),
  }
}

const emptyForm = (): SurveyForm => ({
  type: 'TeachingEvaluation', title: '', description: '', groupId: '', teacherId: '', disciplineId: '',
  opensAt: '', closesAt: '', questions: [newQuestion()],
})

export function SurveyEditPage() {
  const { t } = useTranslation()
  const { id } = useParams()
  const navigate = useNavigate()
  const isNew = !id
  const groups = useLoad(() => api.get<Group[]>('/api/manager/groups'), 'groups')
  const teachers = useLoad(() => api.get<Teacher[]>('/api/manager/teachers'), 'teachers')
  const disciplines = useLoad(() => api.get<Discipline[]>('/api/manager/disciplines'), 'disciplines')
  const survey = useLoad(() => (id ? api.get<SurveyDetail>(`/api/manager/surveys/${id}`) : Promise.resolve(undefined)), id ?? 'new')
  const [form, setForm] = useState<SurveyForm | null>(isNew ? emptyForm() : null)
  const [error, setError] = useState<ApiError | null>(null)
  const [saving, setSaving] = useState(false)
  const fieldError = useFieldError(error)

  useEffect(() => {
    if (survey.data) setForm(fromDetail(survey.data))
  }, [survey.data])

  if (!isNew && (survey.loading || !form)) return survey.error ? <ErrorBanner error={survey.error} /> : <Loading />

  const readOnly = !isNew && survey.data?.status !== 'Draft'
  const f = form!

  const setQ = (index: number, patch: Partial<QuestionForm>) =>
    setForm({ ...f, questions: f.questions.map((q, i) => (i === index ? { ...q, ...patch } : q)) })
  const moveQ = (index: number, delta: number) => {
    const qs = [...f.questions]
    const target = index + delta
    if (target < 0 || target >= qs.length) return
    ;[qs[index], qs[target]] = [qs[target], qs[index]]
    setForm({ ...f, questions: qs })
  }

  const body = () => ({
    type: f.type,
    title: f.title.trim(),
    description: f.description.trim() || null,
    groupId: f.groupId || null,
    teacherId: f.teacherId || null,
    disciplineId: f.disciplineId || null,
    opensAt: f.opensAt || null,
    closesAt: f.closesAt || null,
    questions: f.questions.map((q) => ({
      text: q.text.trim(),
      type: q.type,
      isRequired: q.isRequired,
      scaleMin: q.type === 'Scale' ? Number(q.scaleMin) : null,
      scaleMax: q.type === 'Scale' ? Number(q.scaleMax) : null,
      options: q.type === 'SingleChoice' ? q.options.map((o) => o.trim()).filter(Boolean) : null,
    })),
  })

  const onSubmit = async (e: FormEvent) => {
    e.preventDefault()
    setSaving(true)
    setError(null)
    try {
      const saved = isNew
        ? await api.post<SurveyDetail>('/api/manager/surveys', body())
        : await api.put<SurveyDetail>(`/api/manager/surveys/${id}`, body())
      navigate(`/manager/surveys/${saved.id}`, { replace: true })
      if (!isNew) survey.reload()
    } catch (err) {
      setError(toApiError(err))
    } finally {
      setSaving(false)
    }
  }

  const changeStatus = async (action: 'open' | 'close') => {
    if (!window.confirm(t(action === 'open' ? 'surveys.confirmOpen' : 'surveys.confirmClose'))) return
    setError(null)
    try {
      await api.post(`/api/manager/surveys/${id}/${action}`)
      survey.reload()
    } catch (err) {
      setError(toApiError(err))
    }
  }

  const qError = (i: number, field: string) => fieldError(`questions[${i}].${field}`)
  const s = survey.data

  return (
    <>
      <PageHeader
        title={isNew ? t('surveys.new') : f.title}
        actions={
          <>
            <Link to="/manager/surveys" className="btn">{t('common.back')}</Link>
            {s && s.status !== 'Open' && s.groupStatus === 'Active' && s.questions.length > 0 && (
              <button type="button" className="btn btn-primary" onClick={() => changeStatus('open')}>
                {s.status === 'Draft' ? t('surveys.open') : t('surveys.reopen')}
              </button>
            )}
            {s?.status === 'Open' && (
              <button type="button" className="btn" onClick={() => changeStatus('close')}>{t('surveys.close')}</button>
            )}
            {s && s.status !== 'Draft' && (
              <Link to={`/manager/surveys/${s.id}/results`} className="btn">{t('surveys.results')} ({s.responseCount})</Link>
            )}
          </>
        }
      />
      {s && (
        <p>
          <Badge tone={statusTone(s.status)}>{t(`surveyStatus.${s.status}`)}</Badge>{' '}
          <span className="muted">{t('surveys.notAnonymous')}</span>
        </p>
      )}
      {readOnly && <div className="alert alert-info">{t('surveys.notEditableNote')}</div>}
      <ErrorBanner error={error} />

      <form onSubmit={onSubmit} noValidate>
        <fieldset disabled={readOnly} className="card">
          <div className="grid-2">
            <Field label={t('surveys.type')} required error={fieldError('type')}>
              {(fid) => (
                <select id={fid} value={f.type} onChange={(e) => setForm({ ...f, type: e.target.value as SurveyType })}>
                  {TYPES.map((x) => <option key={x} value={x}>{t(`surveyType.${x}`)}</option>)}
                </select>
              )}
            </Field>
            <Field label={t('common.group')} required error={fieldError('groupId')}>
              {(fid, d) => (
                <Select<{ id: string; name: string }> id={fid} describedBy={d} value={f.groupId}
                  onChange={(v) => setForm({ ...f, groupId: v })}
                  items={readOnly && s ? [{ id: s.groupId, name: s.groupName }] : groups.data}
                  label={(g) => g.name} placeholder={t('common.selectPlaceholder')} required />
              )}
            </Field>
          </div>
          <Field label={t('surveys.surveyTitle')} required error={fieldError('title')}>
            {(fid, d) => <input id={fid} aria-describedby={d} value={f.title} onChange={(e) => setForm({ ...f, title: e.target.value })} />}
          </Field>
          <Field label={t('common.description')} error={fieldError('description')}>
            {(fid, d) => <textarea id={fid} rows={3} aria-describedby={d} value={f.description}
              onChange={(e) => setForm({ ...f, description: e.target.value })} />}
          </Field>
          <div className="grid-2">
            <Field label={t('surveys.teacherOptional')} error={fieldError('teacherId')}>
              {(fid) => <Select id={fid} value={f.teacherId} onChange={(v) => setForm({ ...f, teacherId: v })}
                items={teachers.data} label={(x) => x.fullName} />}
            </Field>
            <Field label={t('common.discipline')} error={fieldError('disciplineId')}>
              {(fid) => <Select id={fid} value={f.disciplineId} onChange={(v) => setForm({ ...f, disciplineId: v })}
                items={disciplines.data} label={(x) => x.name} />}
            </Field>
            <Field label={t('surveys.opensAt')} error={fieldError('opensAt')}>
              {(fid, d) => <input id={fid} type="datetime-local" aria-describedby={d} value={f.opensAt}
                onChange={(e) => setForm({ ...f, opensAt: e.target.value })} />}
            </Field>
            <Field label={t('surveys.closesAt')} error={fieldError('closesAt')}>
              {(fid, d) => <input id={fid} type="datetime-local" aria-describedby={d} value={f.closesAt}
                onChange={(e) => setForm({ ...f, closesAt: e.target.value })} />}
            </Field>
          </div>
          <small className="hint">{t('schedule.timeZoneNote', { tz: getAppTimeZone() })}</small>
          {readOnly && s && (
            <p className="muted">{t('surveys.opensAt')}: {formatLocalDateTime(s.opensAtLocal)} · {t('surveys.closesAt')}: {formatLocalDateTime(s.closesAtLocal)}</p>
          )}
        </fieldset>

        <h2>{t('surveys.questions')}</h2>
        <fieldset disabled={readOnly} className="questions">
          {f.questions.map((q, i) => (
            <div key={q.key} className="card question">
              <div className="question-head">
                <strong>{i + 1}.</strong>
                {!readOnly && (
                  <div className="question-tools">
                    <button type="button" className="btn btn-small" onClick={() => moveQ(i, -1)} disabled={i === 0}>{t('surveys.moveUp')}</button>
                    <button type="button" className="btn btn-small" onClick={() => moveQ(i, 1)} disabled={i === f.questions.length - 1}>{t('surveys.moveDown')}</button>
                    <button type="button" className="btn btn-small btn-danger"
                      onClick={() => setForm({ ...f, questions: f.questions.filter((_, j) => j !== i) })}>
                      {t('surveys.removeQuestion')}
                    </button>
                  </div>
                )}
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
                <label className="checkbox align-end">
                  <input type="checkbox" checked={q.isRequired} onChange={(e) => setQ(i, { isRequired: e.target.checked })} />
                  {t('surveys.requiredQuestion')}
                </label>
              </div>
              {q.type === 'Scale' && (
                <div className="grid-2">
                  <Field label={t('surveys.scaleMin')} error={qError(i, 'scaleMin')}>
                    {(fid, d) => <input id={fid} type="number" step={1} aria-describedby={d} value={q.scaleMin}
                      onChange={(e) => setQ(i, { scaleMin: e.target.value })} />}
                  </Field>
                  <Field label={t('surveys.scaleMax')} error={qError(i, 'scaleMax')}>
                    {(fid, d) => <input id={fid} type="number" step={1} aria-describedby={d} value={q.scaleMax}
                      onChange={(e) => setQ(i, { scaleMax: e.target.value })} />}
                  </Field>
                </div>
              )}
              {q.type === 'SingleChoice' && (
                <div className={`field${qError(i, 'options') ? ' field-invalid' : ''}`}>
                  <span className="label">{t('surveys.options')}</span>
                  {q.options.map((o, j) => (
                    <div key={j} className="option-row">
                      <input aria-label={`${t('surveys.options')} ${j + 1}`} value={o}
                        onChange={(e) => setQ(i, { options: q.options.map((x, k) => (k === j ? e.target.value : x)) })} />
                      {!readOnly && q.options.length > 2 && (
                        <button type="button" className="btn-icon" aria-label={t('common.delete')}
                          onClick={() => setQ(i, { options: q.options.filter((_, k) => k !== j) })}>×</button>
                      )}
                    </div>
                  ))}
                  {!readOnly && (
                    <button type="button" className="btn btn-small" onClick={() => setQ(i, { options: [...q.options, ''] })}>
                      {t('surveys.addOption')}
                    </button>
                  )}
                  {qError(i, 'options') && <small className="field-error">{qError(i, 'options')}</small>}
                </div>
              )}
            </div>
          ))}
        </fieldset>
        {!readOnly && (
          <div className="form-actions spread">
            <button type="button" className="btn" onClick={() => setForm({ ...f, questions: [...f.questions, newQuestion()] })}>
              {t('surveys.addQuestion')}
            </button>
            <button type="submit" className="btn btn-primary" disabled={saving}>{t('common.save')}</button>
          </div>
        )}
      </form>
    </>
  )
}
