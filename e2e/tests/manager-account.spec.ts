import { expect, test, type Page } from '@playwright/test'
import { loginAsManager, managerEmail, managerPassword, openLoginForm } from './helpers'

async function setManagerPassword(page: Page, current: string, next: string) {
  await page.getByLabel('Текущий пароль').fill(current)
  await page.getByLabel('Новый пароль').fill(next)
  await page.getByRole('button', { name: 'Сохранить' }).click()
}

test('manager changes password on the profile page and can sign in with the new one', async ({ page }) => {
  const original = managerPassword()
  const next = 'E2e-MgrTmp-9'
  let current = original

  try {
    await loginAsManager(page)
    await page.goto('/account')
    await expect(page.getByRole('heading', { name: 'Профиль' })).toBeVisible()
    await expect(page.locator('p.muted', { hasText: managerEmail })).toBeVisible()
    await setManagerPassword(page, original, next)
    await expect(page.getByText('Пароль изменён')).toBeVisible()
    current = next

    await page.getByRole('button', { name: 'Выйти' }).click()
    await expect(page.getByLabel('Email')).toBeVisible()
    await page.getByLabel('Email').fill(managerEmail)
    await page.getByLabel('Пароль').fill(next)
    await page.getByRole('button', { name: 'Войти' }).click()
    await expect(page).toHaveURL(/\/manager\//)

    await page.goto('/account')
    await setManagerPassword(page, next, original)
    await expect(page.getByText('Пароль изменён')).toBeVisible()
    current = original

    await page.getByRole('button', { name: 'Выйти' }).click()
    await page.getByLabel('Email').fill(managerEmail)
    await page.getByLabel('Пароль').fill(original)
    await page.getByRole('button', { name: 'Войти' }).click()
    await expect(page).toHaveURL(/\/manager\//)
  } finally {
    if (current !== original) {
      await openLoginForm(page)
      await page.getByLabel('Email').fill(managerEmail)
      await page.getByLabel('Пароль').fill(current)
      await page.getByRole('button', { name: 'Войти' }).click()
      await expect(page).toHaveURL(/\/manager\//)
      await page.goto('/account')
      await page.getByLabel('Текущий пароль').fill(current)
      await page.getByLabel('Новый пароль').fill(original)
      await page.getByRole('button', { name: 'Сохранить' }).click()
      await expect(page.getByText('Пароль изменён')).toBeVisible()
    }
  }
})
