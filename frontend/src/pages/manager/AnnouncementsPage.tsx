import { useState } from 'react'
import { useTranslation } from 'react-i18next'
import { Link } from 'react-router-dom'
import { ApiError, api, query } from '../../api/client'
import type { AnnouncementListItem, AnnouncementStatus, Group } from '../../api/types'
import { Badge, Empty, ErrorBanner, Field, Loading, PageHeader, Select, type ConfirmOptions } from '../../components/ui'
import { formatInstant, formatLocalDateTime } from '../../lib/format'
import { announcementStatusTone, groupLabel } from '../../lib/labels'
import { useConfirm } from '../../lib/useConfirm'
import { toApiError, useLoad } from '../../lib/useLoad'

const STATUSES: AnnouncementStatus[] = ['Draft', 'Published', 'Unpublished']

export function AnnouncementsPage() {
  const { t } = useTranslation()
  const [groupId, setGroupId] = useState('')
  const [status, setStatus] = useState('')
  const groups = useLoad(() => api.get<Group[]>('/api/manager/groups?status=All'), 'groups')
  const q = query({ groupId, status })
  const list = useLoad(() => api.get<AnnouncementListItem[]>(`/api/manager/announcements${q}`), q)
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
        title={t('announcements.title')}
        actions={<Link to="/manager/announcements/new" className="btn btn-primary">{t('announcements.new')}</Link>}
      />
      <div className="filters">
        <Field label={t('common.filterGroup')}>
          {(id) => <Select id={id} value={groupId} onChange={setGroupId} items={groups.data} label={groupLabel(t)} placeholder={t('common.all')} />}
        </Field>
        <Field label={t('common.status')}>
          {(id) => (
            <select id={id} value={status} onChange={(e) => setStatus(e.target.value)}>
              <option value="">{t('common.all')}</option>
              {STATUSES.map((x) => <option key={x} value={x}>{t(`announcementStatus.${x}`)}</option>)}
            </select>
          )}
        </Field>
      </div>
      <ErrorBanner error={list.error ?? actionError} />
      {list.loading && !list.data ? <Loading /> : !list.data?.length ? <Empty>{t('announcements.empty')}</Empty> : (
        <div className="table-wrap">
          <table>
            <thead>
              <tr>
                <th>{t('announcements.announcementTitle')}</th>
                <th>{t('announcements.groups')}</th>
                <th>{t('common.status')}</th>
                <th>{t('announcements.publishedAt')}</th>
                <th>{t('announcements.expiresAt')}</th>
                <th className="actions-col">{t('common.actions')}</th>
              </tr>
            </thead>
            <tbody>
              {list.data.map((a) => (
                <tr key={a.id}>
                  <td data-label={t('announcements.announcementTitle')}>
                    <Link to={`/manager/announcements/${a.id}`}>{a.title}</Link>
                    {a.isPinned && <>{' '}<Badge tone="blue">{t('announcements.pinned')}</Badge></>}
                    {a.isExpired && <>{' '}<Badge tone="amber">{t('announcements.expired')}</Badge></>}
                  </td>
                  <td data-label={t('announcements.groups')} className="wrap">
                    {a.groups.map((g) => (g.status === 'Archived' ? `${g.name} ${t('common.archivedSuffix')}` : g.name)).join(', ')}
                  </td>
                  <td data-label={t('common.status')}>
                    <Badge tone={announcementStatusTone(a.status)}>{t(`announcementStatus.${a.status}`)}</Badge>
                  </td>
                  <td data-label={t('announcements.publishedAt')}>{formatInstant(a.publishedAt)}</td>
                  <td data-label={t('announcements.expiresAt')}>{formatLocalDateTime(a.expiresAtLocal)}</td>
                  <td className="actions-col">
                    <Link className="btn btn-small" to={`/manager/announcements/${a.id}`}>{t('common.edit')}</Link>
                    {a.status !== 'Published' && (
                      <button type="button" className="btn btn-small"
                        onClick={() => act(() => api.post(`/api/manager/announcements/${a.id}/publish`), {
                          title: t('announcements.confirmPublishTitle'),
                          message: t('announcements.confirmPublish', { title: a.title }),
                          confirmLabel: t('announcements.publish'),
                        })}>
                        {t('announcements.publish')}
                      </button>
                    )}
                    {a.status === 'Published' && (
                      <button type="button" className="btn btn-small"
                        onClick={() => act(() => api.post(`/api/manager/announcements/${a.id}/unpublish`), {
                          title: t('announcements.confirmUnpublishTitle'),
                          message: t('announcements.confirmUnpublish', { title: a.title }),
                          confirmLabel: t('announcements.unpublish'),
                        })}>
                        {t('announcements.unpublish')}
                      </button>
                    )}
                    {a.status !== 'Published' && (
                      <button type="button" className="btn btn-small btn-danger"
                        onClick={() => act(() => api.del(`/api/manager/announcements/${a.id}`), {
                          title: t('announcements.confirmDeleteTitle'),
                          message: t('announcements.confirmDelete', { title: a.title }),
                          confirmLabel: t('common.delete'),
                          danger: true,
                        })}>
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
      {dialog}
    </>
  )
}
