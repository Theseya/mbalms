import { expect, test, type Page } from '@playwright/test'
import {
  createDevStaff,
  loginAs,
  loginAsManager,
  staffList,
  staffRow,
  unique,
} from './helpers'

const frontendOrigin = 'http://127.0.0.1:5173'
const setupOnFrontend = new RegExp(`^${frontendOrigin.replaceAll('.', '\\.')}/set-password\\?`)

async function seedAdminAccounts(page: Page, stamp: string) {
  await loginAsManager(page)
  const ownerEmail = `e2e.owner.${stamp}@test.local`
  const ownerPassword = 'Owner-E2e-1'
  const adminEmail = `e2e.acct.${stamp}@test.local`
  const adminPassword = 'Acct-Admin-1'
  const extraAdminEmail = `e2e.acct2.${stamp}@test.local`
  const owner = await createDevStaff(page, ownerEmail, ownerPassword, 'Owner')
  const admin = await createDevStaff(page, adminEmail, adminPassword, 'AccountAdmin')
  const extraAdmin = await createDevStaff(page, extraAdminEmail, 'Acct-Admin-2', 'AccountAdmin')
  return { owner, ownerEmail, ownerPassword, admin, adminEmail, adminPassword, extraAdmin }
}

test('owner uses /admin, invites a manager, and blocking revokes the session', async ({ page, browser }) => {
  const stamp = unique('adm')
  const { owner, ownerEmail, ownerPassword } = await seedAdminAccounts(page, stamp)
  const invitedEmail = `e2e.newmgr.${stamp}@test.local`
  const invitedPassword = 'Invite-Mgr-2'

  await loginAs(page, ownerEmail, ownerPassword, /\/admin\/staff/)
  await expect(page.getByRole('heading', { name: 'Сотрудники' })).toBeVisible()
  await expect(page.getByRole('table')).toBeVisible()
  const listed = await staffList(page, ownerEmail)
  expect(listed.items.some((s) => s.email === ownerEmail && s.roles.includes('Owner'))).toBeTruthy()

  await page.getByRole('button', { name: 'Пригласить менеджера' }).click()
  const invite = page.getByRole('dialog', { name: 'Пригласить менеджера' })
  await invite.getByRole('textbox', { name: 'Email' }).fill(invitedEmail)
  await invite.getByRole('button', { name: 'Сохранить' }).click()
  await expect(invite).toBeHidden()
  await expect(page.getByRole('link', { name: 'Открыть подготовленное письмо' })).toBeVisible()
  await page.getByRole('link', { name: 'Открыть подготовленное письмо' }).click()
  await expect(page.getByText(`Кому: ${invitedEmail}`)).toBeVisible()
  await expect(page.getByText('Письмо не отправлялось по SMTP.')).toBeVisible()

  const rawHref = await page.getByRole('link', { name: 'Открыть ссылку установки пароля' }).getAttribute('href')
  expect(rawHref).toMatch(setupOnFrontend)
  await page.getByRole('link', { name: 'Открыть ссылку установки пароля' }).click()
  await expect(page).toHaveURL(setupOnFrontend)
  await page.getByLabel('Новый пароль').fill(invitedPassword)
  await page.getByLabel('Повтор пароля').fill(invitedPassword)
  await page.getByRole('button', { name: 'Сохранить пароль' }).click()
  await expect(page.getByText('Пароль установлен. Теперь можно войти.')).toBeVisible()

  const managerSession = await browser.newContext()
  const managerPage = await managerSession.newPage()
  await loginAs(managerPage, invitedEmail, invitedPassword, /\/manager\//)
  await managerPage.goto('/admin/staff')
  await expect(managerPage).toHaveURL(/\/manager\/groups/)

  await page.goto('/admin/staff')
  await expect(page.getByRole('heading', { name: 'Сотрудники' })).toBeVisible()
  expect((await staffList(page, invitedEmail)).items.some((s) => s.email === invitedEmail)).toBeTruthy()
  const invitedRow = await staffRow(page, invitedEmail)
  await expect(invitedRow.getByRole('cell', { name: invitedEmail, exact: true })).toBeVisible()
  await invitedRow.getByRole('button', { name: 'Заблокировать' }).click()
  const confirm = page.getByRole('dialog', { name: 'Заблокировать сотрудника' })
  await confirm.getByRole('button', { name: 'Заблокировать' }).click()
  await expect(invitedRow).toContainText('Заблокирован')
  expect((await staffList(page, invitedEmail)).items[0]?.locked).toBeTruthy()

  await managerPage.goto('/manager/groups')
  await expect(managerPage).toHaveURL(/\/login/)

  await page.goto('/manager/groups')
  await expect(page).toHaveURL(/\/admin\/staff/)
  await page.goto('/student')
  await expect(page).toHaveURL(/\/admin\/staff/)

  await managerSession.close()
  expect(owner.id).toBeTruthy()
})

test('account admin cannot act on owner or another account admin', async ({ page }) => {
  const stamp = unique('aa')
  const { owner, ownerEmail, ownerPassword, adminEmail, adminPassword, extraAdmin } = await seedAdminAccounts(page, stamp)

  await loginAs(page, ownerEmail, ownerPassword, /\/admin\/staff/)
  await page.getByRole('button', { name: 'Пригласить менеджера' }).click()
  const invite = page.getByRole('dialog', { name: 'Пригласить менеджера' })
  const mgrEmail = `e2e.seenmgr.${stamp}@test.local`
  await invite.getByRole('textbox', { name: 'Email' }).fill(mgrEmail)
  await invite.getByRole('button', { name: 'Сохранить' }).click()
  await expect(invite).toBeHidden()
  await expect(page.getByRole('link', { name: 'Открыть подготовленное письмо' })).toBeVisible()
  expect((await staffList(page, mgrEmail)).items.some((s) => s.email === mgrEmail)).toBeTruthy()

  const ownerCsrf = await page.request.get('/api/auth/csrf')
  const ownerToken = (await ownerCsrf.json() as { token: string }).token
  const hideAdmin = await page.request.post(`/api/admin/staff/${extraAdmin.id}/block`, {
    headers: { 'X-XSRF-TOKEN': ownerToken },
  })
  expect(hideAdmin.ok()).toBeTruthy()

  await loginAs(page, adminEmail, adminPassword, /\/admin\/staff/)
  await expect(page.getByRole('heading', { name: 'Сотрудники' })).toBeVisible()
  expect((await staffList(page, mgrEmail)).items.some((s) => s.email === mgrEmail)).toBeTruthy()
  expect((await staffList(page, ownerEmail)).items).toHaveLength(0)
  expect((await staffList(page, extraAdmin.email)).items).toHaveLength(0)
  await expect(await staffRow(page, mgrEmail)).toBeVisible()
  await expect(page.getByRole('row', { name: ownerEmail })).toHaveCount(0)
  await expect(page.getByRole('row', { name: extraAdmin.email })).toHaveCount(0)

  await page.goto('/admin/audit')
  await expect(page.getByRole('heading', { name: 'Журнал доступа' })).toBeVisible()
  await expect(page.getByRole('cell', { name: 'Приглашён менеджер' }).first()).toBeVisible()
  await expect(page.getByRole('cell', { name: 'Создан владелец' })).toHaveCount(0)
  await expect(page.getByRole('cell', { name: 'Администратор аккаунтов заблокирован' })).toHaveCount(0)
  await expect(page.getByRole('cell', { name: extraAdmin.email })).toHaveCount(0)

  const auditRes = await page.request.get('/api/admin/audit?page=1&pageSize=200')
  expect(auditRes.ok()).toBeTruthy()
  const audit = await auditRes.json() as {
    items: { action: string; targetKind: string; targetEmail: string | null }[]
  }
  expect(audit.items.length).toBeGreaterThan(0)
  expect(audit.items.every((e) => e.targetKind === 'Manager')).toBeTruthy()
  expect(audit.items.some((e) => e.targetEmail === ownerEmail)).toBeFalsy()
  expect(audit.items.some((e) => e.targetEmail === extraAdmin.email)).toBeFalsy()
  expect(audit.items.some((e) => e.action === 'AccountAdminBlocked' || e.action === 'OwnerBootstrapped')).toBeFalsy()

  const csrf = await page.request.get('/api/auth/csrf')
  const token = (await csrf.json() as { token: string }).token
  const blockOwner = await page.request.post(`/api/admin/staff/${owner.id}/block`, {
    headers: { 'X-XSRF-TOKEN': token },
  })
  expect(blockOwner.status()).toBe(403)
  const blockAdmin = await page.request.post(`/api/admin/staff/${extraAdmin.id}/block`, {
    headers: { 'X-XSRF-TOKEN': token },
  })
  expect(blockAdmin.status()).toBe(403)
})

test('seed manager cannot open the admin cabinet', async ({ page }) => {
  await loginAsManager(page)
  await page.goto('/admin/staff')
  await expect(page).toHaveURL(/\/manager\/groups/)
  await expect(page.getByRole('heading', { name: 'Сотрудники' })).toHaveCount(0)
})
