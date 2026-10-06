import { useEffect, useState, type FormEvent } from 'react'
import { useTranslation } from 'react-i18next'
import { Link, useNavigate, useSearchParams } from 'react-router-dom'
import { ApiError, api } from '../../api/client'
import type { Discipline, Group, SurveyDetail, SurveyTemplateDetail, SurveyTemplateListItem, Teacher } from '../../api/types'
import { ErrorBanner, Field, Loading, PageHeader, Select } from '../../components/ui'
import { getAppTimeZone } from '../../lib/format'
import { useFieldError } from '../../lib/useFieldError'
import { toApiError, useLoad } from '../../lib/useLoad'

export function SurveyFromTemplatePage() {
  const { t } = useTranslation()
  const navigate = useNavigate()
  const [params] = useSearchParams()
  const initialId = params.get('templateId') ?? ''
  const templates = useLoad(() => api.get<SurveyTemplateListItem[]>('/api/manager/survey-templates'), 'templates')
  const groups = useLoad(() => api.get<Group[]>('/api/manager/groups'), 'groups')
  const teachers = useLoad(() => api.get<Teacher[]>('/api/manager/teachers'), 'teachers')
  const disciplines = useLoad(() => api.get<Discipline[]>('/api/manager/disciplines'), 'disciplines')
  const [templateId, setTemplateId] = useState(initialId)
  const detail = useLoad(
    () => (templateId ? api.get<SurveyTemplateDetail>(`/api/manager/survey-templates/${templateId}`) : Promise.resolve(undefined)),
    templateId || 'none',
  )
  const [groupId, setGroupId] = useState('')
  const [teacherId, setTeacherId] = useState('')
  const [disciplineId, setDisciplineId] = useState('')
  const [opensAt, setOpensAt] = useState('')
  const [closesAt, setClosesAt] = useState('')
  const [title, setTitle] = useState('')
  const [error, setError] = useState<ApiError | null>(null)
  const [saving, setSaving] = useState(false)
  const fieldError = useFieldError(error)

  useEffect(() => {
    if (detail.data) setTitle(detail.data.title)
  }, [detail.data])

  const teaching = detail.data?.type === 'TeachingEvaluation'

  const onSubmit = async (e: FormEvent) => {
    e.preventDefault()
    if (!templateId) return
    setSaving(true)
    setError(null)
    try {
      const created = await api.post<SurveyDetail>('/api/manager/surveys/from-template', {
        templateId,
        groupId: groupId || null,
        teacherId: teaching ? teacherId || null : null,
        disciplineId: teaching ? disciplineId || null : null,
        opensAt: opensAt || null,
        closesAt: closesAt || null,
        title: title.trim() || null,
      })
      navigate(`/manager/surveys/${created.id}`, { replace: true })
    } catch (err) {
      setError(toApiError(err))
    } finally {
      setSaving(false)
    }
  }

  return (
    <>
      <PageHeader
        title={t('surveyTemplates.createSurvey')}
        actions={<Link to="/manager/surveys" className="btn">{t('common.back')}</Link>}
      />
      <p className="muted">{t('surveyTemplates.fromTemplateHint')}</p>
      <ErrorBanner error={templates.error ?? detail.error ?? error} />
      {templates.loading && !templates.data ? <Loading /> : (
        <form onSubmit={onSubmit} noValidate>
          <fieldset className="card">
            <Field label={t('surveyTemplates.template')} required error={fieldError('templateId')}>
              {(fid) => (
                <select id={fid} value={templateId} onChange={(e) => setTemplateId(e.target.value)} required>
                  <option value="">{t('common.selectPlaceholder')}</option>
                  {(templates.data ?? []).map((tpl) => (
                    <option key={tpl.id} value={tpl.id}>{tpl.title} ({t(`surveyType.${tpl.type}`)})</option>
                  ))}
                </select>
              )}
            </Field>
            {detail.loading && templateId ? <Loading /> : detail.data && (
              <>
                <p className="muted">{t('surveyTemplates.questionCount', { count: detail.data.questions.length })}</p>
                <Field label={t('surveys.surveyTitle')} error={fieldError('title')}>
                  {(fid, d) => <input id={fid} aria-describedby={d} value={title} onChange={(e) => setTitle(e.target.value)} />}
                </Field>
                <Field label={t('common.group')} required error={fieldError('groupId')}>
                  {(fid, d) => (
                    <Select id={fid} describedBy={d} value={groupId} onChange={setGroupId}
                      items={groups.data} label={(g) => g.name} placeholder={t('common.selectPlaceholder')} required />
                  )}
                </Field>
                {teaching && (
                  <>
                    <small className="hint block">{t('surveys.teachingOnlyNote')}</small>
                    <div className="grid-2">
                      <Field label={t('surveys.teacher')} required error={fieldError('teacherId')}>
                        {(fid, d) => (
                          <Select id={fid} describedBy={d} value={teacherId} onChange={setTeacherId}
                            items={teachers.data} label={(x) => x.fullName} placeholder={t('common.selectPlaceholder')} required />
                        )}
                      </Field>
                      <Field label={t('common.discipline')} required error={fieldError('disciplineId')}>
                        {(fid, d) => (
                          <Select id={fid} describedBy={d} value={disciplineId} onChange={setDisciplineId}
                            items={disciplines.data} label={(x) => x.name} placeholder={t('common.selectPlaceholder')} required />
                        )}
                      </Field>
                    </div>
                  </>
                )}
                <div className="grid-2">
                  <Field label={t('surveys.opensAt')} error={fieldError('opensAt')}>
                    {(fid, d) => <input id={fid} type="datetime-local" aria-describedby={d} value={opensAt}
                      onChange={(e) => setOpensAt(e.target.value)} />}
                  </Field>
                  <Field label={t('surveys.closesAt')} error={fieldError('closesAt')}>
                    {(fid, d) => <input id={fid} type="datetime-local" aria-describedby={d} value={closesAt}
                      onChange={(e) => setClosesAt(e.target.value)} />}
                  </Field>
                </div>
                <small className="hint">{t('schedule.timeZoneNote', { tz: getAppTimeZone() })}</small>
              </>
            )}
          </fieldset>
          <div className="form-actions">
            <button type="submit" className="btn btn-primary" disabled={saving || !templateId || !detail.data}>
              {t('surveyTemplates.createDraft')}
            </button>
          </div>
        </form>
      )}
    </>
  )
}
