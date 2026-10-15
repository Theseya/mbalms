import { expect, test } from '@playwright/test'
import {
  createDiscipline,
  createGroup,
  createStudent,
  loginAsManager,
  unique,
} from './helpers'

test('student sees schedule, submits a survey, and changes password', async ({ page }) => {
  const stamp = unique('stu')
  const names = {
    group: `E2E-${stamp}-группа`,
    teacherLast: `Е2е${stamp.slice(-6)}`,
    teacherFirst: 'Иван',
    teacherName: '',
    discipline: `E2E-${stamp}-дисц`,
    survey: `E2E-${stamp}-опрос`,
    studentEmail: `e2e.stu.${stamp}@test.local`,
  }
  names.teacherName = `${names.teacherLast} ${names.teacherFirst}`
  const studentPassword = 'E2e-Student-1'
  const changedPassword = 'E2e-Student-2'

  await loginAsManager(page)
  await createGroup(page, names.group)
  await createDiscipline(page, names.discipline)
  await createStudent(page, {
    last: 'Е2едоп',
    first: 'Анна',
    email: names.studentEmail,
    group: names.group,
    password: studentPassword,
  })

  await page.goto('/manager/teachers')
  await page.getByRole('button', { name: 'Новый преподаватель' }).click()
  const teacher = page.getByRole('dialog', { name: 'Новый преподаватель' })
  await teacher.getByLabel('Фамилия').fill(names.teacherLast)
  await teacher.getByLabel('Имя').fill(names.teacherFirst)
  await teacher.getByRole('button', { name: 'Сохранить' }).click()
  await expect(teacher).toBeHidden()

  await page.goto('/manager/schedule')
  await page.getByRole('button', { name: 'Новое занятие' }).click()
  const lesson = page.getByRole('dialog', { name: 'Новое занятие' })
  await lesson.getByLabel('Группа').selectOption({ label: names.group })
  await lesson.getByLabel('Дисциплина').selectOption({ label: names.discipline })
  await lesson.getByLabel('Преподаватель').selectOption({ label: names.teacherName })
  await lesson.getByLabel('Дата').fill('2031-04-10')
  await lesson.getByLabel('Начало').fill('12:00')
  await lesson.getByLabel('Окончание').fill('13:30')
  await lesson.getByRole('button', { name: 'Сохранить' }).click()
  await expect(lesson).toBeHidden()

  await page.goto('/manager/surveys/new')
  await page.getByLabel('Тип *').selectOption({ label: 'Сервисный опрос' })
  await page.getByLabel('Группа').selectOption({ label: names.group })
  await page.getByLabel('Название').fill(names.survey)
  await page.getByLabel('Текст вопроса').fill('Насколько удобен кабинет?')
  await page.getByRole('button', { name: 'Сохранить', exact: true }).click()
  await expect(page).toHaveURL(/\/manager\/surveys\/[0-9a-f-]+/)
  await page.getByRole('button', { name: 'Опубликовать' }).click()
  const publish = page.getByRole('dialog', { name: 'Опубликовать опрос' })
  await publish.getByRole('button', { name: 'Опубликовать' }).click()
  await expect(publish).toBeHidden()
  await expect(page.getByText('Открыт', { exact: true })).toBeVisible()

  await page.getByRole('button', { name: 'Выйти' }).click()
  await page.getByLabel('Email').fill(names.studentEmail)
  await page.getByLabel('Пароль').fill(studentPassword)
  await page.getByRole('button', { name: 'Войти' }).click()
  await expect(page).toHaveURL(/\/student/)

  await page.goto('/student/schedule')
  await expect(page.getByRole('heading', { name: names.discipline })).toBeVisible()
  await expect(page.getByText(names.teacherName)).toBeVisible()
  await expect(page.getByText(/12:00/)).toBeVisible()

  await page.goto('/student/surveys')
  await expect(page.getByRole('heading', { name: names.survey })).toBeVisible()
  await page.getByRole('link', { name: 'Ответить' }).click()
  await page.getByRole('radio', { name: '5' }).check()
  await page.getByRole('button', { name: 'Отправить ответ' }).click()
  await expect(page.getByText(/Спасибо! Ваш ответ отправлен/)).toBeVisible()
  await page.reload()
  await expect(page.getByText(/Спасибо! Ваш ответ отправлен/)).toBeVisible()

  await page.goto('/account')
  await page.getByLabel('Текущий пароль').fill(studentPassword)
  await page.getByLabel('Новый пароль').fill(changedPassword)
  await page.getByRole('button', { name: 'Сохранить' }).click()
  await expect(page.getByText('Пароль изменён')).toBeVisible()
  await page.getByRole('button', { name: 'Выйти' }).click()
  await page.getByLabel('Email').fill(names.studentEmail)
  await page.getByLabel('Пароль').fill(changedPassword)
  await page.getByRole('button', { name: 'Войти' }).click()
  await expect(page).toHaveURL(/\/student/)
})
