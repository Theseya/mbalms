import { useMemo, useState } from 'react'
import { useTranslation } from 'react-i18next'
import { Link } from 'react-router-dom'
import { ApiError, api } from '../../api/client'
import type { AppNotification, Dashboard, StudentGrade, StudentLesson, StudentSurveyListItem } from '../../api/types'
import { useAuth } from '../../auth/AuthContext'
import { SurveyCardMascot, SurveyMascot } from '../../components/SurveyMascot'
import { Badge, Empty, ErrorBanner, Loading, PageHeader } from '../../components/ui'
import { formatInstant, formatLocalDate, formatLocalDateTime, formatLocalTime, getAppTimeZone } from '../../lib/format'
import { notificationText } from '../../lib/labels'
import { notifyUnreadChanged } from '../../lib/unread'
import { toApiError, useLoad } from '../../lib/useLoad'

function LessonCard({ lesson, highlight }: { lesson: StudentLesson; highlight?: boolean }) {
  const { t } = useTranslation()
  const isLink = lesson.location?.startsWith('http://') || lesson.location?.startsWith('https://')
  const cancelled = lesson.status === 'Cancelled'
  return (
    <article className={`card lesson${highlight ? ' lesson-next' : ''}${cancelled ? ' lesson-cancelled' : ''}`}>
      <p className="lesson-date">
        {formatLocalDate(lesson.startsAtLocal, true)} · {formatLocalTime(lesson.startsAtLocal)}–{formatLocalTime(lesson.endsAtLocal)}
        {cancelled && <> <Badge tone="red">{t('lessonStatus.Cancelled')}</Badge></>}
      </p>
      <h3>{lesson.disciplineName}</h3>
      <p className="muted">{lesson.teacherName}</p>
      <p>
        {lesson.format && <Badge tone="blue">{t(`format.${lesson.format}`)}</Badge>}{' '}
        {lesson.location && (isLink
          ? <a href={lesson.location} target="_blank" rel="noopener noreferrer">{lesson.location}</a>
          : <span>{lesson.location}</span>)}
      </p>
      {lesson.comment && <p className="comment">{lesson.comment}</p>}
    </article>
  )
}

export function StudentHomePage() {
  const { t } = useTranslation()
  const { me } = useAuth()
  const dash = useLoad(() => api.get<Dashboard>('/api/student/dashboard'), 'dash')
  const surveys = useLoad(() => api.get<StudentSurveyListItem[]>('/api/student/surveys'), 'surveys')

  if (dash.error) return <ErrorBanner error={dash.error} />
  if (!dash.data) return <Loading />
  const d = dash.data

  return (
    <div className="student-dashboard">
      <PageHeader title={t('studentHome.greeting', { name: me?.displayName ?? '' })} />
      {me?.groupName && <p className="muted">{me.groupName}</p>}
      <div className="stats">
        {surveys.data ? <SurveyMascot surveys={surveys.data} /> : (
          <Link to="/student/surveys" className="card stat">
            <span className="stat-value">{d.pendingSurveys}</span>
            <span>{t('studentHome.pendingSurveys')}</span>
          </Link>
        )}
        <Link to="/student/notifications" className="card stat">
          <span className="stat-value">{d.unreadNotifications}</span>
          <span>{t('studentHome.unread')}</span>
        </Link>
      </div>

      <div className="dashboard-grid">
        <section>
          <h2>{t('studentHome.nextLesson')}</h2>
          {d.nextLesson ? <LessonCard lesson={d.nextLesson} highlight /> : <Empty>{t('studentHome.noNextLesson')}</Empty>}
          {d.upcomingLessons.length > 1 && (
            <>
              <h3>{t('studentHome.upcoming')}</h3>
              <ul className="plain-list">
                {d.upcomingLessons.slice(1).map((l) => (
                  <li key={l.id}>
                    <strong>{formatLocalDateTime(l.startsAtLocal)}</strong> — {l.disciplineName}
                  </li>
                ))}
              </ul>
            </>
          )}
          <Link to="/student/schedule">{t('studentHome.allSchedule')} →</Link>
        </section>

        <section>
          <h2>{t('studentHome.recentGrades')}</h2>
          {d.recentGrades.length === 0 ? <Empty>{t('studentHome.noGrades')}</Empty> : (
            <ul className="plain-list">
              {d.recentGrades.map((g) => (
                <li key={g.id}><strong className="grade">{g.value}</strong> {g.disciplineName} <span className="muted">({g.periodName})</span></li>
              ))}
            </ul>
          )}
          <Link to="/student/grades">{t('studentHome.allGrades')} →</Link>
        </section>
      </div>
    </div>
  )
}

export function StudentSchedulePage() {
  const { t } = useTranslation()
  const lessons = useLoad(() => api.get<StudentLesson[]>('/api/student/schedule'), 'schedule')
  const [showPast, setShowPast] = useState(false)
  const [now] = useState(() => Date.now())
  const { upcoming, past } = useMemo(() => {
    const all = lessons.data ?? []
    return {
      upcoming: all.filter((l) => new Date(l.endsAt).getTime() >= now),
      past: all.filter((l) => new Date(l.endsAt).getTime() < now).reverse(),
    }
  }, [lessons.data, now])

  const shown = showPast ? past : upcoming
  return (
    <>
      <div className="layout-fluid" hidden />
      <PageHeader title={t('schedule.title')} />
      <p className="muted">{t('schedule.timeZoneNote', { tz: getAppTimeZone() })}</p>
      <div className="tabs" role="tablist">
        <button type="button" role="tab" aria-selected={!showPast} className={!showPast ? 'tab active' : 'tab'} onClick={() => setShowPast(false)}>
          {t('schedule.upcoming')} ({upcoming.length})
        </button>
        <button type="button" role="tab" aria-selected={showPast} className={showPast ? 'tab active' : 'tab'} onClick={() => setShowPast(true)}>
          {t('schedule.past')} ({past.length})
        </button>
      </div>
      <ErrorBanner error={lessons.error} />
      {lessons.loading && !lessons.data ? <Loading /> : shown.length === 0 ? <Empty>{t('schedule.noLessons')}</Empty> : (
        <div className="lesson-list">
          {shown.map((l, i) => <LessonCard key={l.id} lesson={l} highlight={!showPast && i === 0} />)}
        </div>
      )}
    </>
  )
}

