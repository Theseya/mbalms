# Браузерные e2e (Playwright)

Отдельный пакет, чтобы сборка Docker-образа `web` не тянула Playwright.

Нужны локальные Vite (`http://127.0.0.1:5173`) и API (`http://127.0.0.1:5001`) против отдельной тестовой PostgreSQL, не рабочего сайта. Как поднять БД: `docs/test-environment.md`. Пароль менеджера — в `E2E_MANAGER_PASSWORD`, не в репозитории.

Установка один раз: `npm install` в этом каталоге, затем `npx playwright install chromium`.

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

`npx playwright test` без фильтра также гоняет `more-modules.spec.ts` и `mobile.spec.ts` (преподаватели, расписание, объявления, опросы, журнал изменений, кабинет студента, форма сброса пароля, viewport 390×844).
