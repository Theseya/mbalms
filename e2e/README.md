# Браузерные e2e (Playwright)

Отдельный пакет, чтобы сборка Docker-образа `web` не тянула Playwright.

Нужны локальные Vite (`http://127.0.0.1:5173`) и API (`http://127.0.0.1:5001`) против отдельной тестовой PostgreSQL, не рабочего сайта. Как поднять БД: `docs/test-environment.md`. Пароль менеджера — в `E2E_MANAGER_PASSWORD`, не в репозитории.

Установка один раз: `npm install` в этом каталоге, затем `npx playwright install chromium`.

Локальный API в Development должен отдавать `Email:PublicBaseUrl=http://127.0.0.1:5173` (см. `docs/test-environment.md`), иначе ссылки из `/dev/mail` указывают на live `:8080`. Рабочий `.env` и SMTP не менять. Реальный SMTP не проверяется.

Повышенные лимиты — только в окружении процесса этого API, не в `appsettings.Development.json`. Перед полным набором:

```powershell
dotnet run --project backend/src/MbaLms.Api --launch-profile e2e
```

Профиль задаёт `RateLimiting__LoginPermitsPerMinute=1000`, `RateLimiting__PasswordMailPermitsPerWindow=1000`, `Auth__SessionValidationInterval=00:00:00` (чтобы блокировка сотрудника сразу отзывала cookie) и `Test__StaffFixture=true` (явный тестовый флаг; в профиле `http`, `appsettings*.json` и Production его нет). Обычный `dotnet run --launch-profile http` и Production остаются на 20 логинов / мин, 5 писем / 15 мин и интервал проверки сессии 1 минута; fixture там выключен. Рабочий `.env` и выкладку не использовать. Connection string процесса должен указывать на `127.0.0.1:55433` / `worksheet_manual` (`docs/test-environment.md`). На эту долгоживущую тестовую БД нельзя включать `Database:MigrateOnStartup`: в ней нет `__EFMigrationsHistory`.

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
| `tests/admin.spec.ts` | синтетический Owner (`Test:StaffFixture`, не `bootstrap-owner`) входит в `/admin`, приглашает менеджера через outbox, **блокирует кнопкой в таблице** (пагинация, не API-запрос), открытая сессия теряет доступ; Owner без учебной роли не открывает учебные кабинеты; AccountAdmin не действует на Owner/другого AccountAdmin; seed-менеджер не открывает админку |

Файлы `_tmp_*.spec.ts` в сохранённый набор не входят (`testIgnore` в `playwright.config.ts`).

Карта: `docs/e2e-coverage.md`. Выборочный `npx playwright test tests/admin.spec.ts` не заменяет полный `npx playwright test`.
