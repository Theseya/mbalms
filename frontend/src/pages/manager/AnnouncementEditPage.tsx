import { useEffect, useMemo, useState, type FormEvent } from 'react'
import { useTranslation } from 'react-i18next'
import { Link, useLocation, useNavigate, useParams } from 'react-router-dom'
import { ApiError, api, type FieldErrors } from '../../api/client'
import type { AnnouncementDetail, Group } from '../../api/types'
import { Badge, ErrorBanner, Field, Loading, PageHeader } from '../../components/ui'
import {
  formatInstant, formatLocalDateTime, getAppTimeZone, isAppLocalDateTimeInPast, toInputDateTime,
} from '../../lib/format'
import { announcementStatusTone, groupLabel } from '../../lib/labels'
import { useConfirm } from '../../lib/useConfirm'
import { useFieldError } from '../../lib/useFieldError'
import { toApiError, useLoad } from '../../lib/useLoad'

interface FormState {
  title: string
  body: string
  isPinned: boolean
  expiresAt: string
  groupIds: string[]
}

const empty: FormState = { title: '', body: '', isPinned: false, expiresAt: '', groupIds: [] }

interface PublishFailState {
  savedNotPublished: true
  wasNewDraft: boolean
  publishError: { status: number; code: string; fieldErrors: FieldErrors }
}

function fromDetail(a: AnnouncementDetail): FormState {
  return {
    title: a.title,
    body: a.body,
    isPinned: a.isPinned,
    expiresAt: toInputDateTime(a.expiresAtLocal),
    groupIds: a.groups.map((g) => g.id),
  }
}

