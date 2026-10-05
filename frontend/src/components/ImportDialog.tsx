import { useRef, useState } from 'react'
import { useTranslation } from 'react-i18next'
import { ApiError, api } from '../api/client'
import type { ImportConfirmResult, ImportPreview } from '../api/types'
import { currentLanguage } from '../i18n'
import { toApiError } from '../lib/useLoad'
import { ErrorBanner, Field, Modal } from './ui'

type Step = 'choose' | 'preview' | 'done'

export type ImportPreviewColumn = { key: string; labelKey: string }

export function ImportDialog({
  entityPath,
  previewColumns,
  requirePasswordsForCreates = false,
  onClose,
  onImported,
}: {
  /** e.g. `/api/manager/imports/disciplines` */
  entityPath: string
  /** Value keys from preview rows → i18n label keys (e.g. `common.name`). */
  previewColumns: ImportPreviewColumn[]
  /** When true, Create rows need passwords entered only in confirm UI (never stored in preview). */
  requirePasswordsForCreates?: boolean
  onClose: () => void
  onImported: () => void
}) {
  const { t } = useTranslation()
  const inputRef = useRef<HTMLInputElement>(null)
  const [step, setStep] = useState<Step>('choose')
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<ApiError | null>(null)
  const [preview, setPreview] = useState<ImportPreview | null>(null)
  const [result, setResult] = useState<ImportConfirmResult | null>(null)
  /** Ephemeral: only in component state for confirm body; cleared on reset/done. */
  const [passwords, setPasswords] = useState<Record<number, string>>({})

  const clearPasswords = () => setPasswords({})

  const clearFileInput = () => {
    if (inputRef.current) inputRef.current.value = ''
  }

  const resetToUpload = () => {
    setStep('choose')
    setPreview(null)
    setResult(null)
    clearPasswords()
    clearFileInput()
  }

  const downloadTemplate = async () => {
    setError(null)
    try {
      await api.download(`${entityPath}/template?lang=${currentLanguage()}`)
    } catch (e) {
      setError(toApiError(e))
    }
  }

  const onFile = async (file: File | undefined) => {
    if (!file) return
    setBusy(true)
    setError(null)
    setResult(null)
    clearPasswords()
    try {
      const form = new FormData()
      form.append('file', file)
      const data = await api.upload<ImportPreview>(`${entityPath}/preview`, form)
      setPreview(data)
      setStep('preview')
    } catch (e) {
      setError(toApiError(e))
      setPreview(null)
      setStep('choose')
    } finally {
      setBusy(false)
      clearFileInput()
    }
  }

  const createRows = preview?.rows.filter((r) => r.action === 'Create') ?? []
  const writableCount = (preview?.createCount ?? 0) + (preview?.updateCount ?? 0)
  const passwordsReady = !requirePasswordsForCreates
    || createRows.every((r) => (passwords[r.rowNumber] ?? '').trim().length > 0)
  const canConfirm = !!preview?.importId && writableCount > 0 && (preview.fileErrors?.length ?? 0) === 0 && passwordsReady

  const onConfirm = async () => {
    if (!preview?.importId) return
    setBusy(true)
    setError(null)
    try {
      const body: { importId: string; passwords?: { rowNumber: number; password: string }[] } = {
        importId: preview.importId,
      }
      if (requirePasswordsForCreates && createRows.length > 0) {
        body.passwords = createRows.map((r) => ({
          rowNumber: r.rowNumber,
          password: passwords[r.rowNumber] ?? '',
        }))
      }
      const res = await api.post<ImportConfirmResult>(`${entityPath}/confirm`, body)
      clearPasswords()
      setResult(res)
      setStep('done')
      onImported()
    } catch (e) {
      // Confirm consumes importId even on failure — force a fresh upload.
      setError(toApiError(e))
      resetToUpload()
    } finally {
      setBusy(false)
    }
  }

  return (
    <Modal title={step === 'done' ? t('import.doneTitle') : t('import.title')} onClose={() => { clearPasswords(); onClose() }}>
      <ErrorBanner error={error} />

      {step === 'choose' && (
        <div className="stack">
          <p className="muted">{t('import.hint')}</p>
          <div className="form-actions spread">
            <button type="button" className="btn" onClick={() => void downloadTemplate()} disabled={busy}>
              {t('import.downloadTemplate')}
            </button>
            <label className={`btn btn-primary${busy ? ' disabled' : ''}`}>
              {busy ? t('app.loading') : t('import.chooseFile')}
              <input
                ref={inputRef}
                type="file"
                accept=".xlsx,application/vnd.openxmlformats-officedocument.spreadsheetml.sheet"
                hidden
                disabled={busy}
                onChange={(e) => void onFile(e.target.files?.[0])}
              />
            </label>
          </div>
        </div>
      )}

      {step === 'preview' && preview && (
        <div className="stack">
          {(preview.fileErrors?.length ?? 0) > 0 && (
            <div className="alert alert-error" role="alert">
              <p>{t('import.formulaBlocked')}</p>
              <ul>
                {preview.fileErrors.map((fe, i) => (
                  <li key={`${fe.sheet}-${fe.row}-${fe.column}-${i}`}>
                    {t('import.cellError', {
                      sheet: fe.sheet,
                      row: fe.row,
                      column: fe.column,
                      code: t(`validation.${fe.code}`, { defaultValue: fe.code }),
                    })}
                  </li>
                ))}
              </ul>
            </div>
          )}
          <p>
            {t('import.summary', {
              create: preview.createCount,
              update: preview.updateCount,
              conflict: preview.conflictCount,
              error: preview.errorCount,
            })}
          </p>
          {preview.rows.length > 0 && (
            <div className="table-wrap">
              <table>
                <thead>
                  <tr>
                    <th>{t('import.colRow')}</th>
                    <th>{t('import.colAction')}</th>
                    {previewColumns.map((col) => (
                      <th key={col.key}>{t(col.labelKey)}</th>
                    ))}
                    <th>{t('import.colErrors')}</th>
                    <th>{t('import.colWarnings')}</th>
                  </tr>
                </thead>
                <tbody>
                  {preview.rows.map((row) => (
                    <tr key={row.rowNumber}>
                      <td>{row.rowNumber}</td>
                      <td>{t(`import.action.${row.action}`)}</td>
                      {previewColumns.map((col) => (
                        <td key={col.key}>{row.values[col.key] ?? '—'}</td>
                      ))}
                      <td>
                        {row.errors.length === 0
                          ? '—'
                          : row.errors.map((err) => {
                              const key = `validation.${err.code}`
                              const fromErrors = t(`errors.${err.code}`, { defaultValue: '' })
                              return fromErrors || t(key, { defaultValue: err.code })
                            }).join(', ')}
                      </td>
                      <td>
                        {(row.warnings?.length ?? 0) === 0
                          ? '—'
                          : row.warnings!.map((w) => t(`import.warning.${w.code}`, { defaultValue: w.code })).join(', ')}
                      </td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
          )}
          {requirePasswordsForCreates && createRows.length > 0 && (
            <div className="stack">
              <p className="muted">{t('import.passwordsHint')}</p>
              {createRows.map((row) => (
                <Field
                  key={row.rowNumber}
                  label={t('import.passwordForRow', {
                    row: row.rowNumber,
                    email: row.values.email ?? '',
                  })}
                  required
                  hint={t('account.passwordHint')}
                  error={error?.fieldErrors[`passwords[${row.rowNumber}]`]?.[0]
                    ? t(`validation.${error.fieldErrors[`passwords[${row.rowNumber}]`][0]}`, {
                      defaultValue: error.fieldErrors[`passwords[${row.rowNumber}]`][0],
                    })
                    : undefined}
                >
                  {(id, d) => (
                    <input
                      id={id}
                      type="password"
                      autoComplete="new-password"
                      aria-describedby={d}
                      value={passwords[row.rowNumber] ?? ''}
                      disabled={busy}
                      onChange={(e) => setPasswords((prev) => ({ ...prev, [row.rowNumber]: e.target.value }))}
                    />
                  )}
                </Field>
              ))}
            </div>
          )}
          <div className="form-actions">
            <button type="button" className="btn" onClick={resetToUpload} disabled={busy}>{t('import.uploadAgain')}</button>
            <button type="button" className="btn btn-primary" onClick={() => void onConfirm()} disabled={!canConfirm || busy}>
              {busy ? t('app.loading') : t('import.confirm')}
            </button>
          </div>
        </div>
      )}

      {step === 'done' && result && (
        <div className="stack">
          <p>{t('import.result', { created: result.created, updated: result.updated, skipped: result.skipped })}</p>
          <div className="form-actions">
            <button type="button" className="btn btn-primary" onClick={() => { clearPasswords(); onClose() }}>{t('common.close')}</button>
          </div>
        </div>
      )}
    </Modal>
  )
}
