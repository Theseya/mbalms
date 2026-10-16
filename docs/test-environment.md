# Локальная тестовая PostgreSQL

Отдельный стек `mbalms-test`, не рабочий сайт и не `docker-compose.yml`.

Данные кластера лежат в **именованном volume** `mbalms-worksheet-check-data`, смонтированном в `/var/lib/postgresql/data` (PGDATA образа `postgres:17`). **tmpfs на этом пути ставить нельзя**: restart контейнера тогда заново вызывает `initdb` и теряется база приложения.

## Поднять заново

1. Скопировать `.env.test.example` → `.env.test` и задать `TEST_POSTGRES_PASSWORD` (файл в git не входит).
2. Из корня репозитория:

```powershell
.\scripts\up-test-db.ps1
```

Скрипт:

- создаёт volume `mbalms-worksheet-check-data`, если его нет (никогда не удаляет);
- перед переиспользованием существующего контейнера проверяет: named volume на фактическом PGDATA, на PGDATA нет tmpfs, порт опубликован на `127.0.0.1` из `.env.test`; при несовпадении останавливается и ничего не удаляет;
- переиспользует уже существующий контейнер `mbalms-worksheet-check` (`docker start`, без `rm`);
- если контейнера нет — `compose up -d` с `external` volume;
- создаёт базу приложения `worksheet_manual`, если её ещё нет (`OWNER` в кавычках: роль `check` — зарезервированное слово); `psql` с `ON_ERROR_STOP`, коды выхода docker/psql проверяются.

Повторный запуск того же скрипта должен написать `Reusing running container` / `Application database worksheet_manual already exists` и не требовать ручного удаления контейнера.

Эквивалент без скрипта:

```powershell
docker compose -p mbalms-test -f docker-compose.test.yml --env-file .env.test up -d
```

Затем создать `worksheet_manual` вручную, если кластер пустой.

## API и e2e

Локальный API (не контейнер `mbalms-api-1`):

- `ConnectionStrings__Default`: Host=`127.0.0.1`, Port=`55433`, Database=`worksheet_manual`, Username из `.env.test`;
- `Database__MigrateOnStartup=false` для долгоживущей `worksheet_manual` (у неё нет `__EFMigrationsHistory`; полный Migrate нельзя).
- `Seed__ManagerEmail` / `Seed__ManagerPassword` — только в окружении процесса, не в git;
- `ASPNETCORE_ENVIRONMENT=Development`, `http://127.0.0.1:5001`.

Development overlay (`appsettings.Development.json` + профиль `http` в `launchSettings.json`), не рабочий `.env` и не `appsettings.json`:

- `Email:PublicBaseUrl` = `http://127.0.0.1:5173` — ссылки приглашения и восстановления из Development-outbox открывают локальный Vite без подмены host;
- `Email:Provider` = `Development` (outbox, не SMTP).

Лимиты входа и писем в Development **не** повышены: действуют значения `appsettings.json` (20 логинов / мин, 5 писем / 15 мин). Для полного Playwright-набора запускайте локальный API с явными переменными процесса — профиль `e2e` в `launchSettings.json`, см. `e2e/README.md`. Production не менять.

Реальный SMTP в этой среде не проверяется.

Фронт: Vite `http://127.0.0.1:5173` с прокси на API.

Playwright: каталог `e2e/`, см. `e2e/README.md`. Карта покрытия по пунктам меню: `docs/e2e-coverage.md`. Переменная `E2E_MANAGER_PASSWORD` совпадает с seed менеджера. Для проверки ПУД после `docker restart` того же контейнера: `E2E_RESTART_TEST_DB=1`.

Кабинет `/admin` на этой среде: роли Owner и AccountAdmin для Playwright выдаёт `POST /api/dev/staff-fixture` только если процесс API запущен с явным `Test:StaffFixture=true` (профиль `e2e` в `launchSettings.json`, не профиль `http`, не `appsettings*.json`, не рабочий `.env`). Имя среды Development само по себе fixture не включает. В Production обработчик не регистрируется, даже если флаг выставить. Это не сценарий production и не ручное `bootstrap-owner`. Реального Owner здесь не создаём, пока это не разрешено отдельно.

Миграция `20261016124814_AddAdminAudit` в **новых** Testcontainers-БД применяется через EF `Migrate` и проверяется тестом на пустой базе. Долгоживущая `worksheet_manual` **не** ведёт `__EFMigrationsHistory`. Таблица `AdminAuditEntries` на этом стенде однажды добавлена вручную тем же DDL, что в миграции (контейнер `mbalms-worksheet-check`, не `mbalms-db-1`). Это особенность текущего тестового кластера, **не** рекомендуемый способ выкладки. На рабочую БД миграцию без отдельного разрешения выкладки не накатывать.

## Сохранность данных

- `docker restart mbalms-worksheet-check` — тот же контейнер, данные на volume сохраняются.
- Пересоздание контейнера с тем же volume (`docker compose -p mbalms-test -f docker-compose.test.yml up -d --force-recreate`) — данные сохраняются, `initdb` не должен повторяться.
- **Не выполнять** `docker compose down -v` и `docker volume rm mbalms-worksheet-check-data`, если данные нужны.
- Рабочий проект `mbalms` и volumes `mbalms_db-data`, `mbalms_api-keys`, `mbalms_documents` этим файлом не трогать.

Кластерный `POSTGRES_DB` (`check`) — служебная база образа. Приложение использует `worksheet_manual`. `/health` API — liveness процесса; в Development/Testing `GET /ready` проверяет именно настроенную базу приложения.
