import type { SurveyQuestion } from '../api/types'

export type AnswerValue = { intValue?: number; optionId?: string; textValue?: string }

/** Client-side mirror of the server rules: required questions, scale range and text length. */
export function validateAnswers(questions: SurveyQuestion[], answers: Record<string, AnswerValue>): Record<string, string> {
  const errors: Record<string, string> = {}
  for (const q of questions) {
    const a = answers[q.id]
    const provided =
      q.type === 'Scale' ? a?.intValue !== undefined
        : q.type === 'SingleChoice' ? !!a?.optionId
          : !!a?.textValue?.trim()
    if (!provided) {
      if (q.isRequired) errors[q.id] = 'required'
      continue
    }
    if (q.type === 'Scale' && (a!.intValue! < (q.scaleMin ?? 0) || a!.intValue! > (q.scaleMax ?? 0))) errors[q.id] = 'range'
    if (q.type === 'Text' && a!.textValue!.length > 4000) errors[q.id] = 'max_length'
  }
  return errors
}
