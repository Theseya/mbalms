import { useRef, useState, type ChangeEvent } from 'react'
import { useTranslation } from 'react-i18next'
import { ApiError, api } from '../../api/client'
import type { Discipline, DisciplineProgram } from '../../api/types'
import { PdfViewer } from '../../components/ProgramViewer'
import { ErrorBanner } from '../../components/ui'
import { formatFileSize, formatInstant } from '../../lib/format'
import { useConfirm } from '../../lib/useConfirm'
import { toApiError, useLoad } from '../../lib/useLoad'

const LIMITS_URL = '/api/manager/disciplines/program-limits'

/** ПУД block of the discipline form: one PDF per discipline, uploaded and removed independently of the form fields. */
export function DisciplineProgramSection({ discipline, onChanged }: { discipline: Discipline | null; onChanged: () => void }) {
  const { t } = useTranslation()
  const limits = useLoad(() => api.get<{ maxBytes: number }>(LIMITS_URL), LIMITS_URL)
  const [program, setProgram] = useState<DisciplineProgram | null>(discipline?.program ?? null)
  const [viewing, setViewing] = useState(false)
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<ApiError | null>(null)
  const input = useRef<HTMLInputElement>(null)
  const { confirm, dialog } = useConfirm()

  const maxBytes = limits.data?.maxBytes
  const url = discipline ? `/api/manager/disciplines/${discipline.id}/program` : ''

  const onFile = async (e: ChangeEvent<HTMLInputElement>) => {
    const file = e.target.files?.[0]
    e.target.value = ''
    if (!file || !discipline) return
    setError(null)
    if (!file.name.toLowerCase().endsWith('.pdf')) {
      setError(new ApiError(400, 'file_not_pdf'))
      return
    }
    if (file.size === 0) {
      setError(new ApiError(400, 'file_required'))
      return
    }
    if (maxBytes !== undefined && file.size > maxBytes) {
      setError(new ApiError(413, 'file_too_large'))
      return
    }
    if (program) {
      const ok = await confirm({
        title: t('syllabus.confirmReplaceTitle'),
        message: t('syllabus.confirmReplace', { current: program.fileName, next: file.name }),
        confirmLabel: t('syllabus.replace'),
      })
      if (!ok) return
    }
    setBusy(true)
    try {
      const form = new FormData()
      form.append('file', file)
      setProgram(await api.upload<DisciplineProgram>(url, form))
      setViewing(false)
      onChanged()
    } catch (err) {
      setError(toApiError(err))
    } finally {
      setBusy(false)
    }
  }

  const onDelete = async () => {
    if (!program) return
    const ok = await confirm({
      title: t('syllabus.confirmDeleteTitle'),
      message: t('syllabus.confirmDelete', { name: program.fileName }),
      confirmLabel: t('common.delete'),
      danger: true,
    })
    if (!ok) return
    setError(null)
    setBusy(true)
    try {
      await api.del(url)
      setProgram(null)
      setViewing(false)
      onChanged()
    } catch (err) {
      setError(toApiError(err))
    } finally {
      setBusy(false)
    }
  }

  return (
    <section className="program-section" aria-label={t('syllabus.title')}>
      <h3>{t('syllabus.title')}</h3>
      {!discipline ? <p className="muted">{t('syllabus.afterCreate')}</p> : (
        <>
          <ErrorBanner error={error} />
          {program ? (
            <p className="program-file">
              <strong>{program.fileName}</strong>
              <small className="muted block">
                {t('syllabus.uploaded', { date: formatInstant(program.uploadedAt), size: formatFileSize(program.sizeBytes) })}
              </small>
            </p>
          ) : <p className="empty program-empty">{t('syllabus.empty')}</p>}
          <div className="program-actions">
            {program && (
              <button type="button" className="btn btn-small" onClick={() => setViewing(!viewing)} aria-expanded={viewing}>
                {viewing ? t('syllabus.hide') : t('syllabus.view')}
              </button>
            )}
            <button type="button" className="btn btn-small" disabled={busy} onClick={() => input.current?.click()}>
              {busy ? t('syllabus.uploading') : program ? t('syllabus.replace') : t('syllabus.upload')}
            </button>
            {program && (
              <button type="button" className="btn btn-small btn-danger" disabled={busy} onClick={onDelete}>
                {t('common.delete')}
              </button>
            )}
            <input ref={input} type="file" accept="application/pdf,.pdf" hidden onChange={onFile}
              aria-label={program ? t('syllabus.replace') : t('syllabus.upload')} />
          </div>
          {maxBytes !== undefined && <small className="hint">{t('syllabus.hint', { size: formatFileSize(maxBytes) })}</small>}
          {viewing && program && <PdfViewer url={`${url}?v=${encodeURIComponent(program.uploadedAt)}`} fileName={program.fileName} />}
        </>
      )}
      {dialog}
    </section>
  )
}
