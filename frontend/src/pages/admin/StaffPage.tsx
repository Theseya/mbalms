import { useState, type FormEvent } from 'react'
import { useTranslation } from 'react-i18next'
import { Link } from 'react-router-dom'
import { ApiError, api, query } from '../../api/client'
import type { PagedResult, StaffMember } from '../../api/types'
import { useAuth } from '../../auth/AuthContext'
import { hasRole } from '../../auth/roles'
import { Empty, ErrorBanner, Field, FormActions, Loading, Modal, PageHeader, Pagination } from '../../components/ui'
import { PAGE_SIZE } from '../../lib/paging'
import { useConfirm } from '../../lib/useConfirm'
import { useFieldError } from '../../lib/useFieldError'
import { toApiError, useLoad } from '../../lib/useLoad'

interface InviteResult {
  queued: boolean
  developmentPreviewId: string | null
}

export function StaffPage() {
  const { t } = useTranslation()
  const { me } = useAuth()
  const isOwner = me ? hasRole(me, 'Owner') : false
  const [page, setPage] = useState(1)
  const listQuery = query({ page, pageSize: PAGE_SIZE })
  const list = useLoad(() => api.get<PagedResult<StaffMember>>(`/api/admin/staff${listQuery}`), listQuery)
  const [inviteOpen, setInviteOpen] = useState(false)
  const [email, setEmail] = useState('')
  const [error, setError] = useState<ApiError | null>(null)
  const [actionError, setActionError] = useState<ApiError | null>(null)
  const [saving, setSaving] = useState(false)
  const [invitePreviewId, setInvitePreviewId] = useState<string | null>(null)
  const fieldError = useFieldError(error)
  const { confirm, dialog } = useConfirm()
  const rows = list.data?.items ?? []

  const onInvite = async (e: FormEvent) => {
    e.preventDefault()
    setSaving(true)
    setError(null)
    try {
      const result = await api.post<InviteResult>('/api/admin/staff/managers', { email })
      setInviteOpen(false)
      setEmail('')
      setInvitePreviewId(result.developmentPreviewId)
      list.reload()
    } catch (err) {
      setError(toApiError(err))
    } finally {
      setSaving(false)
    }
  }

  const onBlock = async (row: StaffMember, lock: boolean) => {
    const ok = await confirm({
      title: t(lock ? 'admin.confirmBlockTitle' : 'admin.confirmUnblockTitle'),
      message: t(lock ? 'admin.confirmBlock' : 'admin.confirmUnblock', { email: row.email }),
      confirmLabel: t(lock ? 'admin.block' : 'admin.unblock'),
    })
    if (!ok) return
    setActionError(null)
    try {
      await api.post(`/api/admin/staff/${row.id}/${lock ? 'block' : 'unblock'}`)
      list.reload()
    } catch (err) {
      setActionError(toApiError(err))
    }
  }

  return (
    <>
      <PageHeader
        title={t('admin.staffTitle')}
        actions={
          <button type="button" className="btn btn-primary" onClick={() => { setError(null); setInviteOpen(true) }}>
            {t('admin.inviteManager')}
          </button>
        }
      />
      <ErrorBanner error={list.error ?? actionError} />
      {invitePreviewId && (
        <p className="muted">
          {t('students.inviteQueuedDev')}{' '}
          <Link to={`/dev/mail/${invitePreviewId}`}>{t('students.openPreparedEmail')}</Link>
        </p>
      )}
      {list.loading && !list.data ? <Loading /> : !rows.length ? <Empty /> : (
        <div className="table-wrap">
          <table>
            <thead>
              <tr>
                <th>{t('common.email')}</th>
                <th>{t('admin.roles')}</th>
                <th>{t('admin.status')}</th>
                <th className="actions-col">{t('common.actions')}</th>
              </tr>
            </thead>
            <tbody>
              {rows.map((row) => {
                const canBlock = isOwner || (row.roles.includes('Manager') && !row.roles.includes('Owner') && !row.roles.includes('AccountAdmin'))
                return (
                  <tr key={row.id}>
                    <td data-label={t('common.email')}>{row.email}</td>
                    <td data-label={t('admin.roles')}>{row.roles.join(', ')}</td>
                    <td data-label={t('admin.status')}>{row.locked ? t('admin.locked') : t('admin.active')}</td>
                    <td className="actions-col">
                      {canBlock && (
                        <button type="button" className="btn btn-small" onClick={() => onBlock(row, !row.locked)}>
                          {row.locked ? t('admin.unblock') : t('admin.block')}
                        </button>
                      )}
                    </td>
                  </tr>
                )
              })}
            </tbody>
          </table>
        </div>
      )}
      {list.data && <Pagination page={page} pageSize={PAGE_SIZE} total={list.data.total} onChange={setPage} />}
      {inviteOpen && (
        <Modal title={t('admin.inviteManager')} onClose={() => setInviteOpen(false)}>
          <form onSubmit={onInvite}>
            <ErrorBanner error={error} />
            <Field label={t('common.email')} required error={fieldError('email')}>
              {(id) => (
                <input id={id} type="email" value={email} required onChange={(e) => setEmail(e.target.value)} />
              )}
            </Field>
            <p className="muted">{t('admin.inviteHint')}</p>
            <FormActions onCancel={() => setInviteOpen(false)} saving={saving} />
          </form>
        </Modal>
      )}
      {dialog}
    </>
  )
}