export function AnnouncementEditPage() {
  const { t } = useTranslation()
  const { id: routeId } = useParams()
  const navigate = useNavigate()
  const location = useLocation()
  const [localId, setLocalId] = useState<string | null>(null)
  const id = routeId ?? localId ?? undefined
  const isNew = !id
  const loaded = useLoad(
    () => (routeId ? api.get<AnnouncementDetail>(`/api/manager/announcements/${routeId}`) : Promise.resolve(null)),
    routeId ?? 'new',
  )
  const activeGroups = useLoad(() => api.get<Group[]>('/api/manager/groups?status=Active'), 'active-groups')
  const [form, setForm] = useState<FormState>(empty)
  const [detail, setDetail] = useState<AnnouncementDetail | null>(null)
  const [error, setError] = useState<ApiError | null>(null)
  const [saveNotice, setSaveNotice] = useState<'draft' | 'edit' | null>(null)
  const [saving, setSaving] = useState(false)
  const fieldError = useFieldError(error)
  const { confirm, dialog } = useConfirm()

  useEffect(() => {
    const state = location.state as PublishFailState | null
    if (!state?.savedNotPublished) return
    setSaveNotice(state.wasNewDraft ? 'draft' : 'edit')
    setError(new ApiError(state.publishError.status, state.publishError.code, state.publishError.fieldErrors))
    navigate(location.pathname, { replace: true, state: null })
  }, [location.state, location.pathname, navigate])

  useEffect(() => {
    if (loaded.data) {
      setDetail(loaded.data)
      setForm(fromDetail(loaded.data))
    } else if (!routeId && !localId) {
      setDetail(null)
      setForm(empty)
    }
  }, [loaded.data, routeId, localId])

  const selectableGroups = useMemo(() => {
    const active = activeGroups.data ?? []
    const linkedArchived = (detail?.groups ?? []).filter((g) => g.status === 'Archived')
    const byId = new Map<string, Group>()
    for (const g of active) byId.set(g.id, g)
    for (const g of linkedArchived) {
      if (!byId.has(g.id)) {
        byId.set(g.id, {
          id: g.id, name: g.name, status: g.status, startDate: null, endDate: null, archivedAt: null, studentCount: 0,
        })
      }
    }
    return [...byId.values()].sort((a, b) => a.name.localeCompare(b.name, undefined, { sensitivity: 'base' }))
  }, [activeGroups.data, detail])

  const toggleGroup = (groupId: string) => {
    setForm((f) => ({
      ...f,
      groupIds: f.groupIds.includes(groupId)
        ? f.groupIds.filter((x) => x !== groupId)
        : [...f.groupIds, groupId],
    }))
  }

  const applyPublishFailure = (wasNew: boolean, saved: AnnouncementDetail, publishErr: unknown) => {
    const apiErr = toApiError(publishErr)
    setDetail(saved)
    setSaveNotice(wasNew ? 'draft' : 'edit')
    setError(apiErr)
    if (wasNew) {
      navigate(`/manager/announcements/${saved.id}`, {
        replace: true,
        state: {
          savedNotPublished: true,
          wasNewDraft: true,
          publishError: { status: apiErr.status, code: apiErr.code, fieldErrors: apiErr.fieldErrors },
        } satisfies PublishFailState,
      })
    }
  }

  const save = async (andPublish: boolean) => {
    setError(null)
    setSaveNotice(null)
    if (andPublish && isAppLocalDateTimeInPast(form.expiresAt)) {
      setError(new ApiError(400, 'validation_failed', { expiresAt: ['invalid'] }))
      return
    }

    setSaving(true)
    const body = {
      title: form.title,
      body: form.body,
      isPinned: form.isPinned,
      expiresAt: form.expiresAt || null,
      groupIds: form.groupIds,
    }
    const wasNew = isNew
    try {
      const saved = wasNew
        ? await api.post<AnnouncementDetail>('/api/manager/announcements', body)
        : await api.put<AnnouncementDetail>(`/api/manager/announcements/${id}`, body)

      if (wasNew) {
        setLocalId(saved.id)
        setDetail(saved)
      } else {
        setDetail(saved)
      }

      if (andPublish && saved.status !== 'Published') {
        try {
          await api.post(`/api/manager/announcements/${saved.id}/publish`)
        } catch (publishErr) {
          applyPublishFailure(wasNew, saved, publishErr)
          return
        }
      }
      navigate('/manager/announcements')
    } catch (err) {
      setError(toApiError(err))
    } finally {
      setSaving(false)
    }
  }

  const onSubmit = (e: FormEvent) => {
    e.preventDefault()
    void save(false)
  }

  const onPublish = async () => {
    if (saving) return
    setError(null)
    setSaveNotice(null)
    if (isAppLocalDateTimeInPast(form.expiresAt)) {
      setError(new ApiError(400, 'validation_failed', { expiresAt: ['invalid'] }))
      return
    }
    if (!(await confirm({
      title: t('announcements.confirmPublishTitle'),
      message: t('announcements.confirmPublish', { title: form.title || t('announcements.announcementTitle') }),
      confirmLabel: t('announcements.publish'),
    }))) return
    await save(true)
  }

  if (loaded.loading && routeId) return <Loading />
  if (loaded.error) return <ErrorBanner error={loaded.error} />

  const current = detail

  return (
    <>
      <PageHeader
        title={isNew ? t('announcements.new') : t('announcements.edit')}
        actions={<Link to="/manager/announcements" className="btn">{t('common.back')}</Link>}
      />
      {current && (
        <p className="muted">
          <Badge tone={announcementStatusTone(current.status)}>{t(`announcementStatus.${current.status}`)}</Badge>
          {current.isPinned && <>{' '}<Badge tone="blue">{t('announcements.pinned')}</Badge></>}
          {current.isExpired && <>{' '}<Badge tone="amber">{t('announcements.expired')}</Badge></>}
          {current.publishedAt && <> · {t('announcements.publishedAt')}: {formatInstant(current.publishedAt)}</>}
          {current.expiresAtLocal && <> · {t('announcements.expiresAt')}: {formatLocalDateTime(current.expiresAtLocal)}</>}
        </p>
      )}
      {saveNotice && (
        <div className="alert alert-warning" role="status">
          {t(saveNotice === 'draft'
            ? 'announcements.savedAsDraftNotPublished'
            : 'announcements.savedNotPublished')}
        </div>
      )}
      <ErrorBanner error={error} />
      <form className="stack announcement-form" onSubmit={onSubmit}>
        <Field label={t('announcements.announcementTitle')} required error={fieldError('title')}>
          {(fid, d) => <input id={fid} aria-describedby={d} value={form.title}
            onChange={(e) => setForm({ ...form, title: e.target.value })} />}
        </Field>
        <Field label={t('announcements.body')} required error={fieldError('body')} hint={t('announcements.bodyHint')}>
          {(fid, d) => <textarea id={fid} rows={8} aria-describedby={d} value={form.body}
            onChange={(e) => setForm({ ...form, body: e.target.value })} />}
        </Field>
        <Field label={t('announcements.groups')} required error={fieldError('groupIds')}
          hint={t('announcements.groupsHint')}>
          {(fid) => (
            <div id={fid} className="checkbox-list" role="group" aria-label={t('announcements.groups')}>
              {selectableGroups.length === 0 ? <p className="muted">{t('announcements.noGroups')}</p> : selectableGroups.map((g) => (
                <label key={g.id} className="checkbox">
                  <input
                    type="checkbox"
                    checked={form.groupIds.includes(g.id)}
                    onChange={() => toggleGroup(g.id)}
                  />
                  <span>{groupLabel(t)(g)}</span>
                </label>
              ))}
            </div>
          )}
        </Field>
        <label className="checkbox">
          <input type="checkbox" checked={form.isPinned} onChange={(e) => setForm({ ...form, isPinned: e.target.checked })} />
          <span>{t('announcements.pin')}</span>
        </label>
        <Field label={t('announcements.expiresAt')} error={fieldError('expiresAt')} hint={t('schedule.timeZoneNote', { tz: getAppTimeZone() })}>
          {(fid, d) => <input id={fid} type="datetime-local" aria-describedby={d} value={form.expiresAt}
            onChange={(e) => setForm({ ...form, expiresAt: e.target.value })} />}
        </Field>
        <div className="form-actions">
          <button type="submit" className="btn btn-primary" disabled={saving}>{t('common.save')}</button>
          {(isNew || current?.status !== 'Published') && (
            <button type="button" className="btn" disabled={saving} onClick={() => void onPublish()}>
              {t('announcements.saveAndPublish')}
            </button>
          )}
          <Link to="/manager/announcements" className="btn">{t('common.cancel')}</Link>
        </div>
      </form>
      {dialog}
    </>
  )
}
