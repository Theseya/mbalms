# Частичная проверка безопасности LMS

Это **предварительный отчёт**, не завершённый аудит и **не соответствие OWASP ASVS 5.0.0 L2**.
Отсутствие найденной проблемы не считать доказательством безопасности.

Стандарт для оценки разрывов: [OWASP ASVS 5.0.0](https://github.com/OWASP/ASVS/releases/tag/v5.0.0_release) (май 2025), целевой уровень L1+L2.
Принятый риск и допуск к эксплуатации **не равны** выполнению требования.

Продуктовый код, на который опирается этот документ: **HEAD `74a4b46d6f08b325915aad859cc681d411ab7441`** (`cursor/grade-history`).
Новые интеграционные тесты на момент записи ещё не в коммите.

Рабочий сайт, рабочая БД, `worksheet_manual`, SMTP и дампы этими прогонами не использовались.

## Предварительные выводы чтения кода

Роли входа: Owner, AccountAdmin, Manager, Student. FallbackPolicy требует аутентификацию; публичны CSRF, login, forgot/set-password, `/health`; `/ready` монтируется только вне Production. `/api/admin/*` — Owner или AccountAdmin; учебные API без учебных ролей закрыты. Первый Owner не создаётся при обычном старте.

Студенческие API якорятся на профиле текущего пользователя: свои опубликованные оценки, расписание своей группы, видимые объявления и опросы, ПУД по расписанию или своей опубликованной оценке. Менеджер видит и меняет всю базу — изоляции между менеджерами нет (модель продукта).

Сессия: cookie `mbalms.auth`, HttpOnly, SameSite=Strict, 8 ч sliding. CSRF на изменяющих запросах. Invite/reset — токен Identity в письме, не в JSON API. В Production `Email:Provider` по умолчанию `None` (отправка закрыта).

Известные разрывы со стандартом (не закрыты этим пакетом):

- **6.3.3 (L2):** MFA обязателен для **доступа к приложению**, не только для менеджера. Реализован только пароль.
- **12.2.1 (L1):** TLS клиент ↔ внешний HTTP. В репозитории nginx слушает `:80`, `web` публикуется на `${WEB_PORT:-8080}:80`.
- Пароли: обязательный минимум 8 символов есть; частые/утечные пароли и список контекстных слов — нет.
- Документация сессий, абсолютный lifetime, MFA, внутренний TLS web→api и api→db.

Транспорт: риск перехвата существует, **если** клиент ходит на этот HTTP-порт по сети, где трафик могут читать. Перехват как совершённое событие **не установлен**. Наличие внешнего TLS-терминатора **не установлено**.

## Последовательность запусков

Изоляция тестов: `ApiFactory` поднимает временный `postgres:17-alpine` (Testcontainers), `ConnectionStrings:Default` только от этого контейнера, `Email:Provider=Development` (память, не SMTP), `WebApplicationFactory` без обращения к `:8080`.

### 1. Согласованный пакет существующих тестов (фильтр 142)

```powershell
dotnet test "backend\tests\MbaLms.Api.Tests\MbaLms.Api.Tests.csproj" --filter "FullyQualifiedName~AuthorizationTests|FullyQualifiedName~PasswordInviteTests|FullyQualifiedName~DisciplineProgramTests|FullyQualifiedName~GradeTests|FullyQualifiedName~GradeModuleTests|FullyQualifiedName~GradeImportTests|FullyQualifiedName~WorkSheetTests|FullyQualifiedName~AnnouncementTests|FullyQualifiedName~NotificationTests|FullyQualifiedName~AuthTests"
```

Классы: `AuthorizationTests`, `PasswordInviteTests`, `DisciplineProgramTests`, `GradeTests`, `GradeModuleTests`, `GradeImportTests`, `WorkSheetTests` (не `WorkSheetRulesTests`), `AnnouncementTests`, `NotificationTests`, `AuthTests`.

| Запуск | Результат |
|---|---|
| Первая попытка | Сборка не началась: `MbaLms.Api.exe` держал локальный процесс на `127.0.0.1:5001` (`dotnet run --launch-profile e2e`). Тесты не выполнялись. |
| После остановки только этого процесса | **Passed 142, Failed 0, Skipped 0, Total 142**, ~29 с |

Локальный API после остановки не поднимался.

### 2. Новые негативные тесты

```powershell
dotnet test "backend\tests\MbaLms.Api.Tests\MbaLms.Api.Tests.csproj" --filter "FullyQualifiedName~LoginDefenseTests|FullyQualifiedName~OverpostTests|FullyQualifiedName~PasswordChangeSessionTests|FullyQualifiedName~ImportSessionIsolationTests"
```

| Запуск | Результат |
|---|---|
| Первый | Ошибка компиляции теста CS1061 в `OverpostTests` (не `await` у `StudentAsync`). Продукт не менялся. |
| После правки теста | **Passed 9, Failed 0, Skipped 0, Total 9**, ~5 с |

### 3. Совместный прогон (этот этап, один раз)

Тот же фильтр, что в п. 1, плюс четыре новых класса.

**Passed 151, Failed 0, Skipped 0, Total 151**, ~30 с.

Ожидание 142+9=151 совпало с фактическим числом раннера. Автоповторов не было.

## Новые тесты

Файлы (ещё не в коммите на момент записи):

| Файл | Методы |
|---|---|
| `backend/tests/MbaLms.Api.Tests/LoginDefenseTests.cs` | `Five_wrong_passwords_lock_only_that_account`; `Login_rate_limit_rejects_further_attempts_from_the_same_client_with_429` |
| `backend/tests/MbaLms.Api.Tests/OverpostTests.cs` | `Grade_create_ignores_status_and_stays_a_draft`; `Grade_update_ignores_status_and_foreign_studentId`; `Student_create_ignores_role_and_does_not_grant_manager`; `Announcement_create_ignores_status_and_stays_a_draft`; `Student_cannot_create_a_grade_by_posting_manager_fields` |
| `backend/tests/MbaLms.Api.Tests/PasswordChangeSessionTests.cs` | `Changing_password_ends_the_other_session_of_the_same_user` |
| `backend/tests/MbaLms.Api.Tests/ImportSessionIsolationTests.cs` | `Other_manager_cannot_confirm_foreign_import_and_nothing_is_written` |

Хелпер: `TestBase.CreateManagerAsync` — второй менеджер через `UserManager` в **той же изолированной** Testcontainers-БД (`Factory.Services`). Существующие проверки TestBase не ослаблялись. Продуктовый код и Production-конфигурация не менялись.

Тесты кроме HTTP проверяют состояние: роли в Identity, Draft/владелец оценки, пустой список студента, счётчик оценок, отсутствие группы после чужого confirm, отказ старого пароля, вход другого аккаунта при lockout.

## Подтверждённые свойства (в границах этих тестов)

- Аноним и cross-role 403/401 на зарегистрированных маршрутах (существующий `AuthorizationTests`).
- Студент не читает чужие оценки, расписание, опросы, уведомления, чужой ПУД.
- Черновик оценки и черновик объявления студенту не видны.
- CSRF на публичных POST.
- Invite: токен не в JSON API; reuse/stamp отклоняются.
- Импорт оценок не меняет published; столбец пароля отвергается.
- 5 неверных паролей блокируют **этот** аккаунт (`locked_out` на верный пароль); другой аккаунт входит. Это не 429.
- Лимитер входа на **derived-хосте с 3/мин** отвечает 429 на 4-й запрос. **Порог 3/мин проверяет механизм, но не рабочую настройку 20/мин** (`appsettings.json` / compose).
- Extra JSON `status` / `role` / чужой `studentId` на выбранных DTO не публикует оценку, не выдаёт роль Manager, не перевешивает оценку.
- Смена пароля в одной сессии делает вторую сессию того же пользователя недействительной.
- Второй менеджер не подтверждает чужой `importId` групп; группа не создаётся, пока confirm не сделает владелец.

## Непроверенные области

MFA, HTTPS/HSTS, внутренний TLS, CSP, lockout ровно на 5-й vs 6-й неверный ввод как отдельный факт, rate limit **20/мин** как в Production, обход `X-Forwarded-For`, zip-bomb/polyglot PDF, антивирус, over-post на всех остальных DTO, уведомления о входе, смена email студентом (API нет), журналы security-событий (V16), CVE зависимостей, рабочий сайт, дампы и права на файлы хоста.

## Локальное завершение `/admin` (без выкладки)

Рабочий сайт, рабочая БД, SMTP, `.env` и контейнеры `mbalms` не использовались. Playwright этого этапа ходил в изолированные Vite `:5173` и API `:5001` против `worksheet_manual` на `127.0.0.1:55433`.

Найденная проблема: приглашение менеджера в e2e отвечало 500 (`relation "AdminAuditEntries" does not exist`). `worksheet_manual` не ведёт `__EFMigrationsHistory`; полный `MigrateOnStartup` на неё не включался. Таблица создана тем же DDL, что в `AddAdminAudit`, только в контейнере `mbalms-worksheet-check`. Живая БД не менялась.

### Уточнение 433 / 192 и Playwright 23

Это **не** один вид тестов. API 433 и frontend 192 — предыдущий полный прогон этого этапа (после него API/Vitest не перезапускались). Playwright 23 — отдельный финальный прогон после восстановления блокировки через UI.

| Число | Что это | Команда | Среда |
|---|---|---|---|
| **433** | xUnit + WebApplicationFactory + Testcontainers PostgreSQL | `dotnet test "backend\tests\MbaLms.Api.Tests\MbaLms.Api.Tests.csproj"` | временный `postgres:17-alpine`, не `:8080` и не `worksheet_manual` |
| **192** | Vitest + React Testing Library | `cd frontend; npm test` | **jsdom**, не Chromium |
| **23** | Playwright Chromium после восстановления кнопки «Заблокировать» | `cd e2e; npx playwright test` | изолированные Vite `:5173` и API `:5001` против `worksheet_manual`; без `_tmp_*`, один прогон |

Frontend-192 нельзя называть браузерными тестами. Выборочный `tests/admin.spec.ts` полным прогоном не является. Прогон 23 с блоком через API UI-блокировку не покрывал.

### Последний Owner — имеющиеся тесты, не будущие

Класс `AdminAccessTests`:

| Тест | Что проверяет | Чего нет |
|---|---|---|
| `Last_owner_cannot_be_blocked_revoked_or_deleted` | последовательно 409 `last_owner` на block, revoke-owner и DELETE | — |
| `Parallel_block_of_two_owners_keeps_an_effective_owner` | два одновременных **block**; ≥1 действующий Owner | — |
| `Parallel_revoke_of_two_owners_keeps_an_effective_owner` | два одновременных revoke-owner; ≥1 Owner в БД | — |
| `Parallel_delete_of_two_owners_keeps_an_effective_owner` | два одновременных DELETE; ≥1 Owner, учётки на месте | — |
| `Parallel_mixed_block_revoke_delete_keeps_an_effective_owner` | смесь block/revoke/delete; ≥1 Owner | — |

DELETE Owner **не** готовая функция удаления: при наличии другого Owner ответ 403 `protected_account`, учётка не удаляется. В UI кнопок снятия роли и удаления нет.

`POST /api/dev/staff-fixture` включается только явным `Test:StaffFixture` и не регистрируется в Production (флаг в `appsettings*.json` и рабочем `.env` отсутствует). Имя среды Development само по себе обработчик не открывает.

## Следующий приоритет

1. Письменное решение по MFA (**6.3.3** на все роли входа) и по TLS внешнего HTTP (**12.2.1**), либо внедрение — иначе L2 (и даже L1 по транспорту) недостижимы.
2. Не смешивать допуск к эксплуатации с соответствием ASVS.
3. При необходимости — отдельный тест лимита входа **20/мин** или документировать, что проверен только механизм на 3/мин.
