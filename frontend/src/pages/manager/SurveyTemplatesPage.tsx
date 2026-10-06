import { useState } from 'react'
import { useTranslation } from 'react-i18next'
import { Link } from 'react-router-dom'
import { ApiError, api } from '../../api/client'
import type { SurveyTemplateListItem } from '../../api/types'
import { Empty, ErrorBanner, Loading, PageHeader, type ConfirmOptions } from '../../components/ui'
import { formatInstant } from '../../lib/format'
import { useConfirm } from '../../lib/useConfirm'
import { toApiError, useLoad } from '../../lib/useLoad'

export function SurveyTemplatesPage() {
  const { t } = useTranslation()
  const list = useLoad(() => api.get<SurveyTemplateListItem[]>('/api/manager/survey-templates'), 'templates')
  const [actionError, setActionError] = useState<ApiError | null>(null)
  const { confirm, dialog } = useConfirm()

  const act = async (action: () => Promise<unknown>, options: ConfirmOptions) => {
    if (!(await confirm(options))) return
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
      <div className="layout-fluid" hidden />
      <PageHeader
        title={t('surveyTemplates.title')}
        actions={
          <>
            <Link to="/manager/surveys" className="btn">{t('surveys.title')}</Link>
            <Link to="/manager/surveys/from-template" className="btn">{t('surveyTemplates.createSurvey')}</Link>
            <Link to="/manager/survey-templates/new" className="btn btn-primary">{t('surveyTemplates.new')}</Link>
          </>
        }
      />
      <ErrorBanner error={list.error ?? actionError} />
      {list.loading && !list.data ? <Loading /> : !list.data?.length ? <Empty>{t('surveyTemplates.empty')}</Empty> : (
        <div className="table-wrap">
          <table>
            <thead>
              <tr>
                <th>{t('surveys.surveyTitle')}</th>
                <th>{t('surveys.type')}</th>
                <th>{t('surveys.questions')}</th>
                <th>{t('surveyTemplates.updatedAt')}</th>
                <th className="actions-col">{t('common.actions')}</th>
              </tr>
            </thead>
            <tbody>
              {list.data.map((tpl) => (
                <tr key={tpl.id}>
                  <td data-label={t('surveys.surveyTitle')}>
                    <Link to={`/manager/survey-templates/${tpl.id}`}>{tpl.title}</Link>
                  </td>
                  <td data-label={t('surveys.type')}>{t(`surveyType.${tpl.type}`)}</td>
                  <td data-label={t('surveys.questions')}>{tpl.questionCount}</td>
                  <td data-label={t('surveyTemplates.updatedAt')}>{formatInstant(tpl.updatedAt)}</td>
                  <td className="actions-col">
                    <Link className="btn btn-small" to={`/manager/surveys/from-template?templateId=${tpl.id}`}>
                      {t('surveyTemplates.createSurvey')}
                    </Link>
                    <Link className="btn btn-small" to={`/manager/survey-templates/${tpl.id}`}>{t('common.edit')}</Link>
                    <button type="button" className="btn btn-small btn-danger"
                      onClick={() => act(() => api.del(`/api/manager/survey-templates/${tpl.id}`), {
                        title: t('surveyTemplates.confirmDeleteTitle'),
                        message: t('surveyTemplates.confirmDelete', { title: tpl.title }),
                        confirmLabel: t('common.delete'),
                        danger: true,
                      })}>
                      {t('common.delete')}
                    </button>
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
      {dialog}
    </>
  )
}
