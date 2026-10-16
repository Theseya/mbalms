import { useState } from 'react'
import { useTranslation } from 'react-i18next'
import { api, query } from '../../api/client'
import type { AdminAuditEntry, PagedResult } from '../../api/types'
import { Empty, ErrorBanner, Loading, PageHeader, Pagination } from '../../components/ui'
import { formatInstant } from '../../lib/format'
import { PAGE_SIZE } from '../../lib/paging'
import { useLoad } from '../../lib/useLoad'

export function AdminAuditPage() {
  const { t } = useTranslation()
  const [page, setPage] = useState(1)
  const listQuery = query({ page, pageSize: PAGE_SIZE })
  const list = useLoad(() => api.get<PagedResult<AdminAuditEntry>>(`/api/admin/audit${listQuery}`), listQuery)
  const rows = list.data?.items ?? []

  return (
    <>
      <PageHeader title={t('admin.auditTitle')} />
      <ErrorBanner error={list.error} />
      {list.loading && !list.data ? <Loading /> : !rows.length ? <Empty /> : (
        <div className="table-wrap">
          <table>
            <thead>
              <tr>
                <th>{t('admin.occurredAt')}</th>
                <th>{t('admin.actor')}</th>
                <th>{t('admin.target')}</th>
                <th>{t('admin.action')}</th>
              </tr>
            </thead>
            <tbody>
              {rows.map((row) => (
                <tr key={row.id}>
                  <td data-label={t('admin.occurredAt')}>{formatInstant(row.occurredAt)}</td>
                  <td data-label={t('admin.actor')}>{row.actorEmail ?? t('common.none')}</td>
                  <td data-label={t('admin.target')}>{row.targetEmail ?? t('common.none')}</td>
                  <td data-label={t('admin.action')}>{t(`admin.actions.${row.action}`, { defaultValue: row.action })}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
      {list.data && <Pagination page={page} pageSize={PAGE_SIZE} total={list.data.total} onChange={setPage} />}
    </>
  )
}
