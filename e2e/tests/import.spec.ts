import { expect, test } from '@playwright/test'
import { groupsWorkbookBytes, loginAsManager, unique } from './helpers'

test('broken xlsx is a clear import error; a valid groups workbook still imports', async ({ page }) => {
  const importedGroup = `E2E-${unique('imp')}-импорт`
  await loginAsManager(page)
  await page.goto('/manager/groups')
  await page.getByRole('button', { name: 'Импорт' }).click()
  const dialog = page.getByRole('dialog', { name: 'Импорт из Excel' })
  await expect(dialog).toBeVisible()

  await dialog.locator('input[type="file"]').setInputFiles({
    name: 'not-excel.xlsx',
    mimeType: 'application/vnd.openxmlformats-officedocument.spreadsheetml.sheet',
    buffer: Buffer.from('this is not a workbook'),
  })
  await expect(dialog.getByRole('alert')).toContainText(
    'Файл не подходит для импорта. Проверьте шаблон и формат .xlsx.',
  )
  await expect(dialog.getByText('Ошибка сервера. Попробуйте позже.')).toHaveCount(0)

  await dialog.locator('input[type="file"]').setInputFiles({
    name: 'groups.xlsx',
    mimeType: 'application/vnd.openxmlformats-officedocument.spreadsheetml.sheet',
    buffer: groupsWorkbookBytes(importedGroup),
  })
  await expect(dialog.getByText(/К созданию: 1/)).toBeVisible()
  await dialog.getByRole('button', { name: 'Подтвердить импорт' }).click()
  const done = page.getByRole('dialog', { name: 'Импорт завершён' })
  await expect(done).toBeVisible()
  await done.locator('button.btn-primary', { hasText: 'Закрыть' }).click()
  await page.getByLabel('Поиск').fill(importedGroup)
  await expect(page.getByRole('cell', { name: importedGroup })).toBeVisible()
})
