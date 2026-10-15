import { expect, test } from '@playwright/test'
import {
  createGroup,
  createStudent,
  loginAsManager,
  managerEmail,
  managerPassword,
  openLoginForm,
  unique,
} from './helpers'

const frontendOrigin = 'http://127.0.0.1:5173'
const setupOnFrontend = new RegExp(`^${frontendOrigin.replaceAll('.', '\\.')}/set-password\\?`)

test('invite outbox link opens the test frontend without rewriting the host', async ({ page }) => {
  const stamp = unique('inv')
  const group = `E2E-${stamp}-группа`
  const email = `e2e.inv.${stamp}@test.local`
  const newPassword = 'Invite-Pass-2'

  await loginAsManager(page)
  await createGroup(page, group)
  await createStudent(page, {
    last: 'Е2еинв',
    first: 'Борис',
    email,
    group,
    password: 'Temp-Invite-1',
  })
  await page.getByLabel('Поиск').fill(email)
  const inviteRow = page.getByRole('row', { name: email })
  await expect(inviteRow).toBeVisible()
  await inviteRow.getByRole('button', { name: 'Пригласить' }).click()
  const confirm = page.getByRole('dialog', { name: 'Пригласить студента' })
  await confirm.getByRole('button', { name: 'Пригласить' }).click()
  await expect(page.getByText('Письмо подготовлено для локального просмотра (не отправлено по SMTP).')).toBeVisible()
  await page.getByRole('link', { name: 'Открыть подготовленное письмо' }).click()
  await expect(page.getByText(email)).toBeVisible()
  await expect(page.getByText('Письмо не отправлялось по SMTP.')).toBeVisible()

  const rawHref = await page.getByRole('link', { name: 'Открыть ссылку установки пароля' }).getAttribute('href')
  expect(rawHref, 'setup link from Development outbox').toBeTruthy()
  expect(rawHref!).toMatch(setupOnFrontend)
  expect(rawHref!).not.toMatch(/:8080/)

  await page.getByRole('link', { name: 'Открыть ссылку установки пароля' }).click()
  await expect(page).toHaveURL(setupOnFrontend)
  await page.getByLabel('Новый пароль').fill(newPassword)
  await page.getByLabel('Повтор пароля').fill(newPassword)
  await page.getByRole('button', { name: 'Сохранить пароль' }).click()
  await expect(page.getByText('Пароль установлен. Теперь можно войти.')).toBeVisible()

  await openLoginForm(page)
  await page.getByLabel('Email').fill(email)
  await page.getByLabel('Пароль').fill(newPassword)
  await page.getByRole('button', { name: 'Войти' }).click()
  await expect(page).toHaveURL(/\/student/)
})

test('forgot-password outbox link opens the test frontend and sets a new password', async ({ page }) => {
  const stamp = unique('fg')
  const group = `E2E-${stamp}-группа`
  const email = `e2e.fg.${stamp}@test.local`
  const bootstrap = 'E2e-Student-1'
  const newPassword = 'Reset-Pass-2'

  await loginAsManager(page)
  await createGroup(page, group)
  await createStudent(page, {
    last: 'Е2есброс',
    first: 'Анна',
    email,
    group,
    password: bootstrap,
  })
  await openLoginForm(page)

  await page.goto('/forgot-password')
  await page.getByLabel('Email').fill(email)
  await page.getByRole('button', { name: 'Отправить ссылку' }).click()
  await expect(page.getByText(
    'Если учётная запись существует и почтовый сервис доступен, инструкция отправлена. Проверьте почту.',
  )).toBeVisible()

  await page.goto('/login')
  await page.getByLabel('Email').fill(managerEmail)
  await page.getByLabel('Пароль').fill(managerPassword())
  await page.getByRole('button', { name: 'Войти' }).click()
  await expect(page).toHaveURL(/\/manager\//)

  const latest = await page.request.get(`/api/dev/mail/latest?to=${encodeURIComponent(email)}`)
  expect(latest.ok(), `dev mail latest status ${latest.status()}`).toBeTruthy()
  const body = await latest.json()
  expect(body.to).toBe(email)
  expect(String(body.purpose ?? '')).toMatch(/reset/i)
  const match = String(body.textBody).match(/https?:\/\/\S*\/set-password\?[^\s]+/)
  expect(match, 'setup link in Development outbox').toBeTruthy()
  const setupHref = match![0].replace(/[.)\]]+$/, '')
  expect(setupHref).toMatch(setupOnFrontend)
  expect(setupHref).not.toMatch(/:8080/)

  await page.goto(setupHref)
  await expect(page).toHaveURL(setupOnFrontend)
  await page.getByLabel('Новый пароль').fill(newPassword)
  await page.getByLabel('Повтор пароля').fill(newPassword)
  await page.getByRole('button', { name: 'Сохранить пароль' }).click()
  await expect(page.getByText('Пароль установлен. Теперь можно войти.')).toBeVisible()

  await openLoginForm(page)
  await page.getByLabel('Email').fill(email)
  await page.getByLabel('Пароль').fill(newPassword)
  await page.getByRole('button', { name: 'Войти' }).click()
  await expect(page).toHaveURL(/\/student/)
})
