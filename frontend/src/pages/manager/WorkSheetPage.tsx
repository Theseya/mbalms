import { useEffect, useId, useState, type FormEvent } from 'react'
import { useTranslation } from 'react-i18next'
import { Link } from 'react-router-dom'
import { ApiError, api } from '../../api/client'
import type {
  AssessmentSettings, CalculationMode, Discipline, Group, Period, TransferPreview, TransferResult, WorkSheet, WorkSheetRow,
  WorkSheetSummary,
} from '../../api/types'
import { Badge, Empty, ErrorBanner, Field, Loading, Modal, PageHeader, Select } from '../../components/ui'
import { currentLanguage } from '../../i18n'
import { groupLabel } from '../../lib/labels'
import { useConfirm } from '../../lib/useConfirm'
import { useFieldError } from '../../lib/useFieldError'
import { toApiError, useLoad } from '../../lib/useLoad'
import { parsePercent, parseScore, parseWhole, type Parsed } from '../../lib/worksheet'

const formatNumber = (n: number) => n.toLocaleString(currentLanguage(), { maximumFractionDigits: 2 })
const toInput = (n: number | null | undefined) => (n === null || n === undefined ? '' : String(n))

function looksLikeAttendanceName(name: string) {
  const n = name.trim().toLocaleLowerCase()
  return n === 'посещаемость' || n === 'attendance'
}

function categoryLabel(t: (key: string) => string, c: { kind: string; name: string }) {
  if (c.kind === 'Attendance') return t('worksheet.attendanceAuto')
  return c.name
}

const manualsOf = (settings: AssessmentSettings) => settings.categories.filter((c) => c.kind === 'Manual')
const worksOf = (sheet: WorkSheet) => sheet.categories.filter((c) => c.kind !== 'Attendance').flatMap((c) => c.works)

export function WorkSheetPage() {
  const { t } = useTranslation()
  const [disciplineId, setDisciplineId] = useState('')
  const [groupId, setGroupId] = useState('')
  const [periodId, setPeriodId] = useState('')
  const periodHintId = useId()
  const groups = useLoad(() => api.get<Group[]>('/api/manager/groups?status=All'), 'groups')
  const disciplines = useLoad(() => api.get<Discipline[]>('/api/manager/disciplines'), 'disciplines')
  const periods = useLoad(() => api.get<Period[]>('/api/manager/periods'), 'periods')

  const settingsUrl = disciplineId ? `/api/manager/worksheets/disciplines/${disciplineId}/settings` : ''
  const settings = useLoad(() => (settingsUrl ? api.get<AssessmentSettings>(settingsUrl) : Promise.resolve(null)), settingsUrl)
  const listUrl = groupId && disciplineId ? `/api/manager/worksheets?groupId=${groupId}&disciplineId=${disciplineId}` : ''
  const existing = useLoad(() => (listUrl ? api.get<WorkSheetSummary[]>(listUrl) : Promise.resolve(null)), listUrl)
  const url = groupId && disciplineId && periodId ? `/api/manager/worksheets/${groupId}/${disciplineId}/${periodId}` : ''
  const loaded = useLoad(() => (url ? api.get<WorkSheet>(url) : Promise.resolve(null)), url)
  const [sheet, setSheet] = useState<WorkSheet | null>(null)
  useEffect(() => {
    if (loaded.data) setSheet(loaded.data)
  }, [loaded.data])
  const current = sheet && sheet.groupId === groupId && sheet.disciplineId === disciplineId
    && sheet.periodId === periodId ? sheet : null
  const currentSettings = settings.data && settings.data.disciplineId === disciplineId ? settings.data : null

  const sheetsByPeriod = new Map((existing.data ?? []).map((s) => [s.periodId, s]))
  const periodLabel = (p: Period) => {
    const summary = sheetsByPeriod.get(p.id)
    if (!summary) return p.name
    return t(summary.hasTransfers ? 'worksheet.periodTransferred' : 'worksheet.periodHasSheet', { period: p.name })
  }

  const replaceSheet = (data: WorkSheet) => {
    setSheet(data)
    existing.reload()
  }
  // Element changes recalculate the open sheet; its unsaved input stays in the editor.
  const settingsSaved = async (next: AssessmentSettings) => {
    settings.reload()
    if (url && next.disciplineId === disciplineId) setSheet(await api.get<WorkSheet>(url))
  }

  return (
    <div className="layout-fluid">
      <PageHeader title={t('worksheet.title')} actions={<Link className="btn" to="/manager/gradebook">{t('nav.gradebook')}</Link>} />
      <p className="muted">{t('worksheet.intro')}</p>
      <ErrorBanner error={groups.error ?? disciplines.error ?? periods.error} />
      <div className="worksheet-filters">
        <Field label={t('common.discipline')} required>
          {(id) => <Select id={id} value={disciplineId} onChange={setDisciplineId} items={disciplines.data}
            label={(d) => d.name} placeholder={t('common.selectPlaceholder')} required />}
        </Field>
        <Field label={t('common.group')} required>
          {(id) => <Select id={id} value={groupId} onChange={setGroupId} items={groups.data} label={groupLabel(t)}
            placeholder={t('common.selectPlaceholder')} required />}
        </Field>
        <Field label={t('worksheet.period')} required>
          {(id) => <Select id={id} value={periodId} onChange={setPeriodId} items={periods.data} label={periodLabel}
            placeholder={t('common.selectPlaceholder')} describedBy={periodHintId} required />}
        </Field>
        <small id={periodHintId} className="hint">{t('worksheet.periodHint')}</small>
      </div>
      {listUrl && <ErrorBanner error={existing.error} />}
      {disciplineId && <ErrorBanner error={settings.error} />}
      {currentSettings && (
        <DisciplineSettingsPanel key={disciplineId} settings={currentSettings} url={settingsUrl} onSaved={settingsSaved} />
      )}
      {!url ? <p className="muted">{t('worksheet.choose')}</p> : <ErrorBanner error={loaded.error} />}
      {url && !loaded.error && (!current ? <Loading /> : (
        <SheetEditor key={url} sheet={current} url={url} onChange={replaceSheet} />
      ))}
    </div>
  )
}

