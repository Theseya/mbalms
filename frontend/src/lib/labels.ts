import type { AppNotification, Group, SurveyStatus } from '../api/types'
import { formatLocalDateTime } from './format'

type T = (key: string, opts?: Record<string, string>) => string

export function groupLabel(t: T) {
  return (g: Group) => (g.status === 'Archived' ? `${g.name} ${t('common.archivedSuffix')}` : g.name)
}

export function surveyStatusTone(s: SurveyStatus) {
  return s === 'Open' ? 'green' : s === 'Draft' ? 'amber' : 'gray'
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
      return t('notifications.GradePublished', { discipline: p.disciplineName, period: p.periodName })
  }
}
