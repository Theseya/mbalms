import { expect, test } from '@playwright/test'
import {
  createDiscipline,
  createGroup,
  createPeriod,
  createStudent,
  loginAsManager,
  loginAsStudent,
  unique,
} from './helpers'

test('manager draft is hidden until publish; unpublish hides it again; student is notified', async ({ page }) => {
  const stamp = unique('grd')
  const names = {
    group: `E2E-${stamp}-группа`,
    last: `Е2еоц${stamp.slice(-6)}`,
    first: 'Анна',
    email: `e2e.grd.${stamp}@test.local`,
    discipline: `E2E-${stamp}-дисц`,
    period: `E2E-${stamp}-период`,
  }
  const studentName = `${names.last} ${names.first}`
  const studentPassword = 'E2e-Student-1'

  await loginAsManager(page)
  await createGroup(page, names.group)
  await createStudent(page, {
    last: names.last,
    first: names.first,
    email: names.email,
    group: names.group,
    password: studentPassword,
  })
  await createDiscipline(page, names.discipline)
  await createPeriod(page, names.period)

  await page.goto('/manager/grades')
  await expect(page.getByRole('heading', { name: 'Оценки' })).toBeVisible()
  await page.getByRole('button', { name: 'Новая оценка' }).click()
  const create = page.getByRole('dialog', { name: 'Новая оценка' })
  await create.getByLabel('Студент').selectOption({ label: `${studentName} · ${names.group}` })
  await create.getByLabel('Дисциплина').selectOption({ label: names.discipline })
  await create.getByLabel('Учебный период').selectOption({ label: names.period })
  await create.getByLabel('Оценка (0–100)').fill('70')
  await create.getByRole('button', { name: 'Сохранить' }).click()
  await expect(create).toBeHidden()

  await page.getByLabel('Группа').selectOption({ label: names.group })
  const row = page.getByRole('row', { name: new RegExp(names.last) })
  await expect(row.getByText('70', { exact: true })).toBeVisible()
  await expect(row.getByText('Черновик', { exact: true })).toBeVisible()

  await row.getByRole('button', { name: 'Изменить' }).click()
  const edit = page.getByRole('dialog', { name: 'Изменение оценки' })
  await edit.getByLabel('Оценка (0–100)').fill('85')
  await edit.getByRole('button', { name: 'Сохранить' }).click()
  await expect(edit).toBeHidden()
  await expect(row.getByText('85', { exact: true })).toBeVisible()
  await expect(row.getByText('Черновик', { exact: true })).toBeVisible()

  await loginAsStudent(page, names.email, studentPassword)
  await page.goto('/student/grades')
  await expect(page.getByText('Опубликованных оценок пока нет')).toBeVisible()
  await expect(page.getByRole('cell', { name: names.discipline })).toHaveCount(0)

  await loginAsManager(page)
  await page.goto('/manager/grades')
  await page.getByLabel('Группа').selectOption({ label: names.group })
  await page.getByRole('row', { name: new RegExp(names.last) }).getByRole('button', { name: 'Опубликовать' }).click()
  await expect(page.getByRole('row', { name: new RegExp(names.last) }).getByText('Опубликована', { exact: true })).toBeVisible()

  await loginAsStudent(page, names.email, studentPassword)
  await page.goto('/student/grades')
  const published = page.getByRole('row', { name: new RegExp(names.discipline) })
  await expect(published.getByText(names.discipline)).toBeVisible()
  await expect(published.getByText(names.period)).toBeVisible()
  await expect(published.locator('strong.grade')).toHaveText('85')

  await page.goto('/student/notifications')
  await expect(page.getByRole('heading', { name: 'Уведомления' })).toBeVisible()
  const note = page.getByText(`Опубликована оценка: ${names.discipline} (${names.period})`)
  await expect(note).toBeVisible()
  await page.getByRole('button', { name: 'Прочитано' }).click()
  await expect(page.getByRole('button', { name: 'Прочитано' })).toHaveCount(0)
  await expect(note).toBeVisible()

  await loginAsManager(page)
  await page.goto('/manager/grades')
  await page.getByLabel('Группа').selectOption({ label: names.group })
  const unpublished = page.getByRole('row', { name: new RegExp(names.last) })
  await unpublished.getByRole('button', { name: 'Снять с публикации' }).click()
  await expect(unpublished.getByText('Черновик', { exact: true })).toBeVisible()
  await expect(unpublished.getByText('85', { exact: true })).toBeVisible()
  await expect(unpublished.getByRole('button', { name: 'Снять с публикации' })).toHaveCount(0)
  await expect(unpublished.getByRole('button', { name: 'Опубликовать' })).toBeVisible()

  await loginAsStudent(page, names.email, studentPassword)
  await page.goto('/student/grades')
  await expect(page.getByText('Опубликованных оценок пока нет')).toBeVisible()
  await expect(page.getByRole('cell', { name: names.discipline })).toHaveCount(0)
})