interface WorkDraft { key: string; id: string | null; name: string; scoreCount: number }
interface CategoryDraft {
  key: string; id: string | null; name: string; weight: string; includeInAverage: boolean; works: WorkDraft[]
}

function DisciplineSettingsPanel({ settings, url, onSaved }: {
  settings: AssessmentSettings
  url: string
  onSaved: (next: AssessmentSettings) => Promise<void>
}) {
  const { t } = useTranslation()
  const [open, setOpen] = useState(false)
  const [notice, setNotice] = useState<string | null>(null)
  const manuals = manualsOf(settings)
  const empty = manuals.length === 0
  const average = settings.calculationMode === 'Average'
  const list = [
    ...manuals.map((c) => `${categoryLabel(t, c)} — ${formatNumber(c.weight)}%`),
    `${t('worksheet.attendanceAuto')} — ${formatNumber(settings.attendanceWeight)}%`,
  ].join('; ')

  return (
    <section className="card worksheet-panel" aria-labelledby="ws-discipline-settings">
      <div className="worksheet-panel-header">
        <h2 id="ws-discipline-settings">{t('worksheet.disciplineSettings')}</h2>
        <button type="button" className={empty ? 'btn btn-primary' : 'btn'} onClick={() => { setNotice(null); setOpen(true) }}>
          {empty ? t('worksheet.addElement') : t('worksheet.editElements')}
        </button>
      </div>
      <p className="muted">{t('worksheet.disciplineSettingsHint', { discipline: settings.disciplineName })}</p>
      <p>
        <strong>{t('worksheet.calculationMode')}:</strong>{' '}
        {t(average ? 'worksheet.modeAverage' : 'worksheet.modeWeighted')}
      </p>
      <p className="muted">{t('worksheet.modeAppliesHint')}</p>
      {notice && <div className="alert alert-success" role="status">{notice}</div>}
      {empty ? <p>{t('worksheet.noElements')}</p> : (
        <>
          <p>{t('worksheet.elementsSummary', { list })}</p>
          {average
            ? <Composition terms={settings.averageTerms} includeAttendance={settings.includeAttendanceInAverage} manuals={manuals} />
            : <WeightStatus total={settings.weightTotal} complete={settings.weightsComplete} />}
        </>
      )}
      {empty && average && (
        <Composition terms={settings.averageTerms} includeAttendance={settings.includeAttendanceInAverage} manuals={manuals} />
      )}
      <p>
        {t('worksheet.threshold')}:{' '}
        <strong>{settings.attendanceThreshold === null ? t('worksheet.noThreshold') : `${formatNumber(settings.attendanceThreshold)}%`}</strong>
      </p>
      {open && (
        <ElementsEditor settings={settings} url={url} startWithNew={empty} onClose={() => setOpen(false)}
          onSaved={async (next) => {
            setOpen(false)
            setNotice(t('worksheet.settingsSaved'))
            await onSaved(next)
          }} />
      )}
    </section>
  )
}

function WeightStatus({ total, complete }: { total: number; complete: boolean }) {
  const { t } = useTranslation()
  return (
    <p className="worksheet-weights" role="status">
      <strong>{t('worksheet.weightTotal', { total: formatNumber(total) })}.</strong>{' '}
      {complete ? t('worksheet.weightsComplete') : t('worksheet.weightsIncomplete')}
    </p>
  )
}

function Composition({ terms, includeAttendance, manuals }: {
  terms: string[]
  includeAttendance: boolean
  manuals: { name: string }[]
}) {
  const { t } = useTranslation()
  const doubleCount = includeAttendance && manuals.some((c) => looksLikeAttendanceName(c.name))
  return (
    <>
      <p role="status">
        {terms.length === 0
          ? t('worksheet.compositionEmpty')
          : t('worksheet.composition', { list: terms.join('; ') })}
      </p>
      {doubleCount && <div className="alert alert-info">{t('worksheet.doubleCountWarning')}</div>}
    </>
  )
}

let draftKey = 0

const newWork = (): WorkDraft => ({ key: `work-${++draftKey}`, id: null, name: '', scoreCount: 0 })
const newCategory = (): CategoryDraft => ({
  key: `cat-${++draftKey}`, id: null, name: '', weight: '', includeInAverage: true, works: [newWork()],
})

interface ScoreRemovalDetails {
  categories: { id: string; name: string; scoreCount: number }[]
  works: { id: string; name: string; categoryName: string; scoreCount: number }[]
  scoreCount: number
}

function asItems(value: unknown): { id: string; name: string; scoreCount: number; categoryName?: string }[] {
  if (!Array.isArray(value)) return []
  return value.flatMap((item) => {
    if (!item || typeof item !== 'object') return []
    const rec = item as Record<string, unknown>
    if (typeof rec.name !== 'string') return []
    return [{
      id: typeof rec.id === 'string' ? rec.id : rec.name,
      name: rec.name,
      scoreCount: typeof rec.scoreCount === 'number' ? rec.scoreCount : 0,
      categoryName: typeof rec.categoryName === 'string' ? rec.categoryName : undefined,
    }]
  })
}

function parseScoreRemoval(details: unknown): ScoreRemovalDetails | null {
  if (!details || typeof details !== 'object') return null
  const rec = details as Record<string, unknown>
  const works = asItems(rec.works).map((w) => ({ ...w, categoryName: w.categoryName ?? '' }))
  const categories = asItems(rec.categories)
  if (categories.length === 0 && works.length === 0) return null
  return {
    categories,
    works,
    scoreCount: typeof rec.scoreCount === 'number' ? rec.scoreCount : works.reduce((sum, w) => sum + w.scoreCount, 0),
  }
}

