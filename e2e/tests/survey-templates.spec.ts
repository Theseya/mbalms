import { expect, test } from '@playwright/test'
import { createGroup, loginAsManager, unique } from './helpers'

test('manager creates a survey template and a draft survey from it', async ({ page }) => {
  const stamp = unique('tpl')
  const group = `E2E-${stamp}-группа`
  const title = `E2E-${stamp}-шаблон`
  const question = 'Насколько удобен кабинет?'

  await loginAsManager(page)
  await createGroup(page, group)

  await page.goto('/manager/survey-templates')
  await expect(page.getByRole('heading', { name: 'Шаблоны опросов' })).toBeVisible()
  await page.getByRole('link', { name: 'Новый шаблон' }).click()
  await expect(page).toHaveURL(/\/manager\/survey-templates\/new/)
  await page.getByLabel('Тип *').selectOption({ label: 'Сервисный опрос' })
  await page.getByLabel('Название').fill(title)
  await page.getByLabel('Текст вопроса').fill(question)
  await page.getByRole('button', { name: 'Сохранить' }).click()
  await expect(page).toHaveURL(/\/manager\/survey-templates\/[0-9a-f-]+/)
  await expect(page.getByLabel('Название')).toHaveValue(title)

  await page.getByRole('link', { name: 'Создать из шаблона' }).click()
  await expect(page).toHaveURL(/\/manager\/surveys\/from-template/)
  await expect(page.getByLabel('Название')).toHaveValue(title)
  await page.getByLabel('Группа').selectOption({ label: group })
  await page.getByRole('button', { name: 'Создать черновик опроса' }).click()
  await expect(page).toHaveURL(/\/manager\/surveys\/[0-9a-f-]+/)
  await expect(page.getByLabel('Название')).toHaveValue(title)
  await expect(page.getByText('Черновик', { exact: true })).toBeVisible()
  await expect(page.getByLabel('Текст вопроса')).toHaveValue(question)
})
