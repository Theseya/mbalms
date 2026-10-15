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

- создаёт или переиспользует volume `mbalms-worksheet-check-data`;
- поднимает контейнер `mbalms-worksheet-check` на `127.0.0.1:55433`;
- создаёт базу приложения `worksheet_manual`, если её ещё нет (`OWNER` в кавычках: роль `check` — зарезервированное слово).

Эквивалент без скрипта:

```powershell
docker compose -p mbalms-test -f docker-compose.test.yml --env-file .env.test up -d
```

Затем создать `worksheet_manual` вручную, если кластер пустой.

## API и e2e

Локальный API (не контейнер `mbalms-api-1`):

- `ConnectionStrings__Default`: Host=`127.0.0.1`, Port=`55433`, Database=`worksheet_manual`, Username из `.env.test`;
- `Database__MigrateOnStartup=true`;
- `Seed__ManagerEmail` / `Seed__ManagerPassword` — только в окружении процесса, не в git;
- `ASPNETCORE_ENVIRONMENT=Development`, `http://127.0.0.1:5001`.

Фронт: Vite `http://127.0.0.1:5173` с прокси на API.

Playwright: каталог `e2e/`, см. `e2e/README.md`. Переменная `E2E_MANAGER_PASSWORD` совпадает с seed менеджера. Для проверки ПУД после `docker restart` того же контейнера: `E2E_RESTART_TEST_DB=1`.

## Сохранность данных

- `docker restart mbalms-worksheet-check` — тот же контейнер, данные на volume сохраняются.
- Пересоздание контейнера с тем же volume (`docker compose -p mbalms-test -f docker-compose.test.yml up -d --force-recreate`) — данные сохраняются, `initdb` не должен повторяться.
- **Не выполнять** `docker compose down -v` и `docker volume rm mbalms-worksheet-check-data`, если данные нужны.
- Рабочий проект `mbalms` и volumes `mbalms_db-data`, `mbalms_api-keys`, `mbalms_documents` этим файлом не трогать.

Кластерный `POSTGRES_DB` (`check`) — служебная база образа. Приложение использует `worksheet_manual`. `/health` API — liveness процесса; в Development/Testing `GET /ready` проверяет именно настроенную базу приложения.