function fallbackRemoval(settings: AssessmentSettings, draft: CategoryDraft[]): ScoreRemovalDetails {
  const keptWorks = new Set(draft.flatMap((c) => c.works.map((w) => w.id)).filter((id): id is string => !!id))
  const keptCats = new Set(draft.map((c) => c.id).filter((id): id is string => !!id))
  const manuals = manualsOf(settings)
  const works = manuals.flatMap((c) => c.works.filter((w) => !keptWorks.has(w.id))
    .map((w) => ({ id: w.id, name: w.name, categoryName: c.name, scoreCount: w.scoreCount })))
  return {
    categories: manuals.filter((c) => !keptCats.has(c.id))
      .map((c) => ({ id: c.id, name: c.name, scoreCount: c.works.reduce((sum, w) => sum + w.scoreCount, 0) })),
    works,
    scoreCount: works.reduce((sum, w) => sum + w.scoreCount, 0),
  }
}

function ElementsEditor({ settings, url, startWithNew, onClose, onSaved }: {
  settings: AssessmentSettings
  url: string
  startWithNew: boolean
  onClose: () => void
  onSaved: (next: AssessmentSettings) => Promise<void>
}) {
  const { t } = useTranslation()
  const [categories, setCategories] = useState<CategoryDraft[]>(() => startWithNew ? [newCategory()] : manualsOf(settings).map((c) => ({
    key: c.id, id: c.id, name: c.name, weight: String(c.weight), includeInAverage: c.includeInAverage,
    works: c.works.map((w) => ({ key: w.id, id: w.id, name: w.name, scoreCount: w.scoreCount })),
  })))
  const [mode, setMode] = useState<CalculationMode>(settings.calculationMode)
  const [includeAttendance, setIncludeAttendance] = useState(settings.includeAttendanceInAverage)
  const [attendanceWeight, setAttendanceWeight] = useState(toInput(settings.attendanceWeight))
  const [threshold, setThreshold] = useState(toInput(settings.attendanceThreshold))
  const [error, setError] = useState<ApiError | null>(null)
  const [busy, setBusy] = useState(false)
  const [submitted, setSubmitted] = useState(false)
  const { confirm, dialog } = useConfirm()
  const fieldError = useFieldError(error)

  const parsedWeights = categories.map((c) => parsePercent(c.weight, true))
  const parsedAttendance = parsePercent(attendanceWeight, true)
  const parsedThreshold = parsePercent(threshold, false)
  const names = categories.map((c) => c.name.trim().toLocaleLowerCase())
  const nameError = (i: number) => {
    if (names[i] === '') return 'required'
    if (categories[i].name.trim().length > 100) return 'max_length'
    return names.indexOf(names[i]) < i ? 'duplicate' : null
  }
  const workNameError = (ci: number, wi: number) => {
    const work = categories[ci].works[wi]
    const value = work.name.trim().toLocaleLowerCase()
    if (value === '') return 'required'
    if (work.name.trim().length > 100) return 'max_length'
    return categories[ci].works.findIndex((w) => w.name.trim().toLocaleLowerCase() === value) < wi ? 'duplicate' : null
  }
  const cents = parsedWeights.reduce((sum, p) => sum + (p.ok && p.value !== null ? Math.round(p.value * 100) : 0), 0)
    + (parsedAttendance.ok && parsedAttendance.value !== null ? Math.round(parsedAttendance.value * 100) : 0)
  const total = cents / 100
  const invalid = parsedWeights.some((p) => !p.ok) || !parsedAttendance.ok || !parsedThreshold.ok
    || categories.some((_, i) => nameError(i) !== null)
    || categories.some((c, i) => c.works.some((_, w) => workNameError(i, w) !== null))

  const updateCategory = (key: string, patch: Partial<CategoryDraft>) =>
    setCategories(categories.map((c) => (c.key === key ? { ...c, ...patch } : c)))
  const updateWork = (categoryKey: string, workKey: string, patch: Partial<WorkDraft>) =>
    setCategories(categories.map((c) => c.key !== categoryKey ? c : {
      ...c, works: c.works.map((w) => (w.key === workKey ? { ...w, ...patch } : w)),
    }))

  const submit = async (e: FormEvent) => {
    e.preventDefault()
    setSubmitted(true)
    if (invalid) return
    const body = {
      attendanceThreshold: parsedThreshold.ok ? parsedThreshold.value : null,
      attendanceWeight: (parsedAttendance as { value: number }).value,
      calculationMode: mode,
      includeAttendanceInAverage: includeAttendance,
      categories: categories.map((c, i) => ({
        id: c.id, name: c.name.trim(), weight: (parsedWeights[i] as { value: number }).value,
        includeInAverage: c.includeInAverage,
        works: c.works.map((w) => ({ id: w.id, name: w.name.trim() })),
      })),
    }
    const persist = (confirmScoreRemoval: boolean) =>
      api.put<AssessmentSettings>(url, { ...body, confirmScoreRemoval })
    setBusy(true)
    setError(null)
    try {
      await onSaved(await persist(false))
    } catch (err) {
      const apiErr = toApiError(err)
      if (apiErr.code !== 'scores_removal_not_confirmed') {
        setError(apiErr)
        return
      }
      setBusy(false)
      const preview = parseScoreRemoval(apiErr.details) ?? fallbackRemoval(settings, categories)
      const ok = await confirm({
        title: t('worksheet.confirmRemoveTitle'),
        message: t('worksheet.confirmRemove'),
        confirmLabel: t('worksheet.confirmRemoveAction'),
        danger: true,
        details: [
          ...preview.categories.map((c) => t('worksheet.confirmRemoveCategory', { name: c.name, count: c.scoreCount })),
          ...preview.works.map((w) => t('worksheet.confirmRemoveWork', {
            name: w.name, category: w.categoryName, count: w.scoreCount,
          })),
          t('worksheet.confirmRemoveTotal', { count: preview.scoreCount }),
        ],
      })
      if (!ok) return
      setBusy(true)
      try {
        await onSaved(await persist(true))
      } catch (retry) {
        setError(toApiError(retry))
      }
    } finally {
      setBusy(false)
    }
  }

  const message = (local: string | null, server: string | undefined) =>
    (submitted && local ? t(`validation.${local}`) : undefined) ?? server

  return (
    <Modal title={t('worksheet.disciplineSettings')} onClose={onClose} wide>
      <form onSubmit={submit} noValidate>
        <ErrorBanner error={error} />
        <p className="muted">{t('worksheet.disciplineSettingsHint', { discipline: settings.disciplineName })}</p>
        <fieldset className="worksheet-mode">
          <legend>{t('worksheet.calculationMode')}</legend>
          <p className="muted">{t('worksheet.modeAppliesHint')}</p>
          <label className="radio">
            <input type="radio" name="ws-mode" checked={mode === 'Weighted'} disabled={busy}
              onChange={() => setMode('Weighted')} />
            {t('worksheet.modeWeighted')}
          </label>
          <label className="radio">
            <input type="radio" name="ws-mode" checked={mode === 'Average'} disabled={busy}
              onChange={() => setMode('Average')} />
            {t('worksheet.modeAverage')}
          </label>
          <p className="muted">{t(mode === 'Average' ? 'worksheet.modeAverageHint' : 'worksheet.modeWeightedHint')}</p>
        </fieldset>
        {mode === 'Average' && (
          <Composition
            terms={[
              ...categories.filter((c) => c.includeInAverage).flatMap((c) => c.works.map((w) => w.name.trim()).filter(Boolean)),
              ...(includeAttendance ? [t('worksheet.attendanceAuto')] : []),
            ]}
            includeAttendance={includeAttendance}
            manuals={categories}
          />
        )}
        {categories.length === 0 && <p>{t('worksheet.noElements')}</p>}
        <ul className="worksheet-elements">
          {categories.map((cat, i) => {
            const weight = parsedWeights[i]
            const existing = manualsOf(settings).find((c) => c.id === cat.id)
            return (
              <li key={cat.key} className="worksheet-category">
                <div className="worksheet-element">
                  <Field label={t('worksheet.elementName')} required
                    hint={looksLikeAttendanceName(cat.name) ? t('worksheet.attendanceNameWarning') : undefined}
                    error={message(nameError(i), fieldError(`categories[${i}].name`) ?? fieldError(`categories[${i}].id`))}>
                    {(id, describedBy) => <input id={id} value={cat.name} maxLength={100} aria-describedby={describedBy}
                      onChange={(e) => updateCategory(cat.key, { name: e.target.value })} disabled={busy} />}
                  </Field>
                  <Field label={t('worksheet.weight')} required
                    error={message(weight.ok ? null : weight.error, fieldError(`categories[${i}].weight`))}>
                    {(id, describedBy) => <input id={id} className="ws-input" inputMode="decimal" value={cat.weight}
                      aria-describedby={describedBy} onChange={(e) => updateCategory(cat.key, { weight: e.target.value })} disabled={busy} />}
                  </Field>
                  <button type="button" className="btn btn-small btn-danger" disabled={busy}
                    aria-label={t('worksheet.removeElement', { name: categoryLabel(t, existing ?? { kind: 'Manual', name: cat.name }) })}
                    onClick={() => setCategories(categories.filter((x) => x.key !== cat.key))}>×</button>
                </div>
                {mode === 'Average' && (
                  <label className="checkbox worksheet-include">
                    <input type="checkbox" checked={cat.includeInAverage} disabled={busy}
                      onChange={(e) => updateCategory(cat.key, { includeInAverage: e.target.checked })} />
                    {t('worksheet.includeInAverage')}
                  </label>
                )}
                <ul className="worksheet-works">
                  {cat.works.map((work, wi) => (
                    <li key={work.key} className="worksheet-work">
                      <Field label={t('worksheet.workName')} required
                        error={message(workNameError(i, wi), fieldError(`categories[${i}].works[${wi}].name`))}>
                        {(id, describedBy) => <input id={id} value={work.name} maxLength={100} aria-describedby={describedBy}
                          onChange={(e) => updateWork(cat.key, work.key, { name: e.target.value })} disabled={busy} />}
                      </Field>
                      <button type="button" className="btn btn-small" disabled={busy || cat.works.length === 1}
                        aria-label={t('worksheet.removeWork', { name: work.name || String(wi + 1) })}
                        onClick={() => updateCategory(cat.key, { works: cat.works.filter((x) => x.key !== work.key) })}>×</button>
                    </li>
                  ))}
                </ul>
                <button type="button" className="btn btn-small" disabled={busy}
                  onClick={() => updateCategory(cat.key, { works: [...cat.works, newWork()] })}>
                  {t('worksheet.addWork')}
                </button>
              </li>
            )
          })}
        </ul>
        <button type="button" className="btn btn-small" disabled={busy}
          onClick={() => setCategories([...categories, newCategory()])}>
          {t('worksheet.addElement')}
        </button>
        <div className="worksheet-attendance-settings">
          <h3>{t('worksheet.attendanceBlock')}</h3>
          <p className="muted">{t('worksheet.attendanceBlockHint')}</p>
          {mode === 'Average' && (
            <label className="checkbox worksheet-include">
              <input type="checkbox" checked={includeAttendance} disabled={busy}
                onChange={(e) => setIncludeAttendance(e.target.checked)} />
              {t('worksheet.includeAttendanceInAverage')}
            </label>
          )}
          {mode === 'Average' && <p className="muted">{t('worksheet.includeAttendanceHint')}</p>}
          <Field label={t('worksheet.attendanceWeight')} hint={t('worksheet.attendanceWeightHint')}
            error={message(parsedAttendance.ok ? null : parsedAttendance.error, fieldError('attendanceWeight'))}>
            {(id, describedBy) => (
              <input id={id} className="ws-input" inputMode="decimal" value={attendanceWeight} aria-describedby={describedBy}
                onChange={(e) => setAttendanceWeight(e.target.value)} disabled={busy} />
            )}
          </Field>
          <Field label={t('worksheet.threshold')} hint={t('worksheet.thresholdHint')}
            error={message(parsedThreshold.ok ? null : parsedThreshold.error, fieldError('attendanceThreshold'))}>
            {(id, describedBy) => <input id={id} className="ws-input" inputMode="decimal" value={threshold}
              aria-describedby={describedBy} onChange={(e) => setThreshold(e.target.value)} disabled={busy} />}
          </Field>
        </div>
        {mode === 'Weighted' && (
          <WeightStatus total={total} complete={cents === 10000 && (categories.length > 0 || (parsedAttendance.ok && parsedAttendance.value === 100))} />
        )}
        {mode === 'Average' && (
          <p className="muted">{t('worksheet.weightTotal', { total: formatNumber(total) })}.</p>
        )}
        <div className="form-actions">
          <button type="button" className="btn" onClick={onClose} disabled={busy}>{t('common.cancel')}</button>
          <button type="submit" className="btn btn-primary" disabled={busy}>{t('common.save')}</button>
        </div>
      </form>
      {dialog}
    </Modal>
  )
}

