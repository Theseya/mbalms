# Браузерные e2e (Playwright)

Отдельный пакет, чтобы сборка Docker-образа `web` не тянула Playwright.

Нужны локальные Vite (`http://127.0.0.1:5173`) и API (`http://127.0.0.1:5001`) против отдельной тестовой PostgreSQL, не рабочего сайта. Как поднять БД: `docs/test-environment.md`. Пароль менеджера — в `E2E_MANAGER_PASSWORD`, не в репозитории.

Установка один раз: `npm install` в этом каталоге, затем `npx playwright install chromium`.

Локальный API в Development должен отдавать `Email:PublicBaseUrl=http://127.0.0.1:5173` (см. `docs/test-environment.md`), иначе ссылки из `/dev/mail` указывают на live `:8080`. Рабочий `.env` и SMTP не менять. Реальный SMTP не проверяется.

Повышенные лимиты — только в окружении процесса этого API, не в `appsettings.Development.json`. Перед полным набором:

```powershell
dotnet run --project backend/src/MbaLms.Api --launch-profile e2e
```

Профиль задаёт `RateLimiting__LoginPermitsPerMinute=1000` и `RateLimiting__PasswordMailPermitsPerWindow=1000`. Обычный `dotnet run --launch-profile http` и Production остаются на 20 логинов / мин и 5 писем / 15 мин.

## Восемь основных путей

Тесты сами создают уникальные группы/студентов/дисциплины. Ручные фикстуры не нужны.

```powershell
npx playwright test tests/auth.spec.ts tests/core-paths.spec.ts tests/program-pdf.spec.ts tests/worksheet-delete-confirm.spec.ts
```

| Файл | Сценарии |
|---|---|
| `tests/auth.spec.ts` | неверный пароль; менеджер не открывает кабинет студента; студент не открывает менеджерские страницы |
| `tests/core-paths.spec.ts` | группы/студенты/дисциплины/периоды после reload; ведомость Среднее→Взвешенный; перенос ×10 в журнал |
| `tests/program-pdf.spec.ts` | загрузка ПУД, reload, открытие и совпадение байт |
| `tests/worksheet-delete-confirm.spec.ts` | настройки → баллы → удаление работы → 409 и диалог → отмена → явное подтверждение |

ПУД после restart того же тестового postgres (volume уже должен быть постоянным):

```powershell
$env:E2E_RESTART_TEST_DB = '1'
npx playwright test tests/program-pdf.spec.ts
```

## Дополнительно

`npx playwright test` без фильтра также гоняет:

| Файл | Сценарии |
|---|---|
| `tests/more-modules.spec.ts` | преподаватели, расписание, объявления, опросы, журнал изменений, кабинет студента, форма сброса пароля |
| `tests/mobile.spec.ts` | viewport 390×844 |
| `tests/import.spec.ts` | битый .xlsx → понятная ошибка; валидный файл групп импортируется |
| `tests/access.spec.ts` | приглашение и forgot-password через Development-outbox, ссылка на `127.0.0.1:5173`, установка пароля |
| `tests/student-paths.spec.ts` | расписание студента, отправка опроса, смена пароля в профиле |
| `tests/grades.spec.ts` | черновик оценки скрыт у студента; публикация показывает значение/дисциплину/период и уведомление; снятие публикации |
| `tests/worksheet-coverage.spec.ts` | несколько категорий и работ; автопосещаемость во взвешенном и среднем; reload; изоляция двух периодов |
| `tests/survey-templates.spec.ts` | создание шаблона и черновика опроса из шаблона |
| `tests/announcements-student.spec.ts` | опубликованное объявление видно только студенту своей группы |
| `tests/manager-account.spec.ts` | смена пароля тестового менеджера и повторный вход (пароль восстанавливается) |

Файлы `_tmp_*.spec.ts` в сохранённый набор не входят (`testIgnore` в `playwright.config.ts`).

Карта по пунктам меню: полный набор **20 passed** до финального уточнения локаторов; после уточнения повторно прошли `grades.spec.ts` и `worksheet-coverage.spec.ts`; полный набор после уточнения заново не запускался. Оставшиеся ограничения: `docs/e2e-coverage.md`.
