import { expect, test } from '@playwright/test'
import {
  createDiscipline,
  createGroup,
  createPeriod,
  createStudent,
  loginAsManager,
  openSheet,
  selectPeriod,
  settingsDialog,
  unique,
} from './helpers'

test('worksheet keeps several works, auto attendance in both modes, reload and two periods', async ({ page }) => {
  test.setTimeout(180_000)
  const stamp = unique('wsc')
  const names = {
    group: `E2E-${stamp}-группа`,
    last: `Е2евд${stamp.slice(-6)}`,
    first: 'Иван',
    email: `e2e.wsc.${stamp}@test.local`,
    discipline: `E2E-${stamp}-дисц`,
    periodA: `E2E-${stamp}-период-А`,
    periodB: `E2E-${stamp}-период-Б`,
  }
  const studentName = `${names.last} ${names.first}`

  await loginAsManager(page)
  await createGroup(page, names.group)
  await createStudent(page, { last: names.last, first: names.first, email: names.email, group: names.group })
  await createDiscipline(page, names.discipline)
  await createPeriod(page, names.periodA)
  await createPeriod(page, names.periodB)
  await openSheet(page, { group: names.group, discipline: names.discipline, period: names.periodA })

  await expect(page.getByRole('heading', { name: 'Элементы контроля и посещаемость' })).toBeVisible()
  await page.getByRole('button', { name: 'Добавить категорию' }).click()
  const editor = settingsDialog(page)
  await expect(editor).toBeVisible()
  await editor.getByRole('radio', { name: 'Взвешенный' }).check()
  await editor.getByLabel('Название категории').fill('Кейс')
  await editor.getByLabel('Вес, %').fill('40')
  await editor.getByLabel('Название работы').fill('Работа А1')
  await editor.getByRole('button', { name: 'Добавить работу' }).click()
  await editor.getByLabel('Название работы').nth(1).fill('Работа А2')
  await editor.getByRole('button', { name: 'Добавить категорию' }).click()
  await editor.getByLabel('Название категории').nth(1).fill('Экзамен')
  await editor.getByLabel('Вес, %').nth(1).fill('40')
  await editor.getByLabel('Название работы').nth(2).fill('Экзамен 1')
  await editor.getByLabel('Вес посещаемости, %').fill('20')
  await editor.getByLabel('Порог посещаемости, %').fill('70')
  await editor.getByRole('button', { name: 'Сохранить' }).click()
  await expect(editor).toBeHidden()
  await expect(page.getByRole('status').filter({ hasText: 'Настройки сохранены.' })).toBeVisible()

  const sheet = page.getByRole('region', { name: /Ведомость:/ })
  await sheet.getByLabel('Всего занятий').fill('10')
  await page.getByRole('textbox', { name: `Работа А1: ${studentName}` }).fill('8')
  await page.getByRole('textbox', { name: `Работа А2: ${studentName}` }).fill('6')
  await page.getByRole('textbox', { name: `Экзамен 1: ${studentName}` }).fill('9')
  await page.getByRole('textbox', { name: `Посещено занятий: ${studentName}` }).fill('8')
  await sheet.getByRole('button', { name: 'Сохранить' }).click()
  await expect(page.getByText('Ведомость сохранена.')).toBeVisible()
  const rowA = page.getByRole('row', { name: new RegExp(names.last) })
  await expect(rowA.getByText('8 из 10')).toBeVisible()
  await expect(rowA.getByText('Выполнено', { exact: true })).toBeVisible()
  await expect(rowA.locator('td[data-label$="Балл посещаемости"]')).toHaveText('8')
  await expect(rowA.locator('td[data-label$="Вклад"]')).toHaveText('1,6')

  await selectPeriod(page, names.periodB)
  await expect(page.getByRole('heading', { name: new RegExp(names.periodB) })).toBeVisible()
  const sheetB = page.getByRole('region', { name: /Ведомость:/ })
  await sheetB.getByLabel('Всего занятий').fill('5')
  await page.getByRole('textbox', { name: `Работа А1: ${studentName}` }).fill('3')
  await page.getByRole('textbox', { name: `Работа А2: ${studentName}` }).fill('4')
  await page.getByRole('textbox', { name: `Экзамен 1: ${studentName}` }).fill('5')
  await page.getByRole('textbox', { name: `Посещено занятий: ${studentName}` }).fill('1')
  await sheetB.getByRole('button', { name: 'Сохранить' }).click()
  await expect(page.getByText('Ведомость сохранена.')).toBeVisible()
  const rowB = page.getByRole('row', { name: new RegExp(names.last) })
  await expect(rowB.getByText('1 из 5')).toBeVisible()
  await expect(page.getByRole('textbox', { name: `Работа А1: ${studentName}` })).toHaveValue('3')

  await page.reload()
  await openSheet(page, { group: names.group, discipline: names.discipline, period: names.periodB })
  await expect(page.getByRole('textbox', { name: `Работа А1: ${studentName}` })).toHaveValue('3')
  await expect(page.getByRole('textbox', { name: `Посещено занятий: ${studentName}` })).toHaveValue('1')
  await expect(rowB.getByText('1 из 5')).toBeVisible()

  await selectPeriod(page, names.periodA)
  await expect(page.getByRole('heading', { name: new RegExp(names.periodA) })).toBeVisible()
  await expect(page.getByRole('textbox', { name: `Работа А1: ${studentName}` })).toHaveValue('8')
  await expect(page.getByRole('textbox', { name: `Работа А2: ${studentName}` })).toHaveValue('6')
  await expect(page.getByRole('textbox', { name: `Экзамен 1: ${studentName}` })).toHaveValue('9')
  await expect(page.getByRole('textbox', { name: `Посещено занятий: ${studentName}` })).toHaveValue('8')
  await expect(rowA.getByText('8 из 10')).toBeVisible()

  await page.getByRole('button', { name: 'Изменить категории' }).click()
  await expect(editor).toBeVisible()
  await editor.getByRole('radio', { name: 'Среднее арифметическое' }).check()
  await editor.getByRole('checkbox', { name: 'Учитывать автоматическую посещаемость в среднем' }).check()
  await editor.getByRole('button', { name: 'Сохранить' }).click()
  await expect(editor).toBeHidden()
  await expect(page.getByRole('status').filter({ hasText: 'Настройки сохранены.' })).toBeVisible()
  const composition = /В расчёт входят:.*Работа А1.*Работа А2.*Экзамен 1.*Посещаемость/
  await expect(page.getByRole('region', { name: /Ведомость:/ }).getByText(composition)).toBeVisible()
  await expect(page.getByRole('textbox', { name: `Работа А1: ${studentName}` })).toHaveValue('8')
  await expect(rowA.getByText('8 из 10')).toBeVisible()

  await selectPeriod(page, names.periodB)
  await expect(page.getByRole('heading', { name: new RegExp(names.periodB) })).toBeVisible()
  await expect(page.getByRole('textbox', { name: `Работа А1: ${studentName}` })).toHaveValue('3')
  await expect(page.getByRole('textbox', { name: `Посещено занятий: ${studentName}` })).toHaveValue('1')
  await expect(rowB.getByText('1 из 5')).toBeVisible()
  await expect(page.getByRole('region', { name: /Ведомость:/ }).getByText(composition)).toBeVisible()
})
