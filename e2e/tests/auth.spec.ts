import { expect, test } from '@playwright/test'
import { loginAsManager, managerEmail, managerPassword } from './helpers'

test('wrong password stays on login and does not name the account', async ({ page }) => {
  await page.goto('/login')
  await page.getByLabel('Email').fill(managerEmail)
  await page.getByLabel('Пароль').fill('Wrong-Pass-1')
  await page.getByRole('button', { name: 'Войти' }).click()
  await expect(page.getByRole('alert')).toHaveText('Неверный email или пароль.')
  await expect(page).toHaveURL(/\/login/)
})

test('manager session cannot open the student cabinet', async ({ page }) => {
  await loginAsManager(page)
  await page.goto('/student')
  await expect(page).toHaveURL(/\/manager\/groups/)
})

test('student cannot open manager pages', async ({ page }) => {
  const stamp = Date.now()
  const groupName = `E2E-auth-${stamp}`
  const email = `e2e.auth.${stamp}@test.local`
  const password = 'E2e-Student-1'

  await loginAsManager(page)
  await page.goto('/manager/groups')
  await page.getByRole('button', { name: 'Новая группа' }).click()
  const groupDialog = page.getByRole('dialog', { name: 'Новая группа' })
  await groupDialog.getByLabel('Название').fill(groupName)
  await groupDialog.getByRole('button', { name: 'Сохранить' }).click()
  await expect(groupDialog).toBeHidden()
  await page.getByLabel('Поиск').fill(groupName)
  await expect(page.getByRole('link', { name: groupName })).toBeVisible()

  await page.goto('/manager/students')
  await page.getByRole('button', { name: 'Новый студент' }).click()
  const studentDialog = page.getByRole('dialog', { name: 'Новый студент' })
  await studentDialog.getByLabel('Фамилия').fill('Е2е')
  await studentDialog.getByLabel('Имя').fill('Студент')
  await studentDialog.getByLabel('Email').fill(email)
  await studentDialog.getByLabel('Группа').selectOption({ label: groupName })
  await studentDialog.getByLabel('Пароль для входа').fill(password)
  await studentDialog.getByRole('button', { name: 'Сохранить' }).click()
  await expect(studentDialog).toBeHidden()
  await expect(page.getByText(email)).toBeVisible()

  await page.getByRole('button', { name: 'Выйти' }).click()
  await expect(page).toHaveURL(/\/login/)

  await page.getByLabel('Email').fill(email)
  await page.getByLabel('Пароль').fill(password)
  await page.getByRole('button', { name: 'Войти' }).click()
  await expect(page).toHaveURL(/\/student/)
  await expect(page.getByRole('heading', { name: /Здравствуйте, Студент Е2е/ })).toBeVisible()

  await page.goto('/manager/groups')
  await expect(page).toHaveURL(/\/student/)
})
