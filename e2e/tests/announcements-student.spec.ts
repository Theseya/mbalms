import { expect, test } from '@playwright/test'
import {
  createGroup,
  createStudent,
  loginAsManager,
  loginAsStudent,
  unique,
} from './helpers'

test('student sees a published announcement for their group only', async ({ page }) => {
  const stamp = unique('ann')
  const groupA = `E2E-${stamp}-группа-А`
  const groupB = `E2E-${stamp}-группа-Б`
  const title = `E2E-${stamp}-объявление`
  const body = 'Текст объявления только для группы А.'
  const emailA = `e2e.ann.a.${stamp}@test.local`
  const emailB = `e2e.ann.b.${stamp}@test.local`

  await loginAsManager(page)
  await createGroup(page, groupA)
  await createGroup(page, groupB)
  await createStudent(page, { last: `Е2еаб${stamp.slice(-6)}`, first: 'Анна', email: emailA, group: groupA })
  await createStudent(page, { last: `Е2ебб${stamp.slice(-6)}`, first: 'Борис', email: emailB, group: groupB })

  await page.goto('/manager/announcements/new')
  await page.getByLabel('Заголовок').fill(title)
  await page.getByLabel('Текст').fill(body)
  await page.getByRole('checkbox', { name: groupA }).check()
  await expect(page.getByRole('checkbox', { name: groupA })).toBeChecked()
  await page.getByRole('button', { name: 'Сохранить и опубликовать' }).click()
  const confirm = page.getByRole('dialog', { name: 'Опубликовать объявление' })
  await confirm.getByRole('button', { name: 'Опубликовать' }).click()
  await expect(page).toHaveURL(/\/manager\/announcements$/)
  const row = page.getByRole('row', { name: title })
  await expect(row.getByRole('link', { name: title })).toBeVisible()
  await expect(row.getByText('Опубликовано', { exact: true })).toBeVisible()
  await expect(row.getByRole('button', { name: 'Снять с публикации' })).toBeVisible()

  await loginAsStudent(page, emailA)
  await page.goto('/student/announcements')
  await expect(page.getByRole('heading', { name: 'Объявления' })).toBeVisible()
  await expect(page.getByRole('heading', { name: title })).toBeVisible()
  await expect(page.getByText(body)).toBeVisible()

  await loginAsStudent(page, emailB)
  await page.goto('/student/announcements')
  await expect(page.getByText('Для вашей группы пока нет объявлений')).toBeVisible()
  await expect(page.getByRole('heading', { name: title })).toHaveCount(0)
})
