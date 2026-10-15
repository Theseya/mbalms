import { expect, test } from '@playwright/test'
import { loginAsManager } from './helpers'

test.use({ viewport: { width: 390, height: 844 }, isMobile: true, hasTouch: true })

test('manager pages remain usable at a phone viewport', async ({ page }) => {
  await loginAsManager(page)
  await page.goto('/manager/groups')
  await expect(page.getByRole('heading', { name: 'Группы' })).toBeVisible()
  await expect(page.getByRole('button', { name: 'Новая группа' })).toBeVisible()

  await page.goto('/manager/worksheet')
  await expect(page.getByRole('heading', { name: 'Рабочая ведомость' })).toBeVisible()
  await expect(page.getByLabel('Дисциплина')).toBeVisible()
  await expect(page.getByLabel('Группа')).toBeVisible()

  await page.goto('/manager/announcements')
  await expect(page.getByRole('heading', { name: 'Объявления' })).toBeVisible()
  await expect(page.getByRole('link', { name: 'Новое объявление' })).toBeVisible()

  await page.goto('/manager/disciplines')
  await expect(page.getByRole('heading', { name: 'Дисциплины' })).toBeVisible()
})
