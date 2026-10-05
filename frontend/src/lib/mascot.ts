import type { StudentSurveyListItem } from '../api/types'

export type MascotMood = 'pending' | 'done' | 'none'

export interface MascotState {
  mood: MascotMood
  remaining: number
  /** The only survey left to answer goes straight to it; otherwise the list. */
  link: string
}

/** Derived from the survey list only, so it always matches what the student can actually answer. */
export function mascotState(surveys: StudentSurveyListItem[]): MascotState {
  const pending = surveys.filter((s) => s.canRespond)
  if (pending.length > 0)
    return { mood: 'pending', remaining: pending.length, link: pending.length === 1 ? `/student/surveys/${pending[0].id}` : '/student/surveys' }
  return { mood: surveys.some((s) => s.submitted) ? 'done' : 'none', remaining: 0, link: '/student/surveys' }
}

/**
 * Per-card mood on the student surveys list. Uses the same response flags as the badges/buttons
 * (submitted / canRespond) — no separate client state.
 */
export function surveyCardMood(survey: StudentSurveyListItem): MascotMood {
  if (survey.submitted) return 'done'
  if (survey.canRespond) return 'pending'
  return 'none'
}

/** i18n key for the card caption; closed vs other unavailable both use mood `none` but different copy. */
export function surveyCardCaptionKey(survey: StudentSurveyListItem):
  'studentSurveys.mascot.notCompleted' | 'studentSurveys.mascot.completed' | 'studentSurveys.mascot.closed' | 'studentSurveys.mascot.unavailable' {
  if (survey.submitted) return 'studentSurveys.mascot.completed'
  if (survey.canRespond) return 'studentSurveys.mascot.notCompleted'
  return survey.status === 'Closed' ? 'studentSurveys.mascot.closed' : 'studentSurveys.mascot.unavailable'
}
