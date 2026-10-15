import { currentLanguage } from '../i18n'
import type { DateOnly, Instant, LocalDateTime } from '../api/types'

const locale = () => (currentLanguage() === 'en' ? 'en-GB' : 'ru-RU')

/**
 * Local values from the API are already wall-clock times in the application time zone,
 * so they are formatted as-is (interpreted as UTC to avoid any browser time zone shift).
 */
function wallClock(value: LocalDateTime | DateOnly): Date {
  const iso = value.length === 10 ? `${value}T00:00:00` : value
  return new Date(`${iso.slice(0, 19)}Z`)
}

export function formatLocalDateTime(value: LocalDateTime | null | undefined): string {
  if (!value) return '—'
  return new Intl.DateTimeFormat(locale(), {
    timeZone: 'UTC', day: '2-digit', month: '2-digit', year: 'numeric', hour: '2-digit', minute: '2-digit',
  }).format(wallClock(value))
}

export function formatLocalDate(value: LocalDateTime | DateOnly | null | undefined, long = false): string {
  if (!value) return '—'
  return new Intl.DateTimeFormat(locale(), long
    ? { timeZone: 'UTC', weekday: 'long', day: 'numeric', month: 'long', year: 'numeric' }
    : { timeZone: 'UTC', day: '2-digit', month: '2-digit', year: 'numeric' },
  ).format(wallClock(value))
}

export function formatLocalTime(value: LocalDateTime | null | undefined): string {
  if (!value) return '—'
  return new Intl.DateTimeFormat(locale(), { timeZone: 'UTC', hour: '2-digit', minute: '2-digit' }).format(wallClock(value))
}

export function formatNumber(value: number | null | undefined, fractionDigits = 2): string {
  if (value === null || value === undefined) return '—'
  return new Intl.NumberFormat(locale(), { minimumFractionDigits: fractionDigits, maximumFractionDigits: fractionDigits }).format(value)
}

export function formatFileSize(bytes: number): string {
  const ru = currentLanguage() !== 'en'
  if (bytes < 1024 * 1024) {
    return `${new Intl.NumberFormat(locale(), { maximumFractionDigits: 0 }).format(Math.max(1, Math.round(bytes / 1024)))} ${ru ? 'КБ' : 'KB'}`
  }
  return `${new Intl.NumberFormat(locale(), { maximumFractionDigits: 1 }).format(bytes / (1024 * 1024))} ${ru ? 'МБ' : 'MB'}`
}

let appTimeZone = 'Europe/Moscow'

export function setAppTimeZone(tz: string) {
  appTimeZone = tz
}

export function getAppTimeZone() {
  return appTimeZone
}

/** UTC instants (created/published/submitted) are shown in the application time zone. */
export function formatInstant(value: Instant | null | undefined): string {
  if (!value) return '—'
  return new Intl.DateTimeFormat(locale(), {
    timeZone: appTimeZone, day: '2-digit', month: '2-digit', year: 'numeric', hour: '2-digit', minute: '2-digit',
  }).format(new Date(value))
}

/** Value for <input type="datetime-local"> from an API local value. */
export function toInputDateTime(value: LocalDateTime | null | undefined): string {
  return value ? value.slice(0, 16) : ''
}

/** Formats `date` as `YYYY-MM-DDTHH:mm` wall-clock in the application time zone. */
export function toAppLocalDateTimeInput(date: Date = new Date()): string {
  const parts = new Intl.DateTimeFormat('en-CA', {
    timeZone: appTimeZone,
    year: 'numeric',
    month: '2-digit',
    day: '2-digit',
    hour: '2-digit',
    minute: '2-digit',
    hour12: false,
  }).formatToParts(date)
  const get = (type: Intl.DateTimeFormatPartTypes) => parts.find((p) => p.type === type)?.value ?? ''
  // Some engines emit "24" for midnight; normalize to "00".
  const hour = get('hour').replace(/^24$/, '00')
  return `${get('year')}-${get('month')}-${get('day')}T${hour}:${get('minute')}`
}

/** True when a datetime-local value is at or before "now" in the application time zone. */
export function isAppLocalDateTimeInPast(value: string, now: Date = new Date()): boolean {
  if (!value) return false
  return value.slice(0, 16) <= toAppLocalDateTimeInput(now)
}
