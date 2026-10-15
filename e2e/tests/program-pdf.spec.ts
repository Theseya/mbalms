import { execSync } from 'node:child_process'
import { expect, test } from '@playwright/test'
import { loginAsManager, unique } from './helpers'

test.describe.configure({ mode: 'serial' })

const stamp = unique('ПУД')
const names = { discipline: `E2E-${stamp}` }
const marker = `% e2e-pud-${stamp}`
const pdf = Buffer.from(`%PDF-1.7\n${marker}\n1 0 obj << /Type /Catalog >> endobj\ntrailer << /Root 1 0 R >>\n%%EOF\n`)

async function openDisciplineEditor(page: import('@playwright/test').Page) {
  await page.goto('/manager/disciplines')
  await page.getByLabel('Поиск').fill(names.discipline)
  await page.getByRole('button', { name: 'Изменить' }).click()
  const edit = page.getByRole('dialog', { name: 'Редактирование дисциплины' })
  await expect(edit).toBeVisible()
  return edit
}

test('PUD PDF upload is stored and still visible after reload', async ({ page }) => {
  await loginAsManager(page)
  await page.goto('/manager/disciplines')
  await page.getByRole('button', { name: 'Новая дисциплина' }).click()
  const create = page.getByRole('dialog', { name: 'Новая дисциплина' })
  await create.getByLabel('Название').fill(names.discipline)
  await create.getByRole('button', { name: 'Сохранить' }).click()
  await expect(create).toBeHidden()

  const edit = await openDisciplineEditor(page)
  await edit.locator('input[type="file"]').setInputFiles({ name: 'e2e-pud.pdf', mimeType: 'application/pdf', buffer: pdf })
  await expect(edit.getByText('e2e-pud.pdf')).toBeVisible()
  await edit.getByRole('button', { name: 'Закрыть' }).click()
  await expect(page.getByRole('cell', { name: 'e2e-pud.pdf' })).toBeVisible()

  await page.reload()
  await page.getByLabel('Поиск').fill(names.discipline)
  await expect(page.getByRole('cell', { name: 'e2e-pud.pdf' })).toBeVisible()
})

test('PUD opens and downloaded bytes match the uploaded file', async ({ page }) => {
  if (process.env.E2E_RESTART_TEST_DB === '1') {
    execSync('docker restart mbalms-worksheet-check', { stdio: 'pipe' })
    for (let i = 0; i < 40; i++) {
      try {
        execSync('docker exec mbalms-worksheet-check pg_isready -U check -d worksheet_manual', { stdio: 'pipe' })
        break
      } catch {
        await page.waitForTimeout(500)
      }
    }
    await expect.poll(async () => (await page.request.get('http://127.0.0.1:5001/ready')).ok(), { timeout: 20_000 }).toBe(true)
  }

  await loginAsManager(page)
  const edit = await openDisciplineEditor(page)
  await expect(edit.getByText('e2e-pud.pdf')).toBeVisible()
  await edit.getByRole('button', { name: 'Просмотреть ПУД' }).click()
  const openTab = edit.getByRole('link', { name: 'Открыть в новой вкладке' })
  await expect(openTab).toBeVisible()
  const href = await openTab.getAttribute('href')
  expect(href).toMatch(/\/api\/manager\/disciplines\/.+\/program/)
  const downloaded = await page.request.get(href!)
  expect(downloaded.ok()).toBe(true)
  expect(downloaded.headers()['content-type'] ?? '').toMatch(/pdf/i)
  expect(Buffer.from(await downloaded.body()).equals(pdf)).toBe(true)
})
