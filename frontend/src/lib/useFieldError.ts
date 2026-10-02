import { useTranslation } from 'react-i18next'
import type { ApiError } from '../api/client'

/** Returns the localized message for a field from an API validation error. */
export function useFieldError(error: ApiError | null | undefined) {
  const { t } = useTranslation()
  return (field: string): string | undefined => {
    const codes = error?.fieldErrors?.[field]
    if (!codes?.length) return undefined
    return t(`validation.${codes[0]}`, { defaultValue: t('validation.invalid') })
  }
}
