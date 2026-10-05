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
