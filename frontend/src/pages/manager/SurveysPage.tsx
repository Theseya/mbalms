import { useState } from 'react'
import { useTranslation } from 'react-i18next'
import { Link } from 'react-router-dom'
import { ApiError, api, query } from '../../api/client'
import type { Group, SurveyListItem, SurveyStatus, SurveyType } from '../../api/types'
import { Badge, Empty, ErrorBanner, ExportButton, Field, Loading, PageHeader, Select } from '../../components/ui'
import { formatLocalDateTime } from '../../lib/format'
import { groupLabel, surveyStatusTone as statusTone } from '../../lib/labels'
import { toApiError, useLoad } from '../../lib/useLoad'

const TYPES: SurveyType[] = ['TeachingEvaluation', 'ServiceSurvey']
const STATUSES: SurveyStatus[] = ['Draft', 'Open', 'Closed']

export function SurveysPage() {
  const { t } = useTranslation()
  const [groupId, setGroupId] = useState('')
  const [type, setType] = useState('')
  const [status, setStatus] = useState('')
  const groups = useLoad(() => api.get<Group[]>('/api/manager/groups?status=All'), 'groups')
  const q = query({ groupId, type, status })
  const list = useLoad(() => api.get<SurveyListItem[]>(`/api/manager/surveys${q}`), q)
  const [actionError, setActionError] = useState<ApiError | null>(null)

  const act = async (action: () => Promise<unknown>, confirmText: string) => {
    if (!window.confirm(confirmText)) return
    setActionError(null)
    try {
      await action()
      list.reload()
    } catch (err) {
      setActionError(toApiError(err))
    }
  }

  return (
    <>
      <PageHeader
        title={t('surveys.title')}
        actions={<Link to="/manager/surveys/new" className="btn btn-primary">{t('surveys.new')}</Link>}
      />
      <div className="filters">
        <Field label={t('common.filterGroup')}>
          {(id) => <Select id={id} value={groupId} onChange={setGroupId} items={groups.data} label={groupLabel(t)} placeholder={t('common.all')} />}
        </Field>
        <Field label={t('surveys.type')}>
          {(id) => (
            <select id={id} value={type} onChange={(e) => setType(e.target.value)}>
              <option value="">{t('common.all')}</option>
              {TYPES.map((x) => <option key={x} value={x}>{t(`surveyType.${x}`)}</option>)}
            </select>
          )}
        </Field>
        <Field label={t('common.status')}>
          {(id) => (
            <select id={id} value={status} onChange={(e) => setStatus(e.target.value)}>
              <option value="">{t('common.all')}</option>
              {STATUSES.map((x) => <option key={x} value={x}>{t(`surveyStatus.${x}`)}</option>)}
            </select>
          )}
        </Field>
      </div>
      <ErrorBanner error={list.error ?? actionError} />
      {list.loading && !list.data ? <Loading /> : !list.data?.length ? <Empty /> : (
        <div className="table-wrap">
          <table>
            <thead>
              <tr>
                <th>{t('surveys.surveyTitle')}</th>
                <th>{t('surveys.type')}</th>
                <th>{t('common.group')}</th>
                <th>{t('surveys.closesAt')}</th>
                <th>{t('surveys.responses')}</th>
                <th>{t('common.status')}</th>
                <th className="actions-col">{t('common.actions')}</th>
              </tr>
            </thead>
            <tbody>
              {list.data.map((s) => (
                <tr key={s.id}>
                  <td data-label={t('surveys.surveyTitle')}>
                    <Link to={`/manager/surveys/${s.id}`}>{s.title}</Link>
                    {s.teacherName && <small className="muted block">{s.teacherName}</small>}
                  </td>
                  <td data-label={t('surveys.type')}>{t(`surveyType.${s.type}`)}</td>
                  <td data-label={t('common.group')}>{s.groupName}</td>
                  <td data-label={t('surveys.closesAt')}>{formatLocalDateTime(s.closesAtLocal)}</td>
                  <td data-label={t('surveys.responses')}>{t('surveys.responsesOf', { count: s.responseCount, total: s.studentCount })}</td>
                  <td data-label={t('common.status')}><Badge tone={statusTone(s.status)}>{t(`surveyStatus.${s.status}`)}</Badge></td>
                  <td className="actions-col">
                    {s.status !== 'Open' && s.groupStatus === 'Active' && s.questionCount > 0 && (
                      <button type="button" className="btn btn-small btn-primary"
                        onClick={() => act(() => api.post(`/api/manager/surveys/${s.id}/open`), t('surveys.confirmOpen'))}>
                        {s.status === 'Draft' ? t('surveys.open') : t('surveys.reopen')}
                      </button>
                    )}
                    {s.status === 'Open' && (
                      <button type="button" className="btn btn-small"
                        onClick={() => act(() => api.post(`/api/manager/surveys/${s.id}/close`), t('surveys.confirmClose'))}>
                        {t('surveys.close')}
                      </button>
                    )}
                    {s.status !== 'Draft' && (
                      <Link className="btn btn-small" to={`/manager/surveys/${s.id}/results`}>{t('surveys.results')}</Link>
                    )}
                    {s.status !== 'Draft' && <ExportButton url={`/api/manager/exports/surveys/${s.id}`} />}
                    {s.status === 'Draft' && (
                      <button type="button" className="btn btn-small btn-danger"
                        onClick={() => act(() => api.del(`/api/manager/surveys/${s.id}`), t('common.confirmDelete'))}>
                        {t('common.delete')}
                      </button>
                    )}
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
    </>
  )
}