interface RowDraft { scores: Record<string, string>; attended: string; manual: string }

function draftOf(sheet: WorkSheet, row: WorkSheetRow): RowDraft {
  return {
    scores: Object.fromEntries(worksOf(sheet).map((e) => [e.id, toInput(row.scores[e.id])])),
    attended: toInput(row.attendedLessons),
    manual: toInput(row.manualFinal),
  }
}

const draftsOf = (sheet: WorkSheet) => Object.fromEntries(sheet.rows.map((r) => [r.studentId, draftOf(sheet, r)]))

/**
 * Total lessons and the rows are one form with one save. Attendance is checked against the entered total, and
 * unsaved input survives element changes made from the discipline panel meanwhile.
 */
function SheetEditor({ sheet, url, onChange }: {
  sheet: WorkSheet
  url: string
  onChange: (next: WorkSheet) => void
}) {
  const { t } = useTranslation()
  const [drafts, setDrafts] = useState(() => draftsOf(sheet))
  const [totalDraft, setTotalDraft] = useState(() => toInput(sheet.totalLessons))
  const [error, setError] = useState<ApiError | null>(null)
  const [sent, setSent] = useState<string[]>([])
  const [notice, setNotice] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)
  const [transferOpen, setTransferOpen] = useState(false)
  const fieldError = useFieldError(error)
  const readOnly = sheet.groupStatus === 'Archived'

  const total = parseWhole(totalDraft, 1000)
  const totalDirty = !total.ok || total.value !== sheet.totalLessons
  const totalLessons = total.ok ? total.value : sheet.totalLessons

  const rows = sheet.rows.map((row) => {
    const draft = drafts[row.studentId] ?? draftOf(sheet, row)
    const scores = worksOf(sheet).map((e) => {
      const raw = draft.scores[e.id] ?? toInput(row.scores[e.id])
      return { element: e, raw, parsed: parseScore(raw) }
    })
    const attended = parseWhole(draft.attended, totalLessons, 'attended_exceeds_total')
    const attendedCheck: Parsed = attended.ok && attended.value !== null && totalLessons === null
      ? { ok: false, error: 'total_lessons_required' } : attended
    const manual = parseWhole(draft.manual, 10)
    const dirty = row.inGroup && (
      scores.some((s) => !s.parsed.ok || s.parsed.value !== (row.scores[s.element.id] ?? null))
      || !attendedCheck.ok || attendedCheck.value !== row.attendedLessons
      || !manual.ok || manual.value !== row.manualFinal)
    const invalid = scores.some((s) => !s.parsed.ok) || !attendedCheck.ok || !manual.ok
    return { row, draft, scores, attended: attendedCheck, manual, dirty, invalid }
  })
  const changes = rows.filter((r) => r.dirty)
  const unsaved = changes.length + (totalDirty ? 1 : 0)
  // Rows of students who left the group count too: a smaller total must still cover their attendance.
  const invalid = !total.ok || rows.some((r) => r.invalid)

  const setDraft = (studentId: string, current: RowDraft, patch: Partial<RowDraft>) =>
    setDrafts({ ...drafts, [studentId]: { ...current, ...patch } })

  const reset = (next: WorkSheet, message: string) => {
    onChange(next)
    setDrafts(draftsOf(next))
    setTotalDraft(toInput(next.totalLessons))
    setSent([])
    setNotice(message)
  }

  const save = async (e: FormEvent) => {
    e.preventDefault()
    setNotice(null)
    if (invalid) return
    if (unsaved === 0) {
      setNotice(t('worksheet.noChanges'))
      return
    }
    const payload = changes.map((r) => ({
      studentId: r.row.studentId,
      scores: Object.fromEntries(r.scores.map((s) => [s.element.id, s.parsed.ok ? s.parsed.value : null])),
      attendedLessons: r.attended.ok ? r.attended.value : null,
      manualFinal: r.manual.ok ? r.manual.value : null,
    }))
    const newTotal = total.ok ? total.value : null
    const saveTotal = () => api.put<WorkSheet>(`${url}/settings`, { totalLessons: newTotal })
    const saveRows = () => api.put<WorkSheet>(`${url}/rows`, { rows: payload })
    // A larger total has to be saved before the attendance it allows; a smaller one after the attendance is lowered.
    const totalFirst = totalDirty && newTotal !== null && (sheet.totalLessons === null || newTotal > sheet.totalLessons)
    const steps = [
      totalFirst ? saveTotal : null,
      payload.length > 0 ? saveRows : null,
      totalDirty && !totalFirst ? saveTotal : null,
    ].filter((s) => s !== null)
    setSent(payload.map((x) => x.studentId))
    setBusy(true)
    setError(null)
    try {
      let next = sheet
      for (const step of steps) {
        next = await step()
        onChange(next)
      }
      reset(next, t('worksheet.saved'))
    } catch (err) {
      setError(toApiError(err))
    } finally {
      setBusy(false)
    }
  }

  const serverError = (studentId: string, field: string) => {
    const index = sent.indexOf(studentId)
    return index >= 0 ? fieldError(`rows[${index}].${field}`) : undefined
  }
  const localError = (p: Parsed) => (p.ok ? undefined : t(`validation.${p.error}`))
  const totalError = localError(total) ?? fieldError('totalLessons')

  const transferDisabled = readOnly || busy || unsaved > 0 || !sheet.readyForTransfer
  const average = sheet.calculationMode === 'Average'

  return (
    <section aria-labelledby="ws-sheet-title">
      <h2 id="ws-sheet-title">
        {t('worksheet.sheetTitle', { group: sheet.groupName, discipline: sheet.disciplineName, period: sheet.periodName })}
      </h2>
      <p className="muted">{sheet.id === null ? t('worksheet.sheetNew') : t('worksheet.periodFixed')}</p>
      {sheet.rows.length === 0 ? <Empty>{t('worksheet.noStudents')}</Empty> : (
        <form onSubmit={save} noValidate>
          {readOnly && <div className="alert alert-info">{t('worksheet.archived')}</div>}
          <ErrorBanner error={error} />
          {notice && <div className="alert alert-success" role="status">{notice}</div>}
          <Field label={t('worksheet.totalLessons')} hint={t('worksheet.totalHint')} error={totalError}>
            {(id, describedBy) => (
              <div className="ws-inline">
                <input id={id} className="ws-input" inputMode="numeric" value={totalDraft} aria-describedby={describedBy}
                  aria-invalid={totalError ? true : undefined} onChange={(e) => setTotalDraft(e.target.value)}
                  disabled={readOnly || busy} />
                {totalDirty && <Badge tone="gray">{t('worksheet.unsavedBadge')}</Badge>}
              </div>
            )}
          </Field>
          <p className="muted">{average ? t('worksheet.scoreHintAverage') : t('worksheet.scoreHint')}</p>
          {average && (
            <Composition
              terms={sheet.averageTerms}
              includeAttendance={sheet.includeAttendanceInAverage}
              manuals={sheet.categories.filter((c) => c.kind === 'Manual')}
            />
          )}
          <div className="table-wrap">
            <table className="worksheet-table">
              <thead>
                <tr>
                  <th rowSpan={2}>{t('worksheet.student')}</th>
                  {sheet.categories.filter((c) => c.kind !== 'Attendance').map((c) => (
                    <th key={c.id} colSpan={c.works.length + 1}>
                      {categoryLabel(t, c)}{' '}
                      <small className="muted">
                        {formatNumber(c.weight)}%
                        {average && !c.includeInAverage ? ` · ${t('worksheet.excludedFromAverage')}` : ''}
                      </small>
                    </th>
                  ))}
                  <th colSpan={average ? 3 : 4}>
                    {t('worksheet.attendanceAuto')}{' '}
                    <small className="muted">
                      {average
                        ? (sheet.includeAttendanceInAverage ? t('worksheet.includeInAverage') : t('worksheet.excludedFromAverage'))
                        : `${formatNumber(sheet.attendanceWeight)}%`}
                    </small>
                  </th>
                  <th rowSpan={2}>{t('worksheet.calculated')}</th>
                  <th rowSpan={2}>{t('worksheet.final')}</th>
                </tr>
                <tr>
                  {sheet.categories.filter((c) => c.kind !== 'Attendance').flatMap((c) => [
                    ...c.works.map((w) => <th key={w.id}>{w.name}</th>),
                    <th key={`${c.id}-avg`}>{t('worksheet.categoryAverage')}</th>,
                  ])}
                  <th>{t('worksheet.attended')}</th>
                  <th>{t('worksheet.attendanceScore')}</th>
                  {!average && <th>{t('worksheet.attendanceContribution')}</th>}
                  <th>{t('worksheet.attendance')}</th>
                </tr>
              </thead>
              <tbody>
                {rows.map(({ row, draft, scores, attended, manual, dirty }) => {
                  const locked = readOnly || busy || !row.inGroup
                  const attendedError = localError(attended) ?? serverError(row.studentId, 'attendedLessons')
                  const manualError = localError(manual) ?? serverError(row.studentId, 'manualFinal')
                  const pending = totalDirty || (row.inGroup && attended.ok && attended.value !== row.attendedLessons)
                  return (
                    <tr key={row.studentId} className={row.inGroup ? undefined : 'row-moved'}>
                      <td data-label={t('worksheet.student')}>
                        {row.studentName}
                        {dirty && <span className="block"><Badge tone="gray">{t('worksheet.unsavedBadge')}</Badge></span>}
                        {!row.inGroup && (
                          <small className="block">
                            <Badge tone="gray">{t('worksheet.movedOut')}</Badge>
                            {row.currentGroupName && <> <span className="muted">{t('worksheet.movedTo', { group: row.currentGroupName })}</span></>}
                          </small>
                        )}
                      </td>
                      {sheet.categories.filter((c) => c.kind !== 'Attendance').flatMap((c) => {
                        const catLabel = categoryLabel(t, c)
                        const workCells = c.works.map((work) => {
                          const cell = scores.find((s) => s.element.id === work.id)!
                          const message = localError(cell.parsed) ?? serverError(row.studentId, `scores.${work.id}`)
                          const errorId = message ? `ws-score-${row.studentId}-${work.id}` : undefined
                          return (
                            <td key={work.id} data-label={`${catLabel} · ${work.name}`}>
                              <input className="ws-input" inputMode="decimal" value={cell.raw} disabled={locked}
                                aria-label={t('worksheet.scoreOf', { element: work.name, student: row.studentName })}
                                aria-invalid={message ? true : undefined} aria-describedby={errorId}
                                onChange={(e) => setDraft(row.studentId, draft, { scores: { ...draft.scores, [work.id]: e.target.value } })} />
                              {message && <small id={errorId} className="field-error block">{message}</small>}
                            </td>
                          )
                        })
                        const parsedWorks = c.works.map((work) => scores.find((s) => s.element.id === work.id)!.parsed)
                        const avg = parsedWorks.length > 0 && parsedWorks.every((p) => p.ok && p.value !== null)
                          ? parsedWorks.reduce((sum, p) => sum + (p as { ok: true; value: number }).value, 0) / parsedWorks.length
                          : null
                        return [...workCells, (
                          <td key={`${c.id}-avg`} data-label={`${catLabel} · ${t('worksheet.categoryAverage')}`} className="muted">
                            {avg === null ? '—' : formatNumber(avg)}
                          </td>
                        )]
                      })}
                      <td data-label={`${t('worksheet.attendanceAuto')} · ${t('worksheet.attended')}`} className="ws-nowrap">
                        <input className="ws-input" inputMode="numeric" value={draft.attended} disabled={locked}
                          aria-label={t('worksheet.attendedOf', { student: row.studentName })}
                          aria-invalid={attendedError ? true : undefined}
                          aria-describedby={attendedError ? `ws-att-${row.studentId}` : undefined}
                          onChange={(e) => setDraft(row.studentId, draft, { attended: e.target.value })} />
                        {totalLessons !== null && <span className="muted"> / {totalLessons}</span>}
                        {attendedError && <small id={`ws-att-${row.studentId}`} className="field-error block">{attendedError}</small>}
                      </td>
                      <td data-label={`${t('worksheet.attendanceAuto')} · ${t('worksheet.attendanceScore')}`} className="muted">
                        {pending ? t('worksheet.afterSave') : row.attendanceScore === null ? '—' : formatNumber(row.attendanceScore)}
                      </td>
                      {!average && (
                        <td data-label={`${t('worksheet.attendanceAuto')} · ${t('worksheet.attendanceContribution')}`} className="muted">
                          {pending ? t('worksheet.afterSave') : row.attendanceContribution === null ? '—' : formatNumber(row.attendanceContribution)}
                        </td>
                      )}
                      <td data-label={`${t('worksheet.attendanceAuto')} · ${t('worksheet.attendance')}`}><AttendanceCell sheet={sheet} row={row} pending={pending} /></td>
                      <td data-label={t('worksheet.calculated')} className="ws-calc"><CalculationCell row={row} mode={sheet.calculationMode} /></td>
                      <td data-label={t('worksheet.final')}>
                        <div className="worksheet-final">
                          <strong className="worksheet-final-value">{row.final ?? '—'}</strong>
                          {row.manualFinal !== null && <Badge tone="gray">{t('worksheet.manualBadge')}</Badge>}
                        </div>
                        <div className="worksheet-manual">
                          <input className="ws-input" inputMode="numeric" value={draft.manual} disabled={locked}
                            placeholder="0–10"
                            aria-label={t('worksheet.manualOf', { student: row.studentName })}
                            aria-invalid={manualError ? true : undefined}
                            aria-describedby={manualError ? `ws-man-${row.studentId}` : undefined}
                            onChange={(e) => setDraft(row.studentId, draft, { manual: e.target.value })} />
                          {draft.manual.trim() !== '' && !locked && (
                            <button type="button" className="btn btn-small"
                              aria-label={t('worksheet.clearManualOf', { student: row.studentName })}
                              onClick={() => setDraft(row.studentId, draft, { manual: '' })}>{t('worksheet.clearManual')}</button>
                          )}
                        </div>
                        {manualError && <small id={`ws-man-${row.studentId}`} className="field-error block">{manualError}</small>}
                        {row.transferredFinal !== null && <span className="muted block">{t('worksheet.transferred', { value: row.transferredFinal })}</span>}
                        {row.grade && (
                          <span className="muted block">
                            {t('worksheet.inGradebook', { value: row.grade.value })} · {t(`gradeStatus.${row.grade.status}`)}
                          </span>
                        )}
                      </td>
                    </tr>
                  )
                })}
              </tbody>
            </table>
          </div>
          <p className="muted">{t('worksheet.manualHint')} {t(average ? 'worksheet.attendanceNoteAverage' : 'worksheet.attendanceNote')}</p>
          {sheet.rows.some((r) => !r.inGroup) && <p className="muted">{t('worksheet.movedHint')}</p>}
          {!readOnly && (
            <div className="form-actions">
              {unsaved > 0 && <span className="muted">{t('worksheet.unsaved', { count: unsaved })}</span>}
              <button type="button" className="btn" disabled={transferDisabled} onClick={() => setTransferOpen(true)}>
                {t('worksheet.transfer')}
              </button>
              <button type="submit" className="btn btn-primary" disabled={busy || invalid}>{t('common.save')}</button>
            </div>
          )}
          {transferOpen && (
            <TransferDialog url={url} onClose={() => setTransferOpen(false)}
              onDone={async (result) => {
                setTransferOpen(false)
                reset(await api.get<WorkSheet>(url), t('worksheet.transferDone', {
                  created: result.created, updated: result.updated, unchanged: result.unchanged,
                }))
              }} />
          )}
        </form>
      )}
    </section>
  )
}

