import { expect, test } from '@playwright/test'
import {
  createDiscipline,
  createGroup,
  createStudent,
  loginAsManager,
  unique,
} from './helpers'

test.describe.configure({ mode: 'serial' })

const stamp = unique('mod')
const names = {
  group: `E2E-${stamp}-группа`,
  teacherLast: `Е2е${stamp.slice(-6)}`,
  teacherFirst: 'Пётр',
  teacherName: '',
  discipline: `E2E-${stamp}-дисц`,
  announcement: `E2E-${stamp}-объявление`,
  survey: `E2E-${stamp}-опрос`,
  studentEmail: `e2e.mod.${stamp}@test.local`,
}
names.teacherName = `${names.teacherLast} ${names.teacherFirst}`

test('teachers, schedule, announcements, surveys and change-log persist after reload', async ({ page }) => {
  await loginAsManager(page)
  await createGroup(page, names.group)
  await createDiscipline(page, names.discipline)

  await page.goto('/manager/teachers')
  await page.getByRole('button', { name: 'Новый преподаватель' }).click()
  const teacher = page.getByRole('dialog', { name: 'Новый преподаватель' })
  await teacher.getByLabel('Фамилия').fill(names.teacherLast)
  await teacher.getByLabel('Имя').fill(names.teacherFirst)
  await teacher.getByRole('button', { name: 'Сохранить' }).click()
  await expect(teacher).toBeHidden()
  await page.reload()
  await page.getByLabel('Поиск').fill(names.teacherLast)
  await expect(page.getByRole('cell', { name: names.teacherName })).toBeVisible()

  await page.goto('/manager/schedule')
  await page.getByRole('button', { name: 'Новое занятие' }).click()
  const lesson = page.getByRole('dialog', { name: 'Новое занятие' })
  await lesson.getByLabel('Группа').selectOption({ label: names.group })
  await lesson.getByLabel('Дисциплина').selectOption({ label: names.discipline })
  await lesson.getByLabel('Преподаватель').selectOption({ label: names.teacherName })
  await lesson.getByLabel('Дата').fill('2031-03-15')
  await lesson.getByLabel('Начало').fill('10:00')
  await lesson.getByLabel('Окончание').fill('11:00')
  await lesson.getByRole('button', { name: 'Сохранить' }).click()
  await expect(lesson).toBeHidden()
  await expect(page.getByText(names.discipline).first()).toBeVisible()
  await page.reload()
  await expect(page.getByText(names.discipline).first()).toBeVisible()

  await page.goto('/manager/announcements/new')
  await page.getByLabel('Заголовок').or(page.getByLabel('Название объявления')).fill(names.announcement)
  await page.getByLabel('Текст').fill('Текст объявления для регрессии.')
  await page.getByRole('checkbox', { name: names.group }).check()
  await expect(page.getByRole('checkbox', { name: names.group })).toBeChecked()
  await page.getByRole('button', { name: 'Сохранить', exact: true }).click()
  await expect(page).toHaveURL(/\/manager\/announcements$/)
  await expect(page.getByRole('link', { name: names.announcement })).toBeVisible()
  await page.reload()
  await expect(page.getByRole('link', { name: names.announcement })).toBeVisible()

  await page.goto('/manager/surveys/new')
  await page.getByLabel('Тип *').selectOption({ label: 'Сервисный опрос' })
  await page.getByLabel('Группа').selectOption({ label: names.group })
  await page.getByLabel('Название').fill(names.survey)
  await page.getByLabel('Текст вопроса').fill('Насколько удобен кабинет?')
  await page.getByRole('button', { name: 'Сохранить', exact: true }).click()
  await expect(page).toHaveURL(/\/manager\/surveys\/[0-9a-f-]+/)
  await page.goto('/manager/surveys')
  await expect(page.getByRole('link', { name: names.survey })).toBeVisible()

  await page.goto('/manager/change-log')
  await expect(page.getByRole('heading', { name: 'Журнал изменений' })).toBeVisible()
  await page.getByLabel('Тип объекта').selectOption({ label: 'Преподаватели' })
  await expect(page.getByText(names.teacherName).first()).toBeVisible()
})

test('student cabinet hides draft grades; forgot-password form is public', async ({ page }) => {
  await loginAsManager(page)
  await createStudent(page, {
    last: 'Е2емод',
    first: 'Студент',
    email: names.studentEmail,
    group: names.group,
  })
  await page.getByRole('button', { name: 'Выйти' }).click()
  await page.getByLabel('Email').fill(names.studentEmail)
  await page.getByLabel('Пароль').fill('E2e-Student-1')
  await page.getByRole('button', { name: 'Войти' }).click()
  await expect(page).toHaveURL(/\/student/)
  await page.goto('/student/grades')
  await expect(page.getByText('Опубликованных оценок пока нет')).toBeVisible()
  await page.getByRole('button', { name: 'Выйти' }).click()
  await page.goto('/forgot-password')
  await expect(page.getByRole('heading', { name: 'Сброс пароля' })).toBeVisible()
  await expect(page.getByLabel('Email')).toBeVisible()
})
