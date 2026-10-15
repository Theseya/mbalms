# Карта покрытия браузерных e2e

Частичное покрытие кабинета менеджера и студента, не полная проверка системы.
Рабочий сайт, рабочая БД и SMTP этими тестами не трогаются.

## Среда и запуск

| Что | Значение |
|---|---|
| Фронт | Vite `http://127.0.0.1:5173` |
| API | `dotnet run --project backend/src/MbaLms.Api --launch-profile e2e` на `http://127.0.0.1:5001` |
| БД | контейнер `mbalms-worksheet-check`, база `worksheet_manual`, порт `127.0.0.1:55433` |
| Live | `mbalms-web-1` `:8080` и `mbalms-db-1` `:5433` не использовать |
| Пароль менеджера | `E2E_MANAGER_PASSWORD` в окружении процесса, не в git |

Как поднять тестовую PostgreSQL: `docs/test-environment.md`.
Как ставить Playwright и чем `e2e` отличается от `http`: `e2e/README.md`.

Полный сохранённый набор (без `_tmp_*.spec.ts`):

```powershell
cd e2e
npx playwright test
```

Полный сохранённый набор на этой среде: **20 passed** (до финального уточнения локаторов).
После уточнения повторно прошли `grades.spec.ts` и `worksheet-coverage.spec.ts`. Полный набор после уточнения заново не запускался.

## Пункты меню

### Менеджер

| Пункт | Действия в браузере | Файл / тест | Результат |
|---|---|---|---|
| Группы | создать, reload, поиск | `e2e/tests/core-paths.spec.ts` → `groups, students, disciplines and periods persist after reload` | pass |
| Студенты | создать с паролем, reload | тот же | pass |
| Преподаватели | создать, reload | `e2e/tests/more-modules.spec.ts` → `teachers, schedule, announcements, surveys and change-log persist after reload` | pass |
| Дисциплины | создать, reload | `e2e/tests/core-paths.spec.ts` | pass |
| Учебные периоды | создать, reload | `e2e/tests/core-paths.spec.ts` | pass |
| Расписание | создать занятие, reload | `e2e/tests/more-modules.spec.ts` | pass |
| Рабочая ведомость | Среднее→Взвешенный, балл, перенос ×10 в журнал | `e2e/tests/core-paths.spec.ts` → `worksheet average then weighted persist, scores save, transfer writes journal ×10` | pass |
| Рабочая ведомость | удаление работы: 409, отмена, явное подтверждение | `e2e/tests/worksheet-delete-confirm.spec.ts` | pass |
| Рабочая ведомость | две категории и три работы; автопосещаемость во взвешенном (8 из 10, балл 8, вклад 1,6, «Выполнено») и в среднем; reload; изоляция двух периодов | `e2e/tests/worksheet-coverage.spec.ts` → `worksheet keeps several works, auto attendance in both modes, reload and two periods` | pass |
| Ведомость | после переноса значение 80, статус черновик | `e2e/tests/core-paths.spec.ts` | pass |
| Оценки | черновик 70→85; студент не видит; публикация; студент видит 85, дисциплину и период; снятие публикации → черновик 85, у студента пусто | `e2e/tests/grades.spec.ts` → `manager draft is hidden until publish; unpublish hides it again; student is notified` | pass |
| Опросы | черновик; публикация и ответ студента | `e2e/tests/more-modules.spec.ts`, `e2e/tests/student-paths.spec.ts` | pass |
| Шаблоны опросов | сервисный шаблон; «Создать из шаблона» → черновик с тем же названием и вопросом | `e2e/tests/survey-templates.spec.ts` | pass |
| Объявления | сохранить черновик, reload | `e2e/tests/more-modules.spec.ts` | pass |
| Объявления | «Сохранить и опубликовать»; студент своей группы видит заголовок и текст; студент другой группы не видит | `e2e/tests/announcements-student.spec.ts` | pass |
| Журнал изменений | фильтр «Преподаватели» | `e2e/tests/more-modules.spec.ts` | pass |
| Профиль | смена пароля тестового менеджера, выход, вход новым, восстановление исходного | `e2e/tests/manager-account.spec.ts` | pass |

### Студент

| Пункт | Действия в браузере | Файл / тест | Результат |
|---|---|---|---|
| Главная | кабинет открывается; черновики оценок не видны | `e2e/tests/more-modules.spec.ts` → `student cabinet hides draft grades; forgot-password form is public` | pass |
| Расписание | занятие видно | `e2e/tests/student-paths.spec.ts` | pass |
| Оценки | пустой список без опубликованных; после публикации — значение, дисциплина, период; после снятия — снова пусто | `e2e/tests/more-modules.spec.ts`, `e2e/tests/grades.spec.ts` | pass |
| Опросы | открыть, ответить, reload | `e2e/tests/student-paths.spec.ts` | pass |
| Объявления | показано только объявление своей группы | `e2e/tests/announcements-student.spec.ts` | pass |
| Уведомления | после публикации оценки текст с дисциплиной и периодом; «Прочитано»; запись остаётся | `e2e/tests/grades.spec.ts` | pass |
| Профиль | смена пароля студента и повторный вход | `e2e/tests/student-paths.spec.ts` | pass |

### Прочее

| Тема | Файл / тест | Результат |
|---|---|---|
| Неверный пароль, границы ролей | `e2e/tests/auth.spec.ts` | pass |
| Приглашение и forgot-password через Development-outbox, ссылка на `127.0.0.1:5173` | `e2e/tests/access.spec.ts` | pass |
| Битый xlsx → понятная ошибка; импорт групп | `e2e/tests/import.spec.ts` | pass |
| Загрузка ПУД, reload, совпадение байт | `e2e/tests/program-pdf.spec.ts` | pass |
| Viewport 390×844 | `e2e/tests/mobile.spec.ts` | pass |

## Оставшиеся ограничения

Не гонялось в браузере:

- импорт оценок, массовая публикация, история оценки, правка уже опубликованной оценки с подтверждением;
- снятие объявления, закрепление, срок показа;
- шаблон «Оценка преподавания»; публикация опроса из шаблона и ответ студента;
- «Отметить все как прочитанные»; уведомления расписания и опросов;
- ручной ввод в журнале ведомости, экспорт, архив группы;
- EN-локаль; мобильные экраны новых страниц;
- SMTP и рабочий сайт.

Правила, с которыми сверялись сценарии: студент видит только опубликованные оценки; снятие публикации возвращает черновик и очищает `PublishedAt`; объявления — только опубликованные своей группы и без уведомлений; уведомление `GradePublished` при публикации оценки; балл посещаемости = посещено / всего × 10; настройки дисциплины общие, листы периодов раздельные; из шаблона копируется структура в черновик.