function AttendanceCell({ sheet, row, pending }: { sheet: WorkSheet; row: WorkSheetRow; pending: boolean }) {
  const { t } = useTranslation()
  const tone = row.attendanceStatus === 'Met' ? 'green' : row.attendanceStatus === 'NotMet' ? 'red' : 'gray'
  const facts = row.attendedLessons !== null && sheet.totalLessons !== null && row.attendancePercent !== null
    ? t('worksheet.attendanceOf', {
      attended: row.attendedLessons, total: sheet.totalLessons, percent: formatNumber(row.attendancePercent),
    })
    : null
  const reason = sheet.totalLessons === null ? t('worksheet.noTotal')
    : sheet.attendanceThreshold === null ? t('worksheet.noThreshold') : null
  if (pending) return <div className="worksheet-attendance muted">{t('worksheet.afterSave')}</div>
  return (
    <div className="worksheet-attendance">
      {facts && <span>{facts} · </span>}
      <Badge tone={tone}>{t(`worksheet.attendanceStatus.${row.attendanceStatus}`)}</Badge>
      <span className="muted block">
        {sheet.attendanceThreshold !== null
          ? t('worksheet.requirement', { threshold: formatNumber(sheet.attendanceThreshold) })
          : reason}
      </span>
    </div>
  )
}

