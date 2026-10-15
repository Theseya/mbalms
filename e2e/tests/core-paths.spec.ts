import { expect, test } from '@playwright/test'
import { loginAsManager, settingsDialog } from './helpers'

const stamp = Date.now()
const names = {
  group: `E2E-группа ${stamp}`,
  studentEmail: `e2e.core.${stamp}@test.local`,
  studentLast: 'Е2еядро',
  studentFirst: 'Анна',
  discipline: `E2E-дисциплина ${stamp}`,
  period: `E2E-период ${stamp}`,
}

test.describe.configure({ mode: 'serial' })

test('groups, students, disciplines and periods persist after reload', async ({ page }) => {
  await loginAsManager(page)

  await page.goto('/manager/groups')
  await page.getByRole('button', { name: 'Новая группа' }).click()
  const groupDialog = page.getByRole('dialog', { name: 'Новая группа' })
  await groupDialog.getByLabel('Название').fill(names.group)
  await groupDialog.getByRole('button', { name: 'Сохранить' }).click()
  await expect(groupDialog).toBeHidden()
  await page.reload()
  await page.getByLabel('Поиск').fill(names.group)
  await expect(page.getByRole('link', { name: names.group })).toBeVisible()

  await page.goto('/manager/students')
  await page.getByRole('button', { name: 'Новый студент' }).click()
  const studentDialog = page.getByRole('dialog', { name: 'Новый студент' })
  await studentDialog.getByLabel('Фамилия').fill(names.studentLast)
  await studentDialog.getByLabel('Имя').fill(names.studentFirst)
  await studentDialog.getByLabel('Email').fill(names.studentEmail)
  await studentDialog.getByLabel('Группа').selectOption({ label: names.group })
  await studentDialog.getByLabel('Пароль для входа').fill('E2e-Student-1')
  await studentDialog.getByRole('button', { name: 'Сохранить' }).click()
  await expect(studentDialog).toBeHidden()
  await page.reload()
  await page.getByLabel('Поиск').fill(names.studentEmail)
  await expect(page.getByText(names.studentEmail)).toBeVisible()

  await page.goto('/manager/disciplines')
  await page.getByRole('button', { name: 'Новая дисциплина' }).click()
  const discDialog = page.getByRole('dialog', { name: 'Новая дисциплина' })
  await discDialog.getByLabel('Название').fill(names.discipline)
  await discDialog.getByRole('button', { name: 'Сохранить' }).click()
  await expect(discDialog).toBeHidden()
  await page.reload()
  await page.getByLabel('Поиск').fill(names.discipline)
  await expect(page.getByRole('cell', { name: names.discipline })).toBeVisible()

  await page.goto('/manager/periods')
  await page.getByRole('button', { name: 'Новый период' }).click()
  const periodDialog = page.getByRole('dialog', { name: 'Новый период' })
  await periodDialog.getByLabel('Название').fill(names.period)
  await periodDialog.getByRole('button', { name: 'Сохранить' }).click()
  await expect(periodDialog).toBeHidden()
  await page.reload()
  await page.getByLabel('Поиск').fill(names.period)
  await expect(page.getByRole('cell', { name: names.period })).toBeVisible()
})

test('worksheet average then weighted persist, scores save, transfer writes journal ×10', async ({ page }) => {
  await loginAsManager(page)
  await page.goto('/manager/worksheet')
  await page.getByLabel('Дисциплина').selectOption({ label: names.discipline })
  await expect(page.getByText('Режим расчёта')).toBeVisible()

  await page.getByRole('button', { name: 'Добавить категорию' }).click()
  const editor = settingsDialog(page)
  await editor.getByRole('radio', { name: 'Среднее арифметическое' }).check()
  await editor.getByLabel('Название категории').fill('Кейс')
  await editor.getByLabel('Вес, %').first().fill('100')
  await editor.getByLabel('Название работы').fill('Работа 1')
  await editor.getByRole('button', { name: 'Сохранить' }).click()
  await expect(editor).toBeHidden()
  await page.reload()
  await page.getByLabel('Дисциплина').selectOption({ label: names.discipline })
  await expect(page.getByText('Среднее арифметическое')).toBeVisible()

  await page.getByRole('button', { name: 'Изменить категории' }).click()
  await editor.getByRole('radio', { name: 'Взвешенный' }).check()
  await editor.getByRole('button', { name: 'Сохранить' }).click()
  await expect(editor).toBeHidden()
  await page.reload()
  await page.getByLabel('Дисциплина').selectOption({ label: names.discipline })
  await expect(page.getByText(/Режим расчёта\s*:\s*Взвешенный/)).toBeVisible()

  await page.getByLabel('Группа').selectOption({ label: names.group })
  await page.getByLabel('Учебный период').selectOption({ label: names.period })
  const score = page.getByRole('textbox', { name: `Работа 1: ${names.studentLast} ${names.studentFirst}` })
  await expect(score).toBeVisible()
  await score.fill('8')
  await page.getByRole('region', { name: /Ведомость:/ }).getByRole('button', { name: 'Сохранить' }).click()
  await expect(page.getByText('Ведомость сохранена.')).toBeVisible()
  await expect(score).toHaveValue('8')

  await page.getByRole('button', { name: 'Перенести итоговые оценки' }).click()
  const transfer = page.getByRole('dialog', { name: 'Перенос итоговых оценок' })
  await expect(transfer).toBeVisible()
  await transfer.getByRole('button', { name: /Перенести \(1\)/ }).click()
  await expect(page.getByText(/Перенос выполнен: новых 1/)).toBeVisible()

  await page.goto('/manager/gradebook')
  await page.getByLabel('Группа').selectOption({ label: names.group })
  await page.getByLabel('Дисциплина').selectOption({ label: names.discipline })
  await page.getByLabel('Учебный период').selectOption({ label: names.period })
  const grade = page.getByRole('spinbutton', { name: `Оценка: ${names.studentLast} ${names.studentFirst}` })
  await expect(grade).toHaveValue('80')
  const gradeRow = page.locator('tr', { has: grade })
  await expect(gradeRow.getByText('Черновик', { exact: true })).toBeVisible()

  await page.reload()
  await page.getByLabel('Группа').selectOption({ label: names.group })
  await page.getByLabel('Дисциплина').selectOption({ label: names.discipline })
  await page.getByLabel('Учебный период').selectOption({ label: names.period })
  await expect(page.getByRole('spinbutton', { name: `Оценка: ${names.studentLast} ${names.studentFirst}` })).toHaveValue('80')
})

