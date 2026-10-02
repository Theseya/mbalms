# MBA Mini-LMS

Веб-приложение для менеджера MBA-программы и её студентов: группы и архив, студенты, преподаватели, дисциплины, учебные периоды, расписание, оценки 0–100 с публикацией и историей изменений (видна только менеджеру), опросы (оценка преподавания и сервисные), уведомления в кабинете, выгрузки в Excel (.xlsx). Интерфейс на русском (по умолчанию) и английском.

| Часть | Технологии |
|---|---|
| Backend | .NET 10 (SDK 10.0.302, `global.json`), ASP.NET Core Web API, ASP.NET Core Identity, EF Core 10 + Npgsql, ClosedXML |
| БД | PostgreSQL 17 |
| Frontend | React 19, TypeScript 6, Vite 8, i18next, React Router 7 |
| Тесты | xUnit + WebApplicationFactory + Testcontainers (PostgreSQL); Vitest + React Testing Library |

Структура:

```
backend/src/MbaLms.Api          API (Domain, Data, Infrastructure, Controllers)
backend/tests/MbaLms.Api.Tests  интеграционные и unit-тесты
frontend/                       SPA (React + Vite), nginx-конфиг для Docker
docker-compose.yml              db + api + web
```

## Требования

- Docker Desktop (или Docker Engine + Compose v2) — для запуска в Docker и для тестов backend.
- Для режима разработки дополнительно: .NET SDK 10.0.302+ и Node.js 24 LTS (npm 11).

## Запуск в Docker (всё целиком)

```powershell
copy .env.example .env      # Linux/macOS: cp .env.example .env
```

Отредактируйте `.env`:

- `POSTGRES_PASSWORD` — надёжный пароль БД (без символа `;`).
- `MANAGER_EMAIL` / `MANAGER_PASSWORD` — учётная запись менеджера, создаётся при первом старте (пароль от 8 символов, с заглавной, строчной буквой и цифрой). Если пароль не задан, менеджер не создаётся, в логе API будет предупреждение.

```powershell
docker compose up -d --build
```

Откройте http://localhost:8080 (порт меняется переменной `WEB_PORT`). Миграции БД применяются автоматически при старте API.

После первого входа значение `MANAGER_PASSWORD` можно удалить из `.env`: учётная запись уже создана, повторный старт её не меняет. Сменить пароль можно в кабинете («Профиль»).

Студентов создаёт менеджер (раздел «Студенты»): он задаёт email и пароль и передаёт их студенту.

Остановка: `docker compose down` (данные сохраняются в volume `db-data`). Полное удаление данных: `docker compose down -v`.

## Режим разработки (SDK + БД в Docker)

1. БД:

   ```powershell
   copy .env.example .env   # если ещё нет; задайте POSTGRES_PASSWORD
   docker compose up -d db
   ```

   Postgres доступен на `localhost:5433` (только с локальной машины). Порт 5433 выбран, чтобы не конфликтовать с локально установленным PostgreSQL на 5432; меняется переменной `DB_PORT`.

2. Секреты API через user secrets (не попадают в репозиторий):

   ```powershell
   cd backend/src/MbaLms.Api
   dotnet user-secrets set "ConnectionStrings:Default" "Host=localhost;Port=5433;Database=mbalms;Username=mbalms;Password=<POSTGRES_PASSWORD из .env>"
   dotnet user-secrets set "Seed:ManagerEmail" "manager@example.com"
   dotnet user-secrets set "Seed:ManagerPassword" "<пароль менеджера>"
   cd ../../..
   ```

