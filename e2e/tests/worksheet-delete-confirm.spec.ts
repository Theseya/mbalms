import { expect, test, type Response } from '@playwright/test'
import {
  confirmDialog,
  createDiscipline,
  createGroup,
  createPeriod,
  createStudent,
  isDisciplineSettingsPut,
  loginAsManager,
  openSheet,
  settingsDialog,
} from './helpers'

test.describe.configure({ mode: 'serial' })

test('settings loaded, then new scores, delete work requires visible confirm; cancel then explicit confirm', async ({ page }) => {
  const stamp = Date.now()
  const names = {
    group: `E2E-дел-группа ${stamp}`,
    discipline: `E2E-дел-дисц ${stamp}`,
    period: `E2E-дел-период ${stamp}`,
  }
  const workName = `E2E-${stamp}`
  const puts: { status: number; confirm: boolean }[] = []
  page.on('response', async (res: Response) => {
    if (res.request().method() !== 'PUT' || !isDisciplineSettingsPut(res.url())) return
    const body = res.request().postDataJSON() as { confirmScoreRemoval?: boolean }
    puts.push({ status: res.status(), confirm: body.confirmScoreRemoval === true })
  })

  await loginAsManager(page)
  await createGroup(page, names.group)
  await createStudent(page, { last: 'Иванов', first: 'Иван', email: `e2e.ivan.${stamp}@test.local`, group: names.group })
  await createStudent(page, { last: 'Петрова', first: 'Анна', email: `e2e.anna.${stamp}@test.local`, group: names.group })
  await createDiscipline(page, names.discipline)
  await createPeriod(page, names.period)
  await openSheet(page, names)

  await expect(page.getByRole('heading', { name: 'Элементы контроля и посещаемость' })).toBeVisible()
  await page.getByRole('button', { name: 'Добавить категорию' }).click()
  const editor = settingsDialog(page)
  await expect(editor).toBeVisible()
  await editor.getByLabel('Название категории').fill('A')
  await editor.getByLabel('Вес, %').first().fill('100')
  await editor.getByLabel('Название работы').fill('A1')
  await editor.getByRole('button', { name: 'Сохранить' }).click()
  await expect(editor).toBeHidden()
  await expect(page.getByRole('status').filter({ hasText: 'Настройки сохранены.' })).toBeVisible()

  const kept = page.getByRole('textbox', { name: 'A1: Иванов Иван' })
  await expect(kept).toBeVisible()
  await kept.fill('7')
  await page.getByRole('region', { name: /Ведомость:/ }).getByRole('button', { name: 'Сохранить' }).click()
  await expect(page.getByText('Ведомость сохранена.')).toBeVisible()
  await expect(kept).toHaveValue('7')

  await page.getByRole('button', { name: 'Изменить категории' }).click()
  await expect(editor).toBeVisible()
  await editor.getByRole('button', { name: 'Добавить работу' }).first().click()
  await editor.getByLabel('Название работы').nth(1).fill(workName)
  await editor.getByRole('button', { name: 'Сохранить' }).click()
  await expect(editor).toBeHidden()
  await expect(page.getByRole('status').filter({ hasText: 'Настройки сохранены.' })).toBeVisible()
  expect(puts.filter((p) => p.status === 409)).toHaveLength(0)

  const score = page.getByRole('textbox', { name: `${workName}: Петрова Анна` })
  await expect(score).toBeVisible()
  await score.fill('4')
  await page.getByRole('region', { name: /Ведомость:/ }).getByRole('button', { name: 'Сохранить' }).click()
  await expect(page.getByText('Ведомость сохранена.')).toBeVisible()
  await expect(score).toHaveValue('4')
  await expect(kept).toHaveValue('7')

  await page.getByRole('button', { name: 'Изменить категории' }).click()
  await expect(editor).toBeVisible()
  await editor.getByRole('button', { name: `Удалить работу ${workName}` }).click()
  await editor.getByRole('button', { name: 'Сохранить' }).click()

  const confirm = confirmDialog(page)
  await expect(confirm).toBeVisible()
  await expect(confirm.getByText(`Работа «${workName}» (A) — баллов: 1`)).toBeVisible()
  await expect(confirm.getByText('Всего баллов к удалению: 1.')).toBeVisible()
  expect(puts.filter((p) => p.status === 409 && !p.confirm)).toHaveLength(1)

  await confirm.getByRole('button', { name: 'Отмена' }).click()
  await expect(confirm).toBeHidden()
  await expect(editor).toBeVisible()
  await expect(page.getByRole('textbox', { name: `${workName}: Петрова Анна` })).toHaveValue('4')
  expect(puts.some((p) => p.confirm)).toBe(false)

  await editor.getByRole('button', { name: 'Сохранить' }).click()
  await expect(confirm).toBeVisible()
  await confirm.getByRole('button', { name: 'Подтвердить удаление' }).click()
  await expect(editor).toBeHidden()
  await expect(page.getByRole('textbox', { name: `${workName}: Петрова Анна` })).toHaveCount(0)
  await expect(page.getByRole('textbox', { name: 'A1: Иванов Иван' })).toHaveValue('7')
  expect(puts.some((p) => p.confirm && p.status >= 200 && p.status < 300)).toBe(true)
})
