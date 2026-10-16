import { expect, type Page } from '@playwright/test'

const crcTable = Uint32Array.from({ length: 256 }, (_, n) => {
  let c = n
  for (let k = 0; k < 8; k++) c = c & 1 ? 0xedb88320 ^ (c >>> 1) : c >>> 1
  return c >>> 0
})

function crc32(buf: Buffer) {
  let c = 0xffffffff
  for (const b of buf) c = crcTable[(c ^ b) & 0xff] ^ (c >>> 8)
  return (c ^ 0xffffffff) >>> 0
}

function xmlEscape(value: string) {
  return value.replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;')
}

function zipStore(files: { name: string; data: Buffer }[]) {
  const local: Buffer[] = []
  const central: Buffer[] = []
  let offset = 0
  for (const file of files) {
    const name = Buffer.from(file.name, 'utf8')
    const crc = crc32(file.data)
    const localHeader = Buffer.alloc(30 + name.length)
    localHeader.writeUInt32LE(0x04034b50, 0)
    localHeader.writeUInt16LE(20, 4)
    localHeader.writeUInt16LE(0, 6)
    localHeader.writeUInt16LE(0, 8)
    localHeader.writeUInt16LE(0, 10)
    localHeader.writeUInt16LE(0, 12)
    localHeader.writeUInt32LE(crc, 14)
    localHeader.writeUInt32LE(file.data.length, 18)
    localHeader.writeUInt32LE(file.data.length, 22)
    localHeader.writeUInt16LE(name.length, 26)
    localHeader.writeUInt16LE(0, 28)
    name.copy(localHeader, 30)
    local.push(localHeader, file.data)
    const centralHeader = Buffer.alloc(46 + name.length)
    centralHeader.writeUInt32LE(0x02014b50, 0)
    centralHeader.writeUInt16LE(20, 4)
    centralHeader.writeUInt16LE(20, 6)
    centralHeader.writeUInt16LE(0, 8)
    centralHeader.writeUInt16LE(0, 10)
    centralHeader.writeUInt16LE(0, 12)
    centralHeader.writeUInt16LE(0, 14)
    centralHeader.writeUInt32LE(crc, 16)
    centralHeader.writeUInt32LE(file.data.length, 20)
    centralHeader.writeUInt32LE(file.data.length, 24)
    centralHeader.writeUInt16LE(name.length, 28)
    centralHeader.writeUInt16LE(0, 30)
    centralHeader.writeUInt16LE(0, 32)
    centralHeader.writeUInt16LE(0, 34)
    centralHeader.writeUInt16LE(0, 36)
    centralHeader.writeUInt32LE(0, 38)
    centralHeader.writeUInt32LE(offset, 42)
    name.copy(centralHeader, 46)
    central.push(centralHeader)
    offset += localHeader.length + file.data.length
  }
  const centralBuf = Buffer.concat(central)
  const end = Buffer.alloc(22)
  end.writeUInt32LE(0x06054b50, 0)
  end.writeUInt16LE(0, 4)
  end.writeUInt16LE(0, 6)
  end.writeUInt16LE(files.length, 8)
  end.writeUInt16LE(files.length, 10)
  end.writeUInt32LE(centralBuf.length, 12)
  end.writeUInt32LE(offset, 16)
  end.writeUInt16LE(0, 20)
  return Buffer.concat([...local, centralBuf, end])
}

/** Minimal .xlsx for groups import. Cyrillic headers match the product template. */
export function groupsWorkbookBytes(name: string) {
  const sheet = `<?xml version="1.0" encoding="UTF-8" standalone="yes"?>
<worksheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main">
  <sheetData>
    <row r="1">
      <c r="A1" t="inlineStr"><is><t>Название</t></is></c>
      <c r="B1" t="inlineStr"><is><t>Дата начала</t></is></c>
      <c r="C1" t="inlineStr"><is><t>Дата окончания</t></is></c>
    </row>
    <row r="2">
      <c r="A2" t="inlineStr"><is><t>${xmlEscape(name)}</t></is></c>
    </row>
  </sheetData>
</worksheet>`
  const utf8 = (s: string) => Buffer.from(s, 'utf8')
  return zipStore([
    { name: '[Content_Types].xml', data: utf8(`<?xml version="1.0" encoding="UTF-8" standalone="yes"?>
<Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types">
  <Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/>
  <Default Extension="xml" ContentType="application/xml"/>
  <Override PartName="/xl/workbook.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml"/>
  <Override PartName="/xl/worksheets/sheet1.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml"/>
</Types>`) },
    { name: '_rels/.rels', data: utf8(`<?xml version="1.0" encoding="UTF-8" standalone="yes"?>
<Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
  <Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="xl/workbook.xml"/>
</Relationships>`) },
    { name: 'xl/workbook.xml', data: utf8(`<?xml version="1.0" encoding="UTF-8" standalone="yes"?>
<workbook xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships">
  <sheets><sheet name="Groups" sheetId="1" r:id="rId1"/></sheets>
</workbook>`) },
    { name: 'xl/_rels/workbook.xml.rels', data: utf8(`<?xml version="1.0" encoding="UTF-8" standalone="yes"?>
<Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
  <Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet" Target="worksheets/sheet1.xml"/>
</Relationships>`) },
    { name: 'xl/worksheets/sheet1.xml', data: utf8(sheet) },
  ])
}