export function StudentGradesPage() {
  const { t } = useTranslation()
  const grades = useLoad(() => api.get<StudentGrade[]>('/api/student/grades'), 'grades')
  return (
    <>
      <div className="layout-fluid" hidden />
      <PageHeader title={t('grades.title')} />
      <ErrorBanner error={grades.error} />
      {grades.loading && !grades.data ? <Loading /> : !grades.data?.length ? <Empty>{t('studentHome.noGrades')}</Empty> : (
        <div className="table-wrap">
          <table>
            <thead>
              <tr>
                <th>{t('common.discipline')}</th>
                <th>{t('common.period')}</th>
                <th>{t('grades.valueShort')}</th>
                <th>{t('grades.publishedAt')}</th>
              </tr>
            </thead>
            <tbody>
              {grades.data.map((g) => (
                <tr key={g.id}>
                  <td data-label={t('common.discipline')}>{g.disciplineName}</td>
                  <td data-label={t('common.period')}>{g.periodName}</td>
                  <td data-label={t('grades.valueShort')} className="num"><strong className="grade">{g.value}</strong></td>
                  <td data-label={t('grades.publishedAt')}>{formatInstant(g.publishedAt)}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
    </>
  )
}

export function StudentSurveysPage() {
  const { t } = useTranslation()
  const surveys = useLoad(() => api.get<StudentSurveyListItem[]>('/api/student/surveys'), 'surveys')
  return (
    <>
      <div className="layout-fluid" hidden />
      <PageHeader title={t('studentSurveys.title')} />
      <ErrorBanner error={surveys.error} />
      {surveys.loading && !surveys.data ? <Loading /> : !surveys.data?.length ? <Empty>{t('studentSurveys.noSurveys')}</Empty> : (
        <div className="survey-list">
          {surveys.data.map((s) => (
            <article key={s.id} className="card survey-card">
              <SurveyCardMascot survey={s} />
              <div className="survey-card-body">
                <p className="muted">{t(`surveyType.${s.type}`)}{s.teacherName ? ` · ${s.teacherName}` : ''}</p>
                <h3>{s.title}</h3>
                <p>
                  {s.submitted ? <Badge tone="green">{t('studentSurveys.submitted')}</Badge>
                    : s.canRespond ? <Badge tone="amber">{t('studentSurveys.pending')}</Badge>
                      : <Badge tone="gray">{t('studentSurveys.unavailable')}</Badge>}
                  {s.closesAtLocal && <span className="muted"> · {t('surveys.closesAt')}: {formatLocalDateTime(s.closesAtLocal)}</span>}
                </p>
                <Link to={`/student/surveys/${s.id}`} className={s.canRespond ? 'btn btn-primary' : 'btn'}>
                  {s.canRespond ? t('studentSurveys.answer') : t('studentSurveys.view')}
                </Link>
              </div>
            </article>
          ))}
        </div>
      )}
    </>
  )
}

function notificationLink(n: AppNotification): string {
  if (n.type === 'SurveyAssigned') return `/student/surveys/${n.payload.surveyId}`
  if (n.type === 'GradePublished') return '/student/grades'
  return '/student/schedule'
}

export function NotificationsPage() {
  const { t } = useTranslation()
  const list = useLoad(() => api.get<AppNotification[]>('/api/notifications'), 'notifications')
  const [error, setError] = useState<ApiError | null>(null)

  const act = async (action: () => Promise<unknown>) => {
    setError(null)
    try {
      await action()
      list.reload()
      notifyUnreadChanged()
    } catch (err) {
      setError(toApiError(err))
    }
  }

  const hasUnread = list.data?.some((n) => !n.readAt)
  return (
    <>
      <div className="layout-fluid" hidden />
      <PageHeader
        title={t('notifications.title')}
        actions={hasUnread && (
          <button type="button" className="btn" onClick={() => act(() => api.post('/api/notifications/read-all'))}>
            {t('notifications.markAllRead')}
          </button>
        )}
      />
      <ErrorBanner error={list.error ?? error} />
      {list.loading && !list.data ? <Loading /> : !list.data?.length ? <Empty>{t('notifications.empty')}</Empty> : (
        <ul className="notifications">
          {list.data.map((n) => (
            <li key={n.id} className={n.readAt ? 'read' : 'unread'}>
              <div>
                <Link to={notificationLink(n)} onClick={() => !n.readAt && void api.post(`/api/notifications/${n.id}/read`).then(notifyUnreadChanged, () => undefined)}>
                  {notificationText(n, t)}
                </Link>
                <small className="muted block">{formatInstant(n.createdAt)}</small>
              </div>
              {!n.readAt && (
                <button type="button" className="btn btn-small" onClick={() => act(() => api.post(`/api/notifications/${n.id}/read`))}>
                  {t('notifications.markRead')}
                </button>
              )}
            </li>
          ))}
        </ul>
      )}
    </>
  )
}
