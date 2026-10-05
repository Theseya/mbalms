import type { AppNotification, Group, SurveyStatus } from '../api/types'
import type { ConfirmOptions } from '../components/ui'
import { formatLocalDateTime } from './format'

type T = (key: string, opts?: Record<string, string>) => string

export function groupLabel(t: T) {
  return (g: Group) => (g.status === 'Archived' ? `${g.name} ${t('common.archivedSuffix')}` : g.name)
}

export function surveyStatusTone(s: SurveyStatus) {
  return s === 'Open' ? 'green' : s === 'Draft' ? 'amber' : 'gray'
}

/** Confirmation texts for publishing, reopening and closing a survey. */
export function statusConfirm(t: T, action: 'open' | 'close', status: SurveyStatus | undefined): ConfirmOptions {
  if (action === 'close')
    return { title: t('surveys.confirmCloseTitle'), message: t('surveys.confirmClose'), confirmLabel: t('surveys.confirmCloseTitle') }
  return status === 'Closed'
    ? { title: t('surveys.confirmReopenTitle'), message: t('surveys.confirmReopen'), confirmLabel: t('surveys.reopen') }
    : { title: t('surveys.confirmOpenTitle'), message: t('surveys.confirmOpen'), confirmLabel: t('surveys.open') }
}

/** Notifications store only parameters; the text is rendered in the current UI language. */
export function notificationText(n: AppNotification, t: T): string {
  const p = n.payload
  switch (n.type) {
    case 'SurveyAssigned':
      return t('notifications.SurveyAssigned', { title: p.title })
    case 'ScheduleChanged':
      return t(`notifications.ScheduleChanged_${p.change}`, { discipline: p.disciplineName, date: formatLocalDateTime(p.startsAtLocal) })
    case 'GradePublished':
      return t(p.change === 'updated' ? 'notifications.GradePublished_updated' : 'notifications.GradePublished', {
        discipline: p.disciplineName, period: p.periodName,
      })
  }
}