export const managerEmail = process.env.E2E_MANAGER_EMAIL ?? 'manager@mba.local'

/** Unique label for this process; tests must not rely on leftover rows from earlier runs. */
export function unique(prefix: string) {
  return `${prefix}-${Date.now()}-${Math.floor(Math.random() * 1e6)}`
}

export function managerPassword(): string {
  const value = process.env.E2E_MANAGER_PASSWORD
  if (!value) throw new Error('Set E2E_MANAGER_PASSWORD for the local test manager account.')
  return value
}

export async function loginAsManager(page: Page) {
  await page.goto('/login')
  if (page.url().includes('/manager/')) return
  await openLoginForm(page)
  await page.getByLabel('Email').fill(managerEmail)
  await page.getByLabel('Пароль').fill(managerPassword())
  await page.getByRole('button', { name: 'Войти' }).click()
  await expect(page).toHaveURL(/\/manager\//)
}

export async function loginAs(page: Page, email: string, password: string, url: RegExp) {
  await openLoginForm(page)
  await page.getByLabel('Email').fill(email)
  await page.getByLabel('Пароль').fill(password)
  await page.getByRole('button', { name: 'Войти' }).click()
  await expect(page).toHaveURL(url)
}

/** Isolated tests only: requires Test:StaffFixture. Never a Production bootstrap. */
export async function createDevStaff(page: Page, email: string, password: string, role: 'Owner' | 'AccountAdmin') {
  const csrf = await page.request.get('/api/auth/csrf')
  expect(csrf.ok(), 'csrf for staff fixture').toBeTruthy()
  const token = (await csrf.json() as { token: string }).token
  const res = await page.request.post('/api/dev/staff-fixture', {
    headers: { 'X-XSRF-TOKEN': token },
    data: { email, password, role },
  })
  expect(res.ok(), `staff fixture ${role} ${res.status()}`).toBeTruthy()
  return await res.json() as { id: string; email: string; role: string }
}

export async function loginAsStudent(page: Page, email: string, password = 'E2e-Student-1') {
  await openLoginForm(page)
  await page.getByLabel('Email').fill(email)
  await page.getByLabel('Пароль').fill(password)
  await page.getByRole('button', { name: 'Войти' }).click()
  await expect(page).toHaveURL(/\/student/)
}

/** End the current session so /login shows the form instead of redirecting to a cabinet. */
export async function openLoginForm(page: Page) {
  const csrf = await page.request.get('/api/auth/csrf')
  if (csrf.ok()) {
    const token = (await csrf.json() as { token: string }).token
    await page.request.post('/api/auth/logout', { headers: { 'X-XSRF-TOKEN': token } }).catch(() => undefined)
  }
  await page.context().clearCookies()
  await page.goto('/login')
  const email = page.getByLabel('Email')
  const logout = page.getByRole('button', { name: 'Выйти' })
  await expect(email.or(logout)).toBeVisible()
  if (await logout.isVisible()) {
    await logout.click()
    await expect(logout).toHaveCount(0)
  }
  await expect(page).toHaveURL(/\/login/)
  await expect(email).toBeVisible()
  await expect(page.getByLabel('Пароль')).toBeVisible()
}

export async function staffList(page: Page, search = '') {
  const res = await page.request.get(`/api/admin/staff?search=${encodeURIComponent(search)}&page=1&pageSize=200`)
  expect(res.ok(), `staff list ${res.status()}`).toBeTruthy()
  return await res.json() as { items: { id: string; email: string; roles: string[]; locked: boolean }[]; total: number }
}

/** Staff table has no search; leftover e2e rows can fill page 1. Uses existing pagination only. */
export async function staffRow(page: Page, email: string) {
  await expect(page.getByRole('table')).toBeVisible()
  const rowFor = () =>
    page.getByRole('row').filter({ has: page.getByRole('cell', { name: email, exact: true }) })
  const next = page.getByRole('navigation', { name: 'Страницы' }).getByRole('button', { name: 'Вперёд' })
  for (let i = 0; i < 40; i++) {
    const row = rowFor()
    if (await row.count()) {
      await expect(row.getByRole('cell', { name: email, exact: true })).toBeVisible()
      return row
    }
    if ((await next.count()) === 0 || !(await next.isEnabled())) break
    const firstEmail = page.locator('tbody tr td').first()
    const before = (await firstEmail.textContent()) ?? ''
    await next.click()
    await expect(firstEmail).not.toHaveText(before)
  }
  const row = rowFor()
  await expect(row.getByRole('cell', { name: email, exact: true })).toBeVisible()
  return row
}

export type SheetNames = {
  group: string
  discipline: string
  period: string
}

export async function createGroup(page: Page, name: string) {
  await page.goto('/manager/groups')
  await page.getByRole('button', { name: 'Новая группа' }).click()
  const dialog = page.getByRole('dialog', { name: 'Новая группа' })
  await dialog.getByLabel('Название').fill(name)
  await dialog.getByRole('button', { name: 'Сохранить' }).click()
  await expect(dialog).toBeHidden()
}

export async function createStudent(page: Page, opts: {
  last: string
  first: string
  email: string
  group: string
  password?: string
}) {
  await page.goto('/manager/students')
  await page.getByRole('button', { name: 'Новый студент' }).click()
  const dialog = page.getByRole('dialog', { name: 'Новый студент' })
  await dialog.getByLabel('Фамилия').fill(opts.last)
  await dialog.getByLabel('Имя').fill(opts.first)
  await dialog.getByLabel('Email').fill(opts.email)
  await dialog.getByLabel('Группа').selectOption({ label: opts.group })
  await dialog.getByLabel('Пароль для входа').fill(opts.password ?? 'E2e-Student-1')
  await dialog.getByRole('button', { name: 'Сохранить' }).click()
  await expect(dialog).toBeHidden()
}

export async function createDiscipline(page: Page, name: string) {
  await page.goto('/manager/disciplines')
  await page.getByRole('button', { name: 'Новая дисциплина' }).click()
  const dialog = page.getByRole('dialog', { name: 'Новая дисциплина' })
  await dialog.getByLabel('Название').fill(name)
  await dialog.getByRole('button', { name: 'Сохранить' }).click()
  await expect(dialog).toBeHidden()
}

export async function createPeriod(page: Page, name: string) {
  await page.goto('/manager/periods')
  await page.getByRole('button', { name: 'Новый период' }).click()
  const dialog = page.getByRole('dialog', { name: 'Новый период' })
  await dialog.getByLabel('Название').fill(name)
  await dialog.getByRole('button', { name: 'Сохранить' }).click()
  await expect(dialog).toBeHidden()
}

export async function selectPeriod(page: Page, name: string) {
  const period = page.getByLabel('Учебный период')
  const periodValue = await period.locator('option').filter({ hasText: name }).first().getAttribute('value')
  if (!periodValue) throw new Error(`Period «${name}» not found.`)
  await period.selectOption(periodValue)
}

export async function openSheet(page: Page, names: SheetNames) {
  await page.goto('/manager/worksheet')
  await page.getByLabel('Дисциплина').selectOption({ label: names.discipline })
  await page.getByLabel('Группа').selectOption({ label: names.group })
  await selectPeriod(page, names.period)
  await expect(page.getByRole('heading', { name: new RegExp(`Ведомость:.*${escapeRegExp(names.discipline)}`) })).toBeVisible()
}

export function settingsDialog(page: Page) {
  return page.getByRole('dialog', { name: 'Элементы контроля и посещаемость' })
}

export function confirmDialog(page: Page) {
  return page.getByRole('dialog', { name: 'Подтвердить удаление' })
}

export function isDisciplineSettingsPut(url: string) {
  return /\/api\/manager\/worksheets\/disciplines\/[^/]+\/settings/.test(url)
}

function escapeRegExp(value: string) {
  return value.replace(/[.*+?^${}()|[\]\\]/g, '\\$&')
}
