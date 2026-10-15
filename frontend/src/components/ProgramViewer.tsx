import { useState } from 'react'
import { useTranslation } from 'react-i18next'
import { Modal } from './ui'

/** Embedded PDF with a new-tab fallback; mobile browsers usually cannot embed PDFs, so small screens get the link only. */
export function PdfViewer({ url, fileName }: { url: string; fileName: string }) {
  const { t } = useTranslation()
  const newTab = (
    <a href={url} target="_blank" rel="noopener noreferrer" className="btn btn-small">{t('syllabus.openInNewTab')}</a>
  )
  return (
    <div className="pdf-viewer">
      <object data={url} type="application/pdf" aria-label={fileName} className="pdf-frame">
        <p className="pdf-fallback">{t('syllabus.noEmbed')}</p>
      </object>
      <p className="pdf-actions">{newTab}</p>
    </div>
  )
}

export function ProgramViewerModal({ url, fileName, onClose }: { url: string; fileName: string; onClose: () => void }) {
  const { t } = useTranslation()
  return (
    <Modal title={t('syllabus.viewerTitle', { name: fileName })} onClose={onClose} wide>
      <PdfViewer url={url} fileName={fileName} />
    </Modal>
  )
}

export function studentProgramUrl(disciplineId: string) {
  return `/api/student/disciplines/${disciplineId}/program`
}

/** «Просмотреть ПУД» for a student; renders nothing when the discipline has no accessible programme. */
export function ProgramViewButton({ disciplineId, fileName, className = 'btn btn-small' }: {
  disciplineId: string
  fileName: string | undefined
  className?: string
}) {
  const { t } = useTranslation()
  const [open, setOpen] = useState(false)
  if (!fileName) return null
  return (
    <>
      <button type="button" className={className} onClick={() => setOpen(true)}>{t('syllabus.view')}</button>
      {open && <ProgramViewerModal url={studentProgramUrl(disciplineId)} fileName={fileName} onClose={() => setOpen(false)} />}
    </>
  )
}