function CalculationCell({ row, mode }: { row: WorkSheetRow; mode: CalculationMode }) {
  const { t } = useTranslation()
  if (row.calculationState === 'Calculated' && row.calculatedRaw !== null) {
    return <span>{t('worksheet.calculatedValue', { raw: formatNumber(row.calculatedRaw), rounded: row.calculatedRounded })}</span>
  }
  if (row.calculationState === 'MissingScores') return <span className="muted">{t('worksheet.missing', { count: row.missingCount })}</span>
  if (row.calculationState === 'WeightsIncomplete') return <span className="muted">{t('worksheet.stateWeightsIncomplete')}</span>
  return <span className="muted">{t(mode === 'Average' ? 'worksheet.stateNoData' : 'worksheet.stateNoElements')}</span>
}

function TransferDialog({ url, onClose, onDone }: {
  url: string
  onClose: () => void
  onDone: (result: TransferResult) => Promise<void>
}) {
  const { t } = useTranslation()
  const preview = useLoad(() => api.get<TransferPreview>(`${url}/transfer`), `${url}/transfer`)
  const [confirmed, setConfirmed] = useState(false)
  const [error, setError] = useState<ApiError | null>(null)
  const [busy, setBusy] = useState(false)
  const data = preview.data
  const items = data?.items ?? []
  const changed = items.filter((i) => i.change === 'Changed')
  const toWrite = items.filter((i) => i.change !== 'Unchanged')

  const submit = async () => {
    if (!data) return
    setBusy(true)
    setError(null)
    try {
      const result = await api.post<TransferResult>(`${url}/transfer`, {
        entries: items.map((i) => ({ studentId: i.studentId, value: i.value })),
        confirmChanges: changed.length > 0 && confirmed,
      })
      await onDone(result)
    } catch (err) {
      setError(toApiError(err))
      setBusy(false)
    }
  }

  return (
    <Modal title={t('worksheet.transferTitle')} onClose={onClose} wide>
      <ErrorBanner error={preview.error ?? error} />
      {!data ? (!preview.error && <Loading />) : (
        <>
          <p>{t('worksheet.transferIntro', { period: data.periodName, factor: data.factor })}</p>
          <h3>{t('worksheet.transferReady')} ({items.length})</h3>
          {items.length === 0 ? <p className="muted">{t('worksheet.transferNone')}</p> : (
            <div className="table-wrap">
              <table>
                <thead>
                  <tr>
                    <th>{t('worksheet.student')}</th>
                    <th>{t('worksheet.final')}</th>
                    <th>{t('worksheet.currentValue')}</th>
                    <th>{t('worksheet.newValue')}</th>
                    <th>{t('common.status')}</th>
                  </tr>
                </thead>
                <tbody>
                  {items.map((i) => (
                    <tr key={i.studentId}>
                      <td data-label={t('worksheet.student')}>{i.studentName}</td>
                      <td data-label={t('worksheet.final')}>
                        {i.final} {i.manual && <Badge tone="gray">{t('worksheet.manualBadge')}</Badge>}
                      </td>
                      <td data-label={t('worksheet.currentValue')}>
                        {i.currentValue ?? '—'}
                        {i.currentStatus && <small className="muted"> · {t(`gradeStatus.${i.currentStatus}`)}</small>}
                      </td>
                      <td data-label={t('worksheet.newValue')}><strong>{i.value}</strong></td>
                      <td data-label={t('common.status')}>
                        {i.change === 'Changed'
                          ? <strong>{t(`worksheet.change.${i.change}`)}</strong>
                          : t(`worksheet.change.${i.change}`)}
                      </td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
          )}
          {data.skipped.length > 0 && (
            <>
              <h3>{t('worksheet.transferSkipped')} ({data.skipped.length})</h3>
              <ul className="confirm-details">
                {data.skipped.map((s) => (
                  <li key={s.studentId}>{s.studentName}: {t(`worksheet.skipReason.${s.reason}`, { count: s.missingCount })}</li>
                ))}
              </ul>
            </>
          )}
          {changed.length > 0 && (
            <label className="checkbox">
              <input type="checkbox" checked={confirmed} onChange={(e) => setConfirmed(e.target.checked)} />{' '}
              {t('worksheet.confirmChanges', { count: changed.length })}
            </label>
          )}
          <div className="form-actions">
            <button type="button" className="btn" onClick={onClose} disabled={busy}>{t('common.cancel')}</button>
            <button type="button" className="btn btn-primary" onClick={submit}
              disabled={busy || items.length === 0 || (changed.length > 0 && !confirmed)}>
              {t('worksheet.doTransfer', { count: toWrite.length })}
            </button>
          </div>
        </>
      )}
    </Modal>
  )
}
