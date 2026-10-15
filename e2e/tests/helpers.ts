import { expect, type Page } from '@playwright/test'

export const managerEmail = process.env.E2E_MANAGER_EMAIL ?? 'manager@mba.local'

/** Unique label for this process; tests must not rely on leftover rows from earlier runs. */
export function unique(prefix: string) {
  return `${prefix}-${Date.now()}-${Math.floor(Math.random() * 1e6)}`
}

export function managerPassword(): string {
  const value = process.env.E2E_MANAGER_PASSWORD
  if (!value) throw new Error('Set E2E_MANAGER_PASSWORD for the local test manager account.')
  return value
}

export async function loginAsManager(page: Page) {
  await page.goto('/login')
  if (page.url().includes('/manager/')) return
  await page.getByLabel('Email').fill(managerEmail)
  await page.getByLabel('Пароль').fill(managerPassword())
  await page.getByRole('button', { name: 'Войти' }).click()
  await expect(page).toHaveURL(/\/manager\//)
}

export type SheetNames = {
  group: string
  discipline: string
  period: string
}

export async function createGroup(page: Page, name: string) {
  await page.goto('/manager/groups')
  await page.getByRole('button', { name: 'Новая группа' }).click()
  const dialog = page.getByRole('dialog', { name: 'Новая группа' })
  await dialog.getByLabel('Название').fill(name)
  await dialog.getByRole('button', { name: 'Сохранить' }).click()
  await expect(dialog).toBeHidden()
}

export async function createStudent(page: Page, opts: {
  last: string
  first: string
  email: string
  group: string
  password?: string
}) {
  await page.goto('/manager/students')
  await page.getByRole('button', { name: 'Новый студент' }).click()
  const dialog = page.getByRole('dialog', { name: 'Новый студент' })
  await dialog.getByLabel('Фамилия').fill(opts.last)
  await dialog.getByLabel('Имя').fill(opts.first)
  await dialog.getByLabel('Email').fill(opts.email)
  await dialog.getByLabel('Группа').selectOption({ label: opts.group })
  await dialog.getByLabel('Пароль для входа').fill(opts.password ?? 'E2e-Student-1')
  await dialog.getByRole('button', { name: 'Сохранить' }).click()
  await expect(dialog).toBeHidden()
}

export async function createDiscipline(page: Page, name: string) {
  await page.goto('/manager/disciplines')
  await page.getByRole('button', { name: 'Новая дисциплина' }).click()
  const dialog = page.getByRole('dialog', { name: 'Новая дисциплина' })
  await dialog.getByLabel('Название').fill(name)
  await dialog.getByRole('button', { name: 'Сохранить' }).click()
  await expect(dialog).toBeHidden()
}

export async function createPeriod(page: Page, name: string) {
  await page.goto('/manager/periods')
  await page.getByRole('button', { name: 'Новый период' }).click()
  const dialog = page.getByRole('dialog', { name: 'Новый период' })
  await dialog.getByLabel('Название').fill(name)
  await dialog.getByRole('button', { name: 'Сохранить' }).click()
  await expect(dialog).toBeHidden()
}

export async function openSheet(page: Page, names: SheetNames) {
  await page.goto('/manager/worksheet')
  await page.getByLabel('Дисциплина').selectOption({ label: names.discipline })
  await page.getByLabel('Группа').selectOption({ label: names.group })
  const period = page.getByLabel('Учебный период')
  const periodValue = await period.locator('option').filter({ hasText: names.period }).first().getAttribute('value')
  if (!periodValue) throw new Error(`Period «${names.period}» not found.`)
  await period.selectOption(periodValue)
  await expect(page.getByRole('heading', { name: new RegExp(`Ведомость:.*${escapeRegExp(names.discipline)}`) })).toBeVisible()
}

export function settingsDialog(page: Page) {
  return page.getByRole('dialog', { name: 'Элементы контроля и посещаемость' })
}

export function confirmDialog(page: Page) {
  return page.getByRole('dialog', { name: 'Подтвердить удаление' })
}

export function isDisciplineSettingsPut(url: string) {
  return /\/api\/manager\/worksheets\/disciplines\/[^/]+\/settings/.test(url)
}

function escapeRegExp(value: string) {
  return value.replace(/[.*+?^${}()|[\]\\]/g, '\\$&')
}