3. API (http://localhost:5001, Swagger UI: http://localhost:5001/swagger, OpenAPI: `/openapi/v1.json`):

   ```powershell
   dotnet run --project backend/src/MbaLms.Api --launch-profile http
   ```

   В Development миграции применяются при старте автоматически.

4. Frontend (http://localhost:5173, запросы `/api` проксируются на 5001; другой адрес задаётся `VITE_API_PROXY`):

   ```powershell
   cd frontend
   npm ci
   npm run dev
   ```

## Тесты и проверки

```powershell
dotnet test MbaLms.slnx          # требуется запущенный Docker: Testcontainers поднимает отдельный PostgreSQL
cd frontend
npm test                         # Vitest
npm run typecheck
npm run lint
```

Backend-тесты покрывают: аутентификацию и CSRF, ролевой доступ ко всем разделам, изоляцию данных студентов (расписание, оценки, опросы, уведомления), диапазон оценок 0–100 и уникальность, публикацию оценок, жизненный цикл опросов и валидацию ответов, архивирование групп, уведомления по трём событиям, выгрузки Excel и защиту от формул.

Если Docker Hub недоступен (таймауты при `docker pull`), образы можно взять из зеркала и перетегировать:

```powershell
docker pull mirror.gcr.io/library/postgres:17-alpine; docker tag mirror.gcr.io/library/postgres:17-alpine postgres:17-alpine
docker pull mirror.gcr.io/library/nginx:1.29-alpine;  docker tag mirror.gcr.io/library/nginx:1.29-alpine nginx:1.29-alpine
docker pull mirror.gcr.io/library/node:24-alpine;     docker tag mirror.gcr.io/library/node:24-alpine node:24-alpine
```

## Миграции БД

Схема меняется только через миграции EF Core (инструмент закреплён в `dotnet-tools.json`):

```powershell
dotnet tool restore
dotnet ef migrations add <Name> --project backend/src/MbaLms.Api --output-dir Data/Migrations
dotnet ef database update --project backend/src/MbaLms.Api   # либо автоматически при старте API
```

В production (`Database:MigrateOnStartup=false` по умолчанию вне Docker/Development) можно сгенерировать SQL-скрипт: `dotnet ef migrations script --idempotent --project backend/src/MbaLms.Api`.

## Часовой пояс

- Все моменты времени хранятся в UTC (`timestamptz`).
- Расписание и сроки опросов вводятся и показываются в часовом поясе программы `App:TimeZone` (по умолчанию `Europe/Moscow`, переменная `APP_TIMEZONE` в Docker) — независимо от часового пояса браузера пользователя. Это исключает расхождения у студентов, находящихся в других поясах.
- API принимает локальное время программы без смещения (`2026-10-06T10:00:00`) и отдаёт и UTC-значение, и локальное (`startsAtLocal`). Несуществующее локальное время (переход на летнее время) отклоняется с ошибкой валидации.

## Резервное копирование и восстановление PostgreSQL

Бэкап (формат custom, сжатый):

```powershell
mkdir backups -Force
docker compose exec -T db pg_dump -U mbalms -d mbalms -Fc > backups/mbalms_2026-10-02.dump
```

> В Windows PowerShell 5 перенаправление `>` портит бинарные данные. Используйте PowerShell 7+, `cmd /c "docker compose exec -T db pg_dump -U mbalms -d mbalms -Fc > backups\mbalms.dump"` или дамп внутри контейнера:
> `docker compose exec db pg_dump -U mbalms -d mbalms -Fc -f /tmp/mbalms.dump` и `docker compose cp db:/tmp/mbalms.dump backups/mbalms.dump`.

Восстановление (заменяет текущие данные):

```powershell
docker compose stop api
docker compose cp backups/mbalms.dump db:/tmp/mbalms.dump
docker compose exec db pg_restore -U mbalms -d mbalms --clean --if-exists --no-owner /tmp/mbalms.dump
docker compose start api
```

Каталог `backups/` исключён из git. Дампы содержат персональные данные и пароли в виде хешей — храните их в защищённом месте. Ключи защиты данных ASP.NET (volume `api-keys`) нужны для действительности текущих сессий; при их потере пользователям достаточно войти заново.

## Безопасность

- Аутентификация — ASP.NET Core Identity: пароли хранятся хешами (PBKDF2), блокировка на 5 минут после 5 неудачных попыток, ограничение частоты запросов на вход.
- Сессия — cookie `HttpOnly`, `SameSite=Strict`; все изменяющие запросы требуют CSRF-токен (заголовок `X-XSRF-TOKEN`).
- Роли (Manager / Student) и принадлежность данных проверяются на сервере в каждом endpoint. Студент видит только своё расписание (своей группы), свои опубликованные оценки, свои ответы и уведомления.
- Ошибки возвращаются в формате ProblemDetails с кодом ошибки, без stack trace. Пароли, токены и тексты ответов опросов не логируются.
- Выгрузки доступны только менеджеру; ячейки, начинающиеся с `=`, `+`, `-`, `@`, записываются как текст (защита от formula injection).
- Секреты задаются через `.env` (Docker) или user secrets (разработка); `.env` в git не попадает.
- Для production: разместите приложение за HTTPS-прокси (cookie станет `Secure` автоматически при HTTPS-запросах; при TLS-терминации на прокси передавайте `X-Forwarded-Proto` и настройте ForwardedHeaders). Ключи DataProtection хранятся в volume без шифрования — для MVP допустимо, для production рекомендуется ограничить доступ к volume или настроить шифрование ключей.
- Email-уведомления не отправляются (только в кабинете). Отправку можно добавить отдельно, не привязываясь к провайдеру: уведомления хранятся в БД и создаются через `NotificationService`.
